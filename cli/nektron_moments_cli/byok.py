"""Personal direct-to-OpenAI execution with durable, independently synced results.

No preview or credential is sent to Nektron/S3. An interrupted in-flight call
is retained as Uncertain instead of being automatically billed a second time.
"""
from concurrent.futures import ThreadPoolExecutor, as_completed
from contextlib import contextmanager
from dataclasses import asdict
from datetime import datetime, timezone
from decimal import Decimal
import json
import os
from pathlib import Path
import threading
from uuid import uuid4

from dotenv import dotenv_values
from services.enrichment.model_options import SCENE_MODEL_RATES
from services.enrichment.openai_scene import OpenAISceneDescriptionProvider, SceneDescriptionProviderError, scene_description_cost_usd
from .scene_preview import prepare_scene_preview, ScenePreviewError
from .api_client import ApiError


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

    def recover(self, source):
        with self.state._connect() as db:
            db.execute("UPDATE ByokAnalysis SET State='Uncertain',ErrorCode='INTERRUPTED_CALL' WHERE SourceId=? AND State='Calling'", (source,))

    def add(self, source, task, model):
        with self.state._connect() as db:
            db.execute("INSERT OR IGNORE INTO ByokAnalysis(JobId,SourceId,ClaimId,Model,ContentHash,TaskJson,State) VALUES(?,?,?,?,?,?,'Ready')",
                (task['jobId'], source, str(uuid4()), model, task['assetContentSha256'], json.dumps(task)))

    def rows(self, source, states=('Ready','ResultReady'), limit=64):
        with self.state._connect() as db:
            return [dict(row) for row in db.execute(
                f"SELECT * FROM ByokAnalysis WHERE SourceId=? AND State IN ({','.join('?' for _ in states)}) ORDER BY rowid LIMIT ?",
                (source, *states, limit))]

    def set(self, job, state, error=None, result=None, cost=None):
        with self.state._connect() as db:
            db.execute("UPDATE ByokAnalysis SET State=?,ErrorCode=?,ResultJson=COALESCE(?,ResultJson),CostUsd=COALESCE(?,CostUsd) WHERE JobId=?",
                (state, error, json.dumps(result) if result is not None else None, str(cost) if cost is not None else None, job))

    def reserve(self, job, amount, cap):
        month = datetime.now(timezone.utc).strftime('%Y-%m')
        with self.state._connect() as db:
            db.execute('BEGIN IMMEDIATE')
            total = sum((Decimal(row[0]) for row in db.execute('SELECT CostUsd FROM ByokAnalysis WHERE UsageMonth=?', (month,))), Decimal(0))
            if total + amount > cap:
                return False
            db.execute("UPDATE ByokAnalysis SET State='Calling',UsageMonth=?,CostUsd=? WHERE JobId=? AND State='Ready'", (month, str(amount), job))
            return True

    def counts(self, source):
        with self.state._connect() as db:
            return dict(db.execute('SELECT State,COUNT(*) FROM ByokAnalysis WHERE SourceId=? GROUP BY State', (source,)))


class ByokRunner:
    def __init__(self, api, state, device_id, *, model='gpt-5.6-terra', workers=4,
                 monthly_cap=Decimal('230'), progress=print, provider=None, preview_factory=prepare_scene_preview, include_geocode=False):
        if model not in SCENE_MODEL_RATES or not 1 <= workers <= 16 or not monthly_cap.is_finite() or monthly_cap <= 0:
            raise ValueError('Choose a supported model, 1–16 workers and a positive monthly BYOK limit')
        self.api, self.state, self.device_id = api, state, device_id
        self.model, self.workers, self.cap, self.progress = model, workers, monthly_cap, progress
        self.provider = provider or OpenAISceneDescriptionProvider(personal_key(), model=model, service_tier='default')
        self.preview = preview_factory
        self.journal = ByokJournal(state)
        self.stop = threading.Event()
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
        return self.api.request('POST', f"/v1/jobs/{row['JobId']}/byok",
            json={'claimId': row['ClaimId'], **({'description': description} if description is not None else {})},
            headers={'X-Nektron-Moments-Device-Id': self.device_id,
                     'Idempotency-Key': f"byok:{row['ClaimId']}:{'result' if description else 'claim'}"})

    def sync_result(self, row):
        result = json.loads(row['ResultJson'])
        self.remote(row, result['description'])
        self.journal.set(row['JobId'], 'Synced')
        self.progress('BYOK synchronized · description saved to your library')

    def analyze(self, row):
        if self.stop.is_set():
            return
        task = json.loads(row['TaskJson'])
        try:
            preview = self.preview(task['localLocator'])
            if preview.source_sha256_hex.lower() != task['assetContentSha256'].lower():
                self.journal.set(row['JobId'], 'NeedsAttention', 'SOURCE_CHANGED')
                return
        except (OSError, ScenePreviewError):
            self.journal.set(row['JobId'], 'NeedsAttention', 'PHOTO_UNREADABLE')
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
        return True

    def run(self, binding, limit):
        if not 1 <= limit <= 1_000_000:
            raise ValueError('BYOK run limit must be between 1 and 1000000')
        self.journal.recover(binding.source_id)
        # Flush saved results first. A failed sync never causes repeat inference.
        while rows := self.journal.rows(binding.source_id, ('ResultReady',)):
            for row in rows:
                self.sync_result(row)
        cursor, used, completed_count = None, 0, 0
        seen = set()
        with self.worker_pool() as pool:
            while used < limit and not self.stop.is_set():
                rows = self.journal.rows(binding.source_id, ('Ready',), min(64, limit-used))
                page_count = len(rows)
                next_cursor = cursor
                if not rows:
                    prepared = self.api.prepare_enrichment(binding.source_id,
                        {'types': self.types, 'executionMode': 'BYOK', 'limit': min(64, limit-used), 'descriptionModel': self.model,
                         **({'cursor': cursor} if cursor else {})}, device_id=self.device_id, key=f'byok-prepare:{uuid4()}')
                    if prepared.get('sourceId') != binding.source_id:
                        raise ValueError('BYOK preparation returned a different source')
                    for task in prepared.get('sceneDescriptionTasks', []):
                        self.journal.add(binding.source_id, task, self.model)
                    rows = self.journal.rows(binding.source_id, ('Ready',), min(64, limit-used))
                    page_count = max(len(rows), int(prepared.get('assetsConsidered', len(rows))))
                    next_cursor = prepared.get('nextCursor')
                if not rows:
                    used += page_count
                    if not next_cursor or next_cursor in seen: break
                    seen.add(next_cursor); cursor = next_cursor
                    continue
                futures = []
                for row in rows:
                    if self.stop.is_set(): break
                    if row['Model'] != self.model:
                        raise ValueError('Resume pending BYOK work with its original model before changing models')
                    try:
                        response = self.remote(row)
                    except ApiError as error:
                        if error.problem.code == 'BYOK_JOB_UNAVAILABLE':
                            self.journal.set(row['JobId'], 'Skipped', 'EXISTING_CLOUD_WORK')
                            continue
                        raise
                    if response['status'] == 'Succeeded':
                        self.journal.set(row['JobId'], 'Synced'); continue
                    self.state.mark_description_skipped(row['JobId'], code='BYOK_DEVICE_OWNED',
                        message='Analysis is now owned by the direct BYOK workflow.')
                    futures.append(pool.submit(self.analyze, row))
                for future in as_completed(futures):
                    if future.result():
                        completed_count += 1
                        self.progress(f'BYOK analyzed · {completed_count:,}/{limit:,} descriptions completed · saved locally')
                        for completed in self.journal.rows(binding.source_id, ('ResultReady',)):
                            self.sync_result(completed)
                # Provider threads never use the shared backend auth session.
                for row in self.journal.rows(binding.source_id, ('ResultReady',)):
                    self.sync_result(row)
                used += page_count
                self.progress(f'BYOK progress · {used:,}/{limit:,} photos considered')
                cursor = next_cursor
                if next_cursor: seen.add(next_cursor)
        counts = self.journal.counts(binding.source_id)
        self.progress('BYOK status · ' + ' · '.join(f'{key}: {value:,}' for key,value in counts.items()))
        if self.stop.is_set():
            self.progress(f'BYOK paused · {self.pause_reason or "STOPPED"} · completed results are saved; resume after resolving this condition')
        return counts
