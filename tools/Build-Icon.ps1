# Render the repository's vector artwork to the embedded PNG and multi-size Windows icon.
# Run with Windows PowerShell -STA -File tools/Build-Icon.ps1.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase
$assetDir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../src/Nina.FieldKit.Plugin/Assets'))
[xml]$svg = Get-Content -Raw -LiteralPath (Join-Path $assetDir 'field-kit.svg')
$geometry = [System.Windows.Media.Geometry]::Parse($svg.svg.path.d)
$brush = [System.Windows.Media.BrushConverter]::new().ConvertFromString($svg.svg.path.fill)
$frames = @()
foreach ($size in @(16, 24, 32, 48, 64, 128, 256, 512)) {
    $visual = [System.Windows.Media.DrawingVisual]::new()
    $drawing = $visual.RenderOpen()
    $drawing.PushTransform([System.Windows.Media.ScaleTransform]::new($size / 64.0, $size / 64.0))
    $drawing.DrawGeometry($brush, $null, $geometry)
    $drawing.Pop(); $drawing.Close()
    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [System.IO.MemoryStream]::new()
    $encoder.Save($stream)
    $bytes = $stream.ToArray(); $stream.Dispose()
    if ($size -eq 512) { [System.IO.File]::WriteAllBytes((Join-Path $assetDir 'field-kit.png'), $bytes) }
    else { $frames += [pscustomobject]@{ Size = $size; Bytes = $bytes } }
}
$file = [System.IO.File]::Create((Join-Path $assetDir 'field-kit.ico'))
$writer = [System.IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length); $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
} finally { $writer.Dispose(); $file.Dispose() }
