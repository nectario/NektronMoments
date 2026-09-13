[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $FilePath,
    [string] $SignToolPath,
    [string] $DlibPath = (Join-Path $env:LOCALAPPDATA 'Microsoft/MicrosoftArtifactSigningClientTools/Azure.CodeSigning.Dlib.dll'),
    [string] $MetadataPath = (Join-Path $PSScriptRoot 'signing-profile.json')
)
$ErrorActionPreference = 'Stop'
if (-not $SignToolPath) {
    $SignToolPath = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin\*\x64\signtool.exe' |
        Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
}
foreach ($required in @($FilePath, $SignToolPath, $DlibPath, $MetadataPath)) {
    if (-not $required -or -not (Test-Path -LiteralPath $required -PathType Leaf)) { throw 'A required signing file/tool is missing.' }
}
& $SignToolPath sign /fd SHA256 /td SHA256 /tr 'http://timestamp.acs.microsoft.com/' /dlib $DlibPath /dmdf $MetadataPath $FilePath *> $null
if ($LASTEXITCODE -ne 0) { throw 'Azure Artifact Signing failed. Authentication details are suppressed; verify the existing signing identity and profile access.' }
& $SignToolPath verify /pa /all /tw $FilePath *> $null
if ($LASTEXITCODE -ne 0) { throw 'Authenticode verification failed.' }
$signature = Get-AuthenticodeSignature -LiteralPath $FilePath
if ($signature.Status -ne 'Valid' -or -not $signature.TimeStamperCertificate -or
    $signature.SignerCertificate.GetNameInfo([System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false) -ne 'Nektron, Inc.') {
    throw 'The file does not have a valid timestamped Nektron, Inc. signature.'
}
Write-Output "Verified signed artifact: $([IO.Path]::GetFileName($FilePath))"
