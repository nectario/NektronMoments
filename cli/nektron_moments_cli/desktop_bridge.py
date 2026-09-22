"""Local, stdio-only adapter for the first Windows client.

No listening port, raw credentials, MySQL access, uploads, or enrichment calls.
The existing CLI account/cache remains authoritative. A disposable, account-local
catalog isolates UI sorting/pagination from the CLI's durable sync database.
The source snapshot is rebuilt only when the CLI database/WAL fingerprint changes.
"""
from __future__ import annotations

import json
import hashlib
import re
import sqlite3
import sys
from contextlib import closing
from datetime import datetime, timezone
from pathlib import Path, PurePosixPath
from typing import Any


VIDEO_EXTENSIONS = {".mp4", ".mov", ".avi", ".mkv", ".m4v", ".mts", ".m2ts", ".3gp",
                    ".wmv", ".webm", ".mpeg", ".mpg", ".ogv", ".vob"}


def windows_path(value: str) -> str:
    match = re.match(r"^/mnt/([a-zA-Z])/(.*)$", value)
    if match:
        return match[1].upper() + ":\\" + match[2].replace("/", "\\")
    if re.match(r"^[A-Za-z]:[\\/]", value):
        return value.replace("/", "\\")
    if value.startswith("/"):
        return "\\\\wsl.localhost\\Ubuntu" + value.replace("/", "\\")
    raise ValueError("A library locator must be an absolute path.")


SCREENSHOT_QUERIES = ("screenshot", "screen shot", "screen capture", "screen-shot", "screen-capture", "screencapture",
                      "text", "website", "webpage", "web page", "browser", "digital", "online article")
SCREENSHOT_WORD = re.compile(r"\bscreen[\s-]?(?:shot|capture)s?\b", re.IGNORECASE)
TEXT_CAPTURE = re.compile(
    r"\b(?:website|web\s?page|browser|online article|digital (?:document|page|text))\b"
    r"|\b(?:text[- ]only|text[- ]based|text[- ]heavy) (?:image|capture|graphic)\b"
    r"|\b(?:block|paragraph|excerpt|passage|portion) of (?:\w+\s+){0,2}text\b"
    r"|\b(?:black|white|typed) text on (?:a |an )?(?:plain |solid )?(?:white|black|blank) background\b",
    re.IGNORECASE,
)
PHYSICAL_TEXT = re.compile(
    r"\b(?:book|newspaper|magazine|sign|billboard|poster|handwritten|notebook|paper|receipt|menu|monitor|laptop|person|holding)\b",
    re.IGNORECASE,
)


def describes_screenshot(text):
    if not isinstance(text, str):
        return False
    for match in SCREENSHOT_WORD.finditer(text):
        prefix = text[max(0, match.start() - 40):match.start()]
        if not re.search(r"(?:\bnot|\bno|isn't|rather than|instead of)\s+(?:(?:a|an|the|real|actual|really)\s+){0,4}$", prefix, re.I):
            return True
    # Description-based evidence only: preserve physical scenes containing text,
    # and explicit negations. No filename or image-size guesses, no paid calls.
    if SCREENSHOT_WORD.search(text) or PHYSICAL_TEXT.search(text):
        return False
    for match in TEXT_CAPTURE.finditer(text):
        prefix = text[max(0, match.start() - 40):match.start()]
        if not re.search(r"(?:\bnot|\bno|isn't|rather than|instead of)\s+(?:(?:a|an|the|real|actual|really)\s+){0,4}$", prefix, re.I):
            return True
    return False


class DesktopCatalog:
    def __init__(self, state_path: Path):
        self.state_path = state_path
        self.cache_path = state_path.with_name("desktop-library-v1.sqlite3")
        self.db = sqlite3.connect(":memory:")
        self.db.row_factory = sqlite3.Row
        self.sources: list[dict[str, Any]] = []
        if self._load_cache():
            return
        self.refresh()

    def _fingerprint(self):
        parts = []
        for path in (self.state_path, Path(str(self.state_path) + "-wal")):
            try:
                info = path.stat()
                parts.append((info.st_size, info.st_mtime_ns))
            except FileNotFoundError:
                parts.append(None)
        return json.dumps(parts)

    def _load_cache(self):
        if not self.cache_path.is_file():
            return False
        cached = sqlite3.connect(self.cache_path)
        cached.row_factory = sqlite3.Row
        try:
            metadata = dict(cached.execute("SELECT Name,Value FROM CatalogMeta"))
            if metadata.get("version") != "3":
                cached.close()
                return False
            if metadata.get("fingerprint") != self._fingerprint():
                self.db.close()
                self.db = cached
                self._ensure_order_indexes(cached)
                return False  # Refresh carries indexed descriptions forward by hash.
            sources = json.loads(metadata["sources"])
            if not isinstance(sources, list):
                raise ValueError("Invalid catalog source list")
            self.db.close()
            self.db, self.sources = cached, sources
            self._ensure_order_indexes(cached)
            return True
        except (sqlite3.Error, KeyError, ValueError):
            cached.close()
            return False

    def _source_db(self):
        connection = sqlite3.connect(self.state_path.resolve().as_uri() + "?mode=ro", uri=True)
        connection.row_factory = sqlite3.Row
        connection.execute("PRAGMA query_only=ON")
        return connection

    @staticmethod
    def _ensure_order_indexes(connection):
        connection.create_function("describes_screenshot", 1, describes_screenshot, deterministic=True)
        # Description carry-forward and BYOK merges address Media by Hash, not
        # its Key primary key. Install before merging, including legacy caches.
        connection.execute("CREATE INDEX IF NOT EXISTS IX_Media_Hash ON Media(Hash)")
        connection.execute("CREATE TABLE IF NOT EXISTS Screenshot(Hash TEXT PRIMARY KEY)")
        connection.execute("CREATE INDEX IF NOT EXISTS IX_Media_OrderDesc ON Media((Captured=''),Captured DESC,Key)")
        connection.execute("CREATE INDEX IF NOT EXISTS IX_Media_OrderAsc ON Media((Captured=''),Captured ASC,Key)")
        connection.commit()

    def refresh(self) -> dict[str, Any]:
        fingerprint = self._fingerprint()
        # Build separately so a failed refresh cannot destroy the current view.
        fresh = sqlite3.connect(":memory:")
        fresh.row_factory = sqlite3.Row
        fresh.executescript("""
            CREATE TABLE Media (
                Key TEXT PRIMARY KEY, Hash TEXT, FileName TEXT, Path TEXT,
                Captured TEXT, DateSource TEXT, MediaType TEXT, ByteSize INTEGER, ModifiedNs INTEGER,
                MetadataJson TEXT, Description TEXT DEFAULT '', Address TEXT DEFAULT '',
                AssetId TEXT DEFAULT '', DetailJson TEXT DEFAULT '');
            CREATE TABLE Occurrence (
                Key TEXT, SourceId TEXT, Source TEXT, Path TEXT,
                PRIMARY KEY (SourceId, Path));
            CREATE INDEX IX_Occurrence_Key ON Occurrence(Key);
            CREATE INDEX IX_Media_Date ON Media(Captured DESC, Key);
        """)
        try:
            with closing(self._source_db()) as source:
                source.execute("BEGIN")
                sources = [dict(row) for row in source.execute(
                    "SELECT SourceId, DisplayName, RootPath FROM SourceBinding WHERE StorageMode='Local'"
                )]
                by_id = {row["SourceId"]: row for row in sources}
                cached_paths = set()
                for row in source.execute("SELECT * FROM FileCache"):
                    binding = by_id.get(row["SourceId"])
                    if not binding:
                        continue
                    cached_paths.add((row["SourceId"], row["FilePath"]))
                    try:
                        metadata = json.loads(row["MetadataJson"])
                    except (ValueError, TypeError):
                        metadata = {}
                    if not isinstance(metadata, dict):
                        metadata = {}
                    self._insert(fresh, binding, row["FilePath"], row["ContentSha256"],
                                 row["ByteSize"], row["ModifiedNs"], metadata)
                for row in source.execute("SELECT * FROM KnownOccurrence"):
                    binding = by_id.get(row["SourceId"])
                    if not binding:
                        continue
                    locator = row["RelativePath"]
                    path = locator if locator.startswith("/") or re.match(r"^[A-Za-z]:[\\/]", locator) else binding["RootPath"].rstrip("/") + "/" + locator
                    if (row["SourceId"], path) in cached_paths:
                        continue
                    self._insert(fresh, binding, path, None, None, None, {})
            fresh.commit()
            self._ensure_order_indexes(fresh)
            # Descriptions and screenshot classifications survive a metadata scan.
            if self.db.execute("SELECT 1 FROM sqlite_master WHERE name='Media'").fetchone():
                fresh.executemany("UPDATE Media SET Description=CASE WHEN Description='' THEN ? ELSE Description END,Address=?,AssetId=?,DetailJson=? WHERE Hash=?",
                    self.db.execute("SELECT Description,Address,AssetId,DetailJson,Hash FROM Media WHERE Hash<>'' AND (Description<>'' OR DetailJson<>'')"))
            if self.db.execute("SELECT 1 FROM sqlite_master WHERE name='Screenshot'").fetchone():
                fresh.executemany("INSERT INTO Screenshot VALUES(?)", self.db.execute("SELECT Hash FROM Screenshot"))
            with closing(self._source_db()) as local:
                if local.execute("SELECT 1 FROM sqlite_master WHERE name='ByokAnalysis'").fetchone():
                    fresh.executemany("UPDATE Media SET Description=? WHERE Hash=?", (
                        (json.loads(row['ResultJson'])['description'], row['ContentHash'])
                        for row in local.execute("SELECT ContentHash,ResultJson FROM ByokAnalysis WHERE State IN ('ResultReady','Synced') AND ResultJson IS NOT NULL")))
            projected_sources = [{
                "id": s["SourceId"], "name": s["DisplayName"], "path": windows_path(s["RootPath"]),
            } for s in sources]
            fresh.execute("CREATE TABLE CatalogMeta(Name TEXT PRIMARY KEY, Value TEXT)")
            fresh.executemany("INSERT INTO CatalogMeta VALUES(?,?)", [
                ("version", "3"), ("fingerprint", fingerprint), ("sources", json.dumps(projected_sources)),
            ])
            fresh.commit()
            temporary = self.cache_path.with_suffix(".tmp.sqlite3")
            with closing(sqlite3.connect(temporary)) as disk:
                fresh.backup(disk)
            self.db.close()
            temporary.replace(self.cache_path)
            self.db = sqlite3.connect(self.cache_path)
            self.db.row_factory = sqlite3.Row
            self._ensure_order_indexes(self.db)
            self.sources = projected_sources
            fresh.close()
            return self.overview()
        except Exception:
            fresh.close()
            raise

    @staticmethod
    def _insert(db, binding, path, content_hash, byte_size, modified_ns, metadata):
        key = content_hash or f"pending:{binding['SourceId']}:{path}"
        name = PurePosixPath(path.replace("\\", "/")).name
        captured = metadata.get("capturedAtLocal") or metadata.get("capturedAtUtc") or ""
        provenance = metadata.get("provenance") or []
        date_source = next((str(p.get("source", "")) for p in provenance if p.get("field") == "capturedAt"), "")
        if not captured and modified_ns:
            captured = datetime.fromtimestamp(modified_ns / 1e9, timezone.utc).isoformat()
            date_source = "FileMtime"
        media_type = "Video" if PurePosixPath(name).suffix.lower() in VIDEO_EXTENSIONS else "Photo"
        native = windows_path(path)
        db.execute(
            "INSERT OR IGNORE INTO Media(Key,Hash,FileName,Path,Captured,DateSource,MediaType,ByteSize,ModifiedNs,MetadataJson) VALUES(?,?,?,?,?,?,?,?,?,?)",
            (key, content_hash or "", name, native, captured, date_source, media_type, byte_size, modified_ns, json.dumps(metadata)),
        )
        db.execute("INSERT OR IGNORE INTO Occurrence VALUES(?,?,?,?)",
                   (key, binding["SourceId"], binding["DisplayName"], native))
        description = metadata.get("description") or metadata.get("sceneDescription") or ""
        if isinstance(description, dict):
            description = description.get("text", "")
        if isinstance(description, str) and description:
            db.execute("UPDATE Media SET Description=? WHERE Key=?", (description, key))

    def overview(self):
        row = self.db.execute(
            "SELECT COUNT(*) total, SUM(MediaType='Photo') photos, SUM(MediaType='Video') videos, SUM(Hash='') pending FROM Media"
        ).fetchone()
        return {"total": row["total"], "photos": row["photos"] or 0, "videos": row["videos"] or 0,
                "pending": row["pending"] or 0, "sources": self.sources, "protocol": 1,
                "libraryId": hashlib.sha256(str(self.state_path.resolve()).encode()).hexdigest()}

    @staticmethod
    def _filter(query="", source_id="", media_type="", hide_screenshots=False):
        terms, values = ["1=1"], []
        if query:
            terms.append("(m.FileName LIKE ? ESCAPE '\\' OR m.Description LIKE ? ESCAPE '\\' OR m.Address LIKE ? ESCAPE '\\')")
            escaped = query.replace("\\", "\\\\").replace("%", "\\%").replace("_", "\\_")
            values.extend(["%" + escaped + "%"] * 3)
        if source_id:
            terms.append("EXISTS(SELECT 1 FROM Occurrence o WHERE o.Key=m.Key AND o.SourceId=?)")
            values.append(source_id)
        if media_type in {"Photo", "Video"}:
            terms.append("m.MediaType=?")
            values.append(media_type)
        if hide_screenshots:
            terms.append("NOT (m.MediaType='Photo' AND (describes_screenshot(m.Description) OR EXISTS(SELECT 1 FROM Screenshot s WHERE s.Hash=m.Hash)))")
        return " AND ".join(terms), values

    def catalog(self, *, query="", source_id="", media_type="", ascending=False, hide_screenshots=False):
        """Complete lightweight timeline, not complete decoded images or metadata.

        Publishing this list atomically gives the native virtualizing gallery its
        final extent before browsing starts. Exactly two queries keep duplicate
        fallback paths without a per-photo alias lookup or parsing EXIF JSON.
        Full details stay in get()/the explicit detail command.
        """
        where, values = self._filter(query, source_id, media_type, hide_screenshots)
        aliases = {}
        for row in self.db.execute(
            f"SELECT o.Key,o.Source,o.Path FROM Occurrence o JOIN Media m ON m.Key=o.Key WHERE {where} ORDER BY o.Key,o.Path",
            values,
        ):
            entry = aliases.setdefault(row["Key"], {"source": row["Source"], "paths": []})
            entry["paths"].append(row["Path"])
        order = "ASC" if ascending else "DESC"
        items = []
        for row in self.db.execute(
            f"SELECT m.Key,m.Hash,m.FileName,m.Path,m.Captured,m.DateSource,m.MediaType,m.ByteSize,m.ModifiedNs "
            f"FROM Media m WHERE {where} ORDER BY (Captured=''), Captured {order}, Key",
            values,
        ):
            paths = aliases.get(row["Key"], {"source": "", "paths": []})
            items.append({
                "key": row["Key"], "hash": row["Hash"], "name": row["FileName"],
                "path": row["Path"], "paths": paths["paths"], "source": paths["source"],
                "occurrences": len(paths["paths"]), "captured": row["Captured"],
                "dateSource": row["DateSource"], "mediaType": row["MediaType"],
                "byteSize": row["ByteSize"], "modifiedNs": row["ModifiedNs"],
            })
        return {"items": items, "total": len(items), "nextOffset": len(items), "hasMore": False}

    def page(self, *, query="", source_id="", media_type="", offset=0, limit=120, ascending=False, hide_screenshots=False):
        if not 0 <= offset <= 2_000_000 or not 1 <= limit <= 4096:
            raise ValueError("Invalid page bounds.")
        where, values = self._filter(query, source_id, media_type, hide_screenshots)
        total = self.db.execute("SELECT COUNT(*) FROM Media m WHERE " + where, values).fetchone()[0]
        order = "ASC" if ascending else "DESC"
        rows = self.db.execute(f"SELECT m.* FROM Media m WHERE {where} ORDER BY (Captured=''), Captured {order}, Key LIMIT ? OFFSET ?", [*values, limit, offset])
        items = [self.item(row) for row in rows]
        return {"items": items, "total": total, "nextOffset": offset + len(items),
                "hasMore": offset + len(items) < total}

    def catalog_stream(self, *, query="", source_id="", media_type="", ascending=False, hide_screenshots=False):
        """Read one consistent timeline without retaining its rows in Python.

        The count and joined cursor share a read snapshot, even when a second
        process updates the disposable catalog. A savepoint preserves any outer
        transaction owned by the caller. Aliases for only the current media item
        are retained; fetchmany bounds SQLite-to-Python row materialization.
        """
        where, values = self._filter(query, source_id, media_type, hide_screenshots)
        order = "ASC" if ascending else "DESC"
        connection = self.db
        connection.execute("SAVEPOINT desktop_catalog_stream")
        try:
            total = connection.execute("SELECT COUNT(*) FROM Media m WHERE " + where, values).fetchone()[0]
            yield {"ok": True, "catalogStart": {"version": 1, "total": total}}
            count, item = 0, None
            with closing(connection.execute(
                "SELECT m.Key,m.Hash,m.FileName,m.Path,m.Captured,m.DateSource,m.MediaType,m.ByteSize,m.ModifiedNs,"
                "o.Source AS AliasSource,o.Path AS AliasPath "
                f"FROM Media m LEFT JOIN Occurrence o ON o.Key=m.Key WHERE {where} "
                f"ORDER BY (m.Captured=''),m.Captured {order},m.Key,o.Path",
                values,
            )) as rows:
                while batch := rows.fetchmany(256):
                    for row in batch:
                        if item is None or item["key"] != row["Key"]:
                            if item is not None:
                                count += 1
                                yield {"ok": True, "item": item}
                            item = {
                                "key": row["Key"], "hash": row["Hash"], "name": row["FileName"],
                                "path": row["Path"], "paths": [], "source": row["AliasSource"] or "",
                                "occurrences": 0, "captured": row["Captured"], "dateSource": row["DateSource"],
                                "mediaType": row["MediaType"], "byteSize": row["ByteSize"], "modifiedNs": row["ModifiedNs"],
                            }
                        if row["AliasPath"] is not None:
                            item["paths"].append(row["AliasPath"])
                            item["occurrences"] += 1
                if item is not None:
                    count += 1
                    yield {"ok": True, "item": item}
            if count != total:
                raise ValueError("The catalog snapshot count was inconsistent.")
            yield {"ok": True, "catalogEnd": {"count": count}}
        finally:
            connection.execute("RELEASE SAVEPOINT desktop_catalog_stream")

    def item(self, row):
        aliases = self.db.execute("SELECT Source,Path FROM Occurrence WHERE Key=? ORDER BY Path", (row["Key"],)).fetchall()
        return {"key": row["Key"], "hash": row["Hash"], "name": row["FileName"],
                "path": row["Path"], "paths": [a["Path"] for a in aliases],
                "source": aliases[0]["Source"] if aliases else "", "occurrences": len(aliases),
                "captured": row["Captured"], "dateSource": row["DateSource"],
                "mediaType": row["MediaType"], "byteSize": row["ByteSize"], "modifiedNs": row["ModifiedNs"],
                "metadata": json.loads(row["MetadataJson"]), "description": row["Description"],
                "address": row["Address"], "assetId": row["AssetId"]}

    def get(self, key):
        row = self.db.execute("SELECT * FROM Media WHERE Key=?", (key,)).fetchone()
        if not row:
            raise ValueError("This item is no longer in the library view.")
        return self.item(row)

    def apply_remote(self, asset):
        key = asset.get("contentSha256", "")
        if not key:
            return
        location = asset.get("locationDetail") or asset.get("location") or {}
        address = location.get("streetAddress") or location.get("displayName") or ""
        description = (asset.get("description") or {}).get("text") or asset.get("descriptionExcerpt") or ""
        self.db.execute(
            "UPDATE Media SET Description=?,Address=?,AssetId=?,DetailJson=CASE WHEN ?='' THEN DetailJson ELSE ? END WHERE Hash=?",
            (description, address, asset["mediaAssetId"],
             json.dumps(asset) if "occurrences" in asset else "",
             json.dumps(asset) if "occurrences" in asset else "", key),
        )
        self.db.commit()

    def byok_description(self, content_hash):
        with closing(self._source_db()) as source:
            if not source.execute("SELECT 1 FROM sqlite_master WHERE name='ByokAnalysis'").fetchone():
                return None
            row = source.execute("SELECT ResultJson,State FROM ByokAnalysis WHERE ContentHash=? AND State IN ('ResultReady','Synced') AND ResultJson IS NOT NULL LIMIT 1", (content_hash,)).fetchone()
            return (json.loads(row['ResultJson'])['description'], row['State']) if row else None

    def activity(self):
        with closing(self._source_db()) as source:
            counts = {}
            for table in ("ManifestOutbox", "BulkManifestOutbox", "DescriptionOutbox", "ByokAnalysis"):
                exists = source.execute("SELECT 1 FROM sqlite_master WHERE type='table' AND name=?", (table,)).fetchone()
                counts[table] = dict(source.execute(f"SELECT State,COUNT(*) FROM {table} GROUP BY State")) if exists else {}
            scans = [dict(row) for row in source.execute(
                "SELECT Status,StartedAtUtc,CompletedAtUtc FROM ScanRun ORDER BY StartedAtUtc DESC LIMIT 5"
            )]
        return {"queues": counts, "scans": scans}


class Bridge:
    def __init__(self, catalog, runtime_factory=None):
        self.catalog = catalog
        self.runtime_factory = runtime_factory
        self.runtime = None

    def remote(self):
        if self.runtime is None:
            if self.runtime_factory is None:
                from .runtime import build_runtime
                self.runtime_factory = build_runtime
            self.runtime = self.runtime_factory()
        device = self.runtime.state.get_setting("device-id")
        if not device:
            raise ValueError("Register this workstation using the CLI before requesting indexed details.")
        return self.runtime.api, device

    def responses(self, request):
        if not isinstance(request, dict):
            raise ValueError("A desktop request must be an object.")
        if request.get("command") == "catalog-stream":
            yield from self.catalog.catalog_stream(
                query=str(request.get("query", ""))[:200],
                source_id=str(request.get("sourceId", "")),
                media_type=str(request.get("mediaType", "")),
                ascending=bool(request.get("ascending", False)),
                hide_screenshots=bool(request.get("hideScreenshots", False)),
            )
            return
        result = self.dispatch(request)
        # Signed URLs are not needed by this Local-only viewer.
        if isinstance(result, dict) and isinstance(result.get("remote"), dict):
            result["remote"].pop("remoteAccess", None)
        yield {"ok": True, "result": result}

    def dispatch(self, request):
        command = request.get("command")
        if command == "screenshot-page":
            index = request.get("queryIndex", 0)
            if type(index) is not int or not 0 <= index < len(SCREENSHOT_QUERIES):
                raise ValueError("Invalid screenshot query")
            api, device = self.remote()
            params = {"q": SCREENSHOT_QUERIES[index], "limit": 200, "mediaType": "Photo"}
            cursor = request.get("cursor")
            if cursor:
                if not isinstance(cursor, str) or len(cursor) > 4000:
                    raise ValueError("Invalid cursor")
                params["cursor"] = cursor
            result = api.request("GET", "/v1/media/search", params=params, headers={"X-ImageTracker-Device-Id": device})
            hashes = []
            for hit in result.get("items", []):
                asset = hit.get("asset") or {}
                description = (asset.get("description") or {}).get("text") or asset.get("descriptionExcerpt") or ""
                if hit.get("matchedField") == "Description":
                    description = hit.get("highlight") or description
                content_hash = asset.get("contentSha256", "")
                if describes_screenshot(description) and re.fullmatch(r"[0-9a-fA-F]{64}", content_hash):
                    hashes.append(content_hash.lower())
            return {"hashes": hashes, "nextCursor": (result.get("page") or {}).get("nextCursor"), "queryCount": len(SCREENSHOT_QUERIES)}
        if self.catalog is None:
            raise ValueError("This connection only reads indexed screenshot metadata")
        if command == "screenshot-index-replace":
            hashes = request.get("hashes")
            if not isinstance(hashes, list) or len(hashes) > 100000 or any(not isinstance(h, str) or not re.fullmatch(r"[0-9a-f]{64}", h) for h in hashes):
                raise ValueError("Invalid screenshot index batch")
            candidates = set(hashes)
            with self.catalog.db:
                self.catalog.db.execute("DELETE FROM Screenshot")
                self.catalog.db.executemany("INSERT INTO Screenshot VALUES(?)", ((h,) for h in candidates))
            return {"count": len(candidates)}
        if command == "hello":
            return self.catalog.overview()
        if command == "refresh":
            return self.catalog.refresh()
        if command == "page":
            return self.catalog.page(query=str(request.get("query", ""))[:200],
                                     source_id=str(request.get("sourceId", "")),
                                     media_type=str(request.get("mediaType", "")),
                                     offset=int(request.get("offset", 0)), limit=int(request.get("limit", 120)),
                                     ascending=bool(request.get("ascending", False)), hide_screenshots=bool(request.get("hideScreenshots", False)))
        if command == "catalog":
            return self.catalog.catalog(query=str(request.get("query", ""))[:200],
                                        source_id=str(request.get("sourceId", "")),
                                        media_type=str(request.get("mediaType", "")),
                                        ascending=bool(request.get("ascending", False)), hide_screenshots=bool(request.get("hideScreenshots", False)))
        if command == "activity":
            return self.catalog.activity()
        if command == "search":
            query = str(request.get("query", "")).strip()[:200]
            if not query:
                return self.catalog.page()
            source_id, media_type = str(request.get("sourceId", "")), str(request.get("mediaType", ""))
            hide_screenshots = bool(request.get("hideScreenshots", False))
            local = self.catalog.page(query=query, source_id=source_id, media_type=media_type, limit=200, hide_screenshots=hide_screenshots)
            items = list(local["items"])
            seen = {i["key"] for i in items}
            try:
                api, device = self.remote()
                hits = api.search_media(query, device, limit=200, source_id=source_id or None, media_type=media_type or None)
            except Exception:
                return {**local, "hasMore": False, "bounded": local["hasMore"],
                        "notice": "Indexed search is unavailable. Local filename matches are still shown."}
            for hit in hits:
                asset = hit.get("asset") or {}
                self.catalog.apply_remote(asset)
                if hide_screenshots and self.catalog.db.execute(
                    "SELECT 1 FROM Media m WHERE Hash=? AND MediaType='Photo' AND (describes_screenshot(Description) OR EXISTS(SELECT 1 FROM Screenshot s WHERE s.Hash=m.Hash))",
                    (asset.get("contentSha256"),)).fetchone():
                    items = [i for i in items if i["hash"] != asset.get("contentSha256")]
                    continue
                row = self.catalog.db.execute("SELECT * FROM Media WHERE Hash=?", (asset.get("contentSha256"),)).fetchone()
                if row and row["Key"] not in seen and (not media_type or row["MediaType"] == media_type) and (
                    not source_id or self.catalog.db.execute("SELECT 1 FROM Occurrence WHERE Key=? AND SourceId=?", (row["Key"], source_id)).fetchone()
                ):
                    items.append(self.catalog.item(row))
                    seen.add(row["Key"])
            return {"items": items, "total": len(items), "hasMore": False,
                    "bounded": len(hits) >= 200 or local["hasMore"], "nextOffset": len(items)}
        if command == "detail":
            item = self.catalog.get(str(request.get("key", "")))
            result = {"item": item, "remote": None, "notice": ""}
            byok = self.catalog.byok_description(item['hash']) if item['hash'] else None
            if byok:
                item['description'] = byok[0]
                result['notice'] = 'AI completed on this device' + (' · Saved locally; backend synchronization pending' if byok[1] == 'ResultReady' else ' · Synchronized')
                return result
            if not item["hash"]:
                result["notice"] = "Content hashing is pending. This item has not yet been deduplicated."
                return result
            cached = self.catalog.db.execute("SELECT DetailJson FROM Media WHERE Key=?", (item["key"],)).fetchone()[0]
            if cached:
                result["remote"] = json.loads(cached)
                return result
            try:
                api, device = self.remote()
                asset_id = item["assetId"]
                if not asset_id:
                    hits = api.search_media(item["name"], device, limit=200)
                    match = next((h["asset"] for h in hits if h.get("asset", {}).get("contentSha256") == item["hash"]), None)
                    asset_id = match["mediaAssetId"] if match else ""
                if asset_id:
                    detail = api.get_media(asset_id, device)
                    self.catalog.apply_remote(detail)
                    result.update(item=self.catalog.get(item["key"]), remote=detail)
                else:
                    result["notice"] = "Showing cached file metadata. No matching indexed detail was found."
            except Exception:
                # Do not serialize SDK exceptions, signed URLs, tokens or raw HTTP bodies.
                result["notice"] = "Indexed details are unavailable. Your local original remains accessible."
            return result
        raise ValueError("Unsupported desktop request.")


def _write_responses(output, frames):
    # The client launches Python unbuffered. Combine small NDJSON records into
    # bounded writes rather than making one IPC write/flush per photo.
    pending, size = [], 0
    try:
        for frame in frames:
            line = json.dumps(frame, ensure_ascii=True, allow_nan=False) + "\n"
            pending.append(line)
            size += len(line)
            if size >= 65536 or "catalogStart" in frame:
                output.write("".join(pending))
                output.flush()
                pending, size = [], 0
    finally:
        if pending:
            output.write("".join(pending))
            output.flush()


def serve(bridge, input_stream, output_stream):
    """Finish every response stream before reading the next request."""
    for line in input_stream:
        try:
            if len(line) > 8 * 1024 * 1024:
                raise ValueError("Request too large.")
            request = json.loads(line)
            if len(line) > 8192 and (not isinstance(request, dict) or request.get("command") != "screenshot-index-replace"):
                raise ValueError("Request too large.")
            _write_responses(output_stream, bridge.responses(request))
        except (ValueError, TypeError, KeyError):
            print(json.dumps({"ok": False, "error": "The library request was invalid. Refresh and try again."}),
                  file=output_stream, flush=True)
        except Exception:
            print(json.dumps({"ok": False, "error": "Cannot read the library right now. Check the CLI connection and retry."}),
                  file=output_stream, flush=True)
    return 0


def main():
    from .auth import TokenStore
    from .config import ConfigStore
    store = ConfigStore()
    tokens = TokenStore(store.fallback_token_path).load()
    if not tokens or not tokens.local_subject:
        print(json.dumps({"ok": False, "error": "Sign in with ./scripts/cli.sh auth login first."}), flush=True)
        return 2
    bridge = Bridge(None if "--index-only" in sys.argv else DesktopCatalog(store.state_path_for_subject(tokens.local_subject)))
    return serve(bridge, sys.stdin, sys.stdout)


if __name__ == "__main__":
    raise SystemExit(main())
