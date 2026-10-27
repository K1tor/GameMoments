namespace GameMoments.Core.Gaming;

/// <summary>
/// 一款游戏的静态定义：进程名、窗口标题线索、事件源与支持能力。
/// 只描述“能不能自动检测、能不能拿到官方事件”，不包含任何注入或读内存手段。
/// </summary>
public sealed record GameDefinition(
    string Id,
    string DisplayName,
    IReadOnlyList<string> ExecutableNames,
    IReadOnlyList<string> WindowTitleHints,
    string EventSourceId,
    string EventSourceLabel,
    int GsiAppId,
    string? SteamFolderName,
    string? GsiCfgRelativePath,
    IReadOnlyList<string> SupportedEventKinds,
    string Notes)
{
    /// <summary>是否已接通真实的自动事件源（当前仅 CS2 GSI）。</summary>
    public bool SupportsAutomaticEvents => !string.IsNullOrEmpty(EventSourceId);

    /// <summary>是否可以在 Steam 安装目录写入本程序的 GSI 配置文件。</summary>
    public bool SupportsGsiConfig => SupportsAutomaticEvents
        && !string.IsNullOrEmpty(SteamFolderName)
        && !string.IsNullOrEmpty(GsiCfgRelativePath);

    /// <summary>面向用户的支持矩阵文案。</summary>
    public string SupportText => SupportsAutomaticEvents
        ? "支持自动检测 + 自动事件识别"
        : "支持自动检测 / 手动录制，不支持自动事件识别";

    public string CaptureOnlyText => SupportsAutomaticEvents
        ? "事件源：" + EventSourceLabel
        : "支持自动检测 / 手动录制，不支持自动事件识别";
}

/// <summary>
/// 内置游戏目录。已内置的进程名来自公开资料，未识别时可使用「自定义 exe 列表」补充。
/// </summary>
public static class GameCatalog
{
    public const string Cs2AdapterId = "cs2-gsi";
    public const string Cs2GameId = "cs2";
    public const string CustomGamePrefix = "custom:";

    /// <summary>本程序会写入 Steam 目录的 GSI 配置文件名（绝不会覆盖他人文件）。</summary>
    public const string GsiConfigFileName = "gamestate_integration_gamemoments.cfg";

    public static IReadOnlyList<GameDefinition> BuiltIn { get; } =
    [
        new GameDefinition(
            Cs2GameId,
            "Counter-Strike 2",
            ["cs2", "csgo"],
            ["Counter-Strike 2", "Counter-Strike: Global Offensive"],
            Cs2AdapterId,
            "官方 Game State Integration（游戏主动 HTTP 推送）",
            730,
            "Counter-Strike Global Offensive",
            Path.Combine("game", "csgo", "cfg"),
            [
                GameEventKinds.Kill,
                GameEventKinds.Headshot,
                GameEventKinds.Multikill,
                GameEventKinds.Assist,
                GameEventKinds.Death,
                GameEventKinds.RoundWin,
                GameEventKinds.RoundLoss,
                GameEventKinds.BombPlanted,
                GameEventKinds.BombDefused,
                GameEventKinds.BombExploded,
                GameEventKinds.MatchEnd,
            ],
            "事件来自游戏自带的 GSI：游戏把状态 JSON POST 到本机端口，只读、可随时关闭，不注入、不读内存、不接触反作弊。"),

        new GameDefinition(
            "dota2",
            "Dota 2",
            ["dota2"],
            ["Dota 2"],
            string.Empty,
            "未接入",
            570,
            "dota 2 beta",
            null,
            [],
            "Valve 同样提供 GSI 机制，但本版本未实现 Dota 2 适配器，因此只支持自动检测与手动/整局录制。"),

        new GameDefinition(
            "apex",
            "Apex Legends",
            ["r5apex", "r5apex_dx12"],
            ["Apex Legends"],
            string.Empty,
            "未接入",
            0,
            "Apex Legends",
            null,
            [],
            "受 EasyAntiCheat 保护且没有官方事件接口，只支持自动检测与手动/整局录制。"),

        new GameDefinition(
            "pubg",
            "PUBG: BATTLEGROUNDS",
            ["tslgame"],
            ["PLAYERUNKNOWN'S BATTLEGROUNDS", "PUBG: BATTLEGROUNDS"],
            string.Empty,
            "未接入",
            0,
            "PUBG",
            null,
            [],
            "没有公开的事件接口，只支持自动检测与手动/整局录制。"),

        new GameDefinition(
            "naraka",
            "永劫无间",
            ["narakabladepoint"],
            ["NARAKA", "永劫无间"],
            string.Empty,
            "未接入",
            0,
            "NARAKA BLADEPOINT",
            null,
            [],
            "没有公开的事件接口，只支持自动检测与手动/整局录制。"),

        new GameDefinition(
            "l4d2",
            "Left 4 Dead 2",
            ["left4dead2"],
            ["Left 4 Dead 2"],
            string.Empty,
            "未接入",
            0,
            "Left 4 Dead 2",
            null,
            [],
            "没有公开的事件接口，只支持自动检测与手动/整局录制。"),
    ];

    public static GameDefinition? Find(string? gameId)
    {
        if (string.IsNullOrWhiteSpace(gameId)) return null;
        foreach (var definition in BuiltIn)
        {
            if (string.Equals(definition.Id, gameId, StringComparison.OrdinalIgnoreCase)) return definition;
        }
        return null;
    }

    /// <summary>把进程名或路径统一成小写 exe 名（不含扩展名）。</summary>
    public static string NormalizeExecutableName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var text = value.Trim().Trim('"');
        try
        {
            text = Path.GetFileName(text);
        }
        catch
        {
            // 不是合法路径时保持原样。
        }
        var dot = text.LastIndexOf('.');
        if (dot > 0) text = text[..dot];
        return text.ToLowerInvariant();
    }

    /// <summary>
    /// 解析一条自定义进程配置：<c>cs2</c>、<c>cs2.exe</c> 或 <c>cs2=Counter-Strike 2</c>。
    /// </summary>
    public static (string Executable, string? DisplayName)? ParseCustomEntry(string? entry)
    {
        if (string.IsNullOrWhiteSpace(entry)) return null;
        var text = entry.Trim();
        string? displayName = null;
        var eq = text.IndexOf('=');
        if (eq > 0)
        {
            displayName = text[(eq + 1)..].Trim();
            text = text[..eq].Trim();
            if (displayName.Length == 0) displayName = null;
        }
        var executable = NormalizeExecutableName(text);
        return executable.Length == 0 ? null : (executable, displayName);
    }

    /// <summary>根据进程名（可选窗口标题）匹配游戏；未匹配返回 null。</summary>
    public static GameDefinition? Match(string? processName, string? windowTitle, IReadOnlyList<string>? customExecutables)
    {
        var executable = NormalizeExecutableName(processName);
        if (executable.Length == 0) return null;

        foreach (var definition in BuiltIn)
        {
            if (definition.ExecutableNames.Any(name => string.Equals(name, executable, StringComparison.OrdinalIgnoreCase)))
            {
                return definition;
            }
        }

        // 内置列表没命中时，用窗口标题兜底（只匹配已知游戏，避免把启动器/浏览器当游戏）。
        if (!string.IsNullOrWhiteSpace(windowTitle))
        {
            foreach (var definition in BuiltIn)
            {
                foreach (var hint in definition.WindowTitleHints)
                {
                    if (windowTitle.Contains(hint, StringComparison.OrdinalIgnoreCase)
                        && definition.ExecutableNames.Count > 0)
                    {
                        return definition;
                    }
                }
            }
        }

        if (customExecutables is not null)
        {
            foreach (var entry in customExecutables)
            {
                var parsed = ParseCustomEntry(entry);
                if (parsed is null) continue;
                if (!string.Equals(parsed.Value.Executable, executable, StringComparison.OrdinalIgnoreCase)) continue;
                return CustomDefinition(parsed.Value.Executable, parsed.Value.DisplayName);
            }
        }

        return null;
    }

    /// <summary>构造一个用户自定义游戏定义（只支持检测与手动/整局录制）。</summary>
    public static GameDefinition CustomDefinition(string executable, string? displayName)
    {
        var exe = NormalizeExecutableName(executable);
        var name = string.IsNullOrWhiteSpace(displayName) ? exe : displayName!.Trim();
        return new GameDefinition(
            CustomGamePrefix + exe,
            name,
            [exe],
            [],
            string.Empty,
            "未接入",
            0,
            null,
            null,
            [],
            "用户自定义进程：只支持自动检测与手动/整局录制，不支持自动事件识别。");
    }

    public static bool IsCustom(string? gameId) =>
        gameId is not null && gameId.StartsWith(CustomGamePrefix, StringComparison.OrdinalIgnoreCase);
}
