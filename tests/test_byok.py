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
