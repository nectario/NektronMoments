"""Compatibility entry point. Use NektronMoments.py for new integrations."""
import importlib
import sys

_implementation = importlib.import_module("NektronMoments")
if __name__ == "__main__":
    raise SystemExit(_implementation.main())
else:
    sys.modules[__name__] = _implementation
