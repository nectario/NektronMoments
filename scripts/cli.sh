#!/usr/bin/env bash

set -Eeuo pipefail
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/_common.sh"

require_environment
cd_repository

if (($# > 0)); then
    exec "${NEKTRON_MOMENTS_CLI}" "$@"
fi

step "CLI help"
"${NEKTRON_MOMENTS_CLI}" --help

step "CLI version"
"${NEKTRON_MOMENTS_CLI}" version

step "Local diagnostics"
"${NEKTRON_MOMENTS_CLI}" doctor
