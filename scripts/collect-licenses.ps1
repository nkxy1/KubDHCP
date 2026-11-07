$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$packageRoot = Join-Path $env:USERPROFILE '.nuget\packages'
$parts = [System.Collections.Generic.List[string]]::new()
$parts.Add(@'
KubDHCP 1.0 - Hinweise zu mitgelieferten Komponenten und Daten

WinUI und die benoetigten Windows-App-SDK-Komponenten: Microsoft.
.NET: .NET Foundation und weitere Beitragende.
Die folgenden Original-Lizenztexte und Hinweise gelten fuer die betreffenden Komponenten.

Herstellerzuordnungen: IEEE Registration Authority, MA-L / MA-M / MA-S.
Quellen: https://standards.ieee.org/products-programs/regauth/
https://standards-oui.ieee.org/oui/oui.csv
https://standards-oui.ieee.org/oui28/mam.csv
https://standards-oui.ieee.org/oui36/oui36.csv
Daten abgerufen am 2026-11-07. Namen bezeichnen registrierte Adressblockinhaber.

DHCPv4-Optionsregister: IANA, https://www.iana.org/assignments/bootp-dhcp-parameters
Abgerufen am 2026-11-07. RFC-Referenzen sind in den Optionsmetadaten enthalten.
'@)
foreach ($package in @('microsoft.windowsappsdk.winui','microsoft.windowsappsdk.base','microsoft.windowsappsdk.foundation','microsoft.windowsappsdk.interactiveexperiences')) {
    foreach ($name in @('license.txt','NOTICE.txt')) {
        $path = Join-Path $packageRoot "$package\2.6.5\$name"
        if (Test-Path -LiteralPath $path) { $parts.Add("`n===== $package / $name =====`n"); $parts.Add([IO.File]::ReadAllText($path)) }
    }
}
$runtimeRoot = Join-Path $projectRoot '.tools\dotnet\shared\Microsoft.NETCore.App'
if (Test-Path -LiteralPath $runtimeRoot) {
    $runtime = Get-ChildItem -LiteralPath $runtimeRoot -Directory | Sort-Object Name -Descending | Select-Object -First 1
    foreach ($name in @('LICENSE.txt','ThirdPartyNotices.txt')) {
        $path = Join-Path $runtime.FullName $name
        if (Test-Path -LiteralPath $path) { $parts.Add("`n===== .NET / $name =====`n"); $parts.Add([IO.File]::ReadAllText($path)) }
    }
}
[IO.File]::WriteAllText((Join-Path $projectRoot 'src\KubDHCP.App\ThirdPartyNotices.txt'), ($parts -join "`n"), [Text.UTF8Encoding]::new($false))
