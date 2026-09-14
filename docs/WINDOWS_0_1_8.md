# Windows interaction preview 0.1.8

Addresses the owner's scrolling video and requests for smoother thumbnail sizing,
a wider scrollbar, and smaller slideshow controls. This is an interaction preview;
the original input gesture and the hard display-FPS requirement remain open.

## Changes

- An app-owned native mouse-wheel bridge now intercepts WM_MOUSEWHEEL before
  WinUI, including coordinates over the gallery gutter and scrollbar. It only
  subclasses windows on this app's UI thread, checks the active gallery bounds,
  leaves modifier gestures alone, and removes its hooks on shutdown. The original
  content-only handler did not cover wheel input over the scrollbar.
- A slider drag previews size using a compositor scale transform on existing
  thumbnails. It does not change grid dimensions or request new decodes per value.
  Releasing commits one reflow, preserves the visible anchor, then restores the
  larger look-ahead buffer after idle. Metadata appends wait while a drag is active.
- Smaller sizes reuse already-displayed higher-resolution thumbnails. Requested
  decode size is captured so an asynchronous result cannot be cached under the
  wrong resolution key.
- The gallery scrollbar has a 24-DIP target, 14-DIP minimum native Thumb width,
  and a rounded fill without the default inset stroke. The compact panning
  indicator is at least 12 DIP wide. Native dragging and scrollbar semantics remain.
- Slideshow controls now use the same 24-DIP icons / 40-DIP button widths as the
  top ribbon, with a 48-DIP command height.

## Evidence and limits

Reviewed the supplied `2026-09-13_21-56-45.mp4` (10.286 seconds, 60-fps recording).
The scrollbar is highlighted and the view jumps across large groups. The recording
does not establish whether this is thumb dragging or wheel input over the bar.
The owner was asked to identify the gesture; no answer was available during this work.
**Dragging the thumb still navigates the full loaded range.** The wheel fix must
not be presented as proof that this separate drag behavior was corrected.

Real-photo resize instrumentation, with 24,696 loaded records:

| Run | UI update cadence | Worst update gap | Maximum XAML frame cost | Grid dimension changes during drag |
| --- | ---: | ---: | ---: | ---: |
| Initial | 114.82 Hz | 21.97 ms | 8.06 ms | 0 |
| Profiler-coordinated | 113.75 Hz | 17.87 ms | 8.39 ms | 0 |

The second run's many-column release reflow took 16.15 ms. A synthetic 100,000-item
run also had a 28.55-ms update gap and a 22.50-ms XAML frame cost. Thus typical
timing improved, but a strict minimum of 50 displayed FPS is **not certified**.
These counters measure XAML/update work, not monitor presentation. A scoped run
of the existing NVIDIA PresentMon utility produced no usable CSV; no replacement
tool was installed and no existing profiling session was stopped.

Release checks verify routed coordinates over photos/the scrollbar, native hook
registration, 48-pixel wheel displacement, virtualization, final scrollbar width,
zero grid dimension changes during resize, and typical update timing. They do
not inject physical mouse input. Computer Use's required node_repl/sky tools were
unavailable, so no alternative OS input automation was used.

The 0.1.7 candidate was retained unchanged after publishing; a later check found
WinUI could restore Thumb.Width to 8 during indicator updates. 0.1.8 constrains
the actual minimum width and checks ActualWidth after reflow/rendering instead.

Final installed 0.1.8 check: 100,000 synthetic entries, five native input windows
registered, 23.55-DIP scrollbar and 13.58-DIP rendered thumb after display rounding.
Resize produced zero grid-width mutations during dragging; the measured update
gap p95 was 9.64 ms, worst gap 22.13 ms, maximum XAML frame cost 21.39 ms and
release commit 9.33 ms. The hard 50 displayed-FPS minimum remains unverified.
414 Python tests, 88 native policy assertions, 739 asset comparisons, 38 rendered
icon checks, and signed install/launch/uninstall validation passed.

No user photos, original video, credentials, production database, cloud deployment
or paid enrichment were changed. Builds and measurements remain local. Installers
are written directly to `installer/` as requested.
