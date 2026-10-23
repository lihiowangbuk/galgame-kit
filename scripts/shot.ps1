# 截图（DPI 感知版）—— 用于生成 README 里的界面图。
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\shot.ps1 `
#       -Target "D:\dl\Gamex-123456" -Password 你的口令 -Out docs\shot-run.png -AutoMode
#
# 注意：PowerShell 5.1 自己不是 DPI 感知进程，不先 SetProcessDPIAware 的话
# GetWindowRect 返回的是虚拟化尺寸（150% 缩放下 1207x896 会被报成 805x597），
# 按错误尺寸建位图就会把窗口右边裁掉，看起来像"布局溢出"。
param(
    [string]$Exe,
    [string]$Target,
    [string]$Password = '',
    [string]$Out,
    [switch]$AutoMode
)

$repo = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $Exe) { $Exe = Join-Path $repo 'dist\齿轮开箱器.exe' }
if (-not $Out) { $Out = Join-Path $repo 'docs\shot.png' }
if (-not $Target) { throw '需要一个 -Target 目录（要截哪个界面状态）' }
if (-not (Test-Path -LiteralPath $Exe)) { throw "找不到 $Exe，先跑 scripts\build.ps1" }

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
[void][W]::SetProcessDPIAware()

$appArgs = @('--path', $Target)
if ($AutoMode) {
    $appArgs += @('--auto', '--keep-open')
    if ($Password) { $appArgs += @('--password', $Password) }
}

$proc = Start-Process -FilePath $Exe -ArgumentList $appArgs -PassThru
for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 250
    $proc.Refresh()
    if ($proc.MainWindowHandle -ne 0) { break }
}
if ($proc.MainWindowHandle -eq 0) {
    Write-Host '窗口没出来'
    Stop-Process -Id $proc.Id -Force
    exit 1
}

if ($AutoMode) { Start-Sleep -Seconds 12 } else { Start-Sleep -Seconds 2 }

$proc.Refresh()
$h = $proc.MainWindowHandle
$r = New-Object 'W+RECT'
[void][W]::GetWindowRect($h, [ref]$r)
$w = $r.Right - $r.Left
$ht = $r.Bottom - $r.Top

$bmp = New-Object System.Drawing.Bitmap $w, $ht
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
[void][W]::PrintWindow($h, $hdc, 2)
$g.ReleaseHdc($hdc)
$g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

Write-Host "$Out  ${w}x${ht}"
Stop-Process -Id $proc.Id -Force
