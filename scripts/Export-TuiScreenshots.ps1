param(
    [string]$SnapshotDirectory = (Join-Path $PSScriptRoot '..\artifacts\startup-smoke'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\docs\images')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
# These are the actual character/attribute buffers exported by --tui-test.
# Render them with the standard Windows console palette, without changing content.
$palette = @('#000000', '#000080', '#008000', '#008080', '#800000', '#800080', '#808000', '#C0C0C0', '#808080', '#0000FF', '#00FF00', '#00FFFF', '#FF0000', '#FF00FF', '#FFFF00', '#FFFFFF')
$brushes = $palette | ForEach-Object { [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml($_)) }
$font = [Drawing.Font]::new('Consolas', 14, [Drawing.FontStyle]::Regular, [Drawing.GraphicsUnit]::Pixel)
try {
    foreach ($language in @('de', 'en')) {
        $heading = if ($language -eq 'de') { 'DHCP-Konfiguration' } else { 'DHCP configuration' }
        $main = Get-ChildItem -LiteralPath $SnapshotDirectory -Filter '*.main.cells' |
            Sort-Object LastWriteTime -Descending |
            Where-Object { [IO.File]::ReadAllText([IO.Path]::ChangeExtension($_.FullName, '.txt')).Contains($heading) } |
            Select-Object -First 1
        if (-not $main) { throw "No $language TUI snapshot found." }
        foreach ($view in @('main', 'monitor')) {
            $source = $main.FullName.Replace('.main.cells', ".$view.cells")
            $reader = [IO.BinaryReader]::new([IO.File]::OpenRead($source))
            $bitmap = $null; $graphics = $null
            try {
                $width = $reader.ReadInt32(); $height = $reader.ReadInt32()
                $bitmap = [Drawing.Bitmap]::new($width * 9, $height * 18)
                $graphics = [Drawing.Graphics]::FromImage($bitmap)
                $graphics.TextRenderingHint = [Drawing.Text.TextRenderingHint]::ClearTypeGridFit
                for ($y = 0; $y -lt $height; $y++) {
                    for ($x = 0; $x -lt $width; $x++) {
                        $character = [char]$reader.ReadUInt16(); $attributes = $reader.ReadUInt16()
                        $graphics.FillRectangle($brushes[($attributes -shr 4) -band 15], $x * 9, $y * 18, 9, 18)
                        if (-not [char]::IsControl($character) -and $character -ne ' ') {
                            $graphics.DrawString([string]$character, $font, $brushes[$attributes -band 15], [single]($x * 9), [single]($y * 18), [Drawing.StringFormat]::GenericTypographic)
                        }
                    }
                }
                $name = if ($view -eq 'main') { "tui-$language.png" } else { "tui-monitor-$language.png" }
                $bitmap.Save((Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) $name), [Drawing.Imaging.ImageFormat]::Png)
                Write-Output "Exported $name from $source"
            }
            finally { $reader.Dispose(); if ($graphics) { $graphics.Dispose() }; if ($bitmap) { $bitmap.Dispose() } }
        }
    }
}
finally { $font.Dispose(); foreach ($brush in $brushes) { $brush.Dispose() } }
