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

- Existing CLI library/session reuse without a second sign-in or re-import.
- Virtualized, incrementally paged photo/video gallery and source filters.
- Memory-adaptive metadata and thumbnail look-ahead; separate foreground and
  prefetch workers, bounded decoded/encoded LRU caches and exact-size thumbnails.
- Filename filtering as you type; Enter searches indexed descriptions/addresses.
- Exact-hash deduplication with preserved source occurrences.
- Double-click opens the selected photo/video in the entire gallery canvas.
  Images fit on resize and never exceed original physical pixels unless the
  enlargement checkbox is checked. EXIF orientation is respected.
- Slideshow with pause/stop and 3/5/8/10-second intervals; it uses the same
  enlargement preference and skips videos. Missing/unsupported media stops with
  a readable message instead of silently skipping errors.
- Add folder performs fast discovery followed by full metadata processing in the
  background. The **Process metadata** button runs full sync for the selected
  source, or all local sources when no source filter is active.
- **Stop processing** sends a cooperative interrupt to the CLI job host so saved
  batches survive. No paid scene-description or geocode work is started.
- v1.3 icon-rich ribbon with 24-DIP icons, 40-DIP-wide icon buttons, and
  a compact 48-DIP row matching Nektron Write. Includes
  File/Edit/Library/View/Playback/Help menus. Sun denotes
  light mode; Moon denotes dark mode.
- Remembered thumbnail slider and Small/Medium/Large/X-large presets.
- Ctrl+F search, F5 refresh, Esc return, Left/Right navigation, Space slideshow
  pause/resume, F11 fullscreen, and clipboard/reveal-original commands.

### Adaptive budgets in 0.1.2

Mouse-wheel movement is independent of these budgets and of thumbnail size.
View → Scrolling adjusts wheel pixels per notch (48 by default) separately from
scrollbar glide time (120 ms by default). Thumb dragging retains the full-library
position range and eases toward the latest target without queueing old positions.
Thumbnail resizing now rearranges rows/columns during the gesture, with coalesced
layout, short reposition animations and a bounded set of realized controls.

These are ceilings and look-ahead targets, not upfront allocations:

| Profile | RAM | Metadata buffer | Thumbnails ahead | Encoded / decoded cache | Disk cache |
| --- | --- | ---: | ---: | --- | --- |
| Balanced | Below 24 GiB | 2,048 | 96 | 64 / 192 MiB | 1 GiB |
| Enhanced | 24–95 GiB | 8,192 | 512 | 256 / 768 MiB | 4 GiB |
| Workstation | 96 GiB+ | 24,576 | 1,536 | 768 MiB / 2 GiB | 8 GiB |

The grid stays virtualized. Background appends yield in small chunks and never
reset the existing collection. Catalog indexes match timeline ordering. Old
oversized derived thumbnail cache entries are replaced; original files are untouched.

Native sizing/cache tests: from `apps/windows`, run
`dotnet run --project Tests/ViewingTests.csproj -c Release`. They run automatically in the signed
installer pipeline. Current app-rendered checks live in ignored
`build/windows-verification-013/` (previous 0.1.2 evidence remains in `windows-verification-012`).

Processing metadata reads **available file metadata**; it cannot invent missing
EXIF, descriptions or addresses. Existing indexed descriptions/addresses are
retrieved separately. Paid enrichment remains a separate controlled workflow.

## Deliberate preview boundary

This is a real native UI with an installer, **not yet a fully standalone app**. Ubuntu WSL,
the repository's Python environment, and an existing signed-in CLI account are
required. A stdio-only adapter reuses that account without copying passwords or
tokens into Windows and without listening on a local HTTP port.

The adapter reads the authoritative CLI SQLite database in read-only mode. Its
separate `desktop-library-v1.sqlite3` is a disposable account-local read model,
invalidated when the source database/WAL changes. Native thumbnails stay in the
current Windows user's app-data area. No original file is modified by browsing.
No UI command starts paid enrichment. API requests fetch already indexed metadata.

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
