#!/usr/bin/env bash

set -Eeuo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPOSITORY_ROOT="$(cd -- "${SCRIPT_DIR}/.." && pwd)"
NEKTRON_MOMENTS_VENV="${REPOSITORY_ROOT}/.venv"
NEKTRON_MOMENTS_PYTHON="${NEKTRON_MOMENTS_VENV}/bin/python"
NEKTRON_MOMENTS_CLI="${NEKTRON_MOMENTS_VENV}/bin/nektron-moments"

export NEKTRON_MOMENTS_AWS_REGION="${NEKTRON_MOMENTS_AWS_REGION:-${IMAGETRACKER_AWS_REGION:-us-east-2}}"
export NEKTRON_MOMENTS_STACK_NAME="${NEKTRON_MOMENTS_STACK_NAME:-${IMAGETRACKER_STACK_NAME:-image-tracker-prod}}"
export NEKTRON_MOMENTS_DB_SECRET_PARAMETER="${NEKTRON_MOMENTS_DB_SECRET_PARAMETER:-${IMAGETRACKER_DB_SECRET_PARAMETER:-/imagetracker/prod/mysql}}"

fail() {
    printf 'ERROR: %s\n' "$*" >&2
    exit 1
}

step() {
    printf '\n%s\n' "$*"
}

require_command() {
    command -v "$1" >/dev/null 2>&1 || fail "Required command is unavailable: $1"
}

require_environment() {
    [[ -x "${NEKTRON_MOMENTS_PYTHON}" ]] || fail "Run scripts/setup.sh first; .venv is missing."
    [[ -x "${NEKTRON_MOMENTS_CLI}" ]] || fail "Run scripts/setup.sh first; the nektron-moments CLI is missing."
}

cd_repository() {
    cd -- "${REPOSITORY_ROOT}"
}
