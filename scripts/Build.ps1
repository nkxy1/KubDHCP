param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
# The Windows SDK manifest tool cannot handle UNC project paths. Reuse an
# existing drive mapping when this script is invoked from a network checkout.
if ($projectRoot.StartsWith('\\')) {
    foreach ($drive in Get-PSDrive -PSProvider FileSystem) {
        if ($drive.DisplayRoot -and $projectRoot.StartsWith($drive.DisplayRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
            $projectRoot = Join-Path $drive.Root $projectRoot.Substring($drive.DisplayRoot.Length + 1)
            break
        }
    }
}
Set-Location -LiteralPath $projectRoot
$localDotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
$dotnetCommand = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { (Get-Command dotnet -ErrorAction Stop).Source }
if (-not $SkipTests) {
    foreach ($language in @('de', 'en')) {
        & $dotnetCommand run --project 'tests\KubDHCP.Tests\KubDHCP.Tests.csproj' -c Release --nologo -- "--language=$language"
        if ($LASTEXITCODE -ne 0) { throw "Die Kerntests ($language) sind fehlgeschlagen." }
    }
}
$runtimeDirectory = Join-Path $projectRoot ('artifacts\runtime-' + [Guid]::NewGuid().ToString('N'))
& $dotnetCommand publish 'src\KubDHCP.App\KubDHCP.App.csproj' -c Release -o $runtimeDirectory --nologo
if ($LASTEXITCODE -ne 0) { throw 'Veröffentlichung fehlgeschlagen.' }
$portableDirectory = Join-Path $projectRoot 'artifacts\portable'
New-Item -ItemType Directory -Path $portableDirectory -Force | Out-Null
& (Join-Path $PSScriptRoot 'Build-Launcher.ps1') -RuntimeDirectory $runtimeDirectory -OutputPath (Join-Path $portableDirectory 'KubDHCP.exe')
$distribution = Join-Path $projectRoot 'dist'
New-Item -ItemType Directory -Path $distribution -Force | Out-Null
$exePath = Join-Path $distribution 'KubDHCP.exe'
try { Copy-Item -LiteralPath (Join-Path $projectRoot 'artifacts\portable\KubDHCP.exe') -Destination $exePath -Force }
catch [IO.IOException] {
    [xml]$project = Get-Content -LiteralPath (Join-Path $projectRoot 'src\KubDHCP.App\KubDHCP.App.csproj') -Raw
    $release = Join-Path $distribution ('v' + $project.Project.PropertyGroup.Version)
    New-Item -ItemType Directory -Path $release -Force | Out-Null
    $exePath = Join-Path $release 'KubDHCP.exe'
    Copy-Item -LiteralPath (Join-Path $projectRoot 'artifacts\portable\KubDHCP.exe') -Destination $exePath -Force
    Write-Output "Die laufende EXE bleibt erhalten. Neue Version: $exePath"
}
$exe = Get-Item -LiteralPath $exePath
& (Join-Path $PSScriptRoot 'Test-Launcher.ps1') -ExePath $exe.FullName
$report = Join-Path $projectRoot 'artifacts\transport-test.txt'
$testStartInfo = [Diagnostics.ProcessStartInfo]::new($exe.FullName)
$testStartInfo.UseShellExecute = $false
$testStartInfo.CreateNoWindow = $true
$testStartInfo.ArgumentList.Add('--transport-test')
$testStartInfo.ArgumentList.Add($report)
$testProcess = [Diagnostics.Process]::Start($testStartInfo)
if (-not $testProcess.WaitForExit(60000)) { throw 'Der Loopbacktest hat innerhalb von 60 Sekunden nicht geantwortet.' }
if ($testProcess.ExitCode -ne 0) { throw "Der Windows-Transporttest ist fehlgeschlagen. Siehe $report" }
# Exercise the real WinUI startup under the same filename as the delivered EXE.
$smokeDirectory = Join-Path $projectRoot 'artifacts\startup-smoke'
New-Item -ItemType Directory -Path $smokeDirectory -Force | Out-Null
$smokeExe = Join-Path $smokeDirectory $exe.Name
Copy-Item -LiteralPath $exe.FullName -Destination $smokeExe -Force
foreach ($language in @('de', 'en')) {
$startupReport = Join-Path $smokeDirectory ('startup-' + [Guid]::NewGuid().ToString('N') + '.txt')
$startupInfo = [Diagnostics.ProcessStartInfo]::new($smokeExe)
$startupInfo.UseShellExecute = $false
$startupInfo.CreateNoWindow = $true
$startupInfo.ArgumentList.Add("--language=$language")
$startupInfo.ArgumentList.Add('--startup-test')
$startupInfo.ArgumentList.Add($startupReport)
$startupProcess = [Diagnostics.Process]::Start($startupInfo)
if (-not $startupProcess.WaitForExit(45000)) { $startupProcess.Kill(); throw 'Der WinUI-Starttest hat nicht geantwortet.' }
if ($startupProcess.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $startupReport)) { throw 'Der WinUI-Fensterstart ist fehlgeschlagen.' }
$startupResult = Get-Content -LiteralPath $startupReport -Raw
if (-not $startupResult.StartsWith('PASS:')) { throw $startupResult }
Write-Output $startupResult
$tuiReport = Join-Path $smokeDirectory ('tui-' + [Guid]::NewGuid().ToString('N') + '.txt')
$tuiInfo = [Diagnostics.ProcessStartInfo]::new($smokeExe)
$tuiInfo.UseShellExecute = $false
$tuiInfo.CreateNoWindow = $true
$tuiInfo.ArgumentList.Add("--language=$language")
$tuiInfo.ArgumentList.Add('--tui-test')
$tuiInfo.ArgumentList.Add($tuiReport)
$tuiProcess = [Diagnostics.Process]::Start($tuiInfo)
if (-not $tuiProcess.WaitForExit(45000)) { $tuiProcess.Kill(); throw 'Der TUI-Test hat nicht geantwortet.' }
if ($tuiProcess.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $tuiReport)) { throw "TUI-Test fehlgeschlagen: $tuiReport" }
$tuiResult = Get-Content -LiteralPath $tuiReport -Raw
if (-not $tuiResult.StartsWith('PASS:')) { throw $tuiResult }
Write-Output $tuiResult
& (Join-Path $PSScriptRoot 'Test-TuiSession.ps1') -ExePath $smokeExe -Language $language -ColdStart
}
Write-Output "Portable EXE: $($exe.FullName) ($([Math]::Round($exe.Length / 1MB, 2)) MiB)"
Get-FileHash -LiteralPath $exe.FullName -Algorithm SHA256 | Format-List
