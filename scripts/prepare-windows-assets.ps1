[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$source = Join-Path $repo 'Brand_Images/NektronMoments_Complete_Brand_Package_v1_3'
$project = Join-Path $repo 'apps/windows/NektronMoments'
$assets = Join-Path $project 'Assets'
$manifest = Get-Content (Join-Path $source 'MANIFEST.json') -Raw | ConvertFrom-Json
$hashes = @{}
foreach ($entry in $manifest.assets) { $hashes[$entry.path] = $entry.sha256 }
$copied = [Collections.Generic.List[object]]::new()
function Copy-BrandAsset([string]$relative, [string]$destination) {
    $origin = Join-Path $source $relative
    $digest = (Get-FileHash -LiteralPath $origin -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($digest -ne $hashes[$relative]) { throw "Unverified source asset: $relative" }
    $target = Join-Path $project $destination
    New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
    Copy-Item -LiteralPath $origin -Destination $target
    $copied.Add(@{source=$relative; destination=$destination; sha256=$digest})
}
Copy-BrandAsset 'brand/app/NektronMoments.ico' 'Assets/AppIcon.ico'
Copy-BrandAsset 'brand/window/NektronMoments.Window.ico' 'Assets/Window.ico'
Copy-BrandAsset 'brand/mark/light/png/24.png' 'Assets/Mark.light.png'
Copy-BrandAsset 'brand/mark/dark/png/24.png' 'Assets/Mark.dark.png'
Copy-BrandAsset 'integration/winui3/Moments.Icons.xaml' 'Styles/Moments.Icons.xaml'
Copy-BrandAsset 'LICENSE_AND_PROVENANCE.md' 'Assets/LICENSE_AND_PROVENANCE.md'
foreach ($entry in $manifest.assets) {
    if ($entry.path.StartsWith('icons/ui/svg/') -or $entry.path.StartsWith('platforms/windows/Assets/')) {
        $destination = if ($entry.path.StartsWith('icons/')) { 'Assets/NektronMoments/' + $entry.path } else { $entry.path.Replace('platforms/windows/', '') }
        Copy-BrandAsset $entry.path $destination
    }
}
# Generated inventory only, never source artwork or user media.
@{version='1.3.0'; files=$copied} | ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath (Join-Path $assets 'brand-provenance.json') -Encoding utf8
Write-Output "Imported $($copied.Count) verified Windows assets from brand v1.3."
