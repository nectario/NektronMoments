[CmdletBinding()]
param([Parameter(Mandatory)][string] $ApplicationDirectory, [Parameter(Mandatory)][string] $OutputDirectory)
$ErrorActionPreference = 'Stop'
$ApplicationDirectory = (Resolve-Path -LiteralPath $ApplicationDirectory).Path
if (-not [IO.Path]::IsPathFullyQualified($OutputDirectory)) { throw 'Use an absolute scroll-check output directory.' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$resultPath = Join-Path $OutputDirectory 'scroll.json'
if (Test-Path -LiteralPath $resultPath) { throw 'Use a fresh output directory; stale scroll results are not accepted.' }
$previous = $env:NEKTRON_MOMENTS_SCROLL_CHECK_DIR
$app = $null
try {
    $env:NEKTRON_MOMENTS_SCROLL_CHECK_DIR = $OutputDirectory
    $app = Start-Process -FilePath (Join-Path $ApplicationDirectory 'NektronMoments.exe') -WorkingDirectory $ApplicationDirectory -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    do {
        Start-Sleep -Milliseconds 250
        $app.Refresh()
        if ($app.HasExited) { throw 'The release app exited during pixel-scroll verification.' }
    } until ((Test-Path -LiteralPath $resultPath) -or [DateTime]::UtcNow -ge $deadline)
    if (-not (Test-Path -LiteralPath $resultPath)) { throw 'Pixel-scroll verification timed out.' }
    $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    if (-not $result.passed) { throw ('Pixel-scroll verification failed: ' + ($result.errors -join '; ')) }
    if ($app.MainWindowHandle -eq [IntPtr]::Zero -or -not $app.Responding) { throw 'Pixel-scroll window is not responsive.' }
    Write-Output "Native pixel scrolling passed: $($result.itemCount) items, four thumbnail sizes, fractional deltas, reversal, burst bounds and cancellation."
} finally {
    $env:NEKTRON_MOMENTS_SCROLL_CHECK_DIR = $previous
    if ($app -and -not $app.HasExited) {
        $app.CloseMainWindow() | Out-Null
        if (-not $app.WaitForExit(5000)) { $app.Kill() }
    }
}
