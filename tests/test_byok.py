import json
from decimal import Decimal
from types import SimpleNamespace
from uuid import uuid4
import pytest
from sqlalchemy import select, func
from cli.nektron_moments_cli.byok import ByokRunner, ByokJournal
from cli.nektron_moments_cli.state import LocalState
from services.enrichment.openai_scene import SceneDescriptionResult, OpenAISceneDescriptionProvider, JsonHttpResponse
from services.domain.byok import save_byok_result
from services.domain.models import EnrichmentPrepareCommand
from services.domain.errors import ConflictError
from services.data.models import MediaDescription, ProviderUsageMonth, ProcessingJob
from test_scene_staging_domain import session_factory, setup_photo, run, context, NOW
from services.domain.service import Phase1DomainService


class Api:
    def __init__(self): self.claimed=set(); self.results={}; self.fail_sync=False
    def prepare_enrichment(self, source, payload, **kw):
        return {'sourceId':source, 'sceneDescriptionTasks': [dict(jobId='job',localLocator='local.jpg',assetContentSha256='a'*64)] if not self.claimed else []}
    def request(self, method, path, *, json, headers):
        assert set(json) <= {'claimId','description'}
        assert 'OPENAI_API_KEY' not in str(headers)
        if 'description' in json:
            if self.fail_sync: raise RuntimeError('backend offline')
            self.results[path]=json['description']; return {'status':'Succeeded'}
        self.claimed.add(path); return {'status':'Claimed'}


class Provider:
    def __init__(self): self.calls=0
    def describe_bytes(self, value):
        self.calls+=1
        return SceneDescriptionResult('A quiet garden.', 'OpenAI', 'gpt-5.6-terra', 'scene-search-v1', {'input_tokens':100,'output_tokens':10})


def runner(tmp_path, api, provider, **kw):
    return ByokRunner(api, LocalState(tmp_path/'state.sqlite3'), 'device', provider=provider,
        preview_factory=lambda _: SimpleNamespace(content=b'jpeg',source_sha256_hex='a'*64), progress=lambda _:None, **kw)


def test_direct_result_survives_backend_failure_without_repeat_charge(tmp_path):
    api=Api(); api.fail_sync=True; provider=Provider(); r=runner(tmp_path,api,provider)
    with pytest.raises(RuntimeError): r.run(SimpleNamespace(source_id='source'), 1)
    assert provider.calls==1 and r.journal.counts('source')=={'ResultReady':1}
    api.fail_sync=False
    r=runner(tmp_path,api,provider)
    assert r.run(SimpleNamespace(source_id='source'),1)=={'Synced':1}
    assert provider.calls==1 and len(api.results)==1


def test_local_budget_prevents_provider_call(tmp_path):
    p=Provider(); r=runner(tmp_path,Api(),p,monthly_cap=Decimal('.001'))
    assert r.run(SimpleNamespace(source_id='source'),1)=={'Ready':1}
    assert p.calls==0


def test_interrupted_call_is_not_automatically_repeated(tmp_path):
    p=Provider(); r=runner(tmp_path,Api(),p)
    r.journal.add('source',dict(jobId='job',localLocator='local.jpg',assetContentSha256='a'*64),'gpt-5.6-terra')
    assert r.journal.reserve('job',Decimal('.01'),Decimal('230'))
    assert r.run(SimpleNamespace(source_id='source'),1)=={'Uncertain':1}
    assert p.calls==0


def test_provider_accepts_inline_jpeg_without_weakening_url_validation():
    class Transport:
        def post_json(self,url,**kw):
            self.payload=kw['payload']
            return JsonHttpResponse(200,{'output':[{'type':'message','content':[{'type':'output_text','text':'A quiet garden.'}]}]})
    t=Transport(); p=OpenAISceneDescriptionProvider('test-only',transport=t,service_tier='default')
    assert p.describe_bytes(b'\xff\xd8test').description=='A quiet garden.'
    assert t.payload['input'][0]['content'][1]['image_url'].startswith('data:image/jpeg;base64,')
    assert t.payload['service_tier']=='default'
    with pytest.raises(ValueError):p.describe('data:image/jpeg;base64,AA==')


def test_provider_exposes_only_safe_failure_code_and_retry_delay():
    from services.enrichment.openai_scene import SceneDescriptionProviderError
    class Transport:
        def post_json(self,*args,**kw):
            return JsonHttpResponse(429,None,error_code='rate_limit_exceeded',retry_after_seconds=60)
    p=OpenAISceneDescriptionProvider('test-only',transport=Transport())
    with pytest.raises(SceneDescriptionProviderError) as caught:p.describe_bytes(b'\xff\xd8test')
    assert caught.value.provider_error_code=='rate_limit_exceeded'
    assert caught.value.retry_after_seconds==60


def test_exhausted_credits_pause_without_retrying_or_consuming_local_budget(tmp_path):
    from services.enrichment.openai_scene import SceneDescriptionProviderError
    from services.enrichment.models import ProviderFailureClass
    class Transport:
        def post_json(self,*args,**kw):
            return JsonHttpResponse(429,None,error_code='credit_balance_exhausted')
    p=OpenAISceneDescriptionProvider('test-only',transport=Transport())
    with pytest.raises(SceneDescriptionProviderError) as caught:p.describe_bytes(b'\xff\xd8test')
    assert caught.value.failure.failure_class==ProviderFailureClass.QUOTA
    assert not caught.value.failure.retryable
    class RejectedProvider:
        def describe_bytes(self,value): raise caught.value
    r=runner(tmp_path,Api(),RejectedProvider())
    assert r.run(SimpleNamespace(source_id='source'),1)=={'Ready':1}
    assert r.pause_reason.startswith('OpenAICreditsExhausted')
    assert Decimal(r.journal.rows('source')[0]['CostUsd'])==0


def test_owned_claim_and_result_are_idempotent_and_do_not_charge_managed_budget(session_factory):
    service=Phase1DomainService(session_factory,clock=lambda:NOW)
    user,source,_=setup_photo(service)
    command=EnrichmentPrepareCommand(types=('Description',),limit=1)
    page=run(service.prepare_enrichment(user.user_id,source.device_id,source.source_id,command,context('byok-page'))).value
    job=page.scene_description_tasks[0].job_id; claim=uuid4()
    call=lambda c,d:run(save_byok_result(service,user.user_id,source.device_id,job,c,d))
    assert call(claim,None)=='Claimed'
    with pytest.raises(ConflictError):call(uuid4(),None)
    assert not run(service.prepare_enrichment(user.user_id,source.device_id,source.source_id,command,context('after-claim'))).value.scene_description_tasks
    assert call(claim,'A quiet garden.')=='Succeeded'
    assert call(claim,'A quiet garden.')=='Succeeded'
    with session_factory() as db:
        assert db.scalar(select(func.count()).select_from(MediaDescription))==1
        assert db.scalar(select(func.count()).select_from(ProviderUsageMonth))==0
        assert db.scalar(select(ProcessingJob)).provider=='OpenAI-BYOK'


def test_claim_cannot_steal_cloud_work_or_accept_unclaimed_result(session_factory):
    service=Phase1DomainService(session_factory,clock=lambda:NOW)
    user,source,manifest=setup_photo(service)
    job=manifest.results[0].description_job_id
    with pytest.raises(ConflictError):
        run(save_byok_result(service,user.user_id,source.device_id,job,uuid4(),'Unclaimed result'))
    with session_factory() as db:
        db.scalar(select(ProcessingJob)).status='Queued'; db.commit()
    with pytest.raises(ConflictError):
        run(save_byok_result(service,user.user_id,source.device_id,job,uuid4(),None))


def test_byok_cannot_be_uploaded_to_s3(session_factory):
    from test_scene_staging_domain import MemoryTemporaryStore, upload_command
    store=MemoryTemporaryStore()
    service=Phase1DomainService(session_factory,clock=lambda:NOW,temporary_object_store=store)
    user,source,manifest=setup_photo(service)
    run(save_byok_result(service,user.user_id,source.device_id,manifest.results[0].description_job_id,uuid4(),None))
    with pytest.raises(ConflictError):
        run(service.create_upload_plan(user.user_id,upload_command(source,manifest.results[0]),context('byok-upload-forbidden')))
    assert store.plans==[]


def test_parallelism_is_bounded(tmp_path):
    import threading,time
    class ManyApi(Api):
        def prepare_enrichment(self,source,payload,**kw):
            return {'sourceId':source,'sceneDescriptionTasks':[dict(jobId=f'job-{i}',localLocator='local.jpg',assetContentSha256='a'*64) for i in range(8)] if not self.claimed else []}
    class ConcurrentProvider(Provider):
        def __init__(self): super().__init__(); self.active=0; self.maximum=0; self.lock=threading.Lock()
        def describe_bytes(self,value):
            with self.lock:self.active+=1; self.maximum=max(self.maximum,self.active)
            try:time.sleep(.05); return super().describe_bytes(value)
            finally:
                with self.lock:self.active-=1
    p=ConcurrentProvider(); r=runner(tmp_path,ManyApi(),p,workers=2)
    assert r.run(SimpleNamespace(source_id='source'),8)=={'Synced':8}
    assert p.calls==8 and p.maximum==2


def test_hardware_defaults_and_explicit_worker_bounds(tmp_path, monkeypatch):
    from cli.nektron_moments_cli.byok import recommended_ai_workers
    import cli.nektron_moments_cli.byok as module
    monkeypatch.setattr(module.os, 'cpu_count', lambda:192)
    monkeypatch.setattr(module.os, 'sysconf', lambda name: 4096 if name == 'SC_PAGE_SIZE' else 192*1024**3//4096)
    assert recommended_ai_workers()==64
    monkeypatch.setattr(module.os, 'cpu_count', lambda:8)
    assert recommended_ai_workers()==16
    monkeypatch.setattr(module.os, 'sysconf', lambda _: (_ for _ in ()).throw(OSError()))
    assert recommended_ai_workers()==4
    for count in (0,65):
        with pytest.raises(ValueError):runner(tmp_path,Api(),Provider(),workers=count)


def seed_jobs(r, count):
    for i in range(count):
        r.journal.add('source',dict(jobId=f'job-{i}',localLocator=str(i),assetContentSha256='a'*64),'gpt-5.6-terra')


@pytest.mark.parametrize('workers', [1, 8, 64])
def test_bad_ai_output_is_quarantined_while_other_photos_finish(tmp_path, workers):
    class MixedProvider(Provider):
        def describe_bytes(self, value):
            if value == b'0':
                raise OpenAISceneDescriptionProvider._invalid_response('DESCRIPTION_FORMAT_INVALID')
            return super().describe_bytes(value)
    p = MixedProvider(); r = runner(tmp_path, Api(), p, workers=workers)
    r.preview = lambda path: SimpleNamespace(content=path.encode(), source_sha256_hex='a'*64)
    messages = []; r.progress = messages.append
    seed_jobs(r, 20)
    assert r.run(SimpleNamespace(source_id='source'), 20) == {'Synced':19, 'Uncertain':1}
    assert not r.stop.is_set() and p.calls == 19
    bad = r.journal.result_row('job-0')
    assert bad['ErrorCode'] == 'OpenAIInvalidResponse:DESCRIPTION_FORMAT_INVALID'
    assert Decimal(bad['CostUsd']) > 0  # Possibly billed: preserve the reservation.
    assert any('failed bucket; continuing' in line for line in messages)
    assert any('completed with failures' in line for line in messages)
    resumed = runner(tmp_path, Api(), p, workers=workers)
    resumed.api.claimed.add('already-prepared')
    resumed.run(SimpleNamespace(source_id='source'), 20)
    assert p.calls == 19  # Neither successes nor uncertain calls get billed again.


def test_unreadable_photo_does_not_stop_later_photos(tmp_path):
    p = Provider(); r = runner(tmp_path, Api(), p, workers=1)
    def preview(path):
        if path == '0': raise OSError('unreadable')
        return SimpleNamespace(content=b'jpeg', source_sha256_hex='a'*64)
    r.preview = preview; seed_jobs(r, 4)
    assert r.run(SimpleNamespace(source_id='source'), 4) == {'NeedsAttention':1, 'Synced':3}
    assert not r.stop.is_set() and p.calls == 3


def test_credit_pause_leaves_unstarted_photos_ready(tmp_path):
    class EmptyCreditProvider(Provider):
        def describe_bytes(self, value):
            self.calls += 1
            OpenAISceneDescriptionProvider._raise_for_http_status(JsonHttpResponse(429, None, error_code='credit_balance_exhausted'))
    p = EmptyCreditProvider(); r = runner(tmp_path, Api(), p, workers=1)
    seed_jobs(r, 20)
    assert r.run(SimpleNamespace(source_id='source'), 20) == {'Ready':20}
    assert r.stop.is_set() and p.calls == 1


def test_all_64_workers_can_be_in_flight_and_backend_is_serialized(tmp_path):
    import threading, time
    class CheckedApi(Api):
        def __init__(self):super().__init__(); self.active=0; self.maximum=0
        def request(self,*args,**kwargs):
            self.active+=1; self.maximum=max(self.maximum,self.active)
            try:time.sleep(.001); return super().request(*args,**kwargs)
            finally:self.active-=1
    class BarrierProvider(Provider):
        barrier=threading.Barrier(64, timeout=20)
        def describe_bytes(self,value):
            self.barrier.wait()
            return super().describe_bytes(value)
    api=CheckedApi(); p=BarrierProvider(); r=runner(tmp_path,api,p,workers=64)
    seed_jobs(r,64)
    assert r.run(SimpleNamespace(source_id='source'),64)=={'Synced':64}
    assert p.calls==64 and api.maximum==1


def test_rolling_pipeline_does_not_wait_for_slow_first_page(tmp_path):
    import threading
    reached_next_page=threading.Event()
    class StragglerProvider(Provider):
        def describe_bytes(self,value):
            if value == b'0':
                assert reached_next_page.wait(20), '64-photo page barrier stalled the pipeline'
            if value == b'64':reached_next_page.set()
            return super().describe_bytes(value)
    p=StragglerProvider(); r=runner(tmp_path,Api(),p,workers=4)
    r.preview=lambda path:SimpleNamespace(content=path.encode(),source_sha256_hex='a'*64)
    seed_jobs(r,96)
    messages=[]; r.progress=messages.append
    assert r.run(SimpleNamespace(source_id='source'),96)=={'Synced':96}
    assert reached_next_page.is_set() and p.calls==96
    assert any('96/96 descriptions completed' in message and 'items/s' in message for message in messages)


def test_parallel_budget_reservations_never_exceed_cap(tmp_path):
    import threading
    release=threading.Event()
    class HoldingProvider(Provider):
        def describe_bytes(self,value):
            release.wait(1)
            return super().describe_bytes(value)
    p=HoldingProvider(); r=runner(tmp_path,Api(),p,workers=64,monthly_cap=Decimal('.01'))
    seed_jobs(r,64)
    r.run(SimpleNamespace(source_id='source'),64)
    assert p.calls<=1
    assert r.stop.is_set() and r.pause_reason=='LOCAL_MONTHLY_LIMIT'


def test_backend_failure_circuits_queued_requests_without_losing_results(tmp_path):
    class OfflineApi(Api):
        def __init__(self):super().__init__(); self.failed_requests=0
        def request(self,*args,**kwargs):
            if 'description' in kwargs['json']:
                self.failed_requests+=1
                raise RuntimeError('backend offline')
            return super().request(*args,**kwargs)
    api=OfflineApi(); p=Provider(); r=runner(tmp_path,api,p,workers=64)
    seed_jobs(r,64)
    with pytest.raises(RuntimeError):r.run(SimpleNamespace(source_id='source'),64)
    assert api.failed_requests==1
    assert r.journal.counts('source').get('ResultReady',0)==p.calls


def test_api_never_accepts_a_key_or_image():
    from pydantic import ValidationError
    from services.api.models import ByokResultRequest
    for field in ('apiKey','image','previewUrl'):
        with pytest.raises(ValidationError):
            ByokResultRequest(claimId=uuid4(), **{field:'must-not-cross-this-boundary'})


def test_remote_storage_does_not_change_byok_execution(session_factory):
    from services.data.models import MediaSource
    service=Phase1DomainService(session_factory,clock=lambda:NOW)
    user,source,_=setup_photo(service)
    with session_factory() as db:
        db.scalar(select(MediaSource)).storage_mode='Remote'; db.commit()
    service._enrichment_processing_enabled=False
    page=run(service.prepare_enrichment(user.user_id,source.device_id,source.source_id,
        EnrichmentPrepareCommand(types=('Description',),limit=1,execution_mode='BYOK'),context('remote-byok'))).value
    assert len(page.scene_description_tasks)==1


def test_desktop_byok_switch_does_not_allow_arbitrary_commands():
    from cli.nektron_moments_cli.desktop_job import permitted
    source='10000000-0000-4000-8000-000000000001'
    assert permitted(['sync',source,'--with-enrichment','--enrichment-limit','10000','--description-model','gpt-5.6-terra','--byok','--no-input'])
    assert not permitted(['delete',source,'--with-enrichment','--byok','--no-input'])


def test_geocode_only_pages_still_consume_the_selected_run_allowance(tmp_path):
    class LocationApi(Api):
        def __init__(self): super().__init__(); self.preparations=0
        def prepare_enrichment(self, source, payload, **kw):
            self.preparations+=1
            return {'sourceId':source,'assetsConsidered':1,'sceneDescriptionTasks':[], 'nextCursor':str(self.preparations)}
    api=LocationApi(); p=Provider(); r=runner(tmp_path,api,p,include_geocode=True)
    r.run(SimpleNamespace(source_id='source'),1)
    assert api.preparations==1 and p.calls==0


def test_changing_original_is_rejected_before_ai(tmp_path, monkeypatch):
    from PIL import Image
    from cli.nektron_moments_cli.scene_preview import prepare_scene_preview, ScenePreviewError
    photo=tmp_path/'changing.jpg'
    Image.new('RGB',(80,80),'blue').save(photo)
    original_save=Image.Image.save
    def changed_save(image,*args,**kwargs):
        result=original_save(image,*args,**kwargs)
        with photo.open('ab') as stream:stream.write(b'changed')
        return result
    monkeypatch.setattr(Image.Image,'save',changed_save)
    with pytest.raises(ScenePreviewError):prepare_scene_preview(photo)
