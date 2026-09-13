"""A product rename must not disconnect an existing library or client."""
from __future__ import annotations

import hashlib
import importlib
import json
import os
from types import SimpleNamespace
from uuid import UUID

import httpx
import pytest
from fastapi import Depends, FastAPI
from fastapi.testclient import TestClient

from cli.nektron_moments_cli.api_client import ApiClient
from cli.nektron_moments_cli.auth import (
    KeyringTokenBackend, LEGACY_TOKEN_SERVICE, TOKEN_ACCOUNT, TOKEN_SERVICE, TokenSet,
)
from cli.nektron_moments_cli.config import CliConfig, ConfigStore, default_config_dir
from cli.nektron_moments_cli.legacy import load_legacy_db_config
from services.api.errors import service_error_handler
from services.api.routes import request_device_id
from services.api.service import ServiceError
from services.common.branding import environment_value
from services.common.settings import AppSettings


def test_old_environment_and_new_precedence(monkeypatch):
    monkeypatch.delenv("NEKTRON_MOMENTS_API_URL", raising=False)
    monkeypatch.setenv("IMAGETRACKER_API_URL", "https://old.example")
    assert AppSettings().api_url == "https://old.example"
    assert CliConfig().with_environment().api_url == "https://old.example"
    monkeypatch.setenv("NEKTRON_MOMENTS_API_URL", "https://new.example")
    assert AppSettings().api_url == "https://new.example"
    assert CliConfig().with_environment().api_url == "https://new.example"
    assert AppSettings(api_url="explicit").api_url == "explicit"
    assert environment_value("NEKTRON_MOMENTS_API_URL", environment={}) is None


def test_legacy_database_environment_is_accepted():
    config = load_legacy_db_config(environ={
        "MYSQL_DATABASE_IMAGETRACKER": "ImageTracker",
        "MYSQL_USER": "test-user",
    })
    assert config.database == "ImageTracker"
    assert AppSettings().mysql_database == "ImageTracker"


def test_existing_settings_and_account_state_are_reused(monkeypatch, tmp_path):
    for key in ("NEKTRON_MOMENTS_CONFIG_DIR", "IMAGETRACKER_CONFIG_DIR"):
        monkeypatch.delenv(key, raising=False)
    monkeypatch.setenv("APPDATA", str(tmp_path))
    monkeypatch.setenv("XDG_CONFIG_HOME", str(tmp_path))
    legacy = tmp_path / ("ImageTracker" if os.name == "nt" else "imagetracker")
    legacy.mkdir()
    assert default_config_dir() == legacy
    digest = hashlib.sha256(b"imagetracker-state-v1:existing-subject").hexdigest()
    assert ConfigStore().state_path_for_subject("existing-subject") == (
        legacy / "accounts" / digest / "state.sqlite3"
    )
    monkeypatch.setenv("IMAGETRACKER_CONFIG_DIR", str(tmp_path / "old-override"))
    assert default_config_dir() == tmp_path / "old-override"
    monkeypatch.setenv("NEKTRON_MOMENTS_CONFIG_DIR", str(tmp_path / "new-override"))
    assert default_config_dir() == tmp_path / "new-override"


@pytest.mark.parametrize("module", ["app", "config", "state", "sync", "auth", "media"])
def test_legacy_modules_share_the_implementation(module):
    assert importlib.import_module(f"cli.imagetracker_cli.{module}") is (
        importlib.import_module(f"cli.nektron_moments_cli.{module}")
    )


def test_legacy_importer_shares_implementation():
    assert importlib.import_module("ImageTracker") is importlib.import_module("NektronMoments")


def test_keyring_reuses_old_session_and_logout_clears_both_names():
    payload = dict(
        access_token="test", id_token="test", refresh_token="test",
        expires_at_utc="2030-01-01T00:00:00+00:00",
    )
    values = {(LEGACY_TOKEN_SERVICE, TOKEN_ACCOUNT): json.dumps(payload)}
    backend = KeyringTokenBackend(SimpleNamespace(
        get_password=lambda service, account: values.get((service, account)),
        set_password=lambda service, account, value: values.update({(service, account): value}),
        delete_password=lambda service, account: values.pop((service, account), None),
    ))
    assert backend.load() == TokenSet(**payload)
    backend.save(TokenSet(**payload))
    assert (TOKEN_SERVICE, TOKEN_ACCOUNT) in values
    backend.delete()
    assert backend.load() is None
    assert not values


@pytest.mark.parametrize("header", [
    "X-Nektron-Moments-Device-Id", "X-ImageTracker-Device-Id",
    "x-nektron-moments-device-id",
])
def test_cli_sends_both_device_names_to_old_or_new_servers(header):
    device = str(UUID(int=1))
    tokens = SimpleNamespace(access_token="test-token", refresh_token="")
    def respond(request):
        assert request.headers["X-Nektron-Moments-Device-Id"] == device
        assert request.headers["X-ImageTracker-Device-Id"] == device
        return httpx.Response(200, json={})
    with httpx.Client(transport=httpx.MockTransport(respond)) as http:
        api = ApiClient("https://test.invalid", SimpleNamespace(
            current_tokens=lambda: tokens,
        ), http_client=http)
        assert api.request("GET", "/v1/media", headers={header: device}) == {}


def test_api_accepts_either_header_and_rejects_conflicts_or_missing_values():
    app = FastAPI()
    app.add_exception_handler(ServiceError, service_error_handler)
    @app.get("/")
    def endpoint(device: UUID = Depends(request_device_id)):
        return {"device": str(device)}
    current, legacy = "X-Nektron-Moments-Device-Id", "X-ImageTracker-Device-Id"
    first, second = str(UUID(int=1)), str(UUID(int=2))
    with TestClient(app) as client:
        for headers in ({current: first}, {legacy: first}, {current: first, legacy: first}):
            response = client.get("/", headers=headers)
            assert response.status_code == 200
            assert response.json() == {"device": first}
        response = client.get("/", headers={current: first, legacy: second})
        assert response.status_code == 400
        assert response.json()["code"] == "DEVICE_HEADER_MISMATCH"
        assert client.get("/").json()["code"] == "DEVICE_REQUIRED"
        for header in (current, legacy):
            assert client.get("/", headers={header: "not-a-uuid"}).status_code == 422
