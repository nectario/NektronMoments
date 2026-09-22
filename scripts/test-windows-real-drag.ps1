[CmdletBinding()]
param([Parameter(Mandatory)][string]$ApplicationDirectory, [Parameter(Mandatory)][string]$OutputDirectory, [switch]$ScrollOnly,
    [ValidateRange(112,480)][double]$ThumbnailSize = 240)
$ErrorActionPreference = 'Stop'
$ApplicationDirectory = (Resolve-Path -LiteralPath $ApplicationDirectory).Path
if (-not [IO.Path]::IsPathFullyQualified($OutputDirectory)) { throw 'Use an absolute output path.' }
$reorder = Join-Path $OutputDirectory 'reorder'
$scroll = Join-Path $OutputDirectory 'scroll'
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Use a fresh real-photo evidence directory.' }
New-Item -ItemType Directory -Path $reorder,$scroll -Force | Out-Null
$previousReorder = $env:NEKTRON_MOMENTS_REORDER_CHECK_DIR
$previousScroll = $env:NEKTRON_MOMENTS_STARTUP_SCROLL_CHECK_DIR
$previousWorkspace = $env:NEKTRON_MOMENTS_WORKSPACE
$previousSize = $env:NEKTRON_MOMENTS_SCROLL_THUMBNAIL_SIZE
$app = $null
try {
    $env:NEKTRON_MOMENTS_REORDER_CHECK_DIR = if ($ScrollOnly) { $null } else { $reorder }
    $env:NEKTRON_MOMENTS_STARTUP_SCROLL_CHECK_DIR = $scroll
    $env:NEKTRON_MOMENTS_WORKSPACE = Split-Path $PSScriptRoot -Parent
    $env:NEKTRON_MOMENTS_SCROLL_THUMBNAIL_SIZE = $ThumbnailSize.ToString([Globalization.CultureInfo]::InvariantCulture)
    $app = Start-Process -FilePath (Join-Path $ApplicationDirectory 'NektronMoments.exe') -WorkingDirectory $ApplicationDirectory -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(120)
    do {
        Start-Sleep -Milliseconds 300
        $app.Refresh()
        if ($app.HasExited) { throw 'The real-photo diagnostic app exited.' }
    } until ((Test-Path -LiteralPath (Join-Path $scroll 'startup-scroll.json')) -or [DateTime]::UtcNow -ge $deadline)
    $motionResult = if (-not $ScrollOnly) { Get-Content -LiteralPath (Join-Path $reorder 'real-reorder.json') -Raw | ConvertFrom-Json }
    $scrollResult = Get-Content -LiteralPath (Join-Path $scroll 'startup-scroll.json') -Raw | ConvertFrom-Json
    if (-not $ScrollOnly -and -not $motionResult.passed) { throw ('Real-photo motion failed: ' + ($motionResult.errors -join '; ')) }
    if (-not $scrollResult.completed) { throw ('Real-photo scrolling failed: ' + ($scrollResult.errors -join '; ')) }
    [pscustomobject]@{ RealPhotos=$true; Catalog=$scrollResult.catalogCount; MotionChecks=$motionResult.checks.Count;
        UiWaitP95Ms=$scrollResult.dispatcherWaitP95Ms; UiWaitP99Ms=$scrollResult.dispatcherWaitP99Ms;
        UiWaitMaxMs=$scrollResult.dispatcherWaitMaxMs; GcPauseMs=$scrollResult.gcPauseMilliseconds;
        NewPreparations=$scrollResult.newPreparations; SavedOrderChanged=$false; PaidJobsStarted=$false }
} finally {
    $env:NEKTRON_MOMENTS_REORDER_CHECK_DIR = $previousReorder
    $env:NEKTRON_MOMENTS_STARTUP_SCROLL_CHECK_DIR = $previousScroll
    $env:NEKTRON_MOMENTS_WORKSPACE = $previousWorkspace
    $env:NEKTRON_MOMENTS_SCROLL_THUMBNAIL_SIZE = $previousSize
    if ($app -and -not $app.HasExited) { $app.CloseMainWindow() | Out-Null; if (-not $app.WaitForExit(5000)) { $app.Kill() } }
}
