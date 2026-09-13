"""Nektron Moments identity and narrowly scoped migration compatibility."""
from __future__ import annotations

import os
from typing import Mapping

PRODUCT_NAME = "Nektron Moments"
ENV_PREFIX = "NEKTRON_MOMENTS_"
LEGACY_ENV_PREFIX = "IMAGETRACKER_"
# Deployed identities require a separate resource/data migration.
DATABASE_NAME = "ImageTracker"
STACK_NAME = "image-tracker-prod"
SSM_PREFIX = "/imagetracker/prod"
DEVICE_HEADER = "X-Nektron-Moments-Device-Id"
LEGACY_DEVICE_HEADER = "X-ImageTracker-Device-Id"


def environment_value(
    name: str,
    default: str | None = None,
    *,
    environment: Mapping[str, str] | None = None,
) -> str | None:
    values = os.environ if environment is None else environment
    if name in values:
        return values[name]
    legacy = (
        LEGACY_ENV_PREFIX + name[len(ENV_PREFIX):]
        if name.startswith(ENV_PREFIX)
        else "MYSQL_DATABASE_IMAGETRACKER"
        if name == "MYSQL_DATABASE_NEKTRON_MOMENTS"
        else name
    )
    return values.get(legacy, default)
