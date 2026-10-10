# アプリアイコン（Onta.ico）を元画像から作る。
#   .\Make-OntaIcon.ps1 [-Source onta_icon_source.jpg]
# 元画像は 1:1 の角丸タイル（角の外は白）。角丸の外を透明にした Onta_icon_1024.png と、
# 16〜256 px のエントリを持つ Onta.ico を同じフォルダーへ書き出す。
param(
    [string]$Source = (Join-Path $PSScriptRoot 'onta_icon_source.jpg'),
    # 角丸の半径（元画像 1024 px 基準。生成画像の角の円弧に合わせた値）
    [double]$CornerRadius = 216,
    # 角丸の外周を内側へ寄せる量（元画像 1024 px 基準。JPEG のにじみを切り落とす）
    [double]$Inset = 3
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
$master = 1024
$pngPath = Join-Path $PSScriptRoot 'Onta_icon_1024.png'
$icoPath = Join-Path $PSScriptRoot 'Onta.ico'

# ICO の 1 エントリ分のバイト列を作る。256 px は PNG、それ未満は 32bit DIB
# （256 px 未満の PNG エントリは System.Drawing.Icon など古い読み込み側で化けるため）。
function ConvertTo-IconEntry([System.Drawing.Bitmap]$bmp) {
    $size = $bmp.Width
    $ms = New-Object System.IO.MemoryStream
    if ($size -ge 256) {
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $data = $ms.ToArray()
        $ms.Dispose()
        return , $data
    }
    $w = New-Object System.IO.BinaryWriter $ms
    $maskStride = [int]([math]::Ceiling($size / 32.0) * 4)
    # BITMAPINFOHEADER（高さは XOR + AND の 2 枚分）
    $w.Write([uint32]40)
    $w.Write([int32]$size)
    $w.Write([int32]($size * 2))
    $w.Write([uint16]1)
    $w.Write([uint16]32)
    $w.Write([uint32]0)
    $w.Write([uint32]($size * $size * 4 + $maskStride * $size))
    $w.Write([int32]0)
    $w.Write([int32]0)
    $w.Write([uint32]0)
    $w.Write([uint32]0)
    # XOR（BGRA、下の行から）
    for ($y = $size - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $size; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $w.Write([byte]$c.B)
            $w.Write([byte]$c.G)
            $w.Write([byte]$c.R)
            $w.Write([byte]$c.A)
        }
    }
    # AND マスク（1 = 透明。アルファを持たない描画側向け）
    for ($y = $size - 1; $y -ge 0; $y--) {
        $row = New-Object byte[] $maskStride
        for ($x = 0; $x -lt $size; $x++) {
            if ($bmp.GetPixel($x, $y).A -lt 128) {
                $row[$x -shr 3] = $row[$x -shr 3] -bor (0x80 -shr ($x -band 7))
            }
        }
        $w.Write($row)
    }
    $w.Flush()
    $data = $ms.ToArray()
    $w.Dispose()
    return , $data
}

$src = [System.Drawing.Bitmap]::FromFile((Resolve-Path $Source))
try {
    # 角丸の外を透明にした 1024 px のマスター画像
    $masterBmp = New-Object System.Drawing.Bitmap $master, $master, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($masterBmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $scale = $master / [double]$src.Width
    $r = $CornerRadius * $scale
    $d = 2 * $r
    $x0 = $Inset * $scale
    $x1 = $master - $Inset * $scale
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($x0, $x0, $d, $d, 180, 90)
    $path.AddArc($x1 - $d, $x0, $d, $d, 270, 90)
    $path.AddArc($x1 - $d, $x1 - $d, $d, $d, 0, 90)
    $path.AddArc($x0, $x1 - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $brush = New-Object System.Drawing.TextureBrush $src
    $brush.ScaleTransform($scale, $scale)
    $g.FillPath($brush, $path)
    $brush.Dispose()
    $path.Dispose()
    $g.Dispose()
    $masterBmp.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)

    # 各サイズを作る（半分ずつ縮めて小サイズのつぶれを抑える）
    $entries = @()
    foreach ($size in $sizes) {
        $current = $masterBmp
        while ($current.Width / 2 -ge $size) {
            $half = [int]($current.Width / 2)
            $next = New-Object System.Drawing.Bitmap $half, $half, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
            $gh = [System.Drawing.Graphics]::FromImage($next)
            $gh.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $gh.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $gh.DrawImage($current, 0, 0, $half, $half)
            $gh.Dispose()
            if ($current -ne $masterBmp) { $current.Dispose() }
            $current = $next
        }
        $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $gs = [System.Drawing.Graphics]::FromImage($bmp)
        $gs.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $gs.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $gs.DrawImage($current, 0, 0, $size, $size)
        $gs.Dispose()
        if ($current -ne $masterBmp) { $current.Dispose() }

        $entries += , @{ Size = $size; Data = (ConvertTo-IconEntry $bmp) }
        $bmp.Dispose()
    }
    $masterBmp.Dispose()

    # ICO（ICONDIR + ICONDIRENTRY × n + PNG 本体）
    $fs = [System.IO.File]::Create($icoPath)
    $w = New-Object System.IO.BinaryWriter $fs
    $w.Write([uint16]0)
    $w.Write([uint16]1)
    $w.Write([uint16]$entries.Count)
    $offset = 6 + 16 * $entries.Count
    foreach ($e in $entries) {
        $dim = if ($e.Size -ge 256) { 0 } else { $e.Size }
        $w.Write([byte]$dim)
        $w.Write([byte]$dim)
        $w.Write([byte]0)
        $w.Write([byte]0)
        $w.Write([uint16]1)
        $w.Write([uint16]32)
        $w.Write([uint32]$e.Data.Length)
        $w.Write([uint32]$offset)
        $offset += $e.Data.Length
    }
    foreach ($e in $entries) { $w.Write($e.Data) }
    $w.Dispose()
    $fs.Dispose()
}
finally {
    $src.Dispose()
}

"Wrote $pngPath"
"Wrote $icoPath ($((Get-Item $icoPath).Length) bytes, sizes: $($sizes -join ', '))"
