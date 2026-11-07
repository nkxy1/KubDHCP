param([switch]$Preview)
$ErrorActionPreference = 'Stop'
$project = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\KubDHCP.App\KubDHCP.App.csproj'
$text = [IO.File]::ReadAllText($project)
$match = [regex]::Match($text, '<Version>(\d+)\.(\d+)\.(\d+)</Version>')
if (-not $match.Success) { throw 'Versionsnummer fehlt.' }
$major = [int]$match.Groups[1].Value
$minor = [int]$match.Groups[2].Value
$patch = [int]$match.Groups[3].Value
if ($patch -ge 100) { $minor++; $patch = 0 } else { $patch++ }
$next = "$major.$minor.$patch"
if (-not $Preview) {
    $text = $text.Replace($match.Value, "<Version>$next</Version>")
    [IO.File]::WriteAllText($project, $text, [Text.UTF8Encoding]::new($false))
}
Write-Output "v$next"
