param([Parameter(Mandatory)][string]$ExePath, [ValidateSet('de', 'en')][string]$Language = 'de', [switch]$ColdStart)
$ErrorActionPreference = 'Stop'
# Exercise the delivered EXE inside an actual PowerShell/ConPTY session. No desktop
# terminal automation, SSH service, LAN traffic or adapter changes are needed.
Add-Type -Path (Join-Path $PSScriptRoot 'TuiSessionTest.cs')
$previousCache = $env:KUBDHCP_RUNTIME_CACHE
try {
    if ($ColdStart) { $env:KUBDHCP_RUNTIME_CACHE = Join-Path ([IO.Path]::GetTempPath()) ('KubDHCP-tui-cold-' + [Guid]::NewGuid().ToString('N')) }
    [KubTuiSessionTest]::Run((Get-Command pwsh).Source, [IO.Path]::GetFullPath($ExePath), $Language, $ColdStart.IsPresent)
}
finally { $env:KUBDHCP_RUNTIME_CACHE = $previousCache }
