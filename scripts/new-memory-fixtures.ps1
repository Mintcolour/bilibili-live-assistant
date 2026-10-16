param([Parameter(Mandatory)][string]$Directory)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($Directory)
if (Test-Path -LiteralPath $root) { throw '素材目录已存在，拒绝覆盖证据' }
New-Item -ItemType Directory -Path $root | Out-Null
Add-Type -AssemblyName System.Drawing
$random = [Random]::new(16384)
$entries = [Collections.Generic.List[object]]::new()
foreach ($spec in @(@(64,'png'),@(128,'png'),@(256,'png'),@(512,'png'),@(640,'png'),@(128,'jpg'),@(512,'jpg'),@(1024,'jpg'),@(2048,'jpg'))) {
    $side = [int]$spec[0]; $format = [string]$spec[1]
    $bitmap = [Drawing.Bitmap]::new($side,$side,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $bits = $bitmap.LockBits([Drawing.Rectangle]::new(0,0,$side,$side),[Drawing.Imaging.ImageLockMode]::WriteOnly,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $pixels = [byte[]]::new($side * $side * 4); $random.NextBytes($pixels)
            if ($format -eq 'jpg') { for ($i=3; $i -lt $pixels.Length; $i+=4) { $pixels[$i]=255 } }
            [Runtime.InteropServices.Marshal]::Copy($pixels,0,$bits.Scan0,$pixels.Length)
        } finally { $bitmap.UnlockBits($bits) }
        $filename = '{0:D2}-{1}.{2}' -f $entries.Count,$side,$format
        $path = Join-Path $root $filename
        if ($format -eq 'jpg') {
            $encoder = [Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object MimeType -eq 'image/jpeg'
            $parameters = [Drawing.Imaging.EncoderParameters]::new(1)
            try { $parameters.Param[0] = [Drawing.Imaging.EncoderParameter]::new([Drawing.Imaging.Encoder]::Quality,[long]35); $bitmap.Save($path,$encoder,$parameters) }
            finally { $parameters.Dispose() }
        } else { $bitmap.Save($path,[Drawing.Imaging.ImageFormat]::Png) }
        foreach ($limit in @(64,128,256)) {
            $size = [Math]::Min($side,$limit)
            $thumb = [Drawing.Bitmap]::new($size,$size,[Drawing.Imaging.PixelFormat]::Format32bppPArgb)
            try {
                $graphics = [Drawing.Graphics]::FromImage($thumb)
                try { $graphics.CompositingMode=[Drawing.Drawing2D.CompositingMode]::SourceCopy; $graphics.InterpolationMode=[Drawing.Drawing2D.InterpolationMode]::HighQualityBilinear; $graphics.DrawImage($bitmap,[Drawing.Rectangle]::new(0,0,$size,$size)) }
                finally { $graphics.Dispose() }
                $thumb.Save((Join-Path $root ($filename + '.thumb' + $limit + '.png')),[Drawing.Imaging.ImageFormat]::Png)
            } finally { $thumb.Dispose() }
        }
        $length = (Get-Item -LiteralPath $path).Length
        if ($length -gt 2MB) { throw '生成正文超过正式下载上限' }
        $entries.Add(@{file=$filename; format=$format; width=$side; height=$side; bytes=$length})
    } finally { $bitmap.Dispose() }
}
# A deterministic two-frame GIF, transparent background and explicit frame delay.
$gif = [IO.MemoryStream]::new(); $binary = [IO.BinaryWriter]::new($gif)
try {
    $binary.Write([Text.Encoding]::ASCII.GetBytes('GIF89a')); $binary.Write([ushort]64); $binary.Write([ushort]64)
    $binary.Write([byte[]]@(0xF1,0,0,0,0,0,50,150,230,230,80,130,250,210,80))
    foreach ($frame in @(1,2)) {
        $binary.Write([byte[]]@(0x21,0xF9,4,5,10,0,0,0,0x2C,0,0,0,0,64,0,64,0,0,2))
        $packed = [Collections.Generic.List[byte]]::new(); $value=0; $count=0
        foreach ($code in (@(for($pixel=0; $pixel -lt 4096; $pixel++) { 4; if($pixel % 64 -lt 8) { 0 } else { $frame } }) + @(5))) {
            $value = $value -bor ($code -shl $count); $count += 3
            while($count -ge 8) { $packed.Add([byte]($value -band 255)); $value = $value -shr 8; $count -= 8 }
        }
        if($count -gt 0) { $packed.Add([byte]$value) }
        $payload=$packed.ToArray()
        for($offset=0; $offset -lt $payload.Length; $offset+=255) { $take=[Math]::Min(255,$payload.Length-$offset); $binary.Write([byte]$take); $binary.Write($payload,$offset,$take) }
        $binary.Write([byte]0)
    }
    $binary.Write([byte]0x3B)
    [IO.File]::WriteAllBytes((Join-Path $root '09-animated.gif'),$gif.ToArray())
    $entries.Add(@{file='09-animated.gif';format='gif';width=64;height=64;bytes=$gif.Length})
} finally { $binary.Dispose(); $gif.Dispose() }
$client = [Net.Http.HttpClient]::new(); $client.Timeout=[TimeSpan]::FromSeconds(15)
try {
    $webp=$client.GetByteArrayAsync('https://www.gstatic.com/webp/gallery/1.webp').GetAwaiter().GetResult()
    if($webp.Length -gt 2MB -or [Text.Encoding]::ASCII.GetString($webp,8,4) -ne 'WEBP') { throw 'WebP 素材无效' }
    [IO.File]::WriteAllBytes((Join-Path $root '10-gallery.webp'),$webp)
    $entries.Add(@{file='10-gallery.webp';format='webp';width=550;height=368;bytes=$webp.Length;source='https://www.gstatic.com/webp/gallery/1.webp'})
} finally { $client.Dispose() }
foreach($entry in $entries) { $entry.sha256=(Get-FileHash -LiteralPath (Join-Path $root $entry.file) -Algorithm SHA256).Hash }
@{version=1;seed=16384;entries=@($entries.ToArray())} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root 'manifest.json') -Encoding utf8
$entries | ConvertTo-Json -Depth 3
