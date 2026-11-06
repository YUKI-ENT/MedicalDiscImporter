$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$outDir = $PSScriptRoot
function Rounded-Box($g, $color, [single]$x, [single]$y, [single]$w, [single]$h, [single]$radius) {
    $path = [Drawing.Drawing2D.GraphicsPath]::new()
    $d = $radius * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $brush = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml($color))
    try { $g.FillPath($brush, $path) } finally { $brush.Dispose(); $path.Dispose() }
}
function Ring($g, $color, [single]$x, [single]$y, [single]$r, [single]$width) {
    $pen = [Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml($color), $width)
    try { $g.DrawEllipse($pen, $x-$r, $y-$r, $r*2, $r*2) } finally { $pen.Dispose() }
}
function Line($g, $color, [single]$x1, [single]$y1, [single]$x2, [single]$y2, [single]$width) {
    $pen = [Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml($color), $width)
    $pen.StartCap = $pen.EndCap = [Drawing.Drawing2D.LineCap]::Round
    try { $g.DrawLine($pen, $x1, $y1, $x2, $y2) } finally { $pen.Dispose() }
}
function Polygon($g, $color, [single[]]$coordinates) {
    $points = [Drawing.PointF[]]::new($coordinates.Count / 2)
    for ($i=0; $i -lt $points.Length; $i++) { $points[$i] = [Drawing.PointF]::new($coordinates[$i*2], $coordinates[$i*2+1]) }
    $brush = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml($color))
    try { $g.FillPolygon($brush, $points) } finally { $brush.Dispose() }
}
function Icon-Bitmap([string]$kind, [int]$size) {
    $bmp = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    try {
        $g.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.Clear([Drawing.Color]::Transparent)
        $g.ScaleTransform($size/128.0, $size/128.0)
        $background = if ($kind -eq 'A') { '#087D89' } else { '#1D3547' }
        Rounded-Box $g $background 4 4 120 120 27
        if ($kind -eq 'A') {
            Ring $g '#FFFFFF' 61 60 37 8
            Ring $g '#BCE8E9' 61 60 20 4
            $g.FillEllipse([Drawing.Brushes]::White, 55,54,12,12)
            Rounded-Box $g $background 68 77 43 33 5
            Line $g '#FFFFFF' 76 84 105 84 6
            Line $g '#FFFFFF' 76 94 105 94 6
            Line $g '#FFFFFF' 76 104 98 104 6
        } elseif ($kind -eq 'B') {
            Ring $g '#FFFFFF' 62 52 31 9
            Ring $g '#6CD2D5' 62 52 12 5
            Polygon $g '#FFFFFF' @(43,78,80,78,105,98,61,98)
            Line $g '#6CD2D5' 35 105 103 105 7
        } else {
            Ring $g '#FFFFFF' 54 57 31 8
            $g.FillEllipse([Drawing.Brushes]::White,47,50,14,14)
            $pen = [Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml('#6CD2D5'),4)
            $pen.StartCap = $pen.EndCap = [Drawing.Drawing2D.LineCap]::Round
            try { $g.DrawArc($pen,30,33,48,48,215,55) } finally { $pen.Dispose() }
            Rounded-Box $g $background 59 70 57 38 6
            Polygon $g '#6CD2D5' @(66,81,88,81,88,71,111,89,88,107,88,97,66,97)
        }
    } finally { $g.Dispose() }
    return ,$bmp
}
$names = @('A-disc-slices','B-ct-mri','C-disc-send')
$sizes = @(16,20,24,32,40,48,64,128,256)
foreach ($name in $names) {
    $kind = $name.Substring(0,1)
    $bitmap = Icon-Bitmap $kind 256
    try { $bitmap.Save((Join-Path $outDir ($name+'.png')), [Drawing.Imaging.ImageFormat]::Png) } finally { $bitmap.Dispose() }
    $images = @()
    foreach ($size in $sizes) {
        $bitmap = Icon-Bitmap $kind $size
        $memory = [IO.MemoryStream]::new()
        try { $bitmap.Save($memory, [Drawing.Imaging.ImageFormat]::Png); $images += ,$memory.ToArray() } finally { $bitmap.Dispose(); $memory.Dispose() }
    }
    $stream = [IO.File]::Create((Join-Path $outDir ($name+'.ico')))
    $writer = [IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($i=0; $i -lt $sizes.Count; $i++) {
            $side = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
            $writer.Write([byte]$side); $writer.Write([byte]$side); $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
            $offset += $images[$i].Length
        }
        foreach ($bytes in $images) { $writer.Write([byte[]]$bytes) }
    } finally { $writer.Dispose() }
}
$sheet = [Drawing.Bitmap]::new(960, 420)
$graphics = [Drawing.Graphics]::FromImage($sheet)
$font = [Drawing.Font]::new('Yu Gothic UI',18,[Drawing.FontStyle]::Bold)
$small = [Drawing.Font]::new('Yu Gothic UI',11)
$ink = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#1D3547'))
try {
    $graphics.Clear([Drawing.ColorTranslator]::FromHtml('#EDF2F7'))
    $graphics.DrawString('MedicalDiscImporter  |  Icon concepts', $font, $ink, 28, 20)
    $labels = @('A  ディスク＋断層','B  CT / MRI装置','C  ディスク＋送信')
    for ($i=0; $i -lt 3; $i++) {
        $x = 24 + $i * 312
        Rounded-Box $graphics '#FFFFFF' $x 70 288 320 16
        $bitmap = Icon-Bitmap $names[$i].Substring(0,1) 160
        try { $graphics.DrawImageUnscaled($bitmap, $x+64, 90) } finally { $bitmap.Dispose() }
        $graphics.DrawString($labels[$i], $font, $ink, $x+20, 264)
        $graphics.DrawString('16 px       32 px       48 px', $small, $ink, $x+24, 308)
        $j = 0
        foreach ($size in @(16,32,48)) {
            $bitmap = Icon-Bitmap $names[$i].Substring(0,1) $size
            try { $graphics.DrawImageUnscaled($bitmap, $x+38+$j*77, 342 + (48-$size)/2) } finally { $bitmap.Dispose() }
            $j++
        }
    }
    $sheet.Save((Join-Path $outDir 'comparison.png'), [Drawing.Imaging.ImageFormat]::Png)
} finally { $font.Dispose(); $small.Dispose(); $ink.Dispose(); $graphics.Dispose(); $sheet.Dispose() }
Write-Output 'Generated three SVG-matched PNG/ICO candidates and comparison.png.'
