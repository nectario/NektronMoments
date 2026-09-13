#!/usr/bin/env bash

set -Eeuo pipefail
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/_common.sh"

require_environment
cd_repository

step "Checking additive Nektron Moments database migrations"
"${NEKTRON_MOMENTS_PYTHON}" -B infra/scripts/migrate_enrichment.py "$@"
