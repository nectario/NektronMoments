"""Verify the live schema without preparing any jobs (limit=0 must be rejected)."""
from pathlib import Path
import sys
from uuid import uuid4

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from cli.nektron_moments_cli.runtime import build_runtime

runtime = build_runtime()
try:
    runtime.api.request("GET", "/v1/health")
    print("Authenticated production health: passed")
    tokens = runtime.auth.current_tokens()
    if tokens is None:
        raise RuntimeError("Sign in before running the release check")
    for model in ("gpt-5.6-terra", "gpt-5.6-luna", "gpt-5.6-sol", "invalid-model"):
        response = runtime.api.http.post(
            runtime.api.base_url + f"/v1/sources/{uuid4()}/enrichment/prepare",
            headers={"Authorization": f"Bearer {tokens.access_token}",
                     "Idempotency-Key": f"schema-check:{uuid4()}",
                     "X-Nektron-Moments-Device-Id": str(uuid4())},
            json={"limit": 0, "descriptionModel": model},
        )
        if response.status_code != 422:
            raise RuntimeError(f"Unexpected schema-check status: {response.status_code}")
        fields = {item["field"] for item in response.json().get("fieldErrors", [])}
        expected = {"limit", "descriptionModel"} if model == "invalid-model" else {"limit"}
        if fields != expected:
            raise RuntimeError(f"Model schema differs for {model}: {sorted(fields)}")
        print(f"{model}: schema verified; invalid zero limit prevented job creation")
finally:
    runtime.api.close()
    if runtime.local_state is not None:
        runtime.local_state.close()
