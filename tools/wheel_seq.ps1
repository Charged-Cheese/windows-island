# 滚轮切页中间帧序列：注入真实滚轮 + 连续抓岛区域 PNG。
# 只做自测，不参与发布。
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W {
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, int d, UIntPtr e);
}
"@
$out = Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\wheelseq'
New-Item -ItemType Directory -Force -Path $out | Out-Null
Get-ChildItem $out -Filter *.png | Remove-Item -Force

$steps = if ($args.Count -ge 1) { [int]$args[0] } else { 1 }
$W = 900; $H = 470
$left = [int]((2560 - $W) / 2)

for ($i = 0; $i -lt $steps; $i++) {
    [W]::mouse_event(0x0800, 0, 0, [uint32](-120 -band 0xFFFFFFFF), [UIntPtr]::Zero)
}

$sw = [Diagnostics.Stopwatch]::StartNew()
for ($i = 0; $i -lt 26; $i++) {
    $bmp = New-Object System.Drawing.Bitmap $W, $H
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($left, 0, 0, 0, $bmp.Size)
    $bmp.Save((Join-Path $out ("s{0:D2}.png" -f $i)), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Output ("s{0:D2} @ {1}ms" -f $i, $sw.ElapsedMilliseconds)
    Start-Sleep -Milliseconds 20
}
