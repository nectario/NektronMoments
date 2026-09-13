[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ApplicationDirectory,
    [string] $SourceAssets = (Join-Path $PSScriptRoot '../apps/windows/NektronMoments/Assets'),
    [string] $RenderOutput,
    [switch] $Render
)
$ErrorActionPreference = 'Stop'
$ApplicationDirectory = (Resolve-Path -LiteralPath $ApplicationDirectory).Path
$SourceAssets = (Resolve-Path -LiteralPath $SourceAssets).Path
$expected = @(Get-ChildItem -LiteralPath $SourceAssets -File -Recurse)
if ($expected.Count -eq 0) { throw 'No source assets were found.' }
foreach ($asset in $expected) {
    $relative = [IO.Path]::GetRelativePath($SourceAssets, $asset.FullName)
    $destination = Join-Path $ApplicationDirectory (Join-Path 'Assets' $relative)
    if (-not (Test-Path -LiteralPath $destination -PathType Leaf)) { throw "Published asset missing: Assets/$relative" }
    if ((Get-FileHash -LiteralPath $asset.FullName).Hash -ne (Get-FileHash -LiteralPath $destination).Hash) {
        throw "Published asset differs from approved source: Assets/$relative"
    }
}
Write-Output "Published artwork: $($expected.Count) asset files match the source byte-for-byte."
if (-not $Render) { return }
if (-not $RenderOutput -or -not [IO.Path]::IsPathFullyQualified($RenderOutput)) { throw 'RenderOutput must be absolute.' }
New-Item -ItemType Directory -Path $RenderOutput -Force | Out-Null
$resultPath = Join-Path $RenderOutput 'assets.json'
if (Test-Path -LiteralPath $resultPath) { throw 'Use a fresh render output directory; stale results are not accepted.' }
$previous = $env:NEKTRON_MOMENTS_ASSET_CHECK_DIR
$app = $null
try {
    $env:NEKTRON_MOMENTS_ASSET_CHECK_DIR = $RenderOutput
    $app = Start-Process -FilePath (Join-Path $ApplicationDirectory 'NektronMoments.exe') -WorkingDirectory $ApplicationDirectory -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    do {
        Start-Sleep -Milliseconds 250
        $app.Refresh()
        if ($app.HasExited) { throw 'The release app exited during artwork verification.' }
    } until ((Test-Path -LiteralPath $resultPath) -or [DateTime]::UtcNow -ge $deadline)
    if (-not (Test-Path -LiteralPath $resultPath)) { throw 'Release artwork verification timed out.' }
    $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    if (-not $result.passed -or $result.svgOpened -eq 0) { throw ('Release artwork failed: ' + ($result.errors -join '; ')) }
    if ($app.MainWindowHandle -eq [IntPtr]::Zero -or -not $app.Responding) { throw 'Release app window is not responsive.' }
    Write-Output "Release rendering: $($result.svgOpened) SVG URIs opened; $(@($result.rendered).Count) Light/Dark icon controls painted pixels."
} finally {
    $env:NEKTRON_MOMENTS_ASSET_CHECK_DIR = $previous
    if ($app -and -not $app.HasExited) {
        $app.CloseMainWindow() | Out-Null
        if (-not $app.WaitForExit(5000)) { $app.Kill() }
    }
}
