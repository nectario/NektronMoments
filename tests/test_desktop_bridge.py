from __future__ import annotations
import json
import sqlite3
from pathlib import Path
from types import SimpleNamespace

import pytest
from cli.nektron_moments_cli.desktop_bridge import Bridge, DesktopCatalog, windows_path


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
    for request in ({"command": "hello"}, {"command": "page"}, {"command": "activity"}, {"command": "refresh"}):
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
