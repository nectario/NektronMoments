"""Read live enablement/budget settings; never enqueue work or expose secrets."""
from decimal import Decimal
import boto3

region = "us-east-2"
client = boto3.client("lambda", region_name=region)
for name in ("image-tracker-prod-api", "image-tracker-prod-worker"):
    result = client.get_function_configuration(FunctionName=name)
    env = result.get("Environment", {}).get("Variables", {})
    assert result["LastUpdateStatus"] == "Successful", name
    assert env.get("NEKTRON_MOMENTS_ENRICHMENT_PROCESSING_ENABLED") == "true", name
    assert Decimal(env["NEKTRON_MOMENTS_SCENE_DESCRIPTION_MONTHLY_USD_LIMIT"]) == Decimal("230"), name
    assert env["NEKTRON_MOMENTS_GEOCODE_MONTHLY_CALL_LIMIT"] == "1000", name
    print(f"{name}: enrichment enabled; $230 AI cap and 1,000 geocode-call cap retained")
assert client.get_function_concurrency(FunctionName="image-tracker-prod-worker")["ReservedConcurrentExecutions"] == 1
mappings = client.list_event_source_mappings(FunctionName="image-tracker-prod-worker")["EventSourceMappings"]
assert len(mappings) == 1 and mappings[0]["State"] == "Enabled" and mappings[0]["BatchSize"] == 1
print("Worker: enabled, concurrency 1, batch size 1")
events = boto3.client("events", region_name=region)
assert events.describe_rule(Name="image-tracker-prod-retry-due-jobs")["State"] == "ENABLED"
print("Enrichment retry schedule: enabled")
