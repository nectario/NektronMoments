import subprocess
import sys
from cli.nektron_moments_cli.desktop_job import permitted

SOURCE = "00000000-0000-4000-8000-000000000001"


def test_desktop_jobs_allow_only_metadata_commands():
    assert permitted(["sync", SOURCE, "--no-input"])
    assert permitted(["sync", SOURCE, "--fast-add", "--no-input"])
    assert permitted(["source", "add", "/mnt/d/Pictures", "--json"])
    for args in (
        ["sync", SOURCE, "--with-enrichment"],
        ["sync", "--with-enrichment", "--no-input"],
        ["source", "remove", SOURCE, "--json"],
        ["source", "add", "--help", "--json"],
        ["auth", "login"], ["enrich", SOURCE],
    ):
        assert not permitted(args)


def test_cancel_interrupts_the_actual_job_host_without_killing_test_process():
    code = f"""
import time
from cli.nektron_moments_cli.desktop_job import run
def job():
    print("ready", flush=True)
    time.sleep(30)
raise SystemExit(run(["sync", "{SOURCE}", "--no-input"], command=job))
"""
    process = subprocess.Popen([sys.executable, "-u", "-c", code], stdin=subprocess.PIPE,
                               stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    try:
        assert process.stdout.readline().strip() == "ready"
        process.stdin.write("cancel\n"); process.stdin.flush()
        assert process.wait(timeout=5) == 130
    finally:
        if process.poll() is None:
            process.kill()
        process.communicate()
