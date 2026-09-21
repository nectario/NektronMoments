import json
from types import SimpleNamespace

import pytest
from cli.nektron_moments_cli.desktop_bridge import Bridge, DesktopCatalog, describes_screenshot
from test_desktop_bridge import catalog


@pytest.mark.parametrize("text,expected", [
    ("A screenshot of a conversation.", True), ("Screen capture showing a chart", True),
    ("This screen-shot contains an email.", True), ("This is not a screenshot.", False),
    ("A person in front of a screen.", False), ("A screenplay cover", False),
    ("", False), (None, False), ("This isn't a screen capture", False),
    ("A cropped portion of text from a website.", True),
    ("An excerpt from an online article.", True),
    ("A webpage with paragraphs of text and a headline.", True),
    ("A block of text on a white background.", True),
    ("Black text on a plain white background.", True),
    ("A text-only image containing a quotation.", True),
    ("A digital document containing instructions.", True),
    ("A person holding a book with a block of text.", False),
    ("A street sign advertising a website.", False),
    ("A laptop displaying a webpage on a desk.", False),
    ("A photo of a newspaper article.", False),
    ("This is not a webpage.", False),
])
def test_description_classification(text, expected):
    assert describes_screenshot(text) == expected


def test_filter_preserves_unknown_photos_and_videos_across_all_read_paths(catalog):
    catalog.db.execute("UPDATE Media SET Description='A screenshot of a chat' WHERE Hash=?", ("a" * 64,))
    catalog.db.execute("UPDATE Media SET Description='Screen capture video' WHERE Hash=?", ("b" * 64,))
    catalog.db.commit()
    assert catalog.page(hide_screenshots=True)["total"] == 2
    assert catalog.catalog(hide_screenshots=True)["total"] == 2
    frames = list(catalog.catalog_stream(hide_screenshots=True))
    assert frames[0]["catalogStart"]["total"] == 2
    assert all(frame.get("item", {}).get("hash") != "a" * 64 for frame in frames)
    assert catalog.page()["total"] == 3
    assert catalog.page(source_id="two", hide_screenshots=True)["total"] == 0
    assert catalog.page(media_type="Video", hide_screenshots=True)["total"] == 1
    catalog.refresh()
    assert catalog.page(hide_screenshots=True)["total"] == 2


def test_screenshot_index_updates_atomically_and_survives_refresh_and_restart(catalog):
    bridge = Bridge(catalog)
    assert catalog.page(hide_screenshots=True)["total"] == 3
    bridge.dispatch({"command": "screenshot-index-replace", "hashes": ["a" * 64]})
    assert catalog.page(hide_screenshots=True)["total"] == 2
    catalog.refresh()
    catalog.db.close()
    catalog.state_path.touch()  # A startup scan changes the authoritative fingerprint.
    restored = DesktopCatalog(catalog.state_path)
    assert restored.page(hide_screenshots=True)["total"] == 2
    replacement = Bridge(restored)
    with pytest.raises(ValueError):
        replacement.dispatch({"command": "screenshot-index-replace", "hashes": ["bad"]})
    assert restored.page(hide_screenshots=True)["total"] == 2
    replacement.dispatch({"command": "screenshot-index-replace", "hashes": []})
    assert restored.page(hide_screenshots=True)["total"] == 3


def test_index_only_search_is_paginated_read_only_and_uses_description_not_filename():
    calls = []
    def request(method, path, **kwargs):
        calls.append((method, path, kwargs))
        return {"items": [
            {"asset": {"contentSha256": "a" * 64, "descriptionExcerpt": "A screenshot of a message"}},
            {"asset": {"contentSha256": "b" * 64, "descriptionExcerpt": "Not a screenshot"}},
            {"asset": {"contentSha256": "c" * 64, "fileName": "screenshot.jpg"}},
        ], "page": {"nextCursor": "next-page"}}
    bridge = Bridge(None, lambda: SimpleNamespace(api=SimpleNamespace(request=request), state=SimpleNamespace(get_setting=lambda _: "device")))
    result = bridge.dispatch({"command": "screenshot-page", "queryIndex": 0, "cursor": "previous"})
    assert result["hashes"] == ["a" * 64]
    assert result["nextCursor"] == "next-page"
    assert calls[0][0:2] == ("GET", "/v1/media/search")
    assert calls[0][2]["params"]["cursor"] == "previous"
    with pytest.raises(ValueError):
        bridge.dispatch({"command": "refresh"})


def test_filtered_bridge_search_does_not_reintroduce_screenshots(catalog):
    asset = {"contentSha256": "a" * 64, "mediaAssetId": "asset", "descriptionExcerpt": "A screenshot of a chat"}
    runtime = SimpleNamespace(api=SimpleNamespace(search_media=lambda *a, **k: [{"asset": asset}]), state=SimpleNamespace(get_setting=lambda _: "device"))
    bridge = Bridge(catalog, lambda: runtime)
    result = bridge.dispatch({"command": "search", "query": "photo", "hideScreenshots": True})
    assert all(item["hash"] != "a" * 64 for item in result["items"])


def test_text_capture_hidden_in_local_and_remote_read_paths(catalog):
    description = "A cropped portion of text from a website."
    catalog.db.execute("UPDATE Media SET Description=? WHERE Hash=?", (description, "a" * 64))
    catalog.db.commit()
    assert catalog.catalog(hide_screenshots=True)["total"] == 2
    assert catalog.page(hide_screenshots=True)["total"] == 2
    assert list(catalog.catalog_stream(hide_screenshots=True))[0]["catalogStart"]["total"] == 2
    asset = {"contentSha256": "a" * 64, "descriptionExcerpt": description}
    bridge = Bridge(None, lambda: SimpleNamespace(api=SimpleNamespace(request=lambda *a, **k: {"items": [{"asset": asset}]}), state=SimpleNamespace(get_setting=lambda _: "device")))
    from cli.nektron_moments_cli.desktop_bridge import SCREENSHOT_QUERIES
    result = bridge.dispatch({"command": "screenshot-page", "queryIndex": SCREENSHOT_QUERIES.index("text")})
    assert result["hashes"] == ["a" * 64]
