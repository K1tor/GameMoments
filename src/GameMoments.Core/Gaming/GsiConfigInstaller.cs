using System.Text;
using System.Text.RegularExpressions;
using GameMoments.Core.Logging;
using Microsoft.Win32;

namespace GameMoments.Core.Gaming;

/// <summary>一份 GSI 配置文件的现状。</summary>
public sealed record GsiConfigStatus(
    string GameId,
    string? ConfigPath,
    bool Exists,
    bool OwnedByGameMoments,
    int? ConfiguredPort,
    string Detail)
{
    public bool Installed => Exists && OwnedByGameMoments;
}

/// <summary>安装/卸载结果。FallbackPath 用于游戏目录不可写时导出的备用配置文件。</summary>
public sealed record GsiConfigResult(bool Success, string Message, string? Path, string? FallbackPath = null);

/// <summary>
/// 在 Steam 游戏目录里安装本程序的 GSI 配置（例如 CS2 的 gamestate_integration_*.cfg）。
/// 只写自己命名的文件，遇到同类文件绝不覆盖；卸载即删除该文件，可完全还原。
/// </summary>
public sealed class GsiConfigInstaller
{
    /// <summary>配置里的标记，用于判定文件是否由本程序写入。</summary>
    public const string Marker = "GameMoments";

    private readonly AppLogger _logger;

    public GsiConfigInstaller(AppLogger logger)
    {
        _logger = logger;
    }

    /// <summary>生成 CS2 所用的 GSI 配置文本（UTF-8 无 BOM 写入）。</summary>
    public static string BuildConfigContent(int port)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"\"{Marker} Smart Recording GSI\"");
        builder.AppendLine("{");
        builder.AppendLine($"    \"uri\"               \"http://127.0.0.1:{port}/cs2\"");
        builder.AppendLine("    \"timeout\"           \"5.0\"");
        builder.AppendLine("    \"buffer\"            \"0.1\"");
        builder.AppendLine("    \"throttle\"          \"0.2\"");
        builder.AppendLine("    \"heartbeat\"         \"30.0\"");
        builder.AppendLine("    \"data\"");
        builder.AppendLine("    {");
        builder.AppendLine("        \"provider\"           \"1\"");
        builder.AppendLine("        \"map\"                \"1\"");
        builder.AppendLine("        \"round\"              \"1\"");
        builder.AppendLine("        \"player_id\"          \"1\"");
        builder.AppendLine("        \"player_state\"       \"1\"");
        builder.AppendLine("        \"player_weapons\"     \"1\"");
        builder.AppendLine("        \"player_match_stats\" \"1\"");
        builder.AppendLine("        \"map_round_wins\"     \"1\"");
        builder.AppendLine("    }");
        builder.AppendLine("}");
        return builder.ToString();
    }

    /// <summary>找到游戏的安装目录（通过 Steam 库列表），找不到返回 null。</summary>
    public static string? FindGameDirectory(GameDefinition definition)
    {
        if (!definition.SupportsGsiConfig || string.IsNullOrWhiteSpace(definition.SteamFolderName)) return null;

        foreach (var library in EnumerateSteamLibraries())
        {
            var candidate = Path.Combine(library, "steamapps", "common", definition.SteamFolderName!);
            if (Directory.Exists(candidate)) return candidate;
        }

        return null;
    }

    /// <summary>解析出配置文件的完整路径（不要求文件已存在）。传入 configDirectory 时直接使用该目录（用于自定义安装位置或演练）。</summary>
    public static string? ResolveConfigPath(GameDefinition definition, string? configDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(configDirectory))
        {
            return Path.Combine(Path.GetFullPath(configDirectory), GameCatalog.GsiConfigFileName);
        }

        if (!definition.SupportsGsiConfig || string.IsNullOrWhiteSpace(definition.GsiCfgRelativePath)) return null;
        var gameDirectory = FindGameDirectory(definition);
        if (gameDirectory is null) return null;
        return Path.Combine(gameDirectory, definition.GsiCfgRelativePath, GameCatalog.GsiConfigFileName);
    }

    public GsiConfigStatus Query(GameDefinition definition, string? configDirectory = null)
    {
        var path = ResolveConfigPath(definition, configDirectory);
        if (path is null)
        {
            var gameDirectory = FindGameDirectory(definition);
            return new GsiConfigStatus(
                definition.Id,
                null,
                false,
                false,
                null,
                gameDirectory is null
                    ? "未找到游戏安装目录（可能没有安装，或 Steam 库位于非常规位置）。"
                    : "游戏目录缺少 cfg 子目录。");
        }

        if (!File.Exists(path))
        {
            return new GsiConfigStatus(definition.Id, path, false, false, null, "尚未安装本程序的 GSI 配置。");
        }

        try
        {
            var text = File.ReadAllText(path);
            var ours = text.Contains(Marker, StringComparison.OrdinalIgnoreCase);
            var port = ExtractPort(text);
            return new GsiConfigStatus(
                definition.Id,
                path,
                true,
                ours,
                port,
                ours ? "已安装本程序的 GSI 配置。" : "该文件名已被其他程序占用，本程序不会覆盖它。");
        }
        catch (Exception ex)
        {
            return new GsiConfigStatus(definition.Id, path, true, false, null, "读取配置失败：" + ex.Message);
        }
    }

    /// <summary>安装配置；已存在同名且非本程序写入的文件时放弃，避免破坏用户/其他软件的文件。</summary>
    public GsiConfigResult Install(GameDefinition definition, int port, string? configDirectory = null)
    {
        var configPath = ResolveConfigPath(definition, configDirectory);
        if (configPath is null)
        {
            return new GsiConfigResult(false, $"未找到 {definition.DisplayName} 的配置文件目录，请确认游戏已安装。", null);
        }

        var directory = Path.GetDirectoryName(configPath);
        if (directory is null || !Directory.Exists(directory))
        {
            return new GsiConfigResult(false, "游戏的 cfg 目录不存在，无法写入配置。", configPath);
        }

        if (File.Exists(configPath))
        {
            try
            {
                var existing = File.ReadAllText(configPath);
                if (!existing.Contains(Marker, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.Warn("事件源", "目标 GSI 配置已存在且不是本程序写入的，已跳过，不会覆盖", configPath);
                    return new GsiConfigResult(false,
                        $"已存在同名配置文件（不是本程序写入的）：{configPath}\n为避免破坏其他软件，本程序不会覆盖它。",
                        configPath);
                }
            }
            catch (Exception ex)
            {
                return new GsiConfigResult(false, "读取现有配置失败：" + ex.Message, configPath);
            }
        }

        try
        {
            var content = BuildConfigContent(port);
            File.WriteAllText(configPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            _logger.Info("事件源", $"已写入 GSI 配置（端口 {port}）", configPath);
            return new GsiConfigResult(true, $"已写入 {configPath}（端口 {port}）。删除该文件即可完全还原。", configPath);
        }
        catch (Exception ex)
        {
            _logger.Error("事件源", "写入 GSI 配置失败", ex, configPath);

            // 游戏目录可能被权限、反作弊或安全软件保护：不假装成功，改为导出同内容供用户手动放置。
            var (fallback, exportError) = TryExportFallback(port);
            if (fallback is not null)
            {
                return new GsiConfigResult(
                    false,
                    $"无法写入游戏目录（{ex.GetType().Name}：{ex.Message}）。\n已把相同的配置导出到：{fallback}\n请把它复制到 {configPath}，或右键以管理员身份运行本程序后重试；放好后重启游戏即可生效。",
                    configPath,
                    fallback);
            }

            var note = exportError is null ? string.Empty : $"\n备用导出同样失败：{exportError}";
            return new GsiConfigResult(
                false,
                $"写入配置失败：{ex.Message}{note}\n请以管理员身份运行本程序重试，或手动创建 {configPath}。",
                configPath);
        }
    }

    /// <summary>把配置导出到本程序数据目录（失败则退到临时目录），供用户手动复制到游戏目录。</summary>
    private (string? Path, string? Error) TryExportFallback(int port)
    {
        var candidates = new List<string>();
        try
        {
            candidates.Add(Path.Combine(Platform.AppPaths.LocalAppDataDirectory(), "GameMoments", "gsi"));
        }
        catch (Exception ex)
        {
            _logger.Warn("事件源", "解析本程序数据目录失败", ex.Message);
        }

        candidates.Add(Path.Combine(Path.GetTempPath(), "GameMoments-gsi"));

        string? lastError = null;
        foreach (var directory in candidates)
        {
            try
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, GameCatalog.GsiConfigFileName);
                File.WriteAllText(path, BuildConfigContent(port), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                _logger.Warn("事件源", "已把 GSI 配置导出到备用位置，等待用户手动放置", path);
                return (path, null);
            }
            catch (Exception ex)
            {
                lastError = $"{directory}：{ex.GetType().Name}：{ex.Message}";
                _logger.Warn("事件源", "导出备用 GSI 配置失败", lastError);
            }
        }

        return (null, lastError);
    }

    /// <summary>卸载：仅当文件由本程序写入时删除。</summary>
    public GsiConfigResult Remove(GameDefinition definition, string? configDirectory = null)
    {
        var path = ResolveConfigPath(definition, configDirectory);
        if (path is null || !File.Exists(path))
        {
            return new GsiConfigResult(true, "本程序未安装 GSI 配置，无需清理。", path);
        }

        try
        {
            var text = File.ReadAllText(path);
            if (!text.Contains(Marker, StringComparison.OrdinalIgnoreCase))
            {
                return new GsiConfigResult(false, "该文件不是本程序写入的，已保留。", path);
            }

            File.Delete(path);
            _logger.Info("事件源", "已删除本程序的 GSI 配置", path);
            return new GsiConfigResult(true, "已删除本程序写入的 GSI 配置。", path);
        }
        catch (Exception ex)
        {
            return new GsiConfigResult(false, "删除配置失败：" + ex.Message, path);
        }
    }

    private static int? ExtractPort(string text)
    {
        var match = Regex.Match(text, @"http://127\.0\.0\.1:(\d{2,5})", RegexOptions.IgnoreCase);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var port)) return port;
        return null;
    }

    /// <summary>枚举本机的 Steam 库根目录（含 Steam 安装目录本身）。</summary>
    public static IReadOnlyList<string> EnumerateSteamLibraries()
    {
        var roots = new List<string>();
        var steamRoot = FindSteamRoot();
        if (steamRoot is not null && Directory.Exists(steamRoot)) roots.Add(steamRoot);

        foreach (var root in roots.ToArray())
        {
            var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            foreach (var library in ParseLibraryFolders(vdf))
            {
                if (!roots.Any(r => string.Equals(r, library, StringComparison.OrdinalIgnoreCase))) roots.Add(library);
            }
        }

        return roots;
    }

    private static IEnumerable<string> ParseLibraryFolders(string vdfPath)
    {
        if (!File.Exists(vdfPath)) yield break;
        string text;
        try
        {
            text = File.ReadAllText(vdfPath);
        }
        catch
        {
            yield break;
        }

        foreach (Match match in Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase))
        {
            var path = match.Groups[1].Value.Replace("\\\\", "\\");
            if (Directory.Exists(path)) yield return path;
        }
    }

    private static string? FindSteamRoot()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is string steamPath && steamPath.Length > 0)
            {
                return steamPath.Replace('/', '\\');
            }
        }
        catch
        {
            // 注册表读取失败时用常见路径兜底。
        }

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"),
            @"C:\Program Files (x86)\Steam",
            @"D:\Steam",
        };

        return candidates.FirstOrDefault(Directory.Exists);
    }
}
