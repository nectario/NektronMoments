# Windows 0.1.9 — independent scrolling and live photo reflow

## User-visible changes

- The installer header uses the approved 128-pixel transparent Moment Frame mark
  directly. It fills the image area instead of sitting within a 240-pixel padded
  canvas. `WizardSmallImageBackColor=none` preserves alpha over the wizard background.
- View → Scrolling provides wheel distance (12–144 DIP per notch, default 48) and
  an independent scrollbar glide duration (0–250 ms, default 120 ms). Preferences
  are remembered. Scrollbar arrow steps remain independent of wheel sensitivity.
- A dedicated native ScrollBar drives the existing virtualized gallery through a
  finite-duration absolute glide. New drag positions replace old targets. The
  thumb retains the normal full-library position mapping; it is not a local-range
  scrubber. The gallery's internal scrollbar is hidden so two handlers cannot
  apply competing positions. Range updates follow the actual native scroll extent.
- During thumbnail sizing, cached photo and caption surfaces visibly resize and
  rearrange into new rows/columns. Compositor interpolation bridges updates;
  captions are cropped without distorting photo aspect ratios. The native grid
  commits on release with the visible photo anchor retained. Only a bounded set
  of realized cards is captured, not the entire library. New decoding, file I/O,
  collection appends and preference writes are deferred through the gesture.
- The executable is the only generated release artifact placed directly under
  `installer/`. Generated JSON/checksums moved to `artifacts/releases/metadata/`;
  tracked version summaries moved to `docs/releases/`. No metadata was deleted.

## Engineering and validation

The initial live native-layout approach worked functionally but measured about
23 UI updates/s with real photos. Removing a duplicate forced layout improved
the synthetic run, but was insufficient for image-rich content. The final approach
therefore uses independent compositor surfaces rather than global zoom or repeated
image-control layout. The real-photo prototype showed rows changing between two
and nine columns, with zero native grid mutations during drag and a maximum XAML
frame cost around 4.2 ms. UI update cadence is not a display-FPS measurement.

The owner clarified that 50 FPS was smoothness guidance, not a hard certification
gate. Timing is retained for engineering comparison; functional release checks
cover live column changes, bounded virtualization, independent wheel speeds,
scrollbar intermediate positions/latest-target wins, pixel wheel movement, native
input scope, theme icons, and installer lifecycle/signatures.

The transparent header was checked against the approved source and has alpha 0
at both opposite corners. Brand artwork was reused rather than redrawn.

Final validation: 414 Python tests, 95 native policy assertions, 739 asset checks,
38 rendered-icon checks, and signed install/launch/uninstall checks passed. The
installed 100,000-item test verified independent 12/144-pixel wheel settings,
scrollbar intermediate movement and latest-target settling, live two-to-nine-column
reflow, zero native grid mutations during drag, and 133 realized native containers
after the test. The exact installer size/hash is recorded in `docs/releases/0.1.9.json`.

Private real-photo validation screenshots remain under ignored `build/`.
No media originals, production database, cloud resources or paid enrichment were changed.

## Shutdown handoff

The owner requested workstation shutdown after completion. Save the final version
record and commit before invoking Windows shutdown. Do not force applications with
unsaved data to close. The request to shut down overrides leaving the UI preview open.
