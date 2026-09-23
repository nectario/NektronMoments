# Processing window — reference QA

## Comparisons and quiet startup — 0.1.39

Selecting a pricing row updates the beige comparison amount and model while
retaining the actual run model, allowance and source count. Native checks cover
Luna ($6.32) and Astra ($306.00) at 10,000 descriptions, unchanged Terra execution,
and comparison retention through progress updates. This is illustrative pricing,
not a switch of provider/model or a spending quote.
The native title bar is unchanged; processing body and actions share a 15-pixel
content inset. Light/dark captures in `build/processing-comparison/check-01/`
were inspected and all 163 native checks passed. Startup-style jobs remain hidden
and the button reopens the same job; manual processing remains visible.

## Read-only AI status clarity — 0.1.38

AI-options heading increased to 20, labels/values to 14, and locked note to 13.
Running Enabled status now uses an 18-unit checkmark with no square; Not included
uses a dash. The actual setup checkbox retains native interaction and appearance.
The 1000 × 923 layout, pricing and other typography are unchanged.
Published light/setup captures were inspected under
`build/installer/0.1.38/14ad7c9783e741688db5503a77c06225/published-browsing/`.
Native checks confirm the borderless glyph and full setup/running visibility.

## Exact size and Astra comparison — 0.1.37

Default window: 1000 × 923. Full badge: 16; elapsed time, Enabled, model value and
asset count: 13. Other typography is unchanged. Astra is a pricing-only comparison
row at $10 input / $50 output per million tokens, verified 2026-09-22 against
https://developers.openai.com/api/docs/models/gpt-6-astra . Execution choices and
the Terra default are deliberately unchanged.

Initial checks found the extra pricing row pushed content below the viewport.
Tighter section gaps and pricing-row padding resolved this without losing activity
rows or shrinking unmarked text. Final evidence is under
`build/installer/0.1.37/341849a58552408f939222d2e810709b/published-browsing/`.
The light screenshot was inspected; native checks verify setup and running states
fit without outer scrolling, the four pricing rows, and each marked font size.

## Selection and targeted typography — 0.1.36

The next user request supersedes the unchanged-font requirement only within the
marked upper region. Its headings are now 18, the subtitle and prominent values
15, and the price 36 (from 46); the main heading and lower sections stay unchanged.
Evidence: `build/processing-selection/check-02/processing-selected-light.png`
and `processing-reference-dark.png` in the same directory, visually inspected.
All four tables expose native selection. UI Automation verifies selection,
source/log selection retention through collection replacement, unchanged running
model when selecting a pricing row, and Follow latest pausing for inspection.
The full selected-row text formatter is checked without changing the user's
clipboard. Ctrl+C is wired to the focused table; it is not a global copy of stale
selections. The first test attempt used a visual-container peer instead of the
data-item selection peer; the corrected native run passed all 153 checks.

## Approved compact revision — 0.1.35

The user's subsequent request reduces each default dimension by 20%, retaining
font sizes and keeping all sections visible. The new default is 998 × 992.
Evidence: `build/processing-compact/check-02/processing-reference-light.png`,
`processing-reference-dark.png`, and `processing-setup.png` in that directory.
The native captures were visually inspected in both themes. The first attempt
stacked panels too early at the workstation's display scale; lowering the
responsive breakpoint fixed this without scaling text. All 144 native checks
passed, including zero outer scrolling in setup and running states and the
unchanged 32-unit heading. The full set of XAML font-size declarations is unchanged.
The activity log retains its own bounded scrollbar; smaller manual window sizes
retain responsive reflow and outer scrolling so controls stay accessible.
The original full-size comparison below is historical, not the current geometry.

Source visual truth: `C:/Users/NEKTAR~1/AppData/Local/Temp/codex-clipboard-c043b1de-112d-463d-b06b-dd4ab1433753.png`.

Native WinUI implementation, not a browser prototype. The supplied image is
1254 × 1254. Native captures use a 1254 × 1254 physical window; its content capture
is 1238 × 1245 because Windows owns the outer resize border. Density is 100%; no
CSS viewport applies. Compare content regions, excluding the native frame.

State: Full processing, My Photos, discovery, Terra, 10,000 assets, locked options.
Elapsed time and log timestamps are synthetic and intentionally differ.

## Comparison history

1. `build/processing-redesign/visual-01/processing-reference-light.png`:
   P2: unnecessary empty grid-row spacing pushed progress below the fold.
   P2: disabled AI controls were too faint. P2: cost icon depicted storage media
   instead of the reference's database/cost metaphor.
2. `build/processing-redesign/visual-02/processing-reference-light.png` was opened
   in the same comparison input as the source (source encoded as JPEG in memory
   for transport; original untouched). Read-only values and the library-sourced
   cylinder icon fix readability and icon meaning. P2 remained: the phase counts,
   throughput and remaining time were below the fold. Final density adjustment
   reduces section gaps; the final installer capture must confirm those labels fit.

## Required fidelity surfaces

- Typography: retain native Nektron Segoe typography; stronger heading/estimate
  hierarchy, readable monetary columns and read-only values. Platform rendering
  differs slightly from the mock's text rasterization.
- Layout: same section order and two-column pricing/estimate arrangement. Narrow
  windows stack cards and options; short windows scroll while actions stay fixed.
- Colors: existing Nektron theme tokens plus a warm light/dark estimate surface.
  Solid native surfaces intentionally replace the mock's subtle texture.
- Assets: existing brand title/info icons, native Fluent clock/lock/check icons,
  and an official MIT-licensed Microsoft database SVG (fill recolored only).
- Copy: actual source/run values, dated pricing snapshot, truthful illustrative
  cost disclaimer, source status, bounded log and phase progress. No fake totals.

## Interaction evidence

Two native runs passed 136 assertions covering setup, Start, AI inclusion,
selected estimate, locked options, Hide/reopen, Stop, success/failure and small
window controls. All processing used controlled fixtures, not paid requests.
Light, dark and 760 × 660 captures are in the same visual-02 directory.

## Final comparison

Final implementation evidence:
`build/installer/0.1.33/94d41419c6284c668829755b6b997b6e/published-browsing/processing-reference-light.png`
and its `processing-reference-dark.png` / `processing-reference-small.png` siblings.
These were opened together with the original reference in one comparison input.
The 1238 × 1245 native content captures now show the full progress section,
throughput, remaining time and footer without clipping at the reference-sized
window. The 744 × 651 compact content capture confirms readable stacked options
and fixed footer actions; the body intentionally scrolls to lower sections.
All 136 native interaction assertions passed on this published build.

The table and cost card are clearly readable at full resolution, so an additional
crop was not needed to assess numerical alignment, icon shape and locked values.
No actionable P0/P1/P2 findings remain after the third comparison. The final
native control metrics and existing Nektron typography/colors are retained rather
than forcing exact raster-pixel matching to the mock. No browser-console check
applies; native launch and interaction verification is the runtime evidence.
P3 acceptable differences: native checkbox treatment, no zebra stripes in the
bounded activity log, different elapsed time/log strings, and library icon geometry.

final result: passed
