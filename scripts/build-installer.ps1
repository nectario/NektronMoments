[CmdletBinding()]
param([switch] $DiagnosticUnsigned)
& (Join-Path (Split-Path $PSScriptRoot -Parent) 'installer/Build-InnoInstaller.ps1') -DiagnosticUnsigned:$DiagnosticUnsigned
