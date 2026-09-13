[CmdletBinding()]
param([switch] $DiagnosticUnsigned)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$projectRoot = Join-Path $repo 'apps/windows/NektronMoments'
$project = Join-Path $projectRoot 'NektronMoments.csproj'
[xml]$projectXml = Get-Content -LiteralPath $project -Raw
$version = $projectXml.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Expected a major.minor.patch Windows version.' }
$changelog = Get-Content (Join-Path $repo 'CHANGELOG.md') -Raw
if ($changelog -notmatch ('(?m)^## \[' + [regex]::Escape($version) + '\] - \d{4}-\d{2}-\d{2}')) {
    throw 'A matching dated changelog is required.'
}
$releaseRoot = Join-Path $repo 'artifacts/releases/installers'
$final = Join-Path $releaseRoot "NektronMoments.Setup.$version.exe"
if (-not $DiagnosticUnsigned -and (Test-Path -LiteralPath $final)) {
    throw "Release $version already exists. Increment the version; completed installers are immutable."
}
$compiler = $env:INNO_SETUP_COMPILER
if (-not $compiler) { $compiler = Join-Path $env:LOCALAPPDATA 'Programs/Inno Setup 6/ISCC.exe' }
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Inno Setup is missing. Set INNO_SETUP_COMPILER to ISCC.exe.' }
$attempt = Join-Path $repo ("build/installer/" + $version + "/" + [guid]::NewGuid().ToString('N'))
$payload = Join-Path $attempt 'payload'
$compiled = Join-Path $attempt 'compiled'
New-Item -ItemType Directory -Force -Path $payload,$compiled | Out-Null
$buildLog = Join-Path $attempt 'build.log'
$env:WINAPP_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

function Assert-Signed([string]$Path) {
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid' -or -not $signature.TimeStamperCertificate -or
        $signature.SignerCertificate.GetNameInfo([System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false) -ne 'Nektron, Inc.') {
        throw "A timestamped Nektron, Inc. signature is required: $([IO.Path]::GetFileName($Path))"
    }
}

Push-Location -LiteralPath $projectRoot
try {
    if (-not $DiagnosticUnsigned) {
        Write-Output 'Running the full repository regression suite.'
        $wslRoot = '/mnt/' + $repo.Substring(0,1).ToLowerInvariant() + $repo.Substring(2).Replace('\','/')
        & wsl.exe -d Ubuntu --cd $wslRoot --exec bash scripts/test.sh
        if ($LASTEXITCODE -ne 0) { throw 'Repository validation failed. No installer published.' }
        & dotnet run --project (Join-Path $repo 'apps/windows/Tests/ViewingTests.csproj') -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Native viewing/cache policy tests failed.' }
    }
    Write-Output "Publishing Nektron Moments $version with bundled Windows and .NET runtimes."
    & dotnet publish $project -c Release -p:Platform=x64 -p:PublishProfile=Installer -p:DebugType=None -p:DebugSymbols=false -o $payload -v:minimal
    if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }
    foreach ($required in @('NektronMoments.exe','NektronMoments.dll','coreclr.dll','hostfxr.dll','Microsoft.UI.Xaml.dll','NektronMoments.pri','Assets/AppIcon.ico','Assets/Window.ico')) {
        if (-not (Test-Path -LiteralPath (Join-Path $payload $required))) { throw "Published runtime file missing: $required" }
    }
    $payloadFiles = @(Get-ChildItem -LiteralPath $payload -File -Recurse)
    if ($payloadFiles | Where-Object { $_.Name -in @('.env','credentials.json','config.json','state.sqlite3') -or $_.Extension -in @('.pfx','.key','.sqlite3') }) {
        throw 'Private configuration/state must never ship in the installer.'
    }
    $publishedExe = Join-Path $payload 'NektronMoments.exe'
    $publishedVersion = (Get-Item -LiteralPath $publishedExe).VersionInfo
    if ($publishedVersion.ProductVersion -ne $version -or $publishedVersion.FileVersion -ne "$version.0") {
        throw 'Published binary version does not match the installer version.'
    }
    if (-not $DiagnosticUnsigned) {
        foreach ($binary in @('NektronMoments.exe','NektronMoments.dll')) {
            & (Join-Path $PSScriptRoot 'Sign-Artifact.ps1') -FilePath (Join-Path $payload $binary)
        }
    }
    # Check the direct executable, not the packaged development launcher.
    $oldWorkspace = $env:NEKTRON_MOMENTS_WORKSPACE
    $env:NEKTRON_MOMENTS_WORKSPACE = $repo
    $smoke = Start-Process -FilePath $publishedExe -WorkingDirectory $payload -WindowStyle Hidden -PassThru
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(35)
        do {
            Start-Sleep -Milliseconds 300
            $smoke.Refresh()
            if ($smoke.HasExited) { throw 'The self-contained app exited during startup validation.' }
        } until ($smoke.MainWindowHandle -ne [IntPtr]::Zero -or [DateTime]::UtcNow -ge $deadline)
        if ($smoke.MainWindowHandle -eq [IntPtr]::Zero -or -not $smoke.Responding) { throw 'No responsive native app window appeared.' }
        Write-Output 'Direct-launch window verified.'
    } finally {
        if (-not $smoke.HasExited) { $smoke.CloseMainWindow() | Out-Null; if (-not $smoke.WaitForExit(5000)) { $smoke.Kill() } }
        $env:NEKTRON_MOMENTS_WORKSPACE = $oldWorkspace
    }
    & (Join-Path $PSScriptRoot 'Generate-InstallerBranding.ps1') -RepoRoot $repo
    $arguments = @('/Qp', "/DInstallerRoot=$PSScriptRoot", "/DSourcePublishDir=$payload",
        "/DOutputDir=$compiled", "/DAppVersion=$version", "/DWorkspaceHint=$repo")
    if (-not $DiagnosticUnsigned) {
        $powershell = (Get-Process -Id $PID).Path
        $signScript = Join-Path $PSScriptRoot 'Sign-Artifact.ps1'
        $command = '$q{0}$q -NoProfile -ExecutionPolicy Bypass -File $q{1}$q -FilePath $f' -f $powershell,$signScript
        $arguments += @('/DSignArtifacts=1', "/Snektronmoments=$command")
    }
    $arguments += (Join-Path $PSScriptRoot 'NektronMoments.iss')
    Write-Output 'Compiling the Nektron-family installer.'
    & $compiler @arguments *> $buildLog
    if ($LASTEXITCODE -ne 0) { Get-Content $buildLog -Tail 30 | Write-Output; throw 'Installer compilation failed. No release published.' }
    $installer = Join-Path $compiled "NektronMoments.Setup.$version.exe"
    if (-not (Test-Path -LiteralPath $installer)) { throw 'The compiler did not produce an installer.' }
    if ($DiagnosticUnsigned) {
        Write-Output "Unsigned diagnostic only: $installer"
        return
    }
    Assert-Signed $installer
    & (Join-Path $PSScriptRoot 'Test-Installer.ps1') -InstallerPath $installer -Workspace $repo -LogDirectory (Join-Path $attempt 'canary') -RequireSignature
    New-Item -ItemType Directory -Force -Path $releaseRoot | Out-Null
    # No -Force: never replace a completed release, including concurrent builds.
    Move-Item -LiteralPath $installer -Destination $final
    $sha = (Get-FileHash -LiteralPath $final -Algorithm SHA256).Hash
    $record = [ordered]@{
        product='Nektron Moments'; version=$version; installer=$final; sha256=$sha
        bytes=(Get-Item $final).Length; signature='Valid'; publisher='Nektron, Inc.'
        timestamped=$true; appId='{7A13CB58-5B03-4B35-A2F4-7BC06610ED38}'
        scope='CurrentUser'; runtime='SelfContainedWinX64'; brand='1.3.0'
        canary='Install, reconnect, native launch, signatures and uninstall passed'
        dependency='Existing Ubuntu WSL / CLI workspace'; publishedUtc=[DateTime]::UtcNow.ToString('o')
    }
    $record | ConvertTo-Json | Set-Content -LiteralPath "$final.json" -Encoding utf8
    "$sha  $([IO.Path]::GetFileName($final))" | Set-Content -LiteralPath "$final.sha256" -Encoding ascii
    Write-Output "Signed release: $final"
    Write-Output "SHA-256: $sha"
} finally { Pop-Location }
