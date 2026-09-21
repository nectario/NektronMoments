from dataclasses import replace
from decimal import Decimal
from types import SimpleNamespace

import pytest
from pydantic import ValidationError

from services.api.models import EnrichmentPrepareRequest
from services.enrichment.model_options import SCENE_MODEL_RATES
from services.enrichment.openai_scene import scene_description_maximum_cost_usd
from services.domain.service import Phase1DomainService
from services.worker.processor import DescriptionMessageProcessor
from test_scene_worker import FakeDescriptionRepository, FakeSceneProvider, FakePreviewStore, description_result, body
from cli.nektron_moments_cli.desktop_job import permitted
from cli.nektron_moments_cli.sync import SyncEngine
from cli.nektron_moments_cli.state import LocalState
from test_scene_cli_staging import UploadApi, EmptyScanner, _binding
from test_scene_staging_domain import session_factory, setup_photo, run, context, NOW
from services.domain.models import EnrichmentPrepareCommand
from services.data.models import ProcessingJob
from sqlalchemy import select

SOURCE = "10000000-0000-4000-8000-000000000001"


@pytest.mark.parametrize("limit", [65, 10000, 1000000])
def test_larger_run_is_allowed_but_server_batch_stays_bounded(limit):
    assert permitted(["sync", SOURCE, "--with-enrichment", "--enrichment-limit", str(limit), "--no-input"])
    SyncEngine._validate_enrichment_limit(limit)
    with pytest.raises(ValidationError):
        EnrichmentPrepareRequest(limit=limit)


@pytest.mark.parametrize("stop_at_quota", [False, True])
def test_catchup_pages_without_exceeding_run_allowance(tmp_path, stop_at_quota):
    class PagedApi(UploadApi):
        def prepare_enrichment(self, source_id, payload, **kwargs):
            result = super().prepare_enrichment(source_id, payload, **kwargs)
            result["nextCursor"] = f"page-{len(self.prepare_calls)}"
            result["assetsConsidered"] = payload["limit"]
            return result
    api = PagedApi()
    state = LocalState(tmp_path / "state.sqlite3")
    engine = SyncEngine(api, state, EmptyScanner(), device_id="device-test")
    flushed = []
    def flush(binding, summary, *, limit):
        flushed.append(limit)
        return not stop_at_quota
    engine._flush_description_outbox = flush
    engine.enrich(_binding(tmp_path), limit=150)
    assert flushed == ([64] if stop_at_quota else [64, 64, 22])
    assert [call[1]["limit"] for call in api.prepare_calls] == flushed
    if not stop_at_quota:
        assert api.prepare_calls[1][1]["cursor"] == "page-1"
        assert len({call[2] for call in api.prepare_calls}) == 3


def test_old_server_ends_catchup_safely(tmp_path):
    api = UploadApi()
    engine = SyncEngine(api, LocalState(tmp_path / "state.sqlite3"), EmptyScanner(), device_id="device-test")
    engine.enrich(_binding(tmp_path), limit=10000)
    assert len(api.prepare_calls) == 1  # No cursor, no unbounded request/repeat.

@pytest.mark.parametrize("model", SCENE_MODEL_RATES)
def test_model_contract_and_desktop_arguments(model):
    payload = EnrichmentPrepareRequest(descriptionModel=model, limit=17)
    assert payload.description_model == model
    assert permitted(["sync", SOURCE, "--with-enrichment", "--enrichment-limit", "17", "--description-model", model, "--no-input"])

@pytest.mark.parametrize("limit", ["0", "1000001", "1.5", "NaN"])
def test_invalid_desktop_limit(limit):
    assert not permitted(["sync", SOURCE, "--with-enrichment", "--enrichment-limit", limit, "--no-input"])

def test_invalid_model_rejected():
    with pytest.raises(ValidationError):
        EnrichmentPrepareRequest(descriptionModel="arbitrary-model")
    assert not permitted(["sync", SOURCE, "--with-enrichment", "--enrichment-limit", "1", "--description-model", "arbitrary-model", "--no-input"])

@pytest.mark.parametrize("model", SCENE_MODEL_RATES)
def test_domain_stamps_trusted_model_rates(model):
    service = Phase1DomainService(session_factory=lambda: None)
    request = service._description_request(asset=SimpleNamespace(content_sha256="a" * 64), source=SimpleNamespace(public_id=SOURCE), model=model)
    rates = SCENE_MODEL_RATES[model]
    assert request["model"] == model
    assert Decimal(request["inputUsdPerMillion"]) == rates[0]
    assert Decimal(request["outputUsdPerMillion"]) == rates[2]
    assert Decimal(request["reservedUsdPerRequest"]) >= scene_description_maximum_cost_usd(input_usd_per_million=rates[0], cached_input_usd_per_million=rates[1], output_usd_per_million=rates[2])

@pytest.mark.parametrize("model", ["gpt-5.6-luna", "gpt-5.6-sol"])
def test_worker_uses_selected_model_without_changing_budget(model):
    events = []
    repo = FakeDescriptionRepository(events)
    rates = SCENE_MODEL_RATES[model]
    repo.job = replace(repo.job, model=model, input_usd_per_million=rates[0], cached_input_usd_per_million=rates[1], output_usd_per_million=rates[2], reserved_usd_per_request=rates[3])
    default = FakeSceneProvider(description_result(), events)
    selected = FakeSceneProvider(replace(description_result(), model=model), events, model=model)
    processor = DescriptionMessageProcessor(repository=repo, provider=default, model_providers={model: selected}, preview_store=FakePreviewStore(events), monthly_call_limit=1000)
    processor.process_message(message_id="test", body=body())
    assert not default.urls and len(selected.urls) == 1
    assert repo.completed_result.model == model
    assert "reserve" in events and "consume" in events

def test_cli_forwards_model_and_limit(tmp_path):
    state = LocalState(tmp_path / "state.sqlite3")
    api = UploadApi()
    engine = SyncEngine(api, state, EmptyScanner(), device_id="device-test", description_model="gpt-5.6-luna")
    engine.sync(_binding(tmp_path), with_enrichment=True, enrichment_limit=7)
    assert api.prepare_calls[0][1] == {"types": ["Geocode", "Description"], "limit": 7, "descriptionModel": "gpt-5.6-luna"}

def test_preparation_saves_selected_model_and_keeps_queued_jobs(session_factory):
    service = Phase1DomainService(session_factory, clock=lambda: NOW)
    user, source, _ = setup_photo(service)
    result = run(service.prepare_enrichment(user.user_id, source.device_id, source.source_id,
        EnrichmentPrepareCommand(types=("Description",), limit=1, description_model="gpt-5.6-sol"), context("choose-sol")))
    assert len(result.value.scene_description_tasks) == 1
    with session_factory() as session:
        job = session.scalar(select(ProcessingJob).where(ProcessingJob.job_type == "Description"))
        assert job.request_json["model"] == "gpt-5.6-sol"
        assert Decimal(job.request_json["reservedUsdPerRequest"]) == Decimal(".02")
        job.status = "Queued"
        session.commit()
    result = run(service.prepare_enrichment(user.user_id, source.device_id, source.source_id,
        EnrichmentPrepareCommand(types=("Description",), limit=1, description_model="gpt-5.6-luna"), context("choose-luna")))
    assert not result.value.scene_description_tasks
    with session_factory() as session:
        job = session.scalar(select(ProcessingJob).where(ProcessingJob.job_type == "Description"))
        assert job.request_json["model"] == "gpt-5.6-sol"
