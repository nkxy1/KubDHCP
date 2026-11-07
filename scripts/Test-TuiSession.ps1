param([Parameter(Mandatory)][string]$ExePath)
$ErrorActionPreference = 'Stop'
# Exercise the delivered EXE inside an actual PowerShell/ConPTY session. No desktop
# terminal automation, SSH service, LAN traffic or adapter changes are needed.
Add-Type -Path (Join-Path $PSScriptRoot 'TuiSessionTest.cs')
[KubTuiSessionTest]::Run((Get-Command pwsh).Source, [IO.Path]::GetFullPath($ExePath))
