namespace GameMoments.Core.Platform;

/// <summary>
/// 解析并创建应用使用的目录（数据、日志、临时文件、缩略图、默认保存目录）。
/// </summary>
public sealed class AppPaths
{
    public const string DataEnvVar = "GAMEMOMENTS_DATA";
    public const string FfmpegEnvVar = "GAMEMOMENTS_FFMPEG_DIR";

    public string DataDirectory { get; }
    public string DefaultSaveDirectory { get; }
    public string LogDirectory => Path.Combine(DataDirectory, "logs");
    public string TempDirectory => Path.Combine(DataDirectory, "temp");
    public string ThumbnailDirectory => Path.Combine(DataDirectory, "thumbnails");
    public string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public string ClipsIndexFile => Path.Combine(DataDirectory, "clips.json");

    public AppPaths(string? dataDirectory = null, string? defaultSaveDirectory = null)
    {
        DataDirectory = Normalize(dataDirectory)
                        ?? Normalize(Environment.GetEnvironmentVariable(DataEnvVar))
                        ?? Path.Combine(LocalAppDataDirectory(), "GameMoments");
        DefaultSaveDirectory = Normalize(defaultSaveDirectory)
                               ?? Path.Combine(VideosDirectory(), "GameMoments");
        EnsureCreated();
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(TempDirectory);
        Directory.CreateDirectory(ThumbnailDirectory);
    }

    /// <summary>删除上次运行遗留的临时目录（会话结束时会话目录已删除，这里兜底）。</summary>
    public void CleanupStaleTemp(TimeSpan olderThan)
    {
        try
        {
            if (!Directory.Exists(TempDirectory)) return;
            foreach (var dir in Directory.EnumerateDirectories(TempDirectory))
            {
                try
                {
                    var info = new DirectoryInfo(dir);
                    if (DateTime.UtcNow - info.LastWriteTimeUtc > olderThan)
                    {
                        Directory.Delete(dir, recursive: true);
                    }
                }
                catch
                {
                    // 占用中的临时目录跳过，下次启动再清理。
                }
            }
        }
        catch
        {
            // 清理失败不影响启动。
        }
    }

    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (string.IsNullOrWhiteSpace(expanded)) return null;
        try
        {
            return Path.GetFullPath(expanded);
        }
        catch
        {
            return null;
        }
    }

    public static string LocalAppDataDirectory()
    {
        var path = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(path)
            ? Path.Combine(UserProfileDirectory(), "AppData", "Local")
            : path;
    }

    public static string UserProfileDirectory()
    {
        var path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(path)
            ? Environment.CurrentDirectory
            : path;
    }

    public static string VideosDirectory()
    {
        var path = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        return string.IsNullOrWhiteSpace(path) ? Path.Combine(UserProfileDirectory(), "Videos") : path;
    }

    /// <summary>尝试创建目录并写入探针文件，判断目录是否可写。</summary>
    public static bool CanWrite(string directory, out string? error)
    {
        error = null;
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".gm-write-test-{Guid.NewGuid():N}.tmp");
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.WriteByte(1);
            }
            File.Delete(probe);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static long? GetFreeBytes(string directory)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(directory));
            if (string.IsNullOrEmpty(root)) return null;
            var drive = new DriveInfo(root);
            return drive.IsReady ? drive.AvailableFreeSpace : null;
        }
        catch
        {
            return null;
        }
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.##} {units[unit]}";
    }
}
