"""Compatibility alias for the renamed CLI module."""
import importlib
import sys

sys.modules[__name__] = importlib.import_module("cli.nektron_moments_cli.sync")
