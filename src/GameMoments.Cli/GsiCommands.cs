using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using GameMoments.Core.Gaming;
using GameMoments.Core.Logging;

namespace GameMoments.Cli;

/// <summary>
/// 智能录制相关的命令行子命令：真实事件源配置的安装/卸载/查询、真实进程检测、
/// 以及用真实 HTTP 服务器 + 合成报文的链路冒烟测试。
/// 注意：冒烟测试里的报文体是合成数据，只验证「HTTP → 适配器 → 事件」链路，
/// 不代表真实对局测试；界面与文档都不会把它当作真实游戏事件展示。
/// </summary>
internal static class GsiCommands
{
    public static int Status(Dictionary<string, string?> options)
    {
        var logger = CreateSilentLogger(options);
        var definition = GameCatalog.Find(GameCatalog.Cs2GameId);
        if (definition is null)
        {
            Console.Error.WriteLine("找不到 CS2 的游戏定义。");
            return 2;
        }

        var installer = new GsiConfigInstaller(logger);
        var target = options.TryGetValue("target", out var targetDir) && !string.IsNullOrWhiteSpace(targetDir) ? targetDir : null;
        var status = installer.Query(definition, target);
        var gameDirectory = GsiConfigInstaller.FindGameDirectory(definition);
        var port = options.TryGetValue("port", out var raw) && int.TryParse(raw, out var p) ? p : 48123;

        Console.WriteLine("== CS2 事件源（官方 GSI）==");
        Console.WriteLine($"游戏定义      : {definition.DisplayName}（{definition.SupportText}）");
        Console.WriteLine($"游戏目录      : {gameDirectory ?? "未找到（Steam 库中未安装或路径无法解析）"}");
        Console.WriteLine($"配置文件      : {status.ConfigPath}");
        Console.WriteLine($"文件存在      : {(status.Exists ? "是" : "否")}");
        Console.WriteLine($"由本程序安装  : {(status.OwnedByGameMoments ? "是" : "否")}");
        Console.WriteLine($"当前配置端口  : {(status.ConfiguredPort?.ToString(CultureInfo.InvariantCulture) ?? "-")}");
        Console.WriteLine($"期望端口      : {port}");
        Console.WriteLine($"状态          : {status.Detail}");
        logger.Dispose();
        return status.Exists ? 0 : 1;
    }

    public static int Install(Dictionary<string, string?> options)
    {
        var logger = CreateSilentLogger(options);
        var definition = GameCatalog.Find(GameCatalog.Cs2GameId);
        if (definition is null)
        {
            Console.Error.WriteLine("找不到 CS2 的游戏定义。");
            return 2;
        }

        var port = options.TryGetValue("port", out var raw) && int.TryParse(raw, out var parsed) && parsed is > 0 and < 65536
            ? parsed
            : 48123;
        var installer = new GsiConfigInstaller(logger);
        var target = options.TryGetValue("target", out var targetDir) && !string.IsNullOrWhiteSpace(targetDir) ? targetDir : null;
        var result = installer.Install(definition, port, target);
        Console.WriteLine(result.Success ? $"已写入：{result.Path}" : $"未写入：{result.Message}");
        if (result.Success)
        {
            Console.WriteLine(result.Message);
            Console.WriteLine("可逆说明：删除上面这个文件即可完全还原；本程序不会修改其他 GSI 配置文件。");
        }
        logger.Dispose();
        return result.Success ? 0 : 1;
    }

    public static int Remove(Dictionary<string, string?> options)
    {
        var logger = CreateSilentLogger(options);
        var definition = GameCatalog.Find(GameCatalog.Cs2GameId);
        if (definition is null)
        {
            Console.Error.WriteLine("找不到 CS2 的游戏定义。");
            return 2;
        }

        var installer = new GsiConfigInstaller(logger);
        var target = options.TryGetValue("target", out var targetDir) && !string.IsNullOrWhiteSpace(targetDir) ? targetDir : null;
        var result = installer.Remove(definition, target);
        Console.WriteLine(result.Message);
        logger.Dispose();
        return result.Success ? 0 : 1;
    }

    public static int Detect(Dictionary<string, string?> options)
    {
        var logger = CreateSilentLogger(options);
        var settings = new GameMoments.Core.Configuration.AppSettings();
        using var detection = new GameDetectionService(logger, () => settings);
        var games = detection.Scan();

        Console.WriteLine("== 进程检测（真实扫描）==");
        if (games.Count == 0)
        {
            Console.WriteLine("当前没有检测到受支持的游戏进程。");
            Console.WriteLine("提示：可用 --custom mygame.exe 追加自定义进程名再试。");
            if (options.TryGetValue("custom", out var custom) && !string.IsNullOrWhiteSpace(custom))
            {
                settings.CustomGameExecutables.Add(custom!);
                games = detection.Scan();
                if (games.Count > 0)
                {
                    Console.WriteLine("按自定义进程名重新扫描结果：");
                }
            }
        }

        foreach (var game in games)
        {
            Console.WriteLine($"  {game.DisplayName}  [{game.ProcessName}]  前台={game.IsForeground}");
            Console.WriteLine($"    支持：{game.SupportText}");
        }

        Console.WriteLine();
        Console.WriteLine("== 支持矩阵（内置目录）==");
        foreach (var definition in GameCatalog.BuiltIn)
        {
            Console.WriteLine($"  {definition.DisplayName,-12} 进程={string.Join(", ", definition.ExecutableNames)}");
            Console.WriteLine($"    {definition.SupportText}");
        }

        logger.Dispose();
        return 0;
    }

    /// <summary>
    /// 启动真实的本机 HTTP 事件源，再用真实 HTTP 请求把合成的 CS2 报文推进去，
    /// 断言适配器能推导出预期事件。链路是真的，报文体是合成的。
    /// </summary>
    public static async Task<int> SmokeAsync(Dictionary<string, string?> options)
    {
        var logger = CreateSilentLogger(options);
        var desiredPort = options.TryGetValue("port", out var raw) && int.TryParse(raw, out var parsed) ? parsed : 48123;

        using var server = new GsiServer(logger);
        if (!server.Start(desiredPort, out var port, out var startError))
        {
            Console.Error.WriteLine($"事件源启动失败：{startError}");
            logger.Dispose();
            return 2;
        }

        var adapter = new Cs2GsiAdapter(logger);
        server.PayloadReceived += request =>
        {
            if (!string.Equals(request.GameId, GameCatalog.Cs2GameId, StringComparison.OrdinalIgnoreCase)) return;
            adapter.ProcessPayload(request.Body, out _);
        };

        Console.WriteLine("== GSI 链路冒烟（真实 HTTP 服务器 + 合成报文）==");
        Console.WriteLine($"监听地址      : {server.DescribeEndpoint()}");
        var attempted = string.Join(", ", server.AttemptedPorts);
        Console.WriteLine($"尝试端口      : {(string.IsNullOrWhiteSpace(attempted) ? $"{port}（首次即成功）" : attempted)}");
        var uriLine = GsiConfigInstaller.BuildConfigContent(port)
            .Split('\n')
            .FirstOrDefault(line => line.Contains("\"uri\"", StringComparison.Ordinal))?
            .Trim();
        Console.WriteLine($"GSI 配置应为   : {uriLine}");
        Console.WriteLine();

        var steps = BuildPayloadSteps();
        var failures = new List<string>();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var totalKinds = new List<string>();

        foreach (var (name, payload, expected) in steps)
        {
            var before = adapter.PayloadCount;
            string? postError = null;
            try
            {
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var response = await http.PostAsync($"http://127.0.0.1:{port}/cs2", content);
                if (!response.IsSuccessStatusCode)
                {
                    postError = $"HTTP {(int)response.StatusCode}";
                }
            }
            catch (Exception ex)
            {
                postError = ex.Message;
            }

            // 服务器在后台线程派发，等它处理完这一帧。
            var waited = 0;
            while (adapter.PayloadCount == before && waited < 2000)
            {
                await Task.Delay(20);
                waited += 20;
            }

            var received = adapter.PayloadCount > before;
            var summary = adapter.StateSummary;
            Console.WriteLine($"[{(received && postError is null ? "OK" : "!!")}] {name}");
            Console.WriteLine($"      期望事件：{(expected.Count == 0 ? "无" : string.Join(", ", expected))}");
            Console.WriteLine($"      收到负载：{received}  状态摘要：{summary}");
            if (postError is not null) Console.WriteLine($"      推送错误：{postError}");
            if (!received) failures.Add($"{name}：服务器未收到负载");
            if (postError is not null) failures.Add($"{name}：{postError}");
            totalKinds.AddRange(expected);
        }

        Console.WriteLine();
        Console.WriteLine($"请求总数      : {server.RequestCount}");
        Console.WriteLine($"错误数        : {server.ErrorCount}");
        Console.WriteLine($"负载总数      : {adapter.PayloadCount}");
        Console.WriteLine($"最终摘要      : {adapter.StateSummary}");
        Console.WriteLine($"最近错误      : {adapter.LastError ?? "无"}");

        // 适配器单元级断言：直接喂报文，断言事件类型齐全。
        var direct = new Cs2GsiAdapter(logger);
        var kinds = new List<string>();
        foreach (var (_, payload, _) in steps)
        {
            foreach (var gameEvent in direct.ProcessPayload(payload, out _))
            {
                kinds.Add(gameEvent.Kind);
            }
        }

        var required = new[]
        {
            GameEventKinds.Connected, GameEventKinds.Kill, GameEventKinds.Headshot,
            GameEventKinds.Multikill, GameEventKinds.Assist, GameEventKinds.BombPlanted,
            GameEventKinds.BombExploded, GameEventKinds.RoundLoss, GameEventKinds.MatchEnd
        };
        foreach (var kind in required)
        {
            if (!kinds.Contains(kind)) failures.Add($"适配器未推导出事件：{kind}");
        }

        Console.WriteLine($"推导事件      : {string.Join(", ", kinds.Distinct())}");
        Console.WriteLine();

        if (failures.Count == 0)
        {
            Console.WriteLine("结果：链路冒烟通过（合成报文经真实 HTTP 服务器进入适配器并推导出预期事件）。");
            Console.WriteLine("说明：这次验证使用的是合成报文，真实游戏对局需要在本机启动 CS2 并进入对局才能验证。");
            server.Stop();
            logger.Dispose();
            return 0;
        }

        Console.Error.WriteLine("结果：冒烟失败");
        foreach (var failure in failures) Console.Error.WriteLine("  - " + failure);
        server.Stop();
        logger.Dispose();
        return 1;
    }

    public static int Serve(Dictionary<string, string?> options)
    {
        var logger = CreateSilentLogger(options);
        var desiredPort = options.TryGetValue("port", out var raw) && int.TryParse(raw, out var parsed) ? parsed : 48123;
        var seconds = options.TryGetValue("seconds", out var rawSeconds) && int.TryParse(rawSeconds, out var s) ? s : 30;

        using var server = new GsiServer(logger);
        if (!server.Start(desiredPort, out var port, out var error))
        {
            Console.Error.WriteLine($"事件源启动失败：{error}");
            logger.Dispose();
            return 2;
        }

        var adapter = new Cs2GsiAdapter(logger);
        server.PayloadReceived += request =>
        {
            var events = adapter.ProcessPayload(request.Body, out var payloadError);
            if (payloadError is not null)
            {
                Console.WriteLine($"  负载错误：{payloadError}");
                return;
            }

            Console.WriteLine($"  收到负载 #{adapter.PayloadCount}（{request.Remote}）摘要：{adapter.StateSummary}");
            foreach (var gameEvent in events)
            {
                Console.WriteLine($"    事件：{GameEventKinds.Label(gameEvent.Kind)} · {gameEvent.Title} · {gameEvent.Source}");
            }
        };

        Console.WriteLine("== GSI 事件源监听（真实）==");
        Console.WriteLine($"监听地址      : {server.DescribeEndpoint()}");
        var definition = GameCatalog.Find(GameCatalog.Cs2GameId);
        if (definition is not null)
        {
            var status = new GsiConfigInstaller(logger).Query(definition);
            Console.WriteLine($"GSI 配置      : {status.ConfigPath}");
            Console.WriteLine($"配置状态      : {status.Detail}");
            if (status.ConfiguredPort is not null && status.ConfiguredPort != port)
            {
                Console.WriteLine($"注意：配置端口 {status.ConfiguredPort} 与监听端口 {port} 不一致，游戏会推送到旧端口。");
            }
        }

        Console.WriteLine($"会等待 {seconds} 秒，退出前可按 Ctrl+C。");
        Console.WriteLine("现在启动 CS2 并进入对局，即可看到真实推送（带 previously/added 的增量报文同样支持）。");
        Thread.Sleep(TimeSpan.FromSeconds(Math.Max(1, seconds)));
        Console.WriteLine($"共收到 {server.RequestCount} 个请求，{adapter.PayloadCount} 份负载。");
        server.Stop();
        logger.Dispose();
        return 0;
    }

    /// <summary>构造一串递进的 CS2 全量报文：基线 → 击杀 → 多杀 → 助攻 → 安放 → 爆炸+回合结束 → 比赛结束。</summary>
    public static List<(string Name, string Payload, List<string> Expected)> BuildPayloadSteps()
    {
        return
        [
            ("首帧基线（回合进行中）", Payload(0, 0, 0, 0, "live", "carried", null, "live", 3, 2), [GameEventKinds.Connected]),
            ("第一次击杀（爆头）", Payload(1, 1, 0, 0, "live", "carried", null, "live", 3, 2), [GameEventKinds.Kill, GameEventKinds.Headshot]),
            ("本回合第二杀（双杀）", Payload(2, 2, 0, 0, "live", "carried", null, "live", 3, 2), [GameEventKinds.Kill, GameEventKinds.Multikill, GameEventKinds.Headshot]),
            ("获得助攻", Payload(2, 2, 1, 0, "live", "carried", null, "live", 3, 2), [GameEventKinds.Assist]),
            ("炸弹安放", Payload(2, 2, 1, 0, "live", "planted", null, "live", 3, 2), [GameEventKinds.BombPlanted]),
            ("炸弹爆炸 + 回合失败", Payload(2, 2, 1, 0, "over", "exploded", "T", "live", 3, 3), [GameEventKinds.BombExploded, GameEventKinds.RoundLoss]),
            ("比赛结束", Payload(2, 2, 1, 0, "over", "exploded", "T", "gameover", 10, 13), [GameEventKinds.MatchEnd]),
        ];
    }

    private static string Payload(
        int roundKills,
        int roundKillHs,
        int assists,
        int deaths,
        string roundPhase,
        string bomb,
        string? winTeam,
        string mapPhase,
        int ctScore,
        int tScore)
    {
        var payload = new
        {
            provider = new
            {
                name = "Counter-Strike: Global Offensive",
                appid = 730,
                version = 14000,
                steamid = "76561198000000000",
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            },
            map = new
            {
                mode = "competitive",
                name = "de_dust2",
                phase = mapPhase,
                round = 5,
                team_ct = new { score = ctScore },
                team_t = new { score = tScore }
            },
            round = new { phase = roundPhase, win_team = winTeam, bomb },
            player = new
            {
                steamid = "76561198000000000",
                name = "GameMoments-Smoke",
                team = "CT",
                state = new
                {
                    health = 100,
                    armor = 100,
                    round_kills = roundKills,
                    round_killhs = roundKillHs,
                    round_totaldmg = roundKills * 100,
                    flashed = 0,
                    smoked = 0,
                    burning = 0,
                    money = 800,
                    equip_value = 4200
                },
                match_stats = new { kills = roundKills + 3, assists, deaths, mvps = 1, score = 21 },
                weapons = new { weapon_0 = new { name = "weapon_knife", type = "Knife" } }
            }
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false });
    }

    private static AppLogger CreateSilentLogger(Dictionary<string, string?> options)
    {
        var dataDirectory = options.GetValueOrDefault("data-dir");
        var paths = new GameMoments.Core.Platform.AppPaths(dataDirectory);
        return new AppLogger(paths.LogDirectory);
    }
}
