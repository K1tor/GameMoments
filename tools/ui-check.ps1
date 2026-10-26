param(
    [string]$Exe = "D:\dsh\default-workspace\GameMoments\src\GameMoments.App\bin\Release\net10.0-windows\GameMoments.exe",
    [string]$DataDir = "D:\dsh\default-workspace\GameMoments\.gmtest",
    [string]$OutDir = "D:\dsh\default-workspace\GameMoments\docs\screenshots",
    [string]$ActionsFile = "D:\dsh\default-workspace\GameMoments\tools\ui-actions.json"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class NativeUi {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out RECT rect, int size);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
[void][NativeUi]::SetProcessDPIAware()

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$env:GAMEMOMENTS_DATA = $DataDir

function Get-Frame($hWnd) {
    $rect = New-Object NativeUi+RECT
    $hr = [NativeUi]::DwmGetWindowAttribute($hWnd, 9, [ref]$rect, 16)
    if ($hr -ne 0) { [void][NativeUi]::GetWindowRect($hWnd, [ref]$rect) }
    return $rect
}

function Get-WindowHandle($proc, $timeoutSec = 25) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        $proc.Refresh()
        if ($proc.HasExited) { throw "进程已退出，退出码 $($proc.ExitCode)" }
        if ($proc.MainWindowHandle -ne [IntPtr]::Zero) { return $proc.MainWindowHandle }
        Start-Sleep -Milliseconds 300
    }
    throw "等待主窗口超时"
}

function Save-Shot($hWnd, $fileName) {
    $rect = Get-Frame $hWnd
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    $bmp = New-Object System.Drawing.Bitmap $width, $height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size $width, $height))
    $g.Dispose()
    $path = Join-Path $OutDir $fileName
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "shot: $path ${width}x${height}"
}

function Click-At($hWnd, [int]$x, [int]$y) {
    $rect = Get-Frame $hWnd
    $sx = $rect.Left + $x
    $sy = $rect.Top + $y
    [void][NativeUi]::SetForegroundWindow($hWnd)
    Start-Sleep -Milliseconds 250
    [void][NativeUi]::SetCursorPos($sx, $sy)
    [NativeUi]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    [NativeUi]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Write-Host "click: $sx,$sy"
    Start-Sleep -Milliseconds 1400
}

function Scroll-At($hWnd, [int]$delta) {
    $rect = Get-Frame $hWnd
    [void][NativeUi]::SetCursorPos([int](($rect.Left + $rect.Right) / 2), [int](($rect.Top + $rect.Bottom) / 2))
    $ud = [System.BitConverter]::ToUInt32([System.BitConverter]::GetBytes([int32]$delta), 0)
    [NativeUi]::mouse_event(0x0800, 0, 0, $ud, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 800
}

Get-Process GameMoments -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 700
$proc = Start-Process -FilePath $Exe -PassThru
$hWnd = Get-WindowHandle $proc
[void][NativeUi]::SetForegroundWindow($hWnd)
Start-Sleep -Seconds 4
Write-Host "window: $hWnd"

if (Test-Path $ActionsFile) {
    $plan = Get-Content -Raw -Encoding UTF8 $ActionsFile | ConvertFrom-Json
    foreach ($action in $plan.actions) {
        switch ($action.type) {
            "shot" { Save-Shot $hWnd $action.name }
            "click" { Click-At $hWnd ([int]$action.x) ([int]$action.y) }
            "sleep" { Start-Sleep -Milliseconds ([int]$action.ms) }
            "wheel" { Scroll-At $hWnd ([int]$action.delta) }
            "key" {
                [void][NativeUi]::SetForegroundWindow($hWnd)
                [System.Windows.Forms.SendKeys]::SendWait($action.text)
                Start-Sleep -Milliseconds 900
            }
        }
    }
} else {
    Save-Shot $hWnd "01-first-run.png"
}

if (-not $proc.HasExited) { $proc.Kill() }
Write-Host "done"
