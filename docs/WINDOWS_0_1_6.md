# Windows 0.1.6 — discovery-paced pixel scrolling

The owner reported that scrolling was too fast for discovering photos and asked
for pixel-based movement, referencing DeepTrading's Data Search table. Completed
installers now also belong directly under `installer/`.

## Implementation

Read-only reference: DeepTrading Windows `DataSearchWorkbenchView.xaml` uses
`FastTableSurface`, whose `QueueAnimatedScroll` and frame-based easing maintain
continuous offsets. Its table-specific wheel distances (at least 408 DIP) are
not appropriate defaults for browsing photos and were not copied.

Moments keeps the native virtualized GridView and its ScrollViewer, with a mouse
wheel handler on the inner ItemsPresenter so it runs before native row/page wheel
handling. Transparent panel hit testing includes gaps between thumbnails.

- A wheel notch moves 48 device-independent pixels; fractional deltas are retained.
- One time-based rendering loop eases toward a pixel target, capped at 720 DIP/s.
- Pending travel is limited to 144 DIP. Reversing direction discards the old target.
- Native row snap points are disabled. ChangeView is applied without an additional
  native animation, avoiding competing or accumulating wheel animations.
- Mouse/keyboard presses and direct manipulation cancel queued wheel motion;
  resize, thumbnail-size changes, view changes and shutdown also cancel it.
- Touch, pen, horizontal wheel and modifier gestures are not intercepted. Memory
  buffers, thumbnail look-ahead and virtualization are unchanged.

## Verification

The pure motion policy is linked into the native test executable: 88 combined
viewing/cache/scroll assertions passed. Cases include refresh-rate independence,
fractional deltas, bounded bursts, immediate reversal, cancellation, boundaries,
stalled frames and input routing eligibility.

The opt-in release probe runs the real GridView and controller with 100,000
synthetic entries, without connecting to WSL or reading any user photos. It
measures both the ScrollViewer offset and the on-canvas position of a visible
tile, rather than assuming scroll units equal pixels.

The diagnostic run moved exactly **48 canvas pixels** at Small, Medium, Large
and X-large presets, with 20–27 intermediate offset observations per notch.
A quarter-notch moved 12 pixels; a burst stayed within 144; reversing changed
the target immediately. Cancellation did not pull back a subsequently moved
viewport. Only 147 containers were realized in the 100,000-item boundary check.
These are controller/native-layout regressions, not a physical mouse or
precision-touchpad hardware replay, nor a universal frame-rate claim.

The installer pipeline repeats the native scrolling probe for both the published
payload and the installed copy, alongside artwork and signature verification.

Final release validation passed: 414 Python tests, 88 native policy assertions,
739 asset-file comparisons, 656 SVG loads and 38 rendered icon checks. The installed
100,000-entry gallery moved 48 canvas pixels at each preset, with 25–27 intermediate
offset observations. The burst ended at 644 from a 500 start; reversal moved
529.58 to 481.58. It retained 161 containers at the boundary check. Install, native
launch, scrolling/artwork verification and uninstall passed; the installer has a
valid timestamped Nektron, Inc. signature. Exact hash and size are in the release record.

## Installer output

New releases: `installer/NektronMoments.Setup.<version>.exe` and checksum/JSON
sidecars. Those binaries are ignored by Git. Historical releases in
`artifacts/releases/installers/` are retained unchanged; the builder checks both
locations before allowing a version to be published.

No production deployment, database changes or paid enrichment are included.
