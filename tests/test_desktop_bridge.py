from __future__ import annotations
import json
import io
import sqlite3
from pathlib import Path
from types import SimpleNamespace

import pytest
from cli.nektron_moments_cli.desktop_bridge import Bridge, DesktopCatalog, serve, windows_path


@pytest.fixture
def catalog(tmp_path):
    state = tmp_path / "state.sqlite3"
    with sqlite3.connect(state) as db:
        db.executescript("""
            CREATE TABLE SourceBinding(SourceId TEXT,DisplayName TEXT,RootPath TEXT,StorageMode TEXT);
            CREATE TABLE FileCache(SourceId TEXT,FilePath TEXT,ContentSha256 TEXT,ByteSize INTEGER,ModifiedNs INTEGER,MetadataJson TEXT);
            CREATE TABLE KnownOccurrence(SourceId TEXT,RelativePath TEXT);
            CREATE TABLE ScanRun(Status TEXT,StartedAtUtc TEXT,CompletedAtUtc TEXT);
            INSERT INTO SourceBinding VALUES('one','Photos','/mnt/d/Pictures','Local');
            INSERT INTO SourceBinding VALUES('two','Copies','/mnt/e/Photos','Local');
            INSERT INTO SourceBinding VALUES('remote','Remote','/mnt/f/Remote','Remote');
        """)
        for source, path, content_hash in (
            ("one", "/mnt/d/Pictures/photo.jpg", "a" * 64),
            ("two", "/mnt/e/Photos/copy.jpg", "a" * 64),
            ("one", "/mnt/d/Pictures/100%.mp4", "b" * 64),
            ("remote", "/mnt/f/Remote/hidden.jpg", "c" * 64),
        ):
            db.execute("INSERT INTO FileCache VALUES(?,?,?,?,?,?)", (source, path, content_hash, 100, 1700000000000000000,
                json.dumps({"capturedAtLocal": "2024-02-03T10:11:12", "provenance": [{"field": "capturedAt", "source": "Exif"}]})))
        db.executemany("INSERT INTO KnownOccurrence VALUES(?,?)", [
            ("one", "/mnt/d/Pictures/photo.jpg"), ("two", "copy.jpg"),
            ("one", "/mnt/d/Pictures/100%.mp4"), ("one", "pending.jpg"),
        ])
    return DesktopCatalog(state)


@pytest.mark.parametrize("source,expected", [
    ("/mnt/d/Pictures/a b.jpg", "D:\\Pictures\\a b.jpg"),
    ("C:/Photos/a.jpg", "C:\\Photos\\a.jpg"),
    ("/home/user/a.jpg", "\\\\wsl.localhost\\Ubuntu\\home\\user\\a.jpg"),
])
def test_windows_paths(source, expected):
    assert windows_path(source) == expected


def test_relative_locator_is_not_accepted_as_native_path():
    with pytest.raises(ValueError):
        windows_path("relative/a.jpg")


def test_catalog_dedupes_hashes_without_duplicating_absolute_known_paths(catalog):
    assert catalog.overview()["total"] == 3
    assert catalog.overview()["pending"] == 1
    assert len(catalog.overview()["sources"]) == 2
    photo = catalog.get("a" * 64)
    assert photo["occurrences"] == 2
    assert photo["paths"] == ["D:\\Pictures\\photo.jpg", "E:\\Photos\\copy.jpg"]


def test_source_filter_uses_all_occurrences(catalog):
    assert catalog.page(source_id="two")["total"] == 1
    assert catalog.page(source_id="two")["items"][0]["key"] == "a" * 64
    assert catalog.page(media_type="Video")["total"] == 1


def test_library_identity_is_stable_across_refresh_and_does_not_reveal_state_path(catalog):
    identity = catalog.overview()["libraryId"]
    assert len(identity) == 64 and all(character in "0123456789abcdef" for character in identity)
    assert str(catalog.state_path) not in identity
    catalog.refresh()
    assert catalog.overview()["libraryId"] == identity


def test_library_identity_separates_account_state_locations(catalog, tmp_path):
    first = catalog.overview()["libraryId"]
    catalog.state_path = tmp_path / "different-account.sqlite3"
    assert catalog.overview()["libraryId"] != first


def test_search_is_literal_and_parameterized(catalog):
    assert catalog.page(query="%")["total"] == 1
    assert catalog.page(query="' OR 1=1 --")["total"] == 0
    assert catalog.page(query="_")["total"] == 0


def test_pages_do_not_overlap_and_pending_dates_sort_last(catalog):
    first = catalog.page(limit=2)
    second = catalog.page(offset=first["nextOffset"], limit=2)
    assert first["hasMore"]
    assert not second["hasMore"]
    assert first["items"][0]["captured"]
    assert second["items"][0]["hash"] == ""
    assert not ({x["key"] for x in first["items"]} & {x["key"] for x in second["items"]})


def test_refresh_does_not_write_to_authoritative_cli_cache(catalog):
    before = catalog.state_path.read_bytes()
    catalog.refresh()
    assert catalog.state_path.read_bytes() == before
    with catalog._source_db() as source:
        with pytest.raises(sqlite3.OperationalError):
            source.execute("DELETE FROM SourceBinding")


def test_local_commands_never_instantiate_cloud_runtime(catalog):
    def forbidden():
        pytest.fail("Local browsing contacted the cloud")
    bridge = Bridge(catalog, forbidden)
    for request in ({"command": "hello"}, {"command": "page"}, {"command": "catalog"}, {"command": "activity"}, {"command": "refresh"}):
        assert bridge.dispatch(request) is not None
    assert bridge.dispatch({"command": "detail", "key": "pending:one:/mnt/d/Pictures/pending.jpg"})["remote"] is None


def test_detail_requires_exact_hash_match_not_just_filename(catalog):
    api = SimpleNamespace(search_media=lambda *args, **kw: [{"asset": {
        "contentSha256": "c" * 64, "mediaAssetId": "wrong",
    }}], get_media=lambda *args: pytest.fail("Matched the wrong file"))
    runtime = SimpleNamespace(api=api, state=SimpleNamespace(get_setting=lambda key: "device"))
    result = Bridge(catalog, lambda: runtime).dispatch({"command": "detail", "key": "a" * 64})
    assert result["remote"] is None


def test_no_mutating_rpc_surface(catalog):
    for operation in ("sync", "delete", "enrich", "upload", "execute"):
        with pytest.raises(ValueError):
            Bridge(catalog).dispatch({"command": operation})


def test_offline_indexed_search_retains_local_results(catalog):
    def offline():
        raise RuntimeError("network is unavailable")
    result = Bridge(catalog, offline).dispatch({"command": "search", "query": "photo"})
    assert result["items"][0]["key"] == "a" * 64
    assert "unavailable" in result["notice"]


def test_indexed_search_keeps_type_and_source_filters(catalog):
    api = SimpleNamespace(search_media=lambda *args, **kw: [{"asset": {
        "contentSha256": "b" * 64, "mediaAssetId": "video", "descriptionExcerpt": "a meadow",
    }}, {"asset": {
        "contentSha256": "a" * 64, "mediaAssetId": "photo", "descriptionExcerpt": "a meadow",
    }}])
    runtime = SimpleNamespace(api=api, state=SimpleNamespace(get_setting=lambda key: "device"))
    result = Bridge(catalog, lambda: runtime).dispatch({
        "command": "search", "query": "meadow", "sourceId": "two", "mediaType": "Photo",
    })
    assert [i["key"] for i in result["items"]] == ["a" * 64]
    assert catalog.db.execute("SELECT DetailJson FROM Media WHERE Key=?", ("a" * 64,)).fetchone()[0] == ""


def test_valid_cached_projection_skips_rebuilding_source(catalog, monkeypatch):
    def forbidden(*args):
        pytest.fail("Unchanged catalog was rebuilt")
    monkeypatch.setattr(DesktopCatalog, "refresh", forbidden)
    loaded = DesktopCatalog(catalog.state_path)
    assert loaded.overview()["total"] == 3


def test_projection_invalidates_after_cli_state_changes(catalog):
    with sqlite3.connect(catalog.state_path) as db:
        db.execute("INSERT INTO KnownOccurrence VALUES('one','new.jpg')")
    loaded = DesktopCatalog(catalog.state_path)
    assert loaded.overview()["total"] == 4


def test_video_types_match_the_importer():
    from cli.nektron_moments_cli.media import VIDEO_EXTENSIONS as scanner_types
    from cli.nektron_moments_cli.desktop_bridge import VIDEO_EXTENSIONS
    assert VIDEO_EXTENSIONS == scanner_types


def test_large_desktop_page_is_bounded_and_keeps_file_stamp(catalog):
    page = catalog.page(limit=4096)
    assert page["total"] == 3
    assert page["items"][0]["modifiedNs"] is not None
    with pytest.raises(ValueError):
        catalog.page(limit=4097)


def test_timeline_order_uses_its_index_without_temporary_sort(catalog):
    plan = catalog.db.execute("EXPLAIN QUERY PLAN SELECT * FROM Media ORDER BY (Captured=''),Captured DESC,Key LIMIT 4096").fetchall()
    detail = " ".join(str(row[3]) for row in plan)
    assert "IX_Media_OrderDesc" in detail
    assert "TEMP B-TREE" not in detail


@pytest.mark.parametrize("filters", [
    {}, {"ascending": True}, {"source_id": "two"}, {"media_type": "Video"},
    {"query": "%"}, {"query": "_"}, {"query": "' OR 1=1 --"},
    {"query": "photo", "source_id": "one", "media_type": "Photo"},
])
def test_complete_catalog_matches_paged_filtering_and_order(catalog, filters):
    expected = catalog.page(**filters)
    result = catalog.catalog(**filters)
    assert [item["key"] for item in result["items"]] == [item["key"] for item in expected["items"]]
    assert result["total"] == result["nextOffset"] == len(result["items"])
    assert result["hasMore"] is False
    assert all("metadata" not in item and "description" not in item for item in result["items"])


def test_complete_catalog_preserves_original_fallbacks_and_thumbnail_stamps(catalog):
    result = catalog.catalog(source_id="two")
    item = result["items"][0]
    detail = catalog.get(item["key"])
    for field in ("key", "hash", "name", "path", "paths", "source", "occurrences", "captured",
                  "dateSource", "mediaType", "byteSize", "modifiedNs"):
        assert item[field] == detail[field]
    assert len(item["paths"]) == 2  # fallback in another source is intentionally retained
    assert detail["metadata"]["capturedAtLocal"]  # full details are still available on demand


def test_complete_catalog_is_not_a_4096_row_page_and_uses_two_queries(catalog, monkeypatch):
    def forbidden(*args):
        pytest.fail("Full catalog used per-photo detail/alias retrieval")
    monkeypatch.setattr(catalog, "item", forbidden)
    catalog.db.executemany(
        "INSERT INTO Media(Key,Hash,FileName,Path,Captured,DateSource,MediaType,ByteSize,ModifiedNs,MetadataJson) "
        "VALUES(?,?,?,?,?,?,?,?,?,?)",
        ((f"bulk-{i:05d}", "", f"{i}.jpg", f"D:\\Pictures\\{i}.jpg", "2025-01-01", "Exif", "Photo", 100, 123, "INVALID_JSON")
         for i in range(5000)),
    )
    catalog.db.commit()
    queries = []
    catalog.db.set_trace_callback(queries.append)
    try:
        result = Bridge(catalog, forbidden).dispatch({"command": "catalog", "mediaType": "Photo"})
    finally:
        catalog.db.set_trace_callback(None)
    assert result["total"] == result["nextOffset"] == 5002
    assert result["hasMore"] is False
    assert len(queries) == 2 and all(sql.startswith("SELECT") for sql in queries)
    assert result["items"][0]["key"] == "bulk-00000"
    assert result["items"][-1]["captured"] == ""  # unknown capture date stays last


def test_complete_catalog_does_not_write_authoritative_state(catalog):
    before = catalog.state_path.read_bytes()
    result = catalog.catalog()
    assert result["total"] == 3
    assert catalog.state_path.read_bytes() == before


@pytest.mark.parametrize("filters", [
    {}, {"ascending": True}, {"source_id": "two"}, {"media_type": "Video"},
    {"query": "%"}, {"query": "_"}, {"query": "' OR 1=1 --"},
    {"query": "photo", "source_id": "one", "media_type": "Photo"},
    {"query": "absent"},
])
def test_catalog_stream_matches_compact_catalog_exactly(catalog, filters):
    expected = catalog.catalog(**filters)
    frames = list(catalog.catalog_stream(**filters))
    assert frames[0] == {"ok": True, "catalogStart": {"version": 1, "total": expected["total"]}}
    assert frames[-1] == {"ok": True, "catalogEnd": {"count": expected["total"]}}
    assert [frame["item"] for frame in frames[1:-1]] == expected["items"]
    assert all(frame["ok"] is True for frame in frames)
    assert not catalog.db.in_transaction


def test_catalog_stream_handles_aliases_crossing_cursor_batches(catalog):
    catalog.db.executemany("INSERT INTO Occurrence VALUES(?,?,?,?)", (
        ("a" * 64, "one", "Photos", f"D:\\Pictures\\alias-{i:04d}.jpg") for i in range(300)
    ))
    catalog.db.commit()
    expected = catalog.catalog(source_id="two")["items"]
    frames = list(catalog.catalog_stream(source_id="two"))
    assert [frame["item"] for frame in frames[1:-1]] == expected
    assert frames[1]["item"]["occurrences"] == 302


def test_catalog_stream_count_and_items_share_a_read_snapshot(catalog):
    catalog.db.execute("PRAGMA journal_mode=WAL")
    before = catalog.catalog()
    frames = catalog.catalog_stream()
    assert next(frames)["catalogStart"]["total"] == before["total"]
    with sqlite3.connect(catalog.cache_path) as writer:
        writer.execute("UPDATE Media SET FileName='changed.jpg',Captured='2030-01-01' WHERE Key=?", ("a" * 64,))
        writer.execute("DELETE FROM Occurrence WHERE SourceId='two'")
        writer.execute("DELETE FROM Media WHERE Key=?", ("b" * 64,))
    rest = list(frames)
    assert [frame["item"] for frame in rest[:-1]] == before["items"]
    assert rest[-1]["catalogEnd"]["count"] == before["total"]
    assert catalog.catalog()["total"] == before["total"] - 1


def test_catalog_stream_releases_snapshot_when_reader_stops(catalog):
    stream = catalog.catalog_stream()
    next(stream)
    assert catalog.db.in_transaction
    stream.close()
    assert not catalog.db.in_transaction
    assert catalog.page()["total"] == 3


@pytest.mark.parametrize("order,index", [("DESC", "IX_Media_OrderDesc"), ("ASC", "IX_Media_OrderAsc")])
def test_catalog_stream_uses_timeline_and_alias_indexes(catalog, order, index):
    plan = catalog.db.execute(
        "EXPLAIN QUERY PLAN SELECT m.Key,o.Path FROM Media m LEFT JOIN Occurrence o ON o.Key=m.Key "
        f"ORDER BY (m.Captured=''),m.Captured {order},m.Key,o.Path"
    ).fetchall()
    details = [row[3] for row in plan]
    assert any(index in detail for detail in details)
    assert any("IX_Occurrence_Key" in detail and "SEARCH" in detail for detail in details)
    # SQLite may sort aliases within one media key; it must not sort the whole
    # timeline into a temporary table before the first row can be streamed.
    assert "USE TEMP B-TREE FOR ORDER BY" not in details


def test_catalog_stream_preserves_callers_outer_transaction(catalog):
    catalog.db.execute("BEGIN")
    catalog.db.execute("UPDATE Media SET FileName='temporary.jpg' WHERE Key=?", ("a" * 64,))
    frames = list(catalog.catalog_stream())
    assert frames[1]["item"]["name"] == "temporary.jpg"
    assert catalog.db.in_transaction
    catalog.db.rollback()
    assert catalog.get("a" * 64)["name"] == "photo.jpg"


def test_catalog_stream_materialization_is_bounded_without_n_plus_one(catalog, monkeypatch):
    def forbidden(*args, **kwargs):
        pytest.fail("Streaming used the full catalog, detail lookup, or cloud runtime")
    catalog.db.executemany(
        "INSERT INTO Media(Key,Hash,FileName,Path,Captured,DateSource,MediaType,ByteSize,ModifiedNs,MetadataJson) "
        "VALUES(?,?,?,?,?,?,?,?,?,?)",
        ((f"stream-{i:05d}", "", f"{i}.jpg", f"D:\\Pictures\\{i}.jpg", "2025-01-01", "Exif", "Photo", 100, 123, "INVALID_JSON")
         for i in range(5000)),
    )
    catalog.db.commit()
    for method in ("catalog", "page", "item"):
        monkeypatch.setattr(catalog, method, forbidden)
    connection = catalog.db
    fetch_sizes, queries = [], []

    class Cursor:
        def __init__(self, cursor):
            self.cursor = cursor

        def fetchone(self):
            return self.cursor.fetchone()

        def fetchmany(self, size):
            result = self.cursor.fetchmany(size)
            fetch_sizes.append((size, len(result)))
            return result

        def close(self):
            self.cursor.close()

    class Connection:
        def execute(self, sql, *parameters):
            return Cursor(connection.execute(sql, *parameters))

    monkeypatch.setattr(catalog, "db", Connection())
    connection.set_trace_callback(queries.append)
    try:
        frames = Bridge(catalog, forbidden).responses({"command": "catalog-stream", "mediaType": "Photo"})
        assert next(frames)["catalogStart"]["total"] == 5002
        assert len([sql for sql in queries if sql.startswith("SELECT")]) == 1
        count, first, last = 0, None, None
        for frame in frames:
            if "item" in frame:
                count += 1
                first = first or frame["item"]
                last = frame["item"]
            else:
                assert frame == {"ok": True, "catalogEnd": {"count": 5002}}
    finally:
        connection.set_trace_callback(None)
    assert count == 5002 and first["key"] == "stream-00000" and last["captured"] == ""
    assert len([sql for sql in queries if sql.startswith("SELECT")]) == 2
    assert len(fetch_sizes) > 10
    assert all(requested == 256 and returned <= 256 for requested, returned in fetch_sizes)


def test_stream_protocol_keeps_complete_responses_sequential_and_local(catalog):
    before = catalog.state_path.read_bytes()
    requests = [
        {"command": "catalog-stream", "sourceId": "two", "mediaType": "Photo", "ascending": True},
        {"command": "catalog-stream", "query": "absent"},
        {"command": "hello"},
    ]
    def forbidden():
        pytest.fail("Local stream contacted the cloud")
    output = io.StringIO()
    assert serve(Bridge(catalog, forbidden), io.StringIO("\n".join(map(json.dumps, requests))), output) == 0
    frames = [json.loads(line) for line in output.getvalue().splitlines()]
    assert frames[:3] == list(catalog.catalog_stream(source_id="two", media_type="Photo", ascending=True))
    assert frames[3:5] == list(catalog.catalog_stream(query="absent"))
    assert frames[5] == {"ok": True, "result": catalog.overview()}
    assert catalog.state_path.read_bytes() == before


@pytest.mark.parametrize("invalid_request", ["not json", "[]", "null", '{"command":"unsupported"}', '"' + "x" * 8193 + '"'])
def test_stream_protocol_invalid_requests_do_not_break_next_request(catalog, invalid_request):
    output = io.StringIO()
    serve(Bridge(catalog), io.StringIO(invalid_request + '\n{"command":"hello"}\n'), output)
    frames = [json.loads(line) for line in output.getvalue().splitlines()]
    assert frames[0] == {"ok": False, "error": "The library request was invalid. Refresh and try again."}
    assert frames[1] == {"ok": True, "result": catalog.overview()}


def test_stream_protocol_sanitizes_failure_mid_stream(catalog, monkeypatch):
    def broken(**kwargs):
        yield {"ok": True, "catalogStart": {"version": 1, "total": 2}}
        yield {"ok": True, "item": {"key": "partial"}}
        raise RuntimeError("sensitive internal path or credential")
    monkeypatch.setattr(catalog, "catalog_stream", broken)
    output = io.StringIO()
    serve(Bridge(catalog), io.StringIO('{"command":"catalog-stream"}\n{"command":"hello"}\n'), output)
    frames = [json.loads(line) for line in output.getvalue().splitlines()]
    assert len(frames) == 4 and frames[1]["item"]["key"] == "partial"
    assert frames[2] == {"ok": False, "error": "Cannot read the library right now. Check the CLI connection and retry."}
    assert "sensitive" not in output.getvalue()
    assert frames[3] == {"ok": True, "result": catalog.overview()}


def test_stream_stdio_writes_bounded_chunks_and_flushes_header_immediately(catalog):
    class Output(io.StringIO):
        sizes = None

        def __init__(self):
            super().__init__()
            self.sizes = []

        def write(self, value):
            self.sizes.append(len(value))
            return super().write(value)

    catalog.db.executemany(
        "INSERT INTO Media(Key,Hash,FileName,Path,Captured,DateSource,MediaType,MetadataJson) VALUES(?,?,?,?,?,?,?,?)",
        ((f"chunk-{i:05d}", "", f"{i}.jpg", f"D:\\Pictures\\{i}.jpg", "2025-01-01", "Exif", "Photo", "{}")
         for i in range(5000)),
    )
    catalog.db.commit()
    output = Output()
    serve(Bridge(catalog), io.StringIO('{"command":"catalog-stream"}\n'), output)
    frames = [json.loads(line) for line in output.getvalue().splitlines()]
    assert output.sizes[0] == len(json.dumps(frames[0])) + 1
    assert 2 < len(output.sizes) < 100
    assert max(output.sizes) < 70000
    assert frames[-1]["catalogEnd"]["count"] == 5003
