"""CLI job host with cooperative cancellation over its private stdin pipe."""
from __future__ import annotations
import os
import signal
import sys
import threading
from uuid import UUID
from .sync import MAX_ENRICHMENT_RUN_LIMIT


def permitted(arguments: list[str]) -> bool:
    if len(arguments) in (5, 7, 9) and arguments[-2:] == ["--byok", "--no-input"]:
        return permitted(arguments[:-2] + ["--no-input"])
    if len(arguments) in (6, 8) and arguments[0] == "sync":
        try:
            UUID(arguments[1])
            limit = int(arguments[4])
        except ValueError:
            return False
        return (arguments[2:4] == ["--with-enrichment", "--enrichment-limit"]
                and 1 <= limit <= MAX_ENRICHMENT_RUN_LIMIT and arguments[-1] == "--no-input"
                and (len(arguments) == 6 or (arguments[5] == "--description-model"
                     and arguments[6] in {"gpt-5.6-terra", "gpt-5.6-luna", "gpt-5.6-sol"})))
    # No arbitrary CLI execution or destructive operations. Full startup mode
    # explicitly opts into the existing bounded enrichment path.
    if len(arguments) in (3, 4) and arguments[0] == "sync":
        try:
            UUID(arguments[1])
        except ValueError:
            return False
        return arguments[2:] in (["--no-input"], ["--fast-add", "--no-input"], ["--with-enrichment", "--no-input"])
    return len(arguments) == 4 and arguments[:2] == ["source", "add"] and arguments[2].startswith("/") and arguments[3] == "--json"


def run(arguments: list[str], input_stream=None, command=None) -> int:
    if not permitted(arguments):
        raise ValueError("Unsupported desktop job")
    stopped = threading.Event()
    finished = threading.Event()
    def monitor():
        for line in (input_stream or sys.stdin):
            if line.strip() == "cancel" and not finished.is_set():
                stopped.set()
                os.kill(os.getpid(), signal.SIGINT)
                return
        # Losing the desktop host should not leave an orphaned sync holding a lock.
        if not finished.is_set():
            stopped.set()
            os.kill(os.getpid(), signal.SIGINT)
    if command is None:
        from .app import app
        command = lambda: app(args=arguments, prog_name="nektron-moments")
    threading.Thread(target=monitor, daemon=True).start()
    try:
        command()
        return 130 if stopped.is_set() else 0
    except KeyboardInterrupt:
        return 130
    except SystemExit as error:
        return 130 if stopped.is_set() else int(error.code or 0)
    finally:
        finished.set()


if __name__ == "__main__":
    raise SystemExit(run(sys.argv[1:]))
