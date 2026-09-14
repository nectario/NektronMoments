# Nektron Moments Windows releases

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
