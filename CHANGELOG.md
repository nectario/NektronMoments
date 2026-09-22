# Nektron Moments Windows releases

## [0.1.36] - 2026-09-22

- Enable selection in pricing, cost assumptions, operations and activity tables;
  support Ctrl+C to copy selected rows, including complete log messages.
- Preserve source/log selections during live updates while rows remain in the
  bounded history. Selecting a log entry pauses Follow latest for inspection.
  Pricing selection does not change the model used by the running job.
- Reduce upper-panel typography, with the beige price reduced from 46 to 36.
  Keep the main heading and lower operations/activity/progress typography intact.

## [0.1.35] - 2026-09-22

- Reduce the default processing window from 1248 × 1240 to 998 × 992,
  approximately 20% smaller in each dimension, without shrinking fonts.
- Compact panel padding, table rows and spacing; put Catch up beside the AI
  heading and the description allowance beside the estimate. Keep all sections
  visible at the default size, with internal activity-log scrolling and fixed
  footer actions. Smaller manual sizes retain accessible scrolling/reflow.
- Add native checks for complete setup/running visibility and unchanged heading
  typography. Processing execution and AI settings are unchanged.

## [0.1.34] - 2026-09-22

- Index desktop catalog hashes before merging saved descriptions, avoiding
  thousands of full-library scans at startup. Existing caches upgrade in place.
- Process metadata reconnects a missing library, shows connection feedback and
  opens setup on recovery. Repeated clicks share the pending reconnect; failures
  explain that no processing started. No AI calls are made by reconnection.

## [0.1.33] - 2026-09-22

- Restyle the modeless processing window from the supplied reference: Full badge,
  readable locked options, selected-model pricing table, warm cost estimate card,
  source operations, activity log, phase progress and persistent footer actions.
- Keep pricing derived from the existing dated model rates and selected allowance.
  Stack cards and options at narrow widths; short windows scroll without hiding
  Hide/Stop actions. Preserve setup, cancellation, background work and 64 AI workers.
- Add native reference screenshots and checks for selected cost and locked options.

## [0.1.32] - 2026-09-22

- Auto-tune direct BYOK AI concurrency up to 64 on machines with at least 32
  logical CPUs and 32 GiB RAM; smaller machines use lower defaults. CLI overrides:
  `byok --workers 64` and `sync --byok --with-enrichment --ai-workers 64`.
- Replace the 64-photo completion barrier with a bounded rolling pipeline.
  Preview decoding is capped at eight; backend access and token refresh remain
  serialized, while AI calls overlap. Persist each result before synchronization.
- Show measured AI descriptions/second and estimated remaining phase time.
  Keep the selected model, budget reservation, provider-limit pause, and safe
  resume behavior unchanged. Existing runs retain their original worker count.

## [0.1.31] - 2026-09-21

- Hide the top indeterminate line during processing, including library refreshes
  within a running job. Ordinary non-processing library loading is unchanged.
- Automatic startup processing opens the same detailed modeless progress window
  as manual processing. Hide/close does not stop the job; Activity reopens it.
- Keep the ribbon command available as View progress during a run, reopening the
  same job without duplicate processing, then restore Process metadata afterward.

## [0.1.30] - 2026-09-21

- Make native drag-animation verification continuously observe actual transforms
  and wait for bounded completion instead of sleeping through compositor motion.
  Preserve intermediate-position, retargeting and cleanup assertions, with
  lifecycle traces for diagnosing genuine failures. Production animation timing
  and scrolling behavior are unchanged.
- Record focus-interrupted wheel trials separately and require a complete,
  uninterrupted trial at every speed. Retries are bounded and repeated external
  interruption fails verification; monotonicity and endpoint checks remain intact.

- Personal BYOK: previews are prepared on the source device and sent directly to
  OpenAI with the existing local key, using four concurrent Standard-tier calls.
  No S3 preview staging or managed AI worker is involved. Legacy queued work is
  left alone. Address lookup remains a separate existing backend service.
- Durable device claims and a local result journal make backend synchronization
  independently resumable. Interrupted calls with unknown outcomes require review
  rather than automatic repeat billing. Add `byok` CLI catch-up without rescanning.
- Full Windows processing selects BYOK, displays its progress and reads completed
  local descriptions even while backend synchronization is pending. Personal
  usage has a conservative device-local monthly US$230 guard, separate from the
  existing managed-service budget. NektronAI-managed gateway is future work.

## [0.1.29] - 2026-09-21

- Replace dense Settings pricing prose with a native three-column model/rate
  table and a prominent, live per-source run estimate. Keep assumptions and
  exclusions separate, visible, and wrapped. Stack the estimate below the table
  at narrower widths; retain theme-aware text and accessible rate labels.
- Prices, spending controls and estimate arithmetic are unchanged. Correct
  obsolete startup text that described 64 as a server run ceiling.

## [0.1.28] - 2026-09-21

- Separate the user-selected enrichment run allowance (1–1,000,000 per source)
  from the internal 64-asset server request size. Add a 10,000-photo catch-up
  shortcut in Settings and the processing window, with full-run cost estimates.
- Page through older pending photos, persist each page before preview staging,
  and stop paging on quota deferral. Preserve existing saved preferences and
  server spending controls; installing does not automatically increase spending.
- Subscription entitlements remain a separate future server-side policy, not
  the technical request batch size. No paid enrichment is run by release tests.

## [0.1.27] - 2026-09-21

- Add an idle, foreground-prioritized display-ready cache: up to 1,000 nearby
  thumbnails on workstation-class machines, 400 on enhanced machines and 160 on
  balanced machines, further bounded by the existing image-byte budget.
- Keep deeper lookahead compressed instead of allocating native bitmaps for all
  8,192 lookahead positions. This reduces startup native-memory/GC pressure.
- Overlap up to four asynchronous image uploads while retaining one upload start
  per input frame. Cancel stale warming plans and pause optional uploads during
  scrolling; background work never increases the scrollbar's browsing range.
- Retain original 200/500 browsing ranges, direct native scrollbar ownership,
  captions, and explicit reorder/resize animations. Larger control caches,
  plain pixel-buffer storage and caption-phase experiments were rejected.

## [0.1.26] - 2026-09-21

- Replace modal Settings with a full in-app workspace: AI & processing,
  Appearance, and Browsing. Keep model rates and the current estimate pinned
  above all sections; preserve the library view when returning.
- Put AI inclusion, model, limit, pricing and Start into the existing resizable
  processing window. The same window transitions to progress and remains
  available through Activity; canceling before Start launches no work.
- Add Reset Order for the current library/source view: clear saved custom keys
  and locator order, restore newest first, and leave original media unchanged.
- Request the compositor high-refresh mode during native scrollbar drags where
  supported, releasing it on completion/cancel/deactivation. Keep direct native
  scrolling and publish fewer thumbnail updates per render frame during input.
  This is not a guarantee of 120 GPU-present FPS.

## [0.1.25] - 2026-09-21

- Repair repeated Settings opening; add Edit → Settings and separate About/help.
- Process metadata opens a Start/Cancel dialog with AI enrichment selected by
  default. Unchecking it runs file metadata only; Cancel does no work.
- Settings remembers the per-source AI limit (1–64, default 64) and scene model
  (Terra by default; Luna and Sol alternatives). Manual and Full startup runs
  snapshot these settings; already queued/completed descriptions are not changed.
- Show a live illustrative USD estimate in Settings and before Start, including
  token assumptions, pricing date and excluded services. Existing budgets remain.
- Exclude description-identified text website captures with Hide screenshots,
  while preserving unclassified media and physical photographs containing text.

## [0.1.24] - 2026-09-21

- Replace the startup toggle with Full — metadata + AI, File metadata only, and
  Off. Preserve existing enabled/disabled preferences without opting users into
  paid work. Changes apply on the next launch, not to an already running job.
- Full includes new photos and older pending photos via the existing bounded
  enrichment workflow (up to 64 eligible assets per source per launch). Completed
  results are reused; service quotas remain enforced and API costs are disclosed.
- Report AI/address preparation and scene-preview staging in processing activity;
  distinguish a finished local pass from server-side work still queued or deferred.
- Validate Full using controlled jobs only. Diagnostics and installer checks
  continue to suppress automatic processing; no paid validation run is performed.

## [0.1.23] - 2026-09-21

- Add a persistent Hide screenshots checkbox based on existing description text;
  preserve unknown photos and videos, and never delete originals.
- Cache account-visible screenshot matches using paginated read-only metadata
  searches in a separate background connection. Keep cached classifications and
  descriptions across local catalog rebuilds; preserve the old index on failure.
- Automatically run incremental metadata processing for registered local sources
  after startup, without opening a progress window. Activity restores progress;
  Library > Process new photos on startup controls the persistent preference.
- Keep paid enrichment opt-in and suppress real processing during installer and
  diagnostic checks. Preserve the complete custom order while filtering by
  disabling rearrangement of the filtered subset.

## [0.1.22] - 2026-09-20

- Replace ineffective collection-only drag transitions with explicit 320 ms eased
  movement of visible thumbnails. Retarget from their displayed positions, keep
  the viewport steady, and clear transformations on completion and recycling.
- Suppress nonessential gallery housekeeping during scrollbar dragging, retaining
  the original image-publication throughput. Real-photo measurements rejected
  reduced-priority/upload throttling and GC tuning; those experiments are not shipped.
- Materialize decoded pixel data on workers and create one owned SoftwareBitmap,
  avoiding the temporary bitmap/copy pair identified in real-photo GC traces.
- Add intermediate-position, rapid-reversal and cleanup checks, plus an opt-in
  real-library diagnostic at three thumbnail sizes. Tests preserve saved order
  and originals. Automated timing is not physical pointer replay or display FPS.

## [0.1.21] - 2026-09-20

- Restore coordinated thumbnail reflow without row-by-row staggering. Keep smooth
  compositor motion during resizing and preview drag rearrangements live across rows.
- Commit custom order only on a successful in-gallery drop; cancel restores the
  previous order. Originals remain unchanged.
- Replace the compact modal progress dialog with a resizable, modeless processing
  window: source operations, bounded activity log, overall source completion,
  current-phase progress, throughput and estimated phase time remaining.
- Hide or close the processing window without stopping the job; Activity reopens
  its current state. Keep Stop explicit and preserve the library's usability.
- Verify window resizing, hide/close/reopen, success/failure/cancellation and live
  reorder rollback with isolated fixtures. No paid enrichment or cloud deployment.

## [0.1.20] - 2026-09-20

- Fix the viewer X's clipped glyph with explicit content padding and icon scaling.
- Keep drag-reorder motion but remove staggered gallery reflow from ordinary
  resizing. Shorten live resize movement/size animation to 40/30 milliseconds.
- Explicitly detach and incrementally release all owned resize composition
  resources; stop pending work on shutdown and simplify each thumbnail's placeholder.
- Move preference writes, bridge startup and folder-launch work off the UI thread;
  reuse unchanged viewer icons and serialize duplicate image-decode requests.
- Cancel obsolete catalog/search loads, coalesce repeated refreshes and keep
  unchanged compact view snapshots in a bounded memory cache. Refresh invalidates
  view snapshots; metadata refresh preserves thumbnails with unchanged file keys.
- Preserve rapid next/previous intent and explicitly release video media sources.
- Add mixed-operation latency, request-race and silent-video checks to release
  verification. These diagnostics are not physical mouse replay or display FPS.

## [0.1.19] - 2026-09-19

- Adapt initial browsing and subsequent batches from 200 positions at medium/large
  sizes to 500 for small thumbnails, keeping image preparation independent and
  preserving existing ranges and anchors when size changes.
- Enable native animated thumbnail drag reordering, with locally saved custom
  arrangements, date-sort restoration, stable viewer positions, and keyboard
  reordering using Ctrl+Shift+Arrow. Original files are never moved or renamed.
- Keep pending photos in their chosen positions when metadata processing adds
  content hashes; isolate arrangements by account and library/source/type view.
- Add a dedicated accessible close X at the upper-right of the photo/video viewer.
- Show a hideable metadata-progress dialog with actual phase counts, percentage,
  source, elapsed time, and Stop. Activity reopens the same current or last job.
- Coalesce worker output into small UI updates; discovery remains indeterminate
  until a total is known. No new paid enrichment or cloud deployment is included.

## [0.1.18] - 2026-09-19

- Stream the complete local catalog in bounded frames into a compact UTF-8 index,
  avoiding the single large JSON line and eagerly retained per-photo object graph.
- Materialize browsing rows on workers ahead of publication; keep the initial
  200-photo native range independent of the full catalog and large thumbnail cache.
- Use snapshot identities for constant-time viewer selection and preserve original
  ordering, duplicate fallback paths, filters, and viewer navigation.
- Cache thumbnail identity keys and stop viewport callbacks for ready tiles;
  safely re-arm on recycling, resolution changes, and reload.
- Keep native scrollbar ownership and existing wheel behavior unchanged. Retain
  local opt-in frame/GC diagnostics for repeatable before/after investigation.

## [0.1.17] - 2026-09-19

- Use the branded list-and-clock icon for Processing activity, with descriptive
  tooltip and accessibility help text, distinct from the sun/moon theme toggle.
- Decode and resize thumbnail pixels on background workers. Prefetch no longer
  creates XAML image sources or posts one UI callback per prepared preview.
- Create presentation sources only for near-viewport tiles, in small paced UI
  batches. Reuse sources for revisits and skip recycled/offscreen requests.
- Coalesce cache-counter updates and avoid header text changes during dragging.
- Pause optional warming during input, let visible jobs bypass that pause, and
  defer optional UI buffer expansion until idle. Compact repeated catalog objects.
- Add worker-thread/no-offscreen-XAML assertions and startup-scrubbing queue
  latency instrumentation; these checks are not physical mouse replay or FPS.

## [0.1.16] - 2026-09-17

- Start the native gallery at 200 photos and extend in 200-photo increments,
  preserving the visible photo and deferring range changes while the thumb is held.
- Keep the complete catalog and thumbnail preparation independent of that range;
  full-catalog viewer/slideshow navigation still returns to the correct photo.
- Prepare display-ready bitmaps without creating gallery controls, share in-flight
  work, prioritize visible/next photos, and retain completed work after tile recycling.
- Use a 12 GiB decoded-preview cache on workstations with at least 96 GiB RAM,
  with resolution-aware, direction-prioritized look-ahead and lower-memory profiles.
- Verify generated photos are ready beyond the exposed range and reused by native
  tiles on reveal, plus append anchors, resets, final-item access and viewer return.

## [0.1.15] - 2026-09-17

- Restore the GridView's own native scrollbar, as used in 0.1.1. Remove the
  external proxy, drag-glide/fine-drag controls, and custom thumb controller.
- Keep wheel smoothing and speed settings independent. Native bar input takes
  ownership without a queued wheel freeze overwriting its destination.
- Preserve the full catalog, thumbnail caching, thicker scrollbar, canvas viewer,
  and live resizing. Unlike 0.1.1, the current range is not a growing 120-item page.
  This distinction is recorded explicitly for the user's physical comparison.
- Verify native range/geometry on 120- and 100,000-item fixtures, normal/maximized
  layouts, wheel trains, cancellation handoff, resizing, and last-item access.
  Automated checks do not establish physical dragging feel or display FPS.

## [0.1.14] - 2026-09-16

- Restore ordinary proportional thumb dragging so the handle visibly follows the
  pointer; move the slower six-DIP precision mode to Shift-drag.
- Animate mouse-wheel movement natively, accumulating fractional and rapid input
  without the old UI-frame loop, three-notch truncation or fixed 720-DIP/s ceiling.
- Keep the scrollbar position updating during wheel motion rather than waiting
  for the whole animation to finish. Preserve wheel/drag ownership and reversal.
- Coalesce stops, immediate navigation and new scrolling targets through one
  request channel so an old freeze cannot override a newer position.
- Verify actual template thumb travel and continuous slow/fast wheel input trains,
  in addition to endpoints, cancellation and full-catalog bounds.

## [0.1.13] - 2026-09-16

- Replace moving 100-item ranges with one continuous, full-library scrollbar.
  Remove range arrows, thumb recentering and stationary edge-hold auto-scrolling.
- Scale ordinary thumb movement by pointer pixels, independently of library size;
  expose drag sensitivity separately from wheel speed. Shift-start dragging retains
  fast proportional positioning across the entire library.
- Load a complete lightweight local catalog with two SQL queries and publish it
  atomically, keeping the scrollbar extent stable rather than growing in batches.
- Preserve bounded native virtualization and image caches; fetch full metadata when
  details are opened. No original uploads, cloud provisioning or paid enrichment.
- Verify the actual precision gesture processor/template correction, no stationary
  drift or release jump, full-range bounds, cancellation and normal/maximized views.

## [0.1.12] - 2026-09-15

- Use Windows' native animated scrolling for scrollbar movement, with coalesced
  latest-target requests and no UI-thread frame-by-frame repositioning for the thumb.
- Replace the custom scrollbar timing slider with an honest Smooth scrollbar toggle;
  mouse-wheel speed remains separately adjustable and saved off settings stay off.
- Avoid redundant native viewport-cache assignments during steady scrolling.
- Advance thumbnail prefetch windows in meaningful increments instead of cancelling
  and restarting look-ahead for every small forward movement.
- Add opt-in fully warmed photo-scroll measurements to separate loading work
  from steady-state movement before comparing rendering approaches.

## [0.1.11] - 2026-09-15

- Preserve scrollbar position and velocity across dense thumb events with a
  frame-rate-independent damped follower rather than restarting an easing curve.
- Continue smoothly through fine-control ranges while the thumb is held at an edge;
  recenter the scrollbar on release without moving the photo canvas backwards.
- Indicate that more moments exist below and request another page at the loaded edge.
- Increase the default wheel movement from 48 to 64 pixels; retain saved custom speeds.
- Bound live offscreen XAML controls independently of the large photo/metadata caches,
  prioritize visible thumbnail decodes, and coalesce gallery look-ahead work.
- Test edge continuation, release, asynchronous paging, and restored/maximized scrolling.

## [0.1.10] - 2026-09-14

- Bound scrollbar travel to about 100 row-aligned items, with previous/next range
  controls. Keep the full workstation buffer and continuous pixel-based wheel motion.
- Keep the range fixed during thumb dragging and rebase after wheel motion settles.
- Share native column geometry across scrollbar ranges and live thumbnail reflow.
- Keep single-click selection from opening the details pane and rearranging tiles
  before the second click; open details explicitly from the existing Details command.
- Open the exact double-clicked thumbnail, and ignore gutters or removed items.
- Prevent late detail/file lookups from replacing the newer selected photo, and show
  explicit feedback when an original is missing or cannot be decoded.
- Gate published and installed builds on compact-tile selection and image-loading
  race checks, plus native bounded-scrollbar regressions.

## [0.1.9] - 2026-09-14

- Enlarge the installer header identity and remove the padded square using the
  approved transparent mark directly.
- Add independent wheel-distance and scrollbar-glide settings under View → Scrolling.
- Smooth native scrollbar dragging toward the latest absolute position, without
  queuing obsolete drag targets or applying wheel sensitivity to thumb seeking.
- Rearrange visible photo surfaces into rows/columns live during thumbnail sizing,
  using compositor surfaces/animations and committing native layout on release.
- Keep only installer executables beside the installer sources; move generated
  checksums/JSON to artifacts/releases/metadata and tracked history to docs/releases.

## [0.1.8] - 2026-09-13

- Keep the wider scrollbar thumb after native indicator updates and thumbnail
  reflow, with a rounded native Thumb template and a minimum layout width.
- Pause metadata appends even when a slider drag starts during a batch update.
- Verify the final, rendered scrollbar dimensions rather than only its initial setting.

## [0.1.7] - 2026-09-13

- Route native wheel input across the gallery, including the scrollbar and gutter,
  before the built-in WinUI scroll path can add a row/page movement.
- Preview thumbnail sizing with compositor transforms while dragging; defer
  reflow, new decoding, metadata appends and preference writes until release.
- Preserve the visible photo anchor during the release reflow and reuse sufficiently
  detailed thumbnails when reducing size.
- Widen the gallery scrollbar to 24 DIP with a 14-DIP thumb; reduce slideshow
  icons to 24 DIP and icon buttons to 40 DIP.
- Expand release checks to cover native routing scope, scrollbar dimensions,
  resize layout churn and measured XAML update/render timing.

## [0.1.6] - 2026-09-13

- Use gentle pixel-based gallery wheel scrolling: 48 DIP per notch, fractional
  wheel support, smooth frame-based easing, bounded travel and immediate reversal.
- Keep touch gestures, scrollbar dragging, keyboard navigation, virtualization
  and the workstation thumbnail buffer independent of mouse-wheel speed.
- Verify actual canvas displacement at every thumbnail preset on a 100,000-item
  virtualized gallery, both before packaging and in the installed app.
- Publish completed installers directly under `installer/`, with checksum sidecars.

## [0.1.5] - 2026-09-13

- Match Nektron Write's compact toolbar rhythm: 24-DIP icons in 40-DIP-wide
  icon buttons, and remove the extra vertical ribbon padding.
- Retain native overflow, labels on the two main library actions, theme behavior,
  and the installed artwork fix from 0.1.4.

## [0.1.4] - 2026-09-13

- Refine the top ribbon to 24-DIP icons and 48-DIP command height; keep the
  larger 32-DIP playback controls in the photo viewer.
- Fix missing installed icons by publishing the complete approved SVG/PNG asset
  tree; PRI resource indexes reference these files rather than embedding them.
- Gate releases on byte-for-byte asset checks and actual Light/Dark icon rendering
  from the unpackaged application and installed payload.

## [0.1.3] - 2026-09-13

- Enlarge the approved v1.3 ribbon and canvas playback icons to 32 DIP, with
  56-DIP-tall command targets, matching the more prominent Nektron Mail look.
- Override the native toolbar's 16-DIP icon cap without replacing native
  keyboard, focus, overflow or theme behavior.

## [0.1.2] - 2026-09-13

- Add an icon-rich v1.3 ribbon and File, Edit, Library, View, Playback and Help menus.
- Show Sun in light mode and Moon in dark mode.
- Process metadata and new-folder imports inside the app, with safe cancellation
  and no automatic paid enrichment.
- Replace the photo dialog with a resizing canvas viewer and an original-pixel cap.
- Add a photo slideshow, playback controls and the shared enlargement checkbox.
- Add remembered thumbnail-size presets and a continuous size slider.
- Adapt buffers to machine memory; add bounded decoded/encoded caches, prioritized
  thumbnail look-ahead, exact-size thumbnail streams and indexed timeline ordering.

## [0.1.1] - 2026-09-13

- Add a Nektron-family Inno Setup installer with the approved Moment Frame identity.
- Bundle the .NET and Windows App SDK runtime for direct EXE launch.
- Reconnect the existing Ubuntu/CLI workspace without copying credentials or media.
- Support per-user preferences in both installed and packaged development builds.

## [0.1.0] - 2026-09-13

- Initial native Windows preview: virtualized library, thumbnails, details,
  indexed search, media viewing, folder registration and light/dark themes.
- Reuse the account-local CLI catalog with exact-hash duplicate handling.
