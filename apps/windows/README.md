# Nektron Moments for Windows — first native preview

The packaged C#/WinUI app lives in `NektronMoments/` and uses the approved brand
v1.3. Windows is a first-class client alongside the planned iOS and Android apps.

## Install or run

The traditional Windows installer is built with `./scripts/build-installer.ps1`.
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
- Native thumbnails with eight concurrent workers and a 512 MiB disk-cache limit.
- Filename filtering as you type; Enter searches indexed descriptions/addresses.
- Exact-hash deduplication with preserved source occurrences.
- Details, embedded image/video viewing, open original, and reveal in Explorer.
- Add folder delegates to existing Local-mode CLI registration and metadata-only
  fast sync. Normal CLI sync is still needed for newly added files' full hashing/EXIF.
- Saved queue activity, remembered light/dark theme, compact layouts, Ctrl+F/F5/Esc.

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
