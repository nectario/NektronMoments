[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $InstallerPath,
    [Parameter(Mandatory)][string] $Workspace,
    [Parameter(Mandatory)][string] $LogDirectory,
    [switch] $RequireSignature
)
$ErrorActionPreference = 'Stop'
$productionKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{7A13CB58-5B03-4B35-A2F4-7BC06610ED38}_is1'
$sideBySide = Test-Path -LiteralPath $productionKey
$productionBefore = if ($sideBySide) { Get-ItemProperty -LiteralPath $productionKey | ConvertTo-Json -Compress } else { $null }
$uninstallKey = if ($sideBySide) { 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{5DA57AF9-23F2-4B19-9950-0AA974C3E07D}_is1' } else { $productionKey }
$preferencesKey = 'HKCU:\Software\Nektron\NektronMoments'
if (Test-Path -LiteralPath $uninstallKey) { throw 'A canary installation already exists; inspect it before retrying.' }
$previous = @{}
foreach ($name in @('Workspace','InstallerOwner')) {
    try { $previous[$name] = Get-ItemPropertyValue -LiteralPath $preferencesKey -Name $name -ErrorAction Stop } catch { }
}
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$canary = Join-Path $temporaryRoot ('NektronMoments-InstallCheck-' + [guid]::NewGuid().ToString('N'))
$resolvedCanary = [IO.Path]::GetFullPath($canary)
if (-not $resolvedCanary.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetFileName($resolvedCanary) -notlike 'NektronMoments-InstallCheck-*') { throw 'Unsafe canary destination.' }
New-Item -ItemType Directory -Force -Path $LogDirectory | Out-Null
$app = $null
$installed = $false
$ownsRegistration = $false
$workspaceBefore = $env:NEKTRON_MOMENTS_WORKSPACE
$manifestChecks = @{}
try {
    $arguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-','/NOICONS','/TASKS=""',
        "/DIR=`"$resolvedCanary`"", "/WORKSPACE=`"$Workspace`"", "/LOG=`"$(Join-Path $LogDirectory 'install.log')`"")
    if ($sideBySide) { $arguments += @('/ASSETCANARY=1','/NOCLOSEAPPLICATIONS') }
    $setup = Start-Process -FilePath $InstallerPath -ArgumentList $arguments -WindowStyle Hidden -PassThru
    if (-not $setup.WaitForExit(180000)) { throw 'Installer canary timed out; inspect it before cleanup.' }
    if ($setup.ExitCode -ne 0) { throw "Installer canary failed with exit code $($setup.ExitCode). See install.log." }
    $installed = $true
    if (-not (Test-Path -LiteralPath $uninstallKey)) { throw 'The current-user uninstall registration was not created.' }
    $registration = Get-ItemProperty -LiteralPath $uninstallKey
    if ([IO.Path]::GetFullPath($registration.InstallLocation).TrimEnd('\') -ne $resolvedCanary.TrimEnd('\')) {
        throw 'Uninstall registration does not point to this canary.'
    }
    $ownsRegistration = $true
    $savedWorkspace = Get-ItemPropertyValue -LiteralPath $preferencesKey -Name Workspace
    if ($savedWorkspace -ne $Workspace) { throw 'The workspace reconnect preference was not saved correctly.' }
    & (Join-Path $PSScriptRoot 'Test-PublishedAssets.ps1') -ApplicationDirectory $resolvedCanary -Render -RenderOutput (Join-Path $LogDirectory 'artwork')
    if ($RequireSignature) {
        foreach ($name in @('NektronMoments.exe','NektronMoments.dll','unins000.exe')) {
            $signature = Get-AuthenticodeSignature -LiteralPath (Join-Path $resolvedCanary $name)
            if ($signature.Status -ne 'Valid' -or -not $signature.TimeStamperCertificate) { throw "Installed signature is invalid: $name" }
        }
    }
    # Outside the checkout and with no environment hint: exercise the installed path.
    $env:NEKTRON_MOMENTS_WORKSPACE = $null
    $app = Start-Process -FilePath (Join-Path $resolvedCanary 'NektronMoments.exe') -WorkingDirectory $resolvedCanary -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(35)
    do {
        Start-Sleep -Milliseconds 300
        $app.Refresh()
        if ($app.HasExited) { throw 'The installed app exited unexpectedly.' }
    } until ($app.MainWindowHandle -ne [IntPtr]::Zero -or [DateTime]::UtcNow -ge $deadline)
    if ($app.MainWindowHandle -eq [IntPtr]::Zero -or -not $app.Responding) { throw 'The installed app did not show a responsive window.' }
    $manifestChecks = @{
        installed=$true; userScope=$true; reconnectPreference=$true; directLaunch=$true
        signaturesRequired=[bool]$RequireSignature; canary=$resolvedCanary; artworkRendered=$true; sideBySide=$sideBySide
    }
} finally {
    $env:NEKTRON_MOMENTS_WORKSPACE = $workspaceBefore
    if ($app -and -not $app.HasExited) {
        $app.CloseMainWindow() | Out-Null
        if (-not $app.WaitForExit(5000)) { $app.Kill() }
    }
    $uninstaller = Join-Path $resolvedCanary 'unins000.exe'
    if ($installed -and $ownsRegistration -and (Test-Path -LiteralPath $uninstaller)) {
        $remove = Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',
            "/LOG=`"$(Join-Path $LogDirectory 'uninstall.log')`"") -WindowStyle Hidden -PassThru
        if (-not $remove.WaitForExit(120000) -or $remove.ExitCode -ne 0) { throw 'Canary uninstaller failed; no other cleanup was attempted.' }
        if (Test-Path -LiteralPath $uninstallKey) { throw 'Canary uninstall registration remains.' }
        $manifestChecks['uninstalled'] = $true
    }
    # Restore only preferences touched by our temporary test; never remove user data.
    foreach ($name in @('Workspace','InstallerOwner')) {
        if ($sideBySide) {
            $current = Get-ItemProperty -LiteralPath $preferencesKey -Name $name -ErrorAction SilentlyContinue
            if ($previous.ContainsKey($name) -and $current.$name -ne $previous[$name]) { throw 'Canary changed a user preference.' }
            if (-not $previous.ContainsKey($name) -and $null -ne $current) { throw 'Canary created a user preference.' }
            continue
        }
        if ($previous.ContainsKey($name)) {
            Set-ItemProperty -LiteralPath $preferencesKey -Name $name -Value $previous[$name]
        } elseif (Test-Path -LiteralPath $preferencesKey) {
            Remove-ItemProperty -LiteralPath $preferencesKey -Name $name -ErrorAction SilentlyContinue
        }
    }
    if ($sideBySide -and (Get-ItemProperty -LiteralPath $productionKey | ConvertTo-Json -Compress) -ne $productionBefore) {
        throw 'Existing installation registration changed during canary.'
    }
}
if (-not $manifestChecks['uninstalled']) { throw 'Canary did not complete install/launch/uninstall.' }
$manifestChecks | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $LogDirectory 'canary.json') -Encoding utf8
Write-Output 'Isolated installer canary passed: install, workspace reconnect, native launch and uninstall.'
