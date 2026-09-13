# Windows 0.1.2 — viewing and library workflow

Delivered to the owner in the cumulative **0.1.3** installer, which also includes
the requested larger ribbon/viewer icons. The completed 0.1.2 installer is retained
unchanged; 0.1.3 is the version to install.

Implemented from the owner's September 13 feedback:

- Native File/Edit/Library/View/Playback/Help menu bar and v1.3 icon ribbon.
- Sun for the current Light theme, Moon for Dark.
- Process metadata in-app; Add folder discovers quickly then processes full metadata.
  Both paths reuse the existing metadata-only CLI workflow and preserve saved work
  on cancellation. Paid enrichment is not enabled or invoked.
- Double-click opens the exact clicked item in the gallery canvas, not a dialog.
- Resizing respects aspect ratio and original physical pixels. One remembered
  enlargement checkbox is shared by viewing and slideshow.
- Slideshow advances through photos in the current view, with interval choices,
  pause/stop, keyboard navigation, and F11 fullscreen.
- A remembered thumbnail slider plus Small/Medium/Large/X-large presets.
- Hardware-adaptive data buffers and decoded/encoded thumbnail caches. On the
  192 GB workstation the initial target is 24,576 metadata records, 1,536
  thumbnails ahead, and eight viewport lengths; controls remain virtualized.
- Exact-size Windows thumbnail streams, dedicated foreground/prefetch workers,
  small yielding metadata appends, and timeline-order indexes.

## Verification

- Debug x64 build: zero warnings/errors.
- Full Python regression suite: 414 passed; one existing Starlette/httpx
  deprecation warning. OpenAPI and installed dependency checks passed.
- Native C# policy executable: 36 assertions passed (DPI-aware fit, no-upscale
  behavior, presets, machine budgets, LRU eviction and memory caps).
- Actual job-host cancellation tested in a child process with a fake workload.
  No production metadata sync or paid enrichment was started for testing.
- App-rendered real-library snapshots: Light/Dark ribbon, canvas, original-size
  limit, enlargement, slideshow, Small/X-large thumbnails, scrolling and compact
  layout. These private images stay under ignored build/windows-verification-012.
- Pre-icon-adjustment sample: 144,135 catalog items; 24,696 records initially buffered;
  28,792 after scrolling; 210 visual containers after the resize/scroll sequence.
- Initial library readiness: 1,499 ms from page construction (excludes build,
  installation and OS activation). Larger visual caches expand after that first
  view. Rebuilding an invalidated catalog is slower.
- Small-image scale: 1.0 with the cap enabled, 2.244 after explicit enlargement.
  Slideshow advanced; no reported UI errors.
- Scripted scroll sample: 350 render-event samples, p95 gap 9.86 ms. This is a
  diagnostic sample, not a universal FPS guarantee; the observed monitor was 60 Hz.
- End-of-check cache use: about 212 MiB decoded and 275 MiB encoded, below their
  workstation ceilings. Budgets are not upfront allocations.

### Larger-icon follow-up (0.1.3)

- Shared, scoped native CommandBar resource overrides remove the platform's
  16-DIP icon Viewbox cap. Approved SVG artwork remains unchanged.
- Both ribbon and viewer use 32-DIP icons and 56-DIP command height. Runtime
  measurements for all nine ribbon and five viewer commands were 31.70 DIP
  icon height and 56.15 DIP command height after display-scale pixel rounding.
- Rechecked Light/Dark, compact overflow, canvas cap/enlargement, slideshow,
  thumbnail presets and buffered scrolling. Private snapshots and measurements
  stay in ignored `build/windows-verification-013/`.
- Follow-up sample: 24,696 buffered / 28,792 after scrolling, 203 realized
  containers, 352 render-event samples, p95 gap 10.49 ms. Cache use was about
  215 MiB decoded / 277 MiB encoded; no reported UI error, slideshow advanced.
- Library readiness was 8,341 ms on this launch after the installer regression
  run, compared with the earlier 1,499 ms warm sample. These are observed runs,
  not startup-time guarantees.
- Final cumulative installer: `NektronMoments.Setup.0.1.3.exe`, 72,693,920 bytes,
  valid timestamped Nektron, Inc. signature. The isolated per-user install,
  workspace reconnect, native launch and uninstall canary passed. All 414 Python
  tests and 36 native policy assertions passed again before release packaging.
  The versioned source release record contains its SHA-256.

## Boundaries

The app still uses the existing Ubuntu CLI adapter and account. Metadata processing
extracts what the files contain; descriptions and nearest addresses require their
separate enrichment workflow. No production deployment, MySQL migration, new cloud
resource, or provider spending is included in this UI release.

Relevant platform guidance:
[virtualized ItemsWrapGrid caching](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.itemswrapgrid)
and [exact-size thumbnail options](https://learn.microsoft.com/en-us/uwp/api/windows.storage.fileproperties.thumbnailoptions).
