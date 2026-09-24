"""Personal direct-to-OpenAI execution with durable, independently synced results.

No preview or credential is sent to Nektron/S3. An interrupted in-flight call
is retained as Uncertain instead of being automatically billed a second time.
"""
from concurrent.futures import ThreadPoolExecutor, wait, FIRST_COMPLETED
from contextlib import contextmanager
from dataclasses import asdict
from datetime import datetime, timezone
from decimal import Decimal
import json
import os
from pathlib import Path
import threading
from queue import SimpleQueue, Empty
from time import perf_counter
from uuid import uuid4

from dotenv import dotenv_values
from services.enrichment.model_options import SCENE_MODEL_RATES
from services.enrichment.openai_scene import OpenAISceneDescriptionProvider, SceneDescriptionProviderError, scene_description_cost_usd
from .scene_preview import prepare_scene_preview, ScenePreviewError
from .api_client import ApiError


def recommended_ai_workers() -> int:
    """Network concurrency, with conservative CPU/RAM ceilings for preview work."""
    cpus = os.cpu_count() or 1
    try:
        memory_gib = os.sysconf('SC_PHYS_PAGES') * os.sysconf('SC_PAGE_SIZE') / 1024**3
    except (AttributeError, OSError, ValueError):
        return 4
    if cpus >= 32 and memory_gib >= 32:
        return 64
    if cpus >= 16 and memory_gib >= 16:
        return 32
    if cpus >= 8 and memory_gib >= 8:
        return 16
    return 4


def personal_key() -> str:
    # Shell environment wins; never write or print the key.
    key = os.environ.get("OPENAI_API_KEY") or dotenv_values(Path(__file__).resolve().parents[2] / ".env").get("OPENAI_API_KEY")
    if not key or not key.strip():
        raise ValueError("BYOK needs OPENAI_API_KEY in the WSL environment or ignored .env")
    return key.strip()


class ByokJournal:
    def __init__(self, state):
        self.state = state
        with state._connect() as db:
            db.execute("""CREATE TABLE IF NOT EXISTS ByokAnalysis (
                JobId TEXT PRIMARY KEY, SourceId TEXT NOT NULL, ClaimId TEXT NOT NULL,
                Model TEXT NOT NULL, ContentHash TEXT NOT NULL, TaskJson TEXT NOT NULL, State TEXT NOT NULL,
                ResultJson TEXT, ErrorCode TEXT, UsageMonth TEXT, CostUsd TEXT NOT NULL DEFAULT '0')""")
            db.execute('CREATE INDEX IF NOT EXISTS IX_Byok_ContentHash ON ByokAnalysis(ContentHash)')
            db.execute('CREATE INDEX IF NOT EXISTS IX_Byok_SourceState ON ByokAnalysis(SourceId,State)')
            db.execute('CREATE INDEX IF NOT EXISTS IX_Byok_MonthCost ON ByokAnalysis(UsageMonth,CostUsd)')
            db.execute('''CREATE TABLE IF NOT EXISTS ByokPastAttempt (
                AttemptId TEXT PRIMARY KEY, JobId TEXT NOT NULL, Model TEXT NOT NULL,
                UsageMonth TEXT, CostUsd TEXT NOT NULL, ErrorCode TEXT)''')
            db.execute('CREATE INDEX IF NOT EXISTS IX_ByokPast_Month ON ByokPastAttempt(UsageMonth)')

    def requeue_failure(self, job, *, include_uncertain=False):
        """Explicit retry only. Archive possibly billed attempts before resetting."""
        allowed = ('NeedsAttention', 'Uncertain') if include_uncertain else ('NeedsAttention',)
        with self.state._connect() as db:
            db.execute('BEGIN IMMEDIATE')
            row = db.execute('SELECT * FROM ByokAnalysis WHERE JobId=?', (job,)).fetchone()
            if row is None or row['State'] not in allowed or row['ResultJson'] is not None:
                return False
            db.execute('INSERT INTO ByokPastAttempt VALUES(?,?,?,?,?,?)',
                (str(uuid4()), job, row['Model'], row['UsageMonth'], row['CostUsd'], row['ErrorCode']))
            # Keep the original claim: the server still recognizes this owner.
            db.execute("UPDATE ByokAnalysis SET State='Ready',ErrorCode=NULL,UsageMonth=NULL,CostUsd='0' WHERE JobId=?", (job,))
            return True

    def recover(self, source):
        with self.state._connect() as db:
            db.execute("UPDATE ByokAnalysis SET State='Uncertain',ErrorCode='INTERRUPTED_CALL' WHERE SourceId=? AND State='Calling'", (source,))

    def add(self, source, task, model):
        with self.state._connect() as db:
            db.execute("INSERT OR IGNORE INTO ByokAnalysis(JobId,SourceId,ClaimId,Model,ContentHash,TaskJson,State) VALUES(?,?,?,?,?,?,'Ready')",
                (task['jobId'], source, str(uuid4()), model, task['assetContentSha256'], json.dumps(task)))

    def rows(self, source, states=('Ready','ResultReady'), limit=64, exclude=()):
        with self.state._connect() as db:
            exclusion = f" AND JobId NOT IN ({','.join('?' for _ in exclude)})" if exclude else ''
            return [dict(row) for row in db.execute(
                f"SELECT * FROM ByokAnalysis WHERE SourceId=? AND State IN ({','.join('?' for _ in states)}){exclusion} ORDER BY rowid LIMIT ?",
                (source, *states, *exclude, limit))]

    def result_row(self, job):
        with self.state._connect() as db:
            return dict(db.execute('SELECT * FROM ByokAnalysis WHERE JobId=?', (job,)).fetchone())

    def set(self, job, state, error=None, result=None, cost=None):
        with self.state._connect() as db:
            db.execute("UPDATE ByokAnalysis SET State=?,ErrorCode=?,ResultJson=COALESCE(?,ResultJson),CostUsd=COALESCE(?,CostUsd) WHERE JobId=?",
                (state, error, json.dumps(result) if result is not None else None, str(cost) if cost is not None else None, job))

    def reserve(self, job, amount, cap):
        month = datetime.now(timezone.utc).strftime('%Y-%m')
        with self.state._connect() as db:
            db.execute('BEGIN IMMEDIATE')
            total = sum((Decimal(row[0]) for row in db.execute('SELECT CostUsd FROM ByokAnalysis WHERE UsageMonth=?', (month,))), Decimal(0))
            total += sum((Decimal(row[0]) for row in db.execute('SELECT CostUsd FROM ByokPastAttempt WHERE UsageMonth=?', (month,))), Decimal(0))
            if total + amount > cap:
                return False
            db.execute("UPDATE ByokAnalysis SET State='Calling',UsageMonth=?,CostUsd=? WHERE JobId=? AND State='Ready'", (month, str(amount), job))
            return True

    def counts(self, source):
        with self.state._connect() as db:
            return dict(db.execute('SELECT State,COUNT(*) FROM ByokAnalysis WHERE SourceId=? GROUP BY State', (source,)))


class ByokRunner:
    def __init__(self, api, state, device_id, *, model='gpt-5.6-terra', workers=None,
                 monthly_cap=Decimal('230'), progress=print, provider=None, preview_factory=prepare_scene_preview, include_geocode=False):
        workers = recommended_ai_workers() if workers is None else workers
        if model not in SCENE_MODEL_RATES or not 1 <= workers <= 64 or not monthly_cap.is_finite() or monthly_cap <= 0:
            raise ValueError('Choose a supported model, 1–64 workers and a positive monthly BYOK limit')
        self.api, self.state, self.device_id = api, state, device_id
        self.model, self.workers, self.cap, self.progress = model, workers, monthly_cap, progress
        self.provider = provider or OpenAISceneDescriptionProvider(personal_key(), model=model, service_tier='default')
        self.preview = preview_factory
        self.journal = ByokJournal(state)
        self.stop = threading.Event()
        # ApiClient's token refresh/store remains single-threaded. AI requests
        # never hold this lock, so slow inference does not serialize the pipeline.
        self.backend_lock = threading.Lock()
        self.backend_failed = False
        self.preview_slots = threading.BoundedSemaphore(min(8, workers))
        self.events = SimpleQueue()
        self.pause_reason = None
        self.types = ['Geocode', 'Description'] if include_geocode else ['Description']

    @contextmanager
    def worker_pool(self):
        pool = ThreadPoolExecutor(max_workers=self.workers, thread_name_prefix='byok')
        try:
            yield pool
        except BaseException:
            self.stop.set()
            raise
        finally:
            pool.shutdown(wait=True, cancel_futures=True)

    def remote(self, row, description=None):
        with self.backend_lock:
            if self.backend_failed:
                raise RuntimeError('BYOK backend unavailable; completed descriptions remain saved locally')
            try:
                return self.api.request('POST', f"/v1/jobs/{row['JobId']}/byok",
                    json={'claimId': row['ClaimId'], **({'description': description} if description is not None else {})},
                    headers={'X-Nektron-Moments-Device-Id': self.device_id,
                             'Idempotency-Key': f"byok:{row['ClaimId']}:{'result' if description else 'claim'}"})
            except Exception as error:
                if not (isinstance(error, ApiError) and error.problem.code == 'BYOK_JOB_UNAVAILABLE'):
                    # One network failure must not become 64 consecutive timeouts.
                    self.backend_failed = True
                    self.stop.set()
                raise

    def sync_result(self, row):
        result = json.loads(row['ResultJson'])
        self.remote(row, result['description'])
        self.journal.set(row['JobId'], 'Synced')
        self.events.put('synced')

    def analyze(self, row):
        if self.stop.is_set():
            return
        task = json.loads(row['TaskJson'])
        try:
            with self.preview_slots:
                if self.stop.is_set():
                    return
                preview = self.preview(task['localLocator'])
            if preview.source_sha256_hex.lower() != task['assetContentSha256'].lower():
                self.quarantine(row, 'NeedsAttention', 'SOURCE_CHANGED', cost=Decimal(0))
                return
        except (OSError, ScenePreviewError):
            self.quarantine(row, 'NeedsAttention', 'PHOTO_UNREADABLE', cost=Decimal(0))
            return
        rates = SCENE_MODEL_RATES[row['Model']]
        if self.stop.is_set():
            return
        if not self.journal.reserve(row['JobId'], rates[3], self.cap):
            self.pause_reason = 'LOCAL_MONTHLY_LIMIT'
            self.stop.set()
            return
        try:
            result = self.provider.describe_bytes(preview.content)
        except SceneDescriptionProviderError as error:
            # No blind retry of an ambiguous paid request. Keep the reservation.
            rejected = error.failure.code in {'OpenAIRateLimited', 'OpenAICreditsExhausted', 'OpenAIQuotaDeferred', 'OpenAIAuthenticationFailed'}
            state = 'Ready' if rejected else 'NeedsAttention' if not error.provider_called else 'Uncertain'
            # A rejected output belongs to one photo, not to the whole account.
            # Retain possibly billed reservations and never retry it automatically.
            image_failure = error.failure.code == 'OpenAIInvalidResponse' or error.provider_error_code in {
                'invalid_image', 'invalid_image_format', 'invalid_image_size', 'image_too_large',
                'image_too_small', 'image_parse_error', 'unsupported_image', 'invalid_base64_image',
            }
            if image_failure:
                reason = error.failure.code + (f':{error.provider_error_code}' if error.provider_error_code else '')
                self.quarantine(row, state, reason, cost=Decimal(0) if not error.provider_called else None)
                return
            self.journal.set(row['JobId'], state, error.failure.code,
                cost=Decimal(0) if rejected or not error.provider_called else None)
            self.pause_reason = error.failure.code + (f' ({error.provider_error_code})' if error.provider_error_code else '')
            if error.retry_after_seconds is not None:
                self.pause_reason += f' · retry after {error.retry_after_seconds}s'
            self.stop.set()
            return
        except Exception:
            self.journal.set(row['JobId'], 'Uncertain', 'PROVIDER_OUTCOME_UNKNOWN')
            self.pause_reason = 'PROVIDER_OUTCOME_UNKNOWN'
            self.stop.set()
            return
        charge = scene_description_cost_usd(result.usage, input_usd_per_million=rates[0],
            cached_input_usd_per_million=rates[1], output_usd_per_million=rates[2])
        self.journal.set(row['JobId'], 'ResultReady', result=asdict(result), cost=charge[0] if charge else rates[3])
        self.events.put('analyzed')
        return True

    def quarantine(self, row, state, reason, *, cost=None):
        self.journal.set(row['JobId'], state, reason, cost=cost)
        # TaskJson already preserves the source filename. Log only stable IDs and
        # sanitized codes, never image bytes, response text, credentials or URLs.
        self.events.put(f'BYOK failed photo · {row["JobId"]} · {reason} · saved in failed bucket; continuing')

    def process(self, row, *, retry=False, include_uncertain=False):
        try:
            if self.stop.is_set():
                return
            if row['Model'] != self.model:
                raise ValueError('Resume pending BYOK work with its original model before changing models')
            try:
                response = self.remote(row)
            except ApiError as error:
                if error.problem.code == 'BYOK_JOB_UNAVAILABLE':
                    self.journal.set(row['JobId'], 'Skipped', 'EXISTING_CLOUD_WORK')
                    return
                raise
            if response['status'] == 'Succeeded':
                self.journal.set(row['JobId'], 'Synced')
                return
            if retry and not self.journal.requeue_failure(row['JobId'], include_uncertain=include_uncertain):
                return
            self.state.mark_description_skipped(row['JobId'], code='BYOK_DEVICE_OWNED',
                message='Analysis is now owned by the direct BYOK workflow.')
            if self.analyze(row):
                self.sync_result(self.journal.result_row(row['JobId']))
        except BaseException:
            self.stop.set()
            raise

    def retry_rows(self, rows, *, include_uncertain=False):
        """Try each selected failure once, never discovering unrelated backlog."""
        pending = set()
        remaining = iter(rows)
        exhausted = False
        completed = 0
        self.progress(f'BYOK retry · {len(rows):,} failed photos · original model {self.model}')
        with self.worker_pool() as pool:
            while pending or (not exhausted and not self.stop.is_set()):
                while len(pending) < self.workers * 2 and not exhausted and not self.stop.is_set():
                    row = next(remaining, None)
                    if row is None:
                        exhausted = True
                    else:
                        pending.add(pool.submit(self.process, row, retry=True, include_uncertain=include_uncertain))
                if pending:
                    done, pending = wait(pending, timeout=.25, return_when=FIRST_COMPLETED)
                    for future in done:
                        future.result()
                        completed += 1
                    if done:
                        self.progress(f'BYOK retry · {completed:,}/{len(rows):,} selected photos checked')
                while not self.events.empty():
                    event = self.events.get_nowait()
                    if event not in {'analyzed', 'synced'}:
                        self.progress(event)
        if self.stop.is_set():
            self.progress(f'BYOK paused · {self.pause_reason or "STOPPED"} · completed results are saved')
        else:
            self.progress('BYOK retry finished · successful results saved; remaining failures stay in the failed bucket')

    def run(self, binding, limit):
        if not 1 <= limit <= 1_000_000:
            raise ValueError('BYOK run limit must be between 1 and 1000000')
        self.journal.recover(binding.source_id)
        # Flush saved results first. A failed sync never causes repeat inference.
        while rows := self.journal.rows(binding.source_id, ('ResultReady',)):
            for row in rows:
                self.sync_result(row)
        cursor, used, completed_count = None, 0, 0
        started = perf_counter()
        def report_events():
            nonlocal completed_count
            while True:
                try:
                    event = self.events.get_nowait()
                except Empty:
                    break
                if event == 'analyzed':
                    completed_count += 1
                    rate = completed_count / max(.001, perf_counter() - started)
                    self.progress(f'BYOK analyzed · {completed_count:,}/{limit:,} descriptions completed · saved locally · {rate:.2f} items/s')
                elif event == 'synced':
                    self.progress('BYOK synchronized · description saved to your library')
                else:
                    self.progress(event)
        self.progress(f'BYOK workers · {self.workers} AI requests · up to {min(8, self.workers)} preview decoders')
        seen = set()
        pending = {}
        exhausted = False
        with self.worker_pool() as pool:
            while pending or (used < limit and not exhausted and not self.stop.is_set()):
                report_events()
                for future in list(pending):
                    if future.done():
                        del pending[future]
                        future.result()
                capacity = min(64, self.workers * 2 - len(pending), limit-used)
                if capacity <= 0 or exhausted or self.stop.is_set():
                    if pending:
                        wait(pending, timeout=.25, return_when=FIRST_COMPLETED)
                    continue
                rows = self.journal.rows(binding.source_id, ('Ready',), capacity, exclude=tuple(pending.values()))
                page_count = len(rows)
                next_cursor = cursor
                if not rows:
                    with self.backend_lock:
                        if self.stop.is_set():
                            continue
                        try:
                            prepared = self.api.prepare_enrichment(binding.source_id,
                                {'types': self.types, 'executionMode': 'BYOK', 'limit': capacity, 'descriptionModel': self.model,
                                 **({'cursor': cursor} if cursor else {})}, device_id=self.device_id, key=f'byok-prepare:{uuid4()}')
                        except Exception:
                            self.backend_failed = True
                            self.stop.set()
                            raise
                    if prepared.get('sourceId') != binding.source_id:
                        raise ValueError('BYOK preparation returned a different source')
                    for task in prepared.get('sceneDescriptionTasks', []):
                        self.journal.add(binding.source_id, task, self.model)
                    rows = self.journal.rows(binding.source_id, ('Ready',), capacity, exclude=tuple(pending.values()))
                    page_count = max(len(rows), int(prepared.get('assetsConsidered', len(rows))))
                    next_cursor = prepared.get('nextCursor')
                if not rows:
                    used += page_count
                    if not next_cursor or next_cursor in seen:
                        exhausted = True
                        continue
                    seen.add(next_cursor); cursor = next_cursor
                    continue
                for row in rows:
                    if self.stop.is_set(): break
                    pending[pool.submit(self.process, row)] = row['JobId']
                used += page_count
                cursor = next_cursor
                if next_cursor: seen.add(next_cursor)
        report_events()
        counts = self.journal.counts(binding.source_id)
        self.progress('BYOK status · ' + ' · '.join(f'{key}: {value:,}' for key,value in counts.items()))
        if self.stop.is_set():
            self.progress(f'BYOK paused · {self.pause_reason or "STOPPED"} · completed results are saved; resume after resolving this condition')
        elif failed := counts.get('Uncertain', 0) + counts.get('NeedsAttention', 0):
            self.progress(f'BYOK completed with failures · {failed:,} photos in the saved failed bucket; no automatic paid retries')
        return counts
