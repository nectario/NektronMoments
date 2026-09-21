[CmdletBinding()]
param([Parameter(Mandatory)][string] $ApplicationDirectory, [Parameter(Mandatory)][string] $OutputDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ApplicationDirectory = (Resolve-Path -LiteralPath $ApplicationDirectory).Path
$executable = Join-Path $ApplicationDirectory 'NektronMoments.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'The native photo-opening app executable is missing.' }
if (-not [IO.Path]::IsPathFullyQualified($OutputDirectory)) { throw 'Use an absolute photo-opening output directory.' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$resultPath = Join-Path $OutputDirectory 'selection.json'
if (Test-Path -LiteralPath $resultPath) { throw 'Use a fresh output directory; stale photo-opening results are not accepted.' }
$previous = @{}
$probeVariables = @('NEKTRON_MOMENTS_SELECTION_CHECK_DIR', 'NEKTRON_MOMENTS_SCROLL_CHECK_DIR',
    'NEKTRON_MOMENTS_ASSET_CHECK_DIR', 'NEKTRON_MOMENTS_RESIZE_CHECK_DIR')
$app = $null
try {
    foreach ($name in $probeVariables) {
        $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $null, 'Process')
    }
    $env:NEKTRON_MOMENTS_SELECTION_CHECK_DIR = $OutputDirectory
    $app = Start-Process -FilePath $executable -WorkingDirectory $ApplicationDirectory -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    do {
        Start-Sleep -Milliseconds 250
        $app.Refresh()
        if ($app.HasExited) { throw 'The release app exited during photo-opening verification.' }
    } until ((Test-Path -LiteralPath $resultPath) -or [DateTime]::UtcNow -ge $deadline)
    if (-not (Test-Path -LiteralPath $resultPath)) { throw 'Photo-opening verification timed out.' }
    $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    if ($result.passed -ne $true -or @($result.errors).Count -ne 0) {
        throw ('Photo-opening verification failed: ' + ($result.errors -join '; '))
    }
    if (@($result.checks).Count -lt 32 -or $result.libraryConnected -ne $false -or $result.osInputReplay -ne $false) {
        throw 'Photo-opening verification did not complete its expected isolated fixture checks.'
    }
    $binaryVersion = (Get-Item -LiteralPath $executable).VersionInfo.FileVersion
    if ([Version]$result.version -ne [Version]$binaryVersion) { throw 'Photo-opening report does not match the tested executable version.' }
    if ($app.MainWindowHandle -eq [IntPtr]::Zero -or -not $app.Responding) { throw 'Photo-opening window is not responsive.' }
    Write-Output "Native photo opening passed: $(@($result.checks).Count) fixture checks, four thumbnail sizes, exact selection, repeated opening, stale file completion and explicit errors."
} finally {
    foreach ($name in $previous.Keys) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
    if ($app -and -not $app.HasExited) {
        # Only the specific process launched above may be closed. Never terminate
        # other installed/dev instances by executable name or broad process search.
        if (-not [string]::Equals($app.Path, $executable, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The photo-opening process path changed; scoped cleanup was not attempted.'
        }
        $app.CloseMainWindow() | Out-Null
        if (-not $app.WaitForExit(5000)) { $app.Kill(); $app.WaitForExit(5000) | Out-Null }
        if (-not $app.HasExited) { throw 'The photo-opening verification process did not exit.' }
    }
}
