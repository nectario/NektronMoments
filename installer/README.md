# Nektron Moments installer

The Windows preview uses a Nektron-family Inno Setup wizard: pearl-light surfaces,
the approved Moment Frame icon, restrained cyan/blue atmosphere, native controls,
and blue installation/uninstallation progress. The canvas layout and bounded
foreground behavior are adapted from the existing Nektron Mail installer.

## Build

From Windows PowerShell in the repository root:

```powershell
./scripts/build-installer.ps1
```

The default is a **signed release**, using the existing Nektron Azure Artifact
Signing profile. No private key, certificate password, Azure token, OpenAI key,
or user credential is copied into this repository or into the installer.
`signing-profile.json` contains only the existing endpoint/account/profile names.

The build runs the full repository regression suite and native viewing/cache
policy assertions, publishes a self-contained
untrimmed x64 application, checks every published asset byte-for-byte and verifies
actual Light/Dark icon pixels in the release shell, checks private-data exclusions, signs
the app EXE/DLL, verifies direct launch, compiles/signs the setup and uninstaller,
then tests a temporary install outside the checkout. It checks workspace reconnect,
native launch, installed artwork rendering, signatures and uninstall before publishing the release.

Output: `installer/NektronMoments.Setup.<version>.exe` only.
Generated JSON and SHA-256 records are stored separately in `artifacts/releases/metadata/`;
tracked release summaries live in `docs/releases/`. Completed versions are immutable; increment the
Windows project version, Inno fallback and changelog for each new installer.
Failed attempts remain under ignored `build/installer/` and may be retried.
Older completed installers remain in `artifacts/releases/installers/`; both
locations are checked to prevent reusing an already-published version.
`-DiagnosticUnsigned` produces a diagnostic under `build/`, never a release.

## Installation behavior

- Permanent, Moments-only AppId: `{7A13CB58-5B03-4B35-A2F4-7BC06610ED38}`.
- Installs for the current Windows user under
  `%LOCALAPPDATA%\Programs\Nektron Moments`; no administrator prompt.
- Includes both the .NET and Windows App SDK runtime. No Visual Studio or SDK
  installation is required to run the installed app.
- Creates Start-menu entries; a desktop shortcut is optional.
- Does not register photo/video file associations, services, tasks or firewall rules.
- Preserves original media, CLI state, credentials and per-user app preferences.
- Uses the traditional self-contained/unpackaged distribution model. Packaged
  development remains available through `scripts/windows.ps1`.

This is still a **Windows preview using the existing Ubuntu/CLI library adapter**.
The installer detects the existing workspace (previous preference, environment,
or build-workspace hint) and only asks for its location if necessary. It checks
the Python/bridge dependency before installation. It does not create a new photo
library, sign in, upload originals or start paid enrichment.

The non-secret reconnect preference is `HKCU\Software\Nektron\NektronMoments\Workspace`.
If the workspace moves, rerun setup with `/WORKSPACE="C:\path\to\NektronMoments"`.
The source folder and its configured Ubuntu Python environment must remain
available until the standalone native backend connection replaces this adapter.

## Verification and provenance

- `Test-Installer.ps1` never installs over an existing Moments installation.
  If one exists, `/ASSETCANARY=1` selects a separate test AppId, requires a silent
  install to a dedicated temporary path, and skips user shortcuts/preferences.
  Existing registration and reconnect preferences are checked unchanged. Otherwise
  the original fresh-install test restores any preferences it temporarily touched.
  Only the verified temporary installation is uninstalled; no library data is deleted.
- `Test-PublishedAssets.ps1` verifies all shipped asset files against the source,
  then opens the actual unpackaged EXE in a no-library diagnostic mode. It checks
  SVG URI loading, painted icon pixels, and separate ribbon/viewer sizes. The
  installed copy repeats these checks; a responsive empty window is insufficient.
- `Test-GalleryScrolling.ps1` exercises the real wheel controller and native gallery
  with 100,000 synthetic entries, without opening any user photos. It checks
  canvas movement at all four thumbnail presets, intermediate positions,
  fractional wheel input, reversal, bounded bursts, cancellation and virtualization.
- `Generate-InstallerBranding.ps1` follows the sibling installer layout and draws
  the provided marks without redrawing them. The header uses the approved transparent
  mark directly, filling its image area without a padded light square.
- `shared/WindowFocus.iss` reuses the sibling's bounded foreground pulse. The
  wizard is not permanently topmost.
- The installed app, app DLL, setup and uninstaller are timestamp-signed by
  **Nektron, Inc.**; unsigned builds are not release artifacts.

Deployment basis: [Microsoft self-contained Windows App SDK deployment](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps)
and [traditional unpackaged WinUI distribution](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/unpackage-winui-app).
