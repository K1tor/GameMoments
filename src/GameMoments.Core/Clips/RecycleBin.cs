using System.Runtime.InteropServices;

namespace GameMoments.Core.Clips;

/// <summary>通过 Shell 把文件移入回收站，避免静默永久删除。</summary>
public static class RecycleBin
{
    private const uint FoDelete = 3;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x0400;

    public static bool TryRecycle(string path, out string? error)
    {
        error = null;
        if (!File.Exists(path))
        {
            error = "文件不存在，可能已被移动或删除。";
            return false;
        }

        var fullPath = Path.GetFullPath(path);
        var operation = new ShFileOpStruct
        {
            wFunc = FoDelete,
            pFrom = fullPath + "\0\0",
            fFlags = FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi,
        };

        try
        {
            var result = SHFileOperationW(ref operation);
            if (result != 0)
            {
                error = $"系统回收站操作失败（代码 {result}）。";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = $"调用回收站失败：{ex.Message}";
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref ShFileOpStruct fileOp);
}
