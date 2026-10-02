# Rebuild the code-drawn, multi-resolution Windows icon. No external assets required.
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent
$assetDir = Join-Path $root 'src\OnDemandPerf\Assets'
New-Item -ItemType Directory -Force -Path $assetDir | Out-Null
$images = @()
foreach ($size in @(16, 20, 24, 32, 48, 64, 256)) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $scale = $size / 32.0
    $g.ScaleTransform($scale, $scale)
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc(1, 1, 10, 10, 180, 90)
    $path.AddArc(21, 1, 10, 10, 270, 90)
    $path.AddArc(21, 21, 10, 10, 0, 90)
    $path.AddArc(1, 21, 10, 10, 90, 90)
    $path.CloseFigure()
    $background = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(23, 25, 34))
    $outline = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(69, 228, 237), 1.4)
    $g.FillPath($background, $path)
    $g.DrawPath($outline, $path)
    $cyan = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(69, 228, 237))
    $green = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(104, 234, 113))
    $purple = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(209, 137, 246))
    # Three bold columns stay legible at 16 px; gaps appear at larger sizes.
    foreach ($column in @(@(6, 3, $green), @(14, 5, $cyan), @(22, 4, $purple))) {
        for ($i = 0; $i -lt $column[1]; $i++) { $g.FillRectangle($column[2], [single]$column[0], [single](24 - $i * 4), 4, 3) }
    }
    $stream = [System.IO.MemoryStream]::new()
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $images += ,@($size, $stream.ToArray())
    $stream.Dispose(); $purple.Dispose(); $green.Dispose(); $cyan.Dispose(); $outline.Dispose(); $background.Dispose(); $path.Dispose(); $g.Dispose(); $bitmap.Dispose()
}
$destination = Join-Path $assetDir 'OnDemandPerf.ico'
$file = [System.IO.File]::Create($destination)
$writer = [System.IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$images.Count)
    $offset = 6 + 16 * $images.Count
    foreach ($entry in $images) {
        $dimension = if ($entry[0] -eq 256) { 0 } else { $entry[0] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$entry[1].Length); $writer.Write([uint32]$offset)
        $offset += $entry[1].Length
    }
    foreach ($entry in $images) { $writer.Write([byte[]]$entry[1]) }
} finally { $writer.Dispose(); $file.Dispose() }
Write-Output "Generated $destination"
