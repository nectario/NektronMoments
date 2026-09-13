#!/usr/bin/env bash

set -Eeuo pipefail
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/_common.sh"

require_command python
cd_repository

if [[ ! -x "${NEKTRON_MOMENTS_PYTHON}" ]]; then
    step "Creating the local Python environment"
    python -m venv "${NEKTRON_MOMENTS_VENV}"
fi

step "Installing Nektron Moments and development dependencies"
"${NEKTRON_MOMENTS_PYTHON}" -m pip install --upgrade pip
"${NEKTRON_MOMENTS_PYTHON}" -m pip install -e ".[dev]"

step "Environment ready"
"${NEKTRON_MOMENTS_CLI}" doctor
