# Nektron Moments rename

The public project and GitHub repository are **Nektron Moments** and
[`nectario/NektronMoments`](https://github.com/nectario/NektronMoments).
The physical checkout is `C:\Development\Projects\NektronMoments`.

## New development names

- Distribution and CLI: `nektron-moments`.
- Python CLI imports: `cli.nektron_moments_cli`.
- Standalone importer: `NektronMoments.py`.
- Preferred environment prefix: `NEKTRON_MOMENTS_`.
- Preferred device header: `X-Nektron-Moments-Device-Id`.
- New-install settings directory: `%APPDATA%/NektronMoments` on Windows,
  `$XDG_CONFIG_HOME/nektron-moments` or `~/.config/nektron-moments` on Linux.
- OS credential service for new saves: `NektronAI.NektronMoments`.

The `scripts/*.sh` wrappers use the new command. After moving a Python venv,
reinstall the editable project to regenerate its absolute entry-point paths:

```bash
cd /mnt/c/Development/Projects/NektronMoments
.venv/bin/python -m pip install --no-build-isolation -e '.[dev]'
./scripts/cli.sh doctor
```

## Existing-library compatibility

Existing settings directories are reused when no new directory exists, so sign-in,
source bindings, hashes, outboxes, and library state stay available. The account
state hash salt remains unchanged. `NEKTRON_MOMENTS_CONFIG_DIR` can explicitly
select an existing directory. New environment keys take precedence; old
`IMAGETRACKER_*` keys remain accepted. Keyring reads fall back to the old service;
logout clears both names. The old `imagetracker` command, `ImageTracker.py`, and
`cli.imagetracker_cli` imports forward to the new implementation.

The CLI sends both device-header names during transition. The API accepts either
and rejects conflicting values. No user media, credentials, or provider keys are
rewritten by this source rename.

## Deployed identities retained deliberately

The production database is still `ImageTracker`, the stack/service is still
`image-tracker-prod` / `image-tracker`, and SSM credentials remain under
`/imagetracker/prod`. CloudFormation logical IDs and cost-allocation tags are
preserved, as are their derived S3/SQS/Lambda names. These identifiers address the
existing library, accounts, deployment history, and budget; replacing them in
text would create different resources rather than rename the working service.

This change updates source branding and configuration compatibility. It does not
deploy or migrate live AWS/MySQL resources. An infrastructure rename requires a
separate migration with account/data continuity. Enrichment remains paused in
the deployment template.

Historical handoffs, rollout evidence, applied SQL migrations, and copied sibling
brand assets retain their original names and provenance. `Brand_Images/manifest.json`
checksums continue to match those immutable references.

## Validation — 2026-09-12

- WSL test suite: 385 passed; one upstream Starlette/httpx deprecation warning.
- OpenAPI conventions: 34 operations, 97 schemas, 30 paths validated.
- Installed dependencies: no broken requirements.
- Wheel contains both canonical and compatibility modules and CLI entry points.
- Existing saved sign-in and account-local state recognized by CLI doctor.
- Old and new CLI entry points both launch successfully.
- Infrastructure comparison against the prior committed template confirms unchanged
  resource identities, budgets, tags, storage, processing limits, and schedules;
  differences are product labels, environment aliases, and the added header.
- No production deployment, database migration, or paid enrichment was performed.
