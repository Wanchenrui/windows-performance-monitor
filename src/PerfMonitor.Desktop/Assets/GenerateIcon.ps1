param([string]$OutputPath = (Join-Path $PSScriptRoot 'PerfMonitor.ico'))

# Original pulse mark, drawn with Windows' System.Drawing; no downloaded artwork or packages.
Add-Type -AssemblyName System.Drawing
$frames = @()
foreach ($size in @(16, 24, 32, 48, 256)) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $inset = $size * 0.045
    $edge = $size * 0.91
    $corner = $size * 0.36
    $path.AddArc($inset, $inset, $corner, $corner, 180, 90)
    $path.AddArc($inset + $edge - $corner, $inset, $corner, $corner, 270, 90)
    $path.AddArc($inset + $edge - $corner, $inset + $edge - $corner, $corner, $corner, 0, 90)
    $path.AddArc($inset, $inset + $edge - $corner, $corner, $corner, 90, 90)
    $path.CloseFigure()
    $background = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 25, 36, 49))
    $graphics.FillPath($background, $path)
    $pulse = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 102, 221, 196),
        [single][Math]::Max(1.5, $size * 0.078))
    $pulse.StartCap = $pulse.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pulse.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $coordinates = @(@(0.20, 0.54), @(0.34, 0.54), @(0.43, 0.31),
        @(0.53, 0.73), @(0.63, 0.46), @(0.80, 0.46))
    $points = [System.Drawing.PointF[]]@($coordinates | ForEach-Object {
        [System.Drawing.PointF]::new([single]($_[0] * $size), [single]($_[1] * $size))
    })
    $graphics.DrawLines($pulse, $points)
    $png = [System.IO.MemoryStream]::new()
    $bitmap.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
    $frames += @{ Size = $size; Bytes = $png.ToArray() }
    $png.Dispose()
    $pulse.Dispose()
    $background.Dispose()
    $path.Dispose()
    $graphics.Dispose()
    $bitmap.Dispose()
}

$file = [System.IO.File]::Create([System.IO.Path]::GetFullPath($OutputPath))
$writer = [System.IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length)
        $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
}
finally { $writer.Dispose(); $file.Dispose() }
