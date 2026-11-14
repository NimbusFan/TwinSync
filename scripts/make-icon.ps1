$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.IO;
public static class TwinSyncIconFrames {
    public static byte[] Dib(Bitmap bitmap) {
        int size = bitmap.Width, maskStride = ((size + 31) / 32) * 4;
        byte[] mask = new byte[maskStride * size];
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream)) {
            writer.Write((uint)40);
            writer.Write(size); writer.Write(size * 2);
            writer.Write((ushort)1); writer.Write((ushort)32);
            writer.Write((uint)0); writer.Write((uint)(size * size * 4));
            writer.Write(0); writer.Write(0); writer.Write((uint)0); writer.Write((uint)0);
            for (int row = 0; row < size; row++) {
                for (int column = 0; column < size; column++) {
                    Color pixel = bitmap.GetPixel(column, size - 1 - row);
                    writer.Write(pixel.B); writer.Write(pixel.G); writer.Write(pixel.R); writer.Write(pixel.A);
                    if (pixel.A == 0) mask[row * maskStride + column / 8] |= (byte)(1 << (7 - column % 8));
                }
            }
            writer.Write(mask); writer.Flush();
            return stream.ToArray();
        }
    }
}
'@
$appRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$outDir = Join-Path $appRoot 'native/assets'
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
$out = Join-Path $outDir 'TwinSync.ico'
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = New-Object 'System.Collections.Generic.List[byte[]]'
foreach ($size in $sizes) {
    $renderSize = $size * 4
    $source = New-Object Drawing.Bitmap $renderSize, $renderSize
    $graphics = [Drawing.Graphics]::FromImage($source)
    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([Drawing.Color]::Transparent)
    $graphics.ScaleTransform(($renderSize / 64.0), ($renderSize / 64.0))
    $tile = New-Object Drawing.Drawing2D.GraphicsPath
    $tile.AddArc(0, 0, 28, 28, 180, 90)
    $tile.AddArc(36, 0, 28, 28, 270, 90)
    $tile.AddArc(36, 36, 28, 28, 0, 90)
    $tile.AddArc(0, 36, 28, 28, 90, 90)
    $tile.CloseFigure()
    $background = New-Object Drawing.SolidBrush ([Drawing.ColorTranslator]::FromHtml('#142337'))
    $graphics.FillPath($background, $tile)
    # The same paths as public/brand/twinsync-symbol.svg, with round 6/64 strokes.
    $loops = New-Object Drawing.Drawing2D.GraphicsPath
    $loops.StartFigure()
    $loops.AddLine(34, 13, 24, 13)
    $loops.AddArc(10, 13, 28, 28, 270, -180)
    $loops.AddLine(24, 41, 29, 41)
    $loops.StartFigure()
    $loops.AddLine(30, 51, 40, 51)
    $loops.AddArc(26, 23, 28, 28, 90, -180)
    $loops.AddLine(40, 23, 35, 23)
    $pen = New-Object Drawing.Pen ([Drawing.Color]::White), 6
    $pen.StartCap = [Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [Drawing.Drawing2D.LineJoin]::Round
    $graphics.DrawPath($pen, $loops)
    $bitmap = New-Object Drawing.Bitmap $size, $size
    $outputGraphics = [Drawing.Graphics]::FromImage($bitmap)
    $outputGraphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $outputGraphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $outputGraphics.DrawImage($source, 0, 0, $size, $size)
    $frameStream = New-Object IO.MemoryStream
    if ($size -eq 256) {
        $bitmap.Save($frameStream, [Drawing.Imaging.ImageFormat]::Png)
    } else {
        # WinForms Icon.ToBitmap requires DIB frames at small sizes. Keep PNG for 256px.
        $dib = [TwinSyncIconFrames]::Dib($bitmap)
        $frameStream.Write($dib, 0, $dib.Length)
    }
    $frames.Add($frameStream.ToArray())
    $frameStream.Dispose(); $bitmap.Dispose(); $outputGraphics.Dispose()
    $graphics.Dispose(); $source.Dispose(); $tile.Dispose(); $background.Dispose(); $loops.Dispose(); $pen.Dispose()
}
$stream = New-Object IO.MemoryStream
$writer = New-Object IO.BinaryWriter($stream)
$writer.Write([UInt16]0); $writer.Write([UInt16]1); $writer.Write([UInt16]$sizes.Length)
$offset = 6 + 16 * $sizes.Length
for ($index = 0; $index -lt $sizes.Length; $index++) {
    $dimension = $sizes[$index]
    if ($dimension -eq 256) { $dimension = 0 }
    $writer.Write([Byte]$dimension); $writer.Write([Byte]$dimension)
    $writer.Write([Byte]0); $writer.Write([Byte]0)
    $writer.Write([UInt16]1); $writer.Write([UInt16]32)
    $writer.Write([UInt32]$frames[$index].Length); $writer.Write([UInt32]$offset)
    $offset += $frames[$index].Length
}
foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
[IO.File]::WriteAllBytes($out, $stream.ToArray())
$writer.Dispose(); $stream.Dispose()
Write-Output ('Created multi-resolution ICO: ' + ($sizes -join ', ') + ' px')
