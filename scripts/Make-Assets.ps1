param(
    [Parameter(Mandatory)] [string] $Portrait,
    [Parameter(Mandatory)] [string] $Assets,
    [Parameter(Mandatory)] [string] $Icon
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Path $Assets -Force | Out-Null
$source = [Drawing.Image]::FromFile($Portrait)
try {
    function Export-IconPng([int] $Size) {
        $bitmap = [Drawing.Bitmap]::new($Size, $Size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $attributes = [Drawing.Imaging.ImageAttributes]::new()
        $stream = [IO.MemoryStream]::new()
        try {
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $attributes.SetWrapMode([Drawing.Drawing2D.WrapMode]::TileFlipXY)
            $side = [Math]::Min($source.Width, $source.Height)
            $x = [int](($source.Width - $side) / 2)
            $y = [int](($source.Height - $side) * 0.28)
            $graphics.DrawImage($source, [Drawing.Rectangle]::new(0, 0, $Size, $Size), $x, $y,
                $side, $side, [Drawing.GraphicsUnit]::Pixel, $attributes)
            $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
            return ,$stream.ToArray()
        }
        finally {
            $stream.Dispose()
            $attributes.Dispose()
            $graphics.Dispose()
            $bitmap.Dispose()
        }
    }

    foreach ($asset in @{ Square44 = 44; Square150 = 150; StoreLogo = 50 }.GetEnumerator()) {
        [IO.File]::WriteAllBytes((Join-Path $Assets ($asset.Key + '.png')), (Export-IconPng $asset.Value))
    }
    foreach ($size in 16, 24, 32, 48, 64, 96, 256) {
        [IO.File]::WriteAllBytes((Join-Path $Assets "Square44.targetsize-$size.png"), (Export-IconPng $size))
    }

    function Write-Ico([string] $Path, [scriptblock] $Render) {
        $sizes = 16, 24, 32, 48, 64, 128, 256
        $images = @{}
        foreach ($size in $sizes) { $images[$size] = & $Render $size }
        $writer = [IO.BinaryWriter]::new([IO.File]::Create($Path))
        try {
            $writer.Write([uint16]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]$sizes.Count)
            $offset = 6 + 16 * $sizes.Count
            foreach ($size in $sizes) {
                $dimension = [byte]($size % 256)
                $writer.Write($dimension)
                $writer.Write($dimension)
                $writer.Write([byte]0)
                $writer.Write([byte]0)
                $writer.Write([uint16]1)
                $writer.Write([uint16]32)
                $writer.Write([uint32]$images[$size].Length)
                $writer.Write([uint32]$offset)
                $offset += $images[$size].Length
            }
            foreach ($size in $sizes) { $writer.Write([byte[]]$images[$size]) }
        }
        finally { $writer.Dispose() }
    }

    Write-Ico $Icon { param($size) Export-IconPng $size }

    # File-type icon for archives: a page with a violet zipper, drawn on a 256-unit grid.
    function Export-ArchivePng([int] $Size) {
        $bitmap = [Drawing.Bitmap]::new($Size, $Size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [Drawing.Graphics]::FromImage($bitmap)
        $stream = [IO.MemoryStream]::new()
        try {
            $g.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
            $g.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $g.ScaleTransform($Size / 256.0, $Size / 256.0)
            $stroke = [Math]::Max(6.0, 256.0 / $Size * 1.1)
            $fold = 64
            $page = [Drawing.Drawing2D.GraphicsPath]::new()
            $page.AddLines([Drawing.PointF[]]@(
                [Drawing.PointF]::new(40, 14), [Drawing.PointF]::new(216 - $fold, 14), [Drawing.PointF]::new(216, 14 + $fold),
                [Drawing.PointF]::new(216, 242), [Drawing.PointF]::new(40, 242)))
            $page.CloseFigure()
            $paper = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(255, 252, 251, 255))
            $edge = [Drawing.Pen]::new([Drawing.Color]::FromArgb(255, 120, 104, 168), $stroke)
            $edge.LineJoin = [Drawing.Drawing2D.LineJoin]::Round
            $g.FillPath($paper, $page)
            $flap = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(255, 222, 214, 246))
            $g.FillPolygon($flap, [Drawing.PointF[]]@(
                [Drawing.PointF]::new(216 - $fold, 14), [Drawing.PointF]::new(216 - $fold, 14 + $fold), [Drawing.PointF]::new(216, 14 + $fold)))
            $g.DrawLine($edge, 216 - $fold, 14, 216 - $fold, 14 + $fold)
            $g.DrawLine($edge, 216 - $fold, 14 + $fold, 216, 14 + $fold)

            $accent = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(255, 98, 72, 196))
            $deep = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(255, 70, 48, 156))
            $light = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(255, 236, 231, 251))
            # Zipper tape down the middle, then alternating teeth when there is room to show them.
            $g.FillRectangle($accent, 108, 14, 40, 128)
            if ($Size -ge 32) {
                for ($y = 22; $y -lt 136; $y += 20) {
                    $g.FillRectangle($light, 112, $y, 14, 9)
                    $g.FillRectangle($light, 130, $y + 10, 14, 9)
                }
            }
            # Pull tab
            $tab = [Drawing.Drawing2D.GraphicsPath]::new()
            $tab.AddArc(98, 134, 24, 24, 180, 90); $tab.AddArc(134, 134, 24, 24, 270, 90)
            $tab.AddArc(134, 186, 24, 24, 0, 90); $tab.AddArc(98, 186, 24, 24, 90, 90)
            $tab.CloseFigure()
            $g.FillPath($deep, $tab)
            if ($Size -ge 24) { $g.FillRectangle($light, 118, 170, 20, 24) }
            $g.DrawPath($edge, $page)

            $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
            return ,$stream.ToArray()
        }
        finally {
            $stream.Dispose()
            $g.Dispose()
            $bitmap.Dispose()
        }
    }

    Write-Ico (Join-Path $Assets 'Archive.ico') { param($size) Export-ArchivePng $size }
    [IO.File]::WriteAllBytes((Join-Path $Assets 'ArchiveFile.png'), (Export-ArchivePng 44))
    foreach ($size in 16, 24, 32, 48, 64, 96, 256) {
        [IO.File]::WriteAllBytes((Join-Path $Assets "ArchiveFile.targetsize-$size.png"), (Export-ArchivePng $size))
    }
}
finally { $source.Dispose() }
