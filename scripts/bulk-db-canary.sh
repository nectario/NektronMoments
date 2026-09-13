#!/usr/bin/env bash

set -Eeuo pipefail
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/_common.sh"

require_environment
cd_repository

step "Checking the self-cleaning bulk MySQL canary"
exec "${NEKTRON_MOMENTS_PYTHON}" -B infra/scripts/live_bulk_mysql_canary.py "$@"
