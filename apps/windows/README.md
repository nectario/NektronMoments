# Nektron Moments for Windows — first native preview

The packaged C#/WinUI app lives in `NektronMoments/` and uses the approved brand
v1.3. Windows is a first-class client alongside the planned iOS and Android apps.

## Install or run

The traditional Windows installer is built with `./scripts/build-installer.ps1`.
Completed setups are placed directly in `installer/NektronMoments.Setup.<version>.exe`.
See [installer documentation](../../installer/README.md) for signed releases,
per-user installation, bundled runtimes and the existing-workspace dependency.

From the repository root in **Windows PowerShell**:

```powershell
./scripts/windows.ps1
# Build without opening:
./scripts/windows.ps1 build
```

The script builds with the pinned .NET 10 SDK and uses Microsoft's package-aware
`dotnet run` target. Close an existing preview before rebuilding. After its first
registration, Nektron Moments is also available through Windows Start.

## Implemented in this milestone

- Settings opens from the sidebar gear or **Edit → Settings…**, including repeated
  clicks after closing it. Both expose the saved startup processing choices;
  About and keyboard help remain separate under Help.
- Settings also remembers **AI assets per source per run** (1–64; default 64)
  and **Scene description model** (Terra/default, Luna, Sol). The server ceiling
  remains 64; higher-volume pagination is not part of this change. Both Full
  startup and manual AI processing use a snapshot of these settings.
- The live cost preview assumes 2,560 input and 100 output tokens per image at
  standard USD rates verified 2026-09-21 against
  [OpenAI pricing](https://developers.openai.com/api/docs/pricing). It is not a
  quote or budget: actual tokens/retries differ, Flex/cache reuse can cost less,
  and geocoding/AWS charges and previously queued work are excluded.
- Model selection is forwarded through the CLI and authenticated API into the
  job's persisted configuration. Worker allowlisting and per-model reservations
  preserve the existing monthly limits. Completed and queued jobs retain their
  original model; only pending Preparing jobs may be reconfigured.

- Existing CLI library/session reuse without a second sign-in or re-import.
- Virtualized photo/video gallery with a complete lightweight catalog and source filters.
- Memory-adaptive thumbnail look-ahead; separate foreground and
  prefetch workers, bounded decoded/encoded LRU caches and exact-size thumbnails.
- Filename filtering as you type; Enter searches indexed descriptions/addresses.
- Exact-hash deduplication with preserved source occurrences.
- Double-click opens the exact clicked photo/video in the entire gallery canvas.
  Single-click selects without rearranging the grid; the Details command opens
  the metadata pane explicitly. Late requests cannot replace a newer selection.
  Images fit on resize and never exceed original physical pixels unless the
  enlargement checkbox is checked. EXIF orientation is respected.
- Slideshow with pause/stop and 3/5/8/10-second intervals; it uses the same
  enlargement preference and skips videos. Missing/unsupported media stops with
  a readable message instead of silently skipping errors.
- Add folder performs fast discovery followed by full metadata processing in the
  background. **Process metadata** opens a setup dialog for the selected source,
  or all local sources when no source filter is active. **Include AI enrichment**
  starts checked each time. Nothing starts until **Start**; Cancel does no work.
  Checked runs metadata plus the existing paid AI/address-enrichment pass for new
  and older pending photos (up to 64 eligible assets per source, subject to quotas).
  Unchecked runs file metadata only. Completed descriptions are reused.
- **Stop processing** sends a cooperative interrupt to the CLI job host so saved
  batches survive. Already queued server enrichment may continue after stopping.
- v1.3 icon-rich ribbon with 24-DIP icons, 40-DIP-wide icon buttons, and
  a compact 48-DIP row matching Nektron Write. Includes
  File/Edit/Library/View/Playback/Help menus. Sun denotes
  light mode; Moon denotes dark mode.
- Remembered thumbnail slider and Small/Medium/Large/X-large presets.
- Ctrl+F search, F5 refresh, Esc return, Left/Right navigation, Space slideshow
  pause/resume, F11 fullscreen, and clipboard/reveal-original commands.

### Continuous browsing and adaptive image budgets

Mouse-wheel movement is independent of these budgets and of thumbnail size.
View → Scrolling adjusts wheel pixels per notch (64 by default; saved custom settings
are preserved). The GridView's built-in scrollbar owns direct dragging, track paging,
keyboard navigation and automation, as in 0.1.1. There is no separate proxy scrollbar,
added drag animation, or Shift precision mode. The bar retains the wider thumb.
The gallery uses size-dependent browsing batches: 200 at medium/large sizes, 500
at Small/tiny sizes, and intermediate steps between them. Native thumb dragging
holds the range stable until release. Resizing never shrinks an already exposed range.
Appending preserves the visible photo and pixel offset. A maximized window that can
show more than the initial batch fills with further batches until it can scroll naturally.
There are no range arrows, custom edge-scroll controls, or thumb recentering.
Wheel motion also uses native animation, accumulates fractional/rapid input, and
updates scrollbar position while moving. Its shipping path no longer uses the old
UI-thread frame loop, three-notch truncation, or fixed 720-pixel/second ceiling.
Thumbnail resizing now rearranges rows/columns during the gesture, with coalesced
layout, short reposition animations and a bounded set of realized controls.

These are ceilings and look-ahead targets, not upfront allocations:

| Profile | RAM | Thumbnails ahead | Encoded / decoded cache | Disk cache |
| --- | --- | ---: | --- | --- |
| Balanced | Below 24 GiB | Up to 512 | 128 / 384 MiB | 1 GiB |
| Enhanced | 24–95 GiB | Up to 2,048 | 512 MiB / 2 GiB | 4 GiB |
| Workstation | 96 GiB+ | Up to 8,192 | 1 / 12 GiB | 8 GiB |

The grid stays virtualized. Complete compact headers are retrieved with two local
queries, but only the growing browsing prefix is exposed to the GridView. Thumbnail
preparation and full-catalog slideshow/viewer navigation use the complete snapshot.
Returning from a viewer position beyond the exposed prefix reveals that exact photo.
Full metadata is retrieved when Details opens.
Catalog indexes match timeline ordering. Old
oversized derived thumbnail cache entries are replaced; original files are untouched.
Live XAML buffers are budgeted separately (about 240 offscreen controls, with a
half-viewport minimum), so maximizing does not multiply thousands of live tiles.
Visible thumbnail decodes have reserved capacity; cached bitmaps remain immediate.

Look-ahead decodes and materializes pixels on background workers without creating
XAML image sources or posting a UI callback per preview. Near-viewport tiles
publish through a small paced SoftwareBitmapSource queue; distant prefetch yields
to active scrolling, while promoted visible requests continue.
Visible photos come first, then the next 200 in the direction of travel, then
farther look-ahead with a reverse-side reserve. Its count is resolution-aware and
uses at most half the decoded-cache budget, leaving room for recently viewed images.
For example, the workstation plans up to 3,072 square 512-pixel previews or 768
1024-pixel previews. LRU weights reserve space for decoded pixels and a possible
presentation copy; native buffer lifetime follows its consumers.
Shared in-flight jobs deduplicate version/resolution requests. Recycling a tile
cancels its wait rather than discarding useful preparation. Larger ready previews
can satisfy smaller requests without another decode. Disk-cache maintenance no
longer delays the first visible images. Cache readiness never expands the scrollbar.

Installer checks include generated-image tests for initial200, append anchors,
off-range preparation and actual cached-bitmap reuse by newly revealed native tiles.
An opt-in `NEKTRON_MOMENTS_READINESS_CHECK_DIR` records read-only real-library
readiness counters at 0/1/5/15 seconds, without scrolling, media capture or paid jobs.
The opt-in `NEKTRON_MOMENTS_STARTUP_SCROLL_CHECK_DIR` records native app-driven
scrubbing, UI-queue waits and runtime GC observations. It does not replay a mouse
or establish display FPS; occasional runtime pauses remain in stress observations.

Native sizing/cache tests: from `apps/windows`, run
`dotnet run --project Tests/ViewingTests.csproj -c Release`. They run automatically in the signed
installer pipeline. Current app-rendered checks live in ignored
`build/windows-verification-013/` (previous 0.1.2 evidence remains in `windows-verification-012`).

Processing metadata reads **available file metadata**; it cannot invent missing
EXIF, descriptions or addresses. Existing indexed descriptions/addresses are
retrieved separately. Full startup processing is the explicit opt-in to paid enrichment.

### Screenshot filtering and startup processing (0.1.23)

In 0.1.24, **Library > On startup** offers three modes, saved automatically:

- **Full — metadata + AI:** incremental metadata sync, then the existing bounded
  scene-description/address workflow, including older eligible pending photos.
  Each source gets one preparation pass of up to 64 eligible assets per launch;
  this does not drain the entire backlog in one launch. Completed results are
  reused, quotas remain enforced, and API usage may incur cost. Video transcription
  and face recognition are not added by this setting.
- **File metadata only:** the previous incremental metadata behavior, without
  requesting paid enrichment. The manual Process metadata command also stays
  metadata-only regardless of the startup preference.
- **Off:** no automatic processing pass; manual commands remain available.

The old enabled toggle migrates to File metadata only; disabled migrates to Off.
Missing or invalid settings never select Full. Changing the setting applies on
the next launch and does not escalate a job already running or queued at startup.
Full progress distinguishes local staging from jobs still queued/processing or
quota-deferred on the server. Stopping local processing does not cancel already
queued server-side work. Tests use fake workers and an in-memory preference sink
to validate Full without enabling it in the user's settings or calling providers.

The **Hide screenshots** checkbox beside sorting persists across launches. It
matches screenshot/screen-capture wording in existing description metadata, not
filenames, and keeps photos without descriptions and all videos visible. A
separate background connection pages through existing indexed matches, then
atomically replaces the local screenshot index. Until that succeeds, the last
cached classifications remain available offline. No AI descriptions are generated
by filtering. Simple textual negations such as "not a screenshot" are excluded;
this is description-based filtering, not a new visual classifier.

Custom order is preserved when filtering; reveal screenshots and clear search
before rearranging, so saving a subset cannot overwrite the complete arrangement.

By default, after the first catalog loads, the app runs the normal incremental
metadata sync for all registered Local sources. Existing file-cache/deduplication
and per-source sync locks are reused. Startup does not open a progress window or
start paid enrichment. **Activity** opens progress, and **Stop processing** cancels
safely. As of 0.1.24, **Library > On startup** controls the saved preference.
This runs once per launch, not as a continuous filesystem watcher. Missing folders,
offline services or a source already being synced use the existing failure/status
handling rather than duplicate work or repeated verification prompts.

Installer checks set `NEKTRON_MOMENTS_DISABLE_STARTUP_PROCESSING=1`; diagnostic
check/trace launches also suppress automatic work. These checks never use a
normal launch to quietly initiate real metadata imports.

### Arrangement and processing progress

Drag a thumbnail to rearrange the current library/source/type view. Native WinUI
drag feedback and reorder/reposition transitions retain virtualization. Select a
photo and use Ctrl+Shift+Arrow for keyboard reordering. The Sort button switches
between Newest, Oldest and Custom order. Reordering is disabled while searching;
clear the search to arrange the underlying view.

Windows 0.1.21 previews order during drag hover, including movement across rows.
All affected thumbnails move together, without a row-by-row stagger. Releasing
inside the gallery commits; canceling or dropping outside restores the old order.
Live size changes also retain coordinated compositor motion.

Windows 0.1.22 explicitly animates realized thumbnails with 320 ms eased render
transforms; a theme-transition declaration alone did not provide live drag motion.
The native verification now checks intermediate positions, in-flight reversal,
settled positions and resource cleanup. See
`docs/diagnostics/2026-09-20-real-photo-drag.md` for real-library evidence, rejected
performance experiments, and the remaining dense-grid scrolling limitation.

Custom arrangements are saved locally under the Windows user's
`NektronMoments/arrangements` folder, isolated by the CLI account's library identity
and view. They survive refresh/restart and retain their underlying date direction.
Original files are never moved or renamed; ordering is not yet synchronized to
other devices. Pending-file locator fallback preserves position when hashing
assigns a content identity. New/unarranged media follows the saved prefix.

Process metadata opens a separate, resizable, modeless processing window. It shows
source operations, a bounded activity log, completed-source progress, current-phase
counts, throughput, elapsed time and estimated phase time remaining where available.
The library remains usable. Hide or X does not cancel the job; Activity reopens
the current or last session. Stop processing is explicit. Discovery and phases
without a known total remain indeterminate. Overall progress counts completed
sources, not weighted work or an invented whole-job estimate. Saved queues opens
the existing queue overview. The worker retains 1,000 recent log entries and the
window displays at most 500, updating on a 150 ms poll rather than per file.

The viewer's upper-right X and Esc both return to the same selected photo.

## Catalog and scrolling responsiveness

Windows 0.1.20 adds a mixed-workflow audit to the installer checks. See
`docs/diagnostics/2026-09-20-operation-fluidity.md` for operation coverage and
remaining native-layout/theme hotspots. Resize reflow no longer uses staggered
gallery transitions, and detached composition resources retire incrementally.
Unchanged empty-query library views use a bounded compact-snapshot cache (512 MiB
on workstation-class RAM, 128 MiB otherwise, including per-entry allowances).
Refresh invalidates this cache. Source/type/date ordering and account identity
are part of the cache key; non-empty searches are not cached. Stale view requests
are canceled, and repeated refreshes share the current operation.

The local bridge streams a versioned, bounded-frame catalog into an immutable
UTF-8 index. It does not send one enormous JSON line or retain a separate full
managed photo object for every library position. The initial 200–500 browsing rows
and each following range are materialized on workers before UI publication.
Unexposed records use an 8,192-object working cache; already exposed positions
remain pinned to preserve native selection and scroll identity. This is not a
fixed total memory cap as the user browses farther.

Decoded thumbnail look-ahead remains independent, using the existing machine-
appropriate large cache. Native scrollbar ownership and wheel speed preferences
are unchanged; browsing batches adapt to thumbnail size. A ready tile stops receiving
viewport notifications until its item, resolution, or load lifecycle changes.

`scripts/trace-windows-startup.ps1` records local opt-in frame/GC diagnostics.
See `docs/diagnostics/2026-09-19-startup.md` for the original evidence. Traces
stay under ignored `build/diagnostics/` directories and are not uploaded.

## Deliberate preview boundary

This is a real native UI with an installer, **not yet a fully standalone app**. Ubuntu WSL,
the repository's Python environment, and an existing signed-in CLI account are
required. A stdio-only adapter reuses that account without copying passwords or
tokens into Windows and without listening on a local HTTP port.

The adapter reads the authoritative CLI SQLite database in read-only mode. Its
separate `desktop-library-v1.sqlite3` is a disposable account-local read model,
invalidated when the source database/WAL changes. Native thumbnails stay in the
current Windows user's app-data area. No original file is modified by browsing.
Full startup mode may request paid enrichment through the existing CLI/service
workflow; file-metadata mode and browsing do not. Browsing API requests fetch
already indexed metadata.

Remote search is bounded to 200 server hits and preserves source/type filters.
When offline, local filename matches remain usable. Placeholder icons represent
missing/unsupported originals; codec availability depends on Windows. Catalog
refresh is explicit: this preview does not yet watch the source continuously.

Standalone native authentication/sync, continuous watching, high-contrast/manual
input acceptance, full localization and mobile clients remain
follow-up work. No new AWS infrastructure or production deployment is included.

## Verification — 2026-09-13

- Debug x64 build succeeded with no warnings/errors; a real packaged window opened.
- 402 Python regressions and OpenAPI/dependency checks passed.
- The adapter fetched an existing indexed detail and verified its exact hash.
- App-rendered Light/Dark/detail/compact snapshots are under ignored
  `build/windows-verification/`, with no reported UI errors.
- Warm initial library data: 120 items from 144,135 catalog entries in **851 ms**,
  measured from page construction to initial data readiness. This is not
  build/install/OS activation time. 48 native containers were realized.
- The isolated warm adapter returned the overview and first page in 221 ms.
- Initial/rebuilt catalog preparation is slower; subsequent launches reuse it.

The folder-picker import and physical keyboard/high-contrast interaction paths
are wired but were not exercised against new live sources in this verification.
Snapshots do not replace owner visual acceptance of the native app.

`./scripts/windows.ps1 verify` opts into app-rendered local snapshots while
running the Debug app. These can contain personal photos; never publish the
ignored verification directory.

## Branding and architecture

The app includes verified runtime assets and `Assets/brand-provenance.json`.
`scripts/prepare-windows-assets.ps1` reimports them from the full locally supplied
`Brand_Images/NektronMoments_Complete_Brand_Package_v1_3` package. The complete
design archives and sibling screenshots are not required to build or run.

The temporary CLI adapter is isolated under `Services/`; native controls, media
viewing and thumbnail generation stay on Windows. The previous WPF sibling apps
provided visual references; their executables/control code are not embedded.

The MSIX package identity remains a development identity. The signed Inno installer
has its own permanent product identity and self-contained distribution profile.
