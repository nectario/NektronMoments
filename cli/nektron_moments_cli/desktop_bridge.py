"""Local, stdio-only adapter for the first Windows client.

No listening port, raw credentials, MySQL access, uploads, or enrichment calls.
The existing CLI account/cache remains authoritative. A disposable, account-local
catalog isolates UI sorting/pagination from the CLI's durable sync database.
The source snapshot is rebuilt only when the CLI database/WAL fingerprint changes.
"""
from __future__ import annotations

import json
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
            if metadata.get("version") != "2" or metadata.get("fingerprint") != self._fingerprint():
                cached.close()
                return False
            sources = json.loads(metadata["sources"])
            if not isinstance(sources, list):
                raise ValueError("Invalid catalog source list")
            self.db.close()
            self.db, self.sources = cached, sources
            return True
        except (sqlite3.Error, KeyError, ValueError):
            cached.close()
            return False

    def _source_db(self):
        connection = sqlite3.connect(self.state_path.resolve().as_uri() + "?mode=ro", uri=True)
        connection.row_factory = sqlite3.Row
        connection.execute("PRAGMA query_only=ON")
        return connection

    def refresh(self) -> dict[str, Any]:
        fingerprint = self._fingerprint()
        # Build separately so a failed refresh cannot destroy the current view.
        fresh = sqlite3.connect(":memory:")
        fresh.row_factory = sqlite3.Row
        fresh.executescript("""
            CREATE TABLE Media (
                Key TEXT PRIMARY KEY, Hash TEXT, FileName TEXT, Path TEXT,
                Captured TEXT, DateSource TEXT, MediaType TEXT, ByteSize INTEGER,
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
            projected_sources = [{
                "id": s["SourceId"], "name": s["DisplayName"], "path": windows_path(s["RootPath"]),
            } for s in sources]
            fresh.execute("CREATE TABLE CatalogMeta(Name TEXT PRIMARY KEY, Value TEXT)")
            fresh.executemany("INSERT INTO CatalogMeta VALUES(?,?)", [
                ("version", "2"), ("fingerprint", fingerprint), ("sources", json.dumps(projected_sources)),
            ])
            fresh.commit()
            temporary = self.cache_path.with_suffix(".tmp.sqlite3")
            with closing(sqlite3.connect(temporary)) as disk:
                fresh.backup(disk)
            self.db.close()
            temporary.replace(self.cache_path)
            self.db = sqlite3.connect(self.cache_path)
            self.db.row_factory = sqlite3.Row
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
            "INSERT OR IGNORE INTO Media(Key,Hash,FileName,Path,Captured,DateSource,MediaType,ByteSize,MetadataJson) VALUES(?,?,?,?,?,?,?,?,?)",
            (key, content_hash or "", name, native, captured, date_source, media_type, byte_size, json.dumps(metadata)),
        )
        db.execute("INSERT OR IGNORE INTO Occurrence VALUES(?,?,?,?)",
                   (key, binding["SourceId"], binding["DisplayName"], native))

    def overview(self):
        row = self.db.execute(
            "SELECT COUNT(*) total, SUM(MediaType='Photo') photos, SUM(MediaType='Video') videos, SUM(Hash='') pending FROM Media"
        ).fetchone()
        return {"total": row["total"], "photos": row["photos"] or 0, "videos": row["videos"] or 0,
                "pending": row["pending"] or 0, "sources": self.sources, "protocol": 1}

    def page(self, *, query="", source_id="", media_type="", offset=0, limit=120, ascending=False):
        if not 0 <= offset <= 2_000_000 or not 1 <= limit <= 200:
            raise ValueError("Invalid page bounds.")
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
        where = " AND ".join(terms)
        total = self.db.execute("SELECT COUNT(*) FROM Media m WHERE " + where, values).fetchone()[0]
        order = "ASC" if ascending else "DESC"
        rows = self.db.execute(f"SELECT m.* FROM Media m WHERE {where} ORDER BY (Captured=''), Captured {order}, Key LIMIT ? OFFSET ?", [*values, limit, offset])
        items = [self.item(row) for row in rows]
        return {"items": items, "total": total, "nextOffset": offset + len(items),
                "hasMore": offset + len(items) < total}

    def item(self, row):
        aliases = self.db.execute("SELECT Source,Path FROM Occurrence WHERE Key=? ORDER BY Path", (row["Key"],)).fetchall()
        return {"key": row["Key"], "hash": row["Hash"], "name": row["FileName"],
                "path": row["Path"], "paths": [a["Path"] for a in aliases],
                "source": aliases[0]["Source"] if aliases else "", "occurrences": len(aliases),
                "captured": row["Captured"], "dateSource": row["DateSource"],
                "mediaType": row["MediaType"], "byteSize": row["ByteSize"],
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

    def activity(self):
        with closing(self._source_db()) as source:
            counts = {}
            for table in ("ManifestOutbox", "BulkManifestOutbox", "DescriptionOutbox"):
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

    def dispatch(self, request):
        command = request.get("command")
        if command == "hello":
            return self.catalog.overview()
        if command == "refresh":
            return self.catalog.refresh()
        if command == "page":
            return self.catalog.page(query=str(request.get("query", ""))[:200],
                                     source_id=str(request.get("sourceId", "")),
                                     media_type=str(request.get("mediaType", "")),
                                     offset=int(request.get("offset", 0)), limit=int(request.get("limit", 120)),
                                     ascending=bool(request.get("ascending", False)))
        if command == "activity":
            return self.catalog.activity()
        if command == "search":
            query = str(request.get("query", "")).strip()[:200]
            if not query:
                return self.catalog.page()
            source_id, media_type = str(request.get("sourceId", "")), str(request.get("mediaType", ""))
            local = self.catalog.page(query=query, source_id=source_id, media_type=media_type, limit=200)
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


def main():
    from .auth import TokenStore
    from .config import ConfigStore
    store = ConfigStore()
    tokens = TokenStore(store.fallback_token_path).load()
    if not tokens or not tokens.local_subject:
        print(json.dumps({"ok": False, "error": "Sign in with ./scripts/cli.sh auth login first."}), flush=True)
        return 2
    bridge = Bridge(DesktopCatalog(store.state_path_for_subject(tokens.local_subject)))
    for line in sys.stdin:
        try:
            if len(line) > 8192:
                raise ValueError("Request too large.")
            result = bridge.dispatch(json.loads(line))
            # remoteAccess contains signed URLs and is not needed by this Local-only viewer.
            if isinstance(result, dict) and isinstance(result.get("remote"), dict):
                result["remote"].pop("remoteAccess", None)
            print(json.dumps({"ok": True, "result": result}, ensure_ascii=True, allow_nan=False), flush=True)
        except (ValueError, TypeError, KeyError):
            print(json.dumps({"ok": False, "error": "The library request was invalid. Refresh and try again."}), flush=True)
        except Exception:
            print(json.dumps({"ok": False, "error": "Cannot read the library right now. Check the CLI connection and retry."}), flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
