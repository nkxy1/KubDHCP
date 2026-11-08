param([Parameter(Mandatory)][string]$RuntimeDirectory, [Parameter(Mandatory)][string]$OutputPath)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
[xml]$project = Get-Content -LiteralPath (Join-Path $projectRoot 'src\KubDHCP.App\KubDHCP.App.csproj') -Raw
$version = [string]$project.Project.PropertyGroup.Version
# Keep compiler/cache I/O local even when the checkout lives on SMB.
$toolsRoot = Join-Path $env:LOCALAPPDATA 'KubDHCP.BuildTools'
$zig = Join-Path $toolsRoot 'zig-windows-x86_64-0.13.0\zig.exe'
if (-not (Test-Path -LiteralPath $zig)) {
    New-Item -ItemType Directory -Path $toolsRoot -Force | Out-Null
    $archive = Join-Path $toolsRoot 'zig.zip'
    Invoke-WebRequest -Uri 'https://ziglang.org/download/0.13.0/zig-windows-x86_64-0.13.0.zip' -OutFile $archive
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne 'D859994725EF9402381E557C60BB57497215682E355204D754EE3DF75EE3C158') { throw 'Zig-Download: Prüfsumme stimmt nicht.' }
    [IO.Compression.ZipFile]::ExtractToDirectory($archive, $toolsRoot, $true)
}
$stage = Join-Path $toolsRoot ('launcher-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot 'src\KubDHCP.Launcher\Launcher.cpp') -Destination $stage
Copy-Item -LiteralPath (Join-Path $projectRoot 'src\KubDHCP.App\app.manifest') -Destination $stage
Copy-Item -LiteralPath (Join-Path $projectRoot 'assets\KubDHCP.ico') -Destination $stage
[IO.File]::WriteAllText((Join-Path $stage 'Version.h'), '#define LAUNCHER_VERSION L"' + $version + '"')
$resource = @"
1 ICON "KubDHCP.ico"
1 24 "app.manifest"
1 VERSIONINFO
FILEVERSION $($version.Replace('.', ',')),0
PRODUCTVERSION $($version.Replace('.', ',')),0
FILEOS 0x40004
FILETYPE 1
BEGIN
 BLOCK "StringFileInfo"
 BEGIN
  BLOCK "040904b0"
  BEGIN
   VALUE "FileDescription", "KubDHCP"
   VALUE "FileVersion", "$version.0"
   VALUE "ProductName", "KubDHCP"
   VALUE "ProductVersion", "$version"
   VALUE "OriginalFilename", "KubDHCP.exe"
  END
 END
 BLOCK "VarFileInfo"
 BEGIN
  VALUE "Translation", 0x409, 1200
 END
END
"@
[IO.File]::WriteAllText((Join-Path $stage 'Launcher.rc'), $resource)
Push-Location -LiteralPath $stage
try {
    & $zig rc /fo Launcher.res Launcher.rc
    if ($LASTEXITCODE -ne 0) { throw 'Launcher-Ressourcen fehlgeschlagen.' }
    & $zig c++ Launcher.cpp Launcher.res -std=c++17 -O2 -target x86_64-windows-gnu -municode -static -o KubDHCP.exe -luser32 -lgdi32 -lshell32 -lole32 -lbcrypt -lcabinet -ldwmapi
    if ($LASTEXITCODE -ne 0) { throw 'Nativer Launcher-Build fehlgeschlagen.' }
}
finally { Pop-Location }
Add-Type -Path (Join-Path $PSScriptRoot 'PackageRuntime.cs')
$payload = Join-Path $stage 'runtime.kubpack'
[KubRuntimePackage]::Create([IO.Path]::GetFullPath($RuntimeDirectory), $payload)
$hash = (Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash
$output = [IO.File]::Create($OutputPath)
try {
    $stub = [IO.File]::OpenRead((Join-Path $stage 'KubDHCP.exe'))
    try { $stub.CopyTo($output) } finally { $stub.Dispose() }
    $offset = $output.Position
    $input = [IO.File]::OpenRead($payload)
    try { $input.CopyTo($output) } finally { $input.Dispose() }
    $footer = [IO.BinaryWriter]::new($output, [Text.Encoding]::ASCII, $true)
    $footer.Write([long]$offset)
    $footer.Write([Convert]::FromHexString($hash))
    $footer.Write([Text.Encoding]::ASCII.GetBytes('KUBPACK1'))
    $footer.Dispose()
}
finally { $output.Dispose() }
Write-Output "Native portable package: $OutputPath (v$version)"
