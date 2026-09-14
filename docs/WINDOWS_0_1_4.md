# Windows 0.1.4 — installed artwork and ribbon refinement

The final user-facing update is cumulative **0.1.5**, including the tighter
Nektron Write spacing requested while 0.1.4 was being finalized.

The owner reported blank custom icons in the installed 0.1.3 application, then
requested a smaller top ribbon. Both reports are addressed together.

## Cause and change

The unpackaged publish contained `NektronMoments.pri` but only two loose ICO files.
The PRI indexed paths to SVG and PNG files; it did not contain their image bytes.
Packaged Debug deployment had those files, which is why the earlier development
screenshots passed while the installed product displayed blank artwork.

`CopyPublishedAssets` now publishes the entire application Assets tree, preserving
its paths and approved artwork. No logos were redrawn. The top ribbon uses
24-DIP icons and 48-DIP command height; the viewer remains 32/56 DIP.

## Release verification

- `Test-PublishedAssets.ps1` compares every asset with its source by SHA-256.
- The actual Release executable supports an explicit, environment-scoped artwork
  check before opening any library connection. It loads all shipped SVGs through
  their `ms-appx` URIs, renders the real shell's icon controls, checks painted
  pixels and command dimensions, and saves Light/Dark snapshots.
- The installer canary repeats that check on the **installed payload outside the
  checkout**, rather than relying on the existence of a responsive window.
- With an existing installation present, the same installer can use a separate
  canary AppId, only in silent mode and a dedicated temporary directory. It does
  not touch user shortcuts or reconnect preferences; the test verifies the live
  installation registration is unchanged and uninstalls only its own temporary app.

Observed before signing: all **739** assets matched, all **656** SVG URIs opened,
and **38** actual icon controls painted pixels across Light/Dark. The 28 command
measurements (14 per theme) confirmed 24/48 DIP ribbon and 32/56 DIP viewer sizing,
allowing one DIP for display-scale rounding.

Negative checks also passed: the old 0.1.3 payload was rejected for missing assets;
temporarily removing `source-add.svg` from a private diagnostic payload caused the
render check to report the blank ribbon icon. That test asset was restored.

## Nektron Write spacing follow-up (0.1.5)

Read-only reference: `NektronAI Products/NektronWrite/src/NektronWrite/` —
`Themes/Foundation.xaml` uses 24-DIP toolbar icons; `Themes/Controls.xaml`
defines 40-DIP minimum ribbon buttons; `MainWindow.xaml` uses a 48-DIP toolbar.
No files in Nektron Write were changed.

Moments now matches that horizontal rhythm through a scoped native AppBarButton
style: icon-only buttons measure 39.85 DIP after display rounding, down from
67.92 DIP, while the two labeled actions still size to their text. The extra
vertical ribbon padding is removed. The real release check also resizes the app,
opens overflow, and confirms overflow buttons expand for their labels (158.49 DIP
in the sample); compact sizing does not truncate the menu.

The theme button remains a two-state toggle: Sun indicates current Light mode,
Moon indicates current Dark mode. Each click switches to the other state and
saves the preference; the tooltip describes the destination. Both SVG states
were checked for actual painted pixels in the release and installed shell.

Final 0.1.5 validation: 414 Python tests, 36 native policy assertions, 739 matching
asset files, 656 opened SVGs, 38 painted icon controls, 28 dimension checks and
compact overflow passed. The signed install / native launch / artwork / uninstall
canary passed on the fresh-install path. The side-by-side guard remains conditional
and was not exercised on this host because no existing uninstall registration
was visible. The user's running 0.1.3 EXE remained byte-identical to its original.
Installer signature is valid, timestamped, and issued to Nektron, Inc.; exact
SHA-256 and size are recorded in `docs/releases/0.1.5.json`.

Private build evidence remains under ignored `build/installer/0.1.4/`. Diagnostic
artwork snapshots contain no user photos. No paid enrichment, cloud deployment,
database migration, or change to the user's installed app is part of these checks.

Platform references: [SVG asynchronous loading events](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.media.imaging.svgimagesource)
and [Inno Setup AppId isolation](https://jrsoftware.org/ishelp/topic_setup_appid.htm).
