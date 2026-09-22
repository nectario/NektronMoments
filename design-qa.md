# Processing window — reference QA

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
