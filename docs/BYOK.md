# Personal BYOK workflow

Local and Remote describe original-file storage, not AI execution. The source
device prepares a metadata-free JPEG and sends it directly to OpenAI. No preview
or API key is uploaded to Nektron or S3 by this path. The backend receives a
device-owned claim and the resulting description. Original uploads for Remote
storage remain independent; this work does not implement missing Remote upload UI.

The existing WSL OPENAI_API_KEY wins over the ignored workspace .env. No key is
copied or changed. Terra remains the default model. Standard service tier is
used; the initial concurrency is four, configurable from one to sixteen in CLI.

After updating the backend and CLI, catch up indexed photos without a rescan:

```sh
./scripts/cli.sh byok "My Photos" --limit 10000 --workers 4
```

For file metadata first, followed by direct AI:

```sh
./scripts/cli.sh sync "My Photos" --with-enrichment --byok --enrichment-limit 10000
```

Windows Full processing uses this explicit BYOK switch. Metadata-only remains
unchanged. Standalone byok handles scene descriptions only; Full sync also
requests address enrichment through the existing backend, with its existing quota.

## Durability, costs and coexistence

- Claims are scoped to the authenticated source device and original asset.
  Existing queued/running cloud jobs and active cloud uploads are not taken over.
- A local ByokAnalysis journal stores completed results before synchronization.
  Retrying a failed backend save does not call OpenAI again. Windows details can
  read locally completed results even when the backend is unavailable.
- An interrupted in-flight call becomes Uncertain. It is deliberately not
  automatically retried: a response may have been billed before the device lost
  it. Such calls currently require review; bulk automatic uncertain-call recovery
  is not implemented. Do not delete the journal to force a retry.
- Stop cancels unsent tasks. Already-running requests may finish and save their
  results; abrupt termination leaves their outcome marked uncertain on resume.
- The personal workflow has a conservative US$230 monthly device/account-local
  guard. It is NOT an organization-wide OpenAI spending cap and does not include
  calls from other devices/tools or the legacy managed pipeline. Do not erase
  local state to reset it. Existing managed cloud budgets are unchanged.
- Authentication/provider errors or local budget exhaustion pause the run.
  No invisible fallback to the NektronAI/company key is allowed.

## Managed offering (later)

NektronAI AI will be a separate authenticated gateway. Its provider key stays
server-side; account/plan entitlements and usage are checked there. It must not
silently replace BYOK. No new managed gateway is enabled in this implementation.

JetBrains documents the same direct-BYOK versus managed-service routing split:
https://www.jetbrains.com/help/ai-assistant/activation-scenarios.html
OpenAI supports inline base64 image inputs without URL staging:
https://developers.openai.com/api/docs/guides/images-vision
