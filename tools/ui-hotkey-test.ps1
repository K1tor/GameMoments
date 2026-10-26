param(
    [string]$Exe = "D:\dsh\default-workspace\GameMoments\src\GameMoments.App\bin\Release\net10.0-windows\GameMoments.exe",
    [string]$DataDir = "D:\dsh\default-workspace\GameMoments\.gmtest-hk"
)

$ErrorActionPreference = 'Stop'
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class HkNative {
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
"@

$env:GAMEMOMENTS_DATA = $DataDir
Get-Process GameMoments -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
if (Test-Path $DataDir) { Remove-Item $DataDir -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
'{"replaySeconds":15,"postTriggerSeconds":0,"firstRunCompleted":true,"autoStartCapture":true,"showTrayNotifications":false,"closeToTray":true}' |
    Set-Content (Join-Path $DataDir 'settings.json') -Encoding UTF8

$proc = Start-Process -FilePath $Exe -PassThru
for ($i = 0; $i -lt 100; $i++) {
    Start-Sleep -Milliseconds 300
    if ($proc.HasExited) { throw "进程已退出，退出码 $($proc.ExitCode)" }
    $proc.Refresh()
    if ($proc.MainWindowHandle -ne [IntPtr]::Zero) { break }
}
if ($proc.MainWindowHandle -eq [IntPtr]::Zero) { throw 'main window not found' }
"pid=$($proc.Id) hwnd=$($proc.MainWindowHandle)"

# 36s: let the 30s ring buffer fill completely before triggering the save hotkey.
Start-Sleep -Seconds 26
[void][HkNative]::PostMessage($proc.MainWindowHandle, 0x0312, [IntPtr]0x4A01, [IntPtr]::Zero)
Start-Sleep -Seconds 16

$clips = @(Get-ChildItem (Join-Path $DataDir 'clips') -Filter *.mp4 -ErrorAction SilentlyContinue)
"clips=$($clips.Count)"
foreach ($c in $clips) { "  {0}  {1} bytes" -f $c.Name, $c.Length }
foreach ($c in $clips) {
    $probe = & "D:\dsh\default-workspace\GameMoments\tools\ffmpeg\ffprobe.exe" -v error -show_entries format=duration -of default=nw=1:nk=1 $c.FullName
    "  ffprobe duration=$probe"
}

$log = Get-ChildItem (Join-Path $DataDir 'logs') -Filter *.jsonl | Select-Object -First 1
if ($log) {
    Get-Content $log.FullName -Encoding UTF8 -Tail 60 | Where-Object { $_ -match '保存|快捷键|捕获|组装' } |
        ForEach-Object { $o = $_ | ConvertFrom-Json; "LOG {0:HH:mm:ss} {1}: {2}" -f ([datetime]$o.Time), $o.Category, $o.Message }
}

[void][HkNative]::PostMessage($proc.MainWindowHandle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
Start-Sleep -Seconds 3
"aliveAfterClose=$(-not $proc.HasExited)"
if ($log) {
    Get-Content $log.FullName -Encoding UTF8 -Tail 4 |
        ForEach-Object { $o = $_ | ConvertFrom-Json; "TAIL {0:HH:mm:ss} {1}: {2}" -f ([datetime]$o.Time), $o.Category, $o.Message }
}

if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }


