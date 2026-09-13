[CmdletBinding()]
param(
    [ValidateSet('run', 'build', 'verify')] [string] $Action = 'run'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$projectRoot = Join-Path $repo 'apps/windows/NektronMoments'
$project = Join-Path $projectRoot 'NektronMoments.csproj'
if (-not (Test-Path -LiteralPath $project)) { throw 'The Windows app project is missing.' }
$env:NEKTRON_MOMENTS_WORKSPACE = $repo
$env:WINAPP_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
Push-Location -LiteralPath $projectRoot
try {
    dotnet build $project -p:Platform=x64 -v:minimal
    if ($LASTEXITCODE -ne 0) { throw 'Windows build failed.' }
    if ($Action -eq 'build') { return }
    if ($Action -eq 'verify') {
        New-Item -ItemType Directory -Force -Path (Join-Path $repo 'build') | Out-Null
        New-Item -ItemType File -Force -Path (Join-Path $repo 'build/verify-windows-ui.flag') | Out-Null
        Write-Output 'UI snapshots stay under ignored build/windows-verification-013. They may contain your photos; do not publish them.'
    }
    dotnet run --no-build --no-launch-profile -p:Platform=x64
    if ($LASTEXITCODE -ne 0) { throw 'Windows app exited with an error.' }
} finally {
    if ($Action -eq 'verify') {
        $marker = Join-Path $repo 'build/verify-windows-ui.flag'
        if (Test-Path -LiteralPath $marker) { Remove-Item -LiteralPath $marker }
    }
    Pop-Location
}
