using System.Runtime.InteropServices;

namespace GameMoments.Core.Platform;

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public override string ToString() => $"{Width}×{Height}";
}

/// <summary>一块物理显示器。</summary>
public sealed record DisplayInfo(int Index, string DeviceName, string FriendlyName, PixelRect Bounds, bool IsPrimary)
{
    public string SizeText => $"{Bounds.Width}×{Bounds.Height}";

    public string Label
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(FriendlyName) ? $"显示器 {Index + 1}" : FriendlyName;
            var primary = IsPrimary ? "（主显示器）" : string.Empty;
            return $"{name}{primary} · {SizeText}";
        }
    }
}

public static class Displays
{
    public static IReadOnlyList<DisplayInfo> Enumerate()
    {
        var raw = new List<(PixelRect Bounds, string Device, bool Primary)>();
        try
        {
            NativeDisplay.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr hdc, ref NativeDisplay.RECT rect, IntPtr data) =>
            {
                var info = new NativeDisplay.MONITORINFOEX
                {
                    cbSize = Marshal.SizeOf<NativeDisplay.MONITORINFOEX>(),
                    szDevice = string.Empty,
                };
                if (NativeDisplay.GetMonitorInfo(monitor, ref info))
                {
                    var bounds = new PixelRect(info.rcMonitor.Left, info.rcMonitor.Top,
                        info.rcMonitor.Right - info.rcMonitor.Left, info.rcMonitor.Bottom - info.rcMonitor.Top);
                    var primary = (info.dwFlags & 1) != 0;
                    raw.Add((bounds, info.szDevice, primary));
                }
                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // 枚举失败时退回主屏尺寸。
        }

        if (raw.Count == 0)
        {
            raw.Add((new PixelRect(0, 0, 1920, 1080), string.Empty, true));
        }

        var ordered = raw
            .OrderByDescending(item => item.Primary)
            .ThenBy(item => item.Bounds.X)
            .ThenBy(item => item.Bounds.Y)
            .ToList();

        var result = new List<DisplayInfo>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            var (bounds, device, primary) = ordered[i];
            result.Add(new DisplayInfo(i, device, FriendlyNameOf(device), bounds, primary));
        }
        return result;
    }

    private static string FriendlyNameOf(string deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName)) return string.Empty;
        try
        {
            var device = new NativeDisplay.DISPLAY_DEVICE
            {
                cb = Marshal.SizeOf<NativeDisplay.DISPLAY_DEVICE>(),
                DeviceName = string.Empty,
                DeviceString = string.Empty,
                DeviceID = string.Empty,
                DeviceKey = string.Empty,
            };
            if (NativeDisplay.EnumDisplayDevices(deviceName, 0, ref device, 0))
            {
                return device.DeviceString;
            }
        }
        catch
        {
            // 忽略。
        }
        return string.Empty;
    }
}

internal static class NativeDisplay
{
    internal delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DISPLAY_DEVICE
    {
        public int cb;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;

        public uint StateFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [DllImport("user32.dll")]
    internal static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clipRect, MonitorEnumProc proc, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool EnumDisplayDevices(string? device, uint deviceNumber, ref DISPLAY_DEVICE displayDevice, uint flags);
}
