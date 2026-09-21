"""Device-owned AI results. This module never calls a provider or stores a key."""
from uuid import uuid4
from sqlalchemy import select
from services.data.database import transaction_scope
from services.data.models import ProcessingJob, MediaSource, MediaAsset, MediaDescription, UploadSession
from services.domain.repositories import DeviceRepository, ChangeRepository
from services.domain.errors import ConflictError, ForbiddenError, NotFoundError
from services.enrichment.openai_scene import SCENE_DESCRIPTION_PROMPT_VERSION


async def save_byok_result(service, user_id, device_id, job_id, claim_id, description):
    with transaction_scope(service._session_factory) as session:
        account = service._account(session, user_id)
        device = DeviceRepository(session).require(user_id=account.id, device_public_id=device_id)
        job = session.scalar(select(ProcessingJob).where(
            ProcessingJob.user_id == account.id, ProcessingJob.public_id == str(job_id)
        ).with_for_update())
        if job is None:
            raise NotFoundError("ProcessingJobNotFound", "Processing job not found")
        source = session.get(MediaSource, job.media_source_id)
        if source is None or source.device_id != device.id:
            raise ForbiddenError("SourceDeviceMismatch", "Only the source device can submit a BYOK result")
        request = dict(job.request_json or {})
        existing = request.get("byokClaimId")
        if job.job_type != "Description" or (existing and existing != str(claim_id)):
            raise ConflictError("ByokClaimConflict", "This photo is already claimed by another analysis")
        if job.status == "Succeeded" and existing == str(claim_id):
            return "Succeeded"  # Lost acknowledgement must not cause another paid call.
        if job.status != "Preparing":
            raise ConflictError("ByokJobUnavailable", "Already queued or completed work is not reprocessed")
        if existing is None and session.scalar(select(UploadSession.id).where(
            UploadSession.user_id == account.id, UploadSession.media_asset_id == job.media_asset_id,
            UploadSession.object_purpose == "TemporaryProcessing", UploadSession.active_lease_marker == 1)):
            raise ConflictError("ByokJobUnavailable", "An existing cloud upload owns this photo; it will not be analyzed twice")
        asset = session.get(MediaAsset, job.media_asset_id)
        if asset is None or asset.lifecycle_state != "Active":
            raise ConflictError("ByokAssetUnavailable", "This photo is no longer active")
        now = service._now()
        if description is None:
            if existing is None:
                job.provider = "OpenAI-BYOK"
                request["byokClaimId"] = str(claim_id)
                request["executionMode"] = "BYOK"
                job.request_json = request
                job.updated_at_utc = now
                service._add_job_change(session, job=job, now=now)
            return "Claimed"
        if existing != str(claim_id) or job.provider != "OpenAI-BYOK":
            raise ConflictError("ByokClaimRequired", "Claim the photo before submitting its description")
        description = " ".join(description.split())
        if not description or len(description) > 2000:
            raise ConflictError("InvalidDescription", "A non-empty bounded description is required")
        for current in session.scalars(select(MediaDescription).where(
            MediaDescription.user_id == account.id,
            MediaDescription.media_asset_id == asset.id, MediaDescription.is_current == 1)):
            current.is_current = 0
        result = MediaDescription(public_id=str(uuid4()), user_id=account.id,
            media_asset_id=asset.id, description=description, provider="OpenAI-BYOK",
            model=str(request["model"]), prompt_version=SCENE_DESCRIPTION_PROMPT_VERSION,
            status="Succeeded", is_current=1, requested_at_utc=job.created_at_utc,
            completed_at_utc=now, created_at_utc=now, updated_at_utc=now)
        session.add(result)
        job.status = "Succeeded"
        job.completed_at_utc = job.updated_at_utc = now
        job.next_attempt_at_utc = None
        asset.last_processed_at_utc = asset.updated_at_utc = now
        session.flush()
        ChangeRepository(session).add(user_id=account.id, source_id=source.id,
            asset_id=asset.id, entity_type="MediaDescription", entity_id=result.id,
            entity_public_id=result.public_id, change_type="Upsert", now=now)
        service._add_job_change(session, job=job, now=now)
        return "Succeeded"
