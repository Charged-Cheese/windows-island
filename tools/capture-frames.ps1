# 连续抓取岛上区域帧，用于检查切页过渡的中间帧。
# 只做屏幕读取与落盘，不注入任何输入。
# 路径全部相对脚本目录拼接，避免中文字面量在 PowerShell 里被按 GBK 解码。
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

$outDir = Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\seq'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$width = 900
$height = 380
$left = [int](($bounds.Width - $width) / 2)

Start-Sleep -Milliseconds 900

for ($i = 0; $i -lt 70; $i++) {
    $bmp = New-Object System.Drawing.Bitmap $width, $height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($left, 0, 0, 0, $bmp.Size)
    $path = Join-Path $outDir ('f{0:D2}.png' -f $i)
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose()
    $bmp.Dispose()
    Start-Sleep -Milliseconds 26
}

Write-Output ("captured into " + $outDir)
