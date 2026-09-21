[CmdletBinding()]
param([Parameter(Mandatory)][string] $ApplicationDirectory, [Parameter(Mandatory)][string] $OutputDirectory, [switch] $IncludeAudit)
$ErrorActionPreference = 'Stop'
$ApplicationDirectory = (Resolve-Path -LiteralPath $ApplicationDirectory).Path
if (-not [IO.Path]::IsPathFullyQualified($OutputDirectory)) { throw 'Use an absolute browsing-check output directory.' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$resultPath = Join-Path $OutputDirectory 'browsing.json'
if (Test-Path -LiteralPath $resultPath) { throw 'Use a fresh output directory; stale browsing results are not accepted.' }
$previous = $env:NEKTRON_MOMENTS_BROWSING_CHECK_DIR
$previousAudit = $env:NEKTRON_MOMENTS_FLOW_AUDIT
$app = $null
try {
    $env:NEKTRON_MOMENTS_BROWSING_CHECK_DIR = $OutputDirectory
    if ($IncludeAudit) { $env:NEKTRON_MOMENTS_FLOW_AUDIT = '1' }
    $app = Start-Process -FilePath (Join-Path $ApplicationDirectory 'NektronMoments.exe') -WorkingDirectory $ApplicationDirectory -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(120)
    do {
        Start-Sleep -Milliseconds 250
        $app.Refresh()
        if ($app.HasExited) { throw 'The app exited during native browsing verification.' }
    } until ((Test-Path -LiteralPath $resultPath) -or [DateTime]::UtcNow -ge $deadline)
    if (-not (Test-Path -LiteralPath $resultPath)) { throw 'Native browsing verification timed out.' }
    $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    if (-not $result.passed) { throw ('Native browsing verification failed: ' + ($result.errors -join '; ')) }
    if ($IncludeAudit) {
        $audit = Get-Content -LiteralPath (Join-Path $OutputDirectory 'interaction-audit.json') -Raw | ConvertFrom-Json
        if ($audit.samples.Count -ne 57 -or ($audit.samples | Where-Object { $_.retirementFailures -gt 0 -or $_.sizingActive -or $_.sizingVisuals -gt 0 })) {
            throw 'Mixed interaction audit is incomplete or retained active resize resources.'
        }
    }
    if ($result.libraryConnected -or -not $result.preparedWithoutControl -or -not $result.displayedReadyObject) {
        throw 'The generated-photo cache proof is missing or used a live library.'
    }
    if ($app.MainWindowHandle -eq [IntPtr]::Zero -or -not $app.Responding) { throw 'Native browsing window is not responsive.' }
    Write-Output "Native browsing and prepared thumbnails passed: $($result.checks.Count) checks, initial 200, preserved append anchors, cache reuse before/after tile creation, and background look-ahead beyond the browsing range."
} finally {
    $env:NEKTRON_MOMENTS_BROWSING_CHECK_DIR = $previous
    $env:NEKTRON_MOMENTS_FLOW_AUDIT = $previousAudit
    if ($app -and -not $app.HasExited) {
        $app.CloseMainWindow() | Out-Null
        if (-not $app.WaitForExit(5000)) { $app.Kill() }
    }
}
