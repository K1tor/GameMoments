using System.Runtime.InteropServices;

namespace GameMoments.Core.Hotkeys;

/// <summary>User32 全局快捷键注册封装。</summary>
public static class HotkeyRegistrar
{
    private const int ErrorHotkeyAlreadyRegistered = 1409;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr windowHandle, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr windowHandle, int id);

    /// <summary>注册全局快捷键；失败时返回可读原因（如被占用）。</summary>
    public static bool TryRegister(IntPtr windowHandle, int id, HotkeyGesture gesture, out string? error)
    {
        error = null;
        if (!RegisterHotKey(windowHandle, id, (uint)gesture.Modifiers, gesture.VirtualKey))
        {
            var code = Marshal.GetLastWin32Error();
            error = code == ErrorHotkeyAlreadyRegistered
                ? $"{gesture} 已被其他程序占用，请换一个组合。"
                : $"{gesture} 注册失败（Win32 错误 {code}）。";
            return false;
        }
        return true;
    }

    public static void Unregister(IntPtr windowHandle, int id)
    {
        if (windowHandle != IntPtr.Zero)
        {
            UnregisterHotKey(windowHandle, id);
        }
    }
}
