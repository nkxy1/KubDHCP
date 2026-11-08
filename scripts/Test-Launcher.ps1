param([Parameter(Mandatory)][string]$ExePath)
$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('KubDHCP-launcher-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root -Force | Out-Null
$previousCache = $env:KUBDHCP_RUNTIME_CACHE
$env:KUBDHCP_RUNTIME_CACHE = Join-Path $root 'cache'
function Start-Check([string]$name, [string]$path = $ExePath, [switch]$Silent) {
    $report = Join-Path $root ($name + '.txt')
    $info = [Diagnostics.ProcessStartInfo]::new($path)
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true
    $info.ArgumentList.Add('--launcher-test'); $info.ArgumentList.Add($report)
    if ($Silent) { $info.ArgumentList.Add('--transport-test') }
    $process = [Diagnostics.Process]::Start($info)
    return @{ Process=$process; Report=$report }
}
function Finish-Check($check) {
    if (-not $check.Process.WaitForExit(60000)) { $check.Process.Kill($true); throw 'Launcher-Test antwortet nicht.' }
    if ($check.Process.ExitCode -ne 0) { throw "Launcher-Test fehlgeschlagen: $($check.Report)" }
    $report = [IO.File]::ReadAllText($check.Report, [Text.Encoding]::Unicode)
    if (-not $report.StartsWith('PASS:')) { throw $report }
    return $report
}
try {
    $cold = Finish-Check (Start-Check 'cold')
    if ($cold -notmatch 'cache=extracted; progress=100; window=1;') { throw "Erststart ohne sichtbaren Entpackfortschritt: $cold" }
    $warm = Finish-Check (Start-Check 'warm')
    if ($warm -notmatch 'cache=hit; progress=0; window=0;') { throw "Cache wurde nicht wiederverwendet: $warm" }
    $cache = ($warm -split '; path=',2)[1]
    # Simulate an interrupted extraction and an incomplete cached executable.
    [IO.File]::WriteAllBytes((Join-Path $cache 'KubDHCP.exe'), [byte[]]@(0))
    $repair = Finish-Check (Start-Check 'repair')
    if ($repair -notmatch 'cache=extracted; progress=100;') { throw 'Unvollständiger Cache wurde nicht repariert.' }
    $env:KUBDHCP_RUNTIME_CACHE = Join-Path $root 'concurrent'
    $first = Start-Check 'parallel-1'; $second = Start-Check 'parallel-2'
    $results = @((Finish-Check $first), (Finish-Check $second))
    if (($results -match 'cache=extracted').Count -ne 1 -or ($results -match 'cache=hit').Count -ne 1) { throw 'Parallele Starts verwenden den Cache nicht exklusiv.' }
    # Change the embedded digest, preserving a valid PE and archive structure.
    $brokenPath = Join-Path $root 'KubDHCP.exe'
    Copy-Item -LiteralPath $ExePath -Destination $brokenPath
    $file = [IO.File]::Open($brokenPath, 'Open', 'ReadWrite')
    try { $file.Position=$file.Length-40; $old=$file.ReadByte(); $file.Position--; $file.WriteByte($old -bxor 1) } finally { $file.Dispose() }
    $broken = Start-Check 'corrupt' $brokenPath -Silent
    if (-not $broken.Process.WaitForExit(30000)) { $broken.Process.Kill($true); throw 'Beschädigtes Paket hängt.' }
    if ($broken.Process.ExitCode -eq 0) { throw 'Beschädigtes Paket wurde akzeptiert.' }
    Write-Output 'PASS: native launcher; visible cold extraction reaches 100%; warm cache skips splash; incomplete cache repair; concurrent starts; corrupt payload rejected.'
}
finally { $env:KUBDHCP_RUNTIME_CACHE = $previousCache }
