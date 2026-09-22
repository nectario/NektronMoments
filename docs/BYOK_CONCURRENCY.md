# Direct AI concurrency (Windows 0.1.32)

The Windows app and CLI share the same BYOK runner. Unspecified concurrency is
selected in WSL: 64 requests with at least 32 logical CPUs and 32 GiB RAM; 32
with 16 CPUs / 16 GiB; 16 with 8 CPUs / 8 GiB; otherwise 4. If memory detection
is unavailable, the default is 4. This workstation reports 192 CPUs and selects
64. This is request concurrency, not the per-source photo allowance.

CLI overrides (1–64):

```bash
./scripts/cli.sh byok "My Photos" --limit 10000 --workers 64
./scripts/cli.sh sync "My Photos" --with-enrichment --byok --ai-workers 64
```

Do not launch a second run for a source that is already processing. An existing
process retains its original concurrency; the next run uses the new code.

The rolling pipeline holds at most twice the worker count in submitted tasks.
It does not wait for every request in a 64-photo page to finish before admitting
later photos. At most eight workers decode previews at once. Backend preparation,
claims, result uploads and token refresh remain serialized; OpenAI calls run
outside that lock. This protects the shared authentication session and existing
backend infrastructure. Backend round-trip latency can still limit throughput.

Each successful description is committed locally before synchronization. Failed
synchronization leaves ResultReady data for retry without another paid inference.
The existing monthly reservation guard, provider-limit pause and uncertain-call
review rules remain unchanged. Concurrency does not increase the spending cap
or bypass provider limits. Terra and the image/prompt quality are unchanged.

The progress dialog now reads measured descriptions/second from the worker and
estimates time to the selected allowance. It is a phase estimate, not a guarantee
that every considered asset needs or will receive a new description.

## Offline verification

`python scripts/benchmark-byok.py` uses a generated JPEG, temporary journals and
simulated network calls. It never reads keys or user photos and makes no paid
requests. On this workstation (128 images, simulated 250 ms AI latency):

| Pipeline | Workers | Seconds | Images/s |
| --- | ---: | ---: | ---: |
| Previous release | 4 | 9.15 | 13.98 |
| Rolling | 4 | 9.66 | 13.25 |
| Rolling | 64 | 2.85 | 44.94 |

These numbers measure scheduling under fixed simulated latency, not production
OpenAI speed. The four-worker case is slightly slower because result sync uses
the same worker; the larger pool overlaps that wait. Dedicated tests verify all
64 requests can be active, a slow first-page request does not stall later pages,
backend access stays serialized, and concurrent budget reservations stay bounded.
