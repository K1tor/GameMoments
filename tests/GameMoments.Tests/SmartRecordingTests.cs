using System.Text.Json.Nodes;
using GameMoments.Core.Configuration;
using GameMoments.Core.Gaming;

namespace GameMoments.Tests;

/// <summary>
/// 智能录制相关测试：设置规范化、GSI 报文推导、事件规则引擎、GSI 配置文件读写。
/// 注意：这里的 GSI 报文是手工构造的合成数据，只验证解析与规则逻辑，
/// 不能代表真实对局验证（真实对局需用户在装有 CS2 的机器上实测）。
/// </summary>
internal sealed partial class TestSuite
{
    private void SmartSettingsTests()
    {
        Check("设置: 智能录制相关字段被夹取到合法范围", () =>
        {
            var settings = new AppSettings { SmartGsiPort = 12, WholeRecordingMinutes = 9999, MaxEventHistory = 1, WholeRecordingStopDelaySeconds = -5 };
            var warnings = settings.Normalize(_root);
            Assert(warnings is not null, "Normalize 应返回列表");
            Assert(settings.SmartGsiPort == 1024, $"端口未夹取：{settings.SmartGsiPort}");
            Assert(settings.WholeRecordingMinutes == 600, $"整局时长未夹取：{settings.WholeRecordingMinutes}");
            Assert(settings.MaxEventHistory == 10, $"事件历史上限未夹取：{settings.MaxEventHistory}");
            Assert(settings.WholeRecordingStopDelaySeconds == 0, $"停止延迟未夹取：{settings.WholeRecordingStopDelaySeconds}");
        });

        Check("设置: 内置游戏自动补齐配置且默认关闭自动接管", () =>
        {
            var settings = new AppSettings();
            settings.Normalize(_root);
            Assert(settings.GameConfigs.ContainsKey(GameCatalog.Cs2GameId), "缺少 CS2 配置项");
            var cs2 = settings.GetGameConfig(GameCatalog.Cs2GameId);
            Assert(cs2.Mode == GameModes.Events, $"默认模式应为事件剪辑：{cs2.Mode}");
            Assert(cs2.IsEventEnabled(GameEventKinds.Kill), "击杀事件默认应开启");
            Assert(!cs2.IsEventEnabled(GameEventKinds.RoundLoss), "回合失败重播默认应关闭");
            Assert(!settings.SmartRecordingEnabled, "智能录制默认应关闭，避免自动接管用户录制");
        });

        Check("设置: 按游戏配置的非法值被修正", () =>
        {
            var settings = new AppSettings();
            settings.GameConfigs[GameCatalog.Cs2GameId] = new GameSettings
            {
                Mode = "nonsense",
                ReplaySeconds = 5,
                PreSeconds = 1,
                PostSeconds = 99,
                CooldownSeconds = 999,
                MergeWindowSeconds = -3,
                MinConfidence = 3.5,
            };
            var warnings = settings.Normalize(_root);
            var cs2 = settings.GetGameConfig(GameCatalog.Cs2GameId);
            Assert(cs2.Mode == GameModes.Events, $"模式未重置：{cs2.Mode}");
            Assert(cs2.ReplaySeconds == 0, $"过短的独立回放时长应改为跟随全局：{cs2.ReplaySeconds}");
            Assert(cs2.PreSeconds == 3, $"事件前时长未夹取：{cs2.PreSeconds}");
            Assert(cs2.PostSeconds == 30, $"事件后时长未夹取：{cs2.PostSeconds}");
            Assert(cs2.CooldownSeconds == 120, $"冷却未夹取：{cs2.CooldownSeconds}");
            Assert(cs2.MergeWindowSeconds == 0, $"合并窗口未夹取：{cs2.MergeWindowSeconds}");
            Assert(Math.Abs(cs2.MinConfidence - 0.8) < 0.001, $"置信度未重置：{cs2.MinConfidence}");
            Assert(warnings.Count > 0, "应产生修复提示");
        });

        Check("设置: 自定义游戏进程名被规范化、去重并建配置", () =>
        {
            var settings = new AppSettings();
            settings.CustomGameExecutables = new List<string> { "MyGame.EXE", "mygame", "  mygame.exe  ", "other.exe=我的游戏", "" };
            settings.Normalize(_root);
            Assert(settings.CustomGameExecutables.Count == 2, $"去重失败：{string.Join(",", settings.CustomGameExecutables)}");
            Assert(settings.CustomGameExecutables[0] == "mygame.exe", $"首个条目不正确：{settings.CustomGameExecutables[0]}");
            Assert(settings.CustomGameExecutables[1] == "other.exe=我的游戏", $"显示名丢失：{settings.CustomGameExecutables[1]}");
            var id = GameCatalog.CustomDefinition("mygame", null).Id;
            Assert(settings.GameConfigs.ContainsKey(id), "自定义游戏缺少配置项");
        });

        Check("设置: Clone 深拷贝游戏配置与自定义列表", () =>
        {
            var settings = new AppSettings();
            settings.Normalize(_root);
            settings.GetGameConfig(GameCatalog.Cs2GameId).PreSeconds = 12;
            var clone = settings.Clone();
            clone.GetGameConfig(GameCatalog.Cs2GameId).PreSeconds = 33;
            clone.CustomGameExecutables.Add("zzz.exe");
            Assert(settings.GetGameConfig(GameCatalog.Cs2GameId).PreSeconds == 12, "Clone 未深拷贝游戏配置");
            Assert(settings.CustomGameExecutables.Count == 0, "Clone 未深拷贝自定义游戏列表");
        });

        Check("设置: 三个快捷键互不相同时保留原值", () =>
        {
            var settings = new AppSettings { SaveHotkey = "Ctrl+Alt+F10", ToggleRecordingHotkey = "Ctrl+Alt+F9", BookmarkHotkey = "Ctrl+Alt+F8" };
            var warnings = settings.Normalize(_root);
            Assert(settings.SaveHotkey == "Ctrl+Alt+F10", "保存快捷键被改动");
            Assert(settings.BookmarkHotkey == "Ctrl+Alt+F8", "书签快捷键被改动");
            Assert(warnings.All(w => !w.Contains("快捷键")), "不应产生快捷键警告：" + string.Join(";", warnings));
        });
    }

    private void GsiAdapterTests()
    {
        Check("GSI: 首帧只产生接入事件并生成对局摘要", () =>
        {
            var adapter = new Cs2GsiAdapter(_logger);
            var events = adapter.ProcessPayload(Cs2Payload(kills: 0, killhs: 0, roundPhase: "live", mapPhase: "live"), out var error);
            Assert(error is null, "首帧不应报错：" + error);
            Assert(events.Count == 1 && events[0].Kind == GameEventKinds.Connected, "首帧应只有接入事件");
            Assert(events[0].Confidence == 1.0, "GSI 事件置信度应为 1.0");
            Assert(adapter.StateSummary.Contains("de_dust2"), "摘要缺少地图名：" + adapter.StateSummary);
            Assert(adapter.PayloadCount == 1, "负载计数不正确");
        });

        Check("GSI: 击杀、多杀、爆头、助攻、阵亡从增量推导", () =>
        {
            var adapter = new Cs2GsiAdapter(_logger);
            adapter.ProcessPayload(Cs2Payload(0, 0, "live", "live", assists: 0, deaths: 0), out _);
            var first = adapter.ProcessPayload(Cs2Payload(1, 1, "live", "live", assists: 0, deaths: 0), out _);
            Assert(Contains(first, GameEventKinds.Kill), "未推导出击杀");
            Assert(Contains(first, GameEventKinds.Headshot), "未推导出爆头");
            Assert(!Contains(first, GameEventKinds.Multikill), "单杀不应产生多杀");

            var second = adapter.ProcessPayload(Cs2Payload(3, 1, "live", "live", assists: 1, deaths: 0), out _);
            Assert(second.Count(e => e.Kind == GameEventKinds.Kill) == 2, $"连杀数不正确：{second.Count(e => e.Kind == GameEventKinds.Kill)}");
            Assert(Contains(second, GameEventKinds.Multikill), "三杀应产生多杀事件");
            Assert(Contains(second, GameEventKinds.Assist), "未推导出助攻");

            var third = adapter.ProcessPayload(Cs2Payload(3, 1, "live", "live", assists: 1, deaths: 1), out _);
            Assert(Contains(third, GameEventKinds.Death), "未推导出阵亡");
            Assert(!Contains(third, GameEventKinds.Kill), "状态不变时不应重复产生击杀");
        });

        Check("GSI: 回合结束按所属队伍判定胜负", () =>
        {
            var adapter = new Cs2GsiAdapter(_logger);
            adapter.ProcessPayload(Cs2Payload(2, 0, "live", "live", team: "CT"), out _);
            var win = adapter.ProcessPayload(Cs2Payload(2, 0, "over", "live", team: "CT", winTeam: "CT"), out _);
            Assert(Contains(win, GameEventKinds.RoundWin), "我方获胜未识别");
            Assert(!Contains(win, GameEventKinds.RoundLoss), "不应同时产生失败");

            adapter.ProcessPayload(Cs2Payload(0, 0, "live", "live", team: "CT"), out _);
            var loss = adapter.ProcessPayload(Cs2Payload(0, 0, "over", "live", team: "CT", winTeam: "T"), out _);
            Assert(Contains(loss, GameEventKinds.RoundLoss), "我方失败未识别");
        });

        Check("GSI: 炸弹状态与比赛结束事件", () =>
        {
            var adapter = new Cs2GsiAdapter(_logger);
            adapter.ProcessPayload(Cs2Payload(1, 0, "live", "live", bomb: "carried"), out _);
            var planted = adapter.ProcessPayload(Cs2Payload(1, 0, "live", "live", bomb: "planted"), out _);
            Assert(Contains(planted, GameEventKinds.BombPlanted), "未识别安放");

            var exploded = adapter.ProcessPayload(Cs2Payload(1, 0, "over", "live", bomb: "exploded"), out _);
            Assert(Contains(exploded, GameEventKinds.BombExploded), "未识别爆炸");

            var matchEnd = adapter.ProcessPayload(Cs2Payload(1, 0, "over", "gameover", bomb: "exploded"), out _);
            Assert(Contains(matchEnd, GameEventKinds.MatchEnd), "未识别比赛结束");
        });

        Check("GSI: previously/added 增量报文被正确合并", () =>
        {
            var adapter = new Cs2GsiAdapter(_logger);
            adapter.ProcessPayload(Cs2Payload(1, 0, "live", "live"), out _);
            var delta = """
            {
              "player": {
                "state": {
                  "previously": { "round_kills": 1 },
                  "added": { "round_kills": 2 }
                }
              }
            }
            """;
            var events = adapter.ProcessPayload(delta, out var error);
            Assert(error is null, "增量报文解析失败：" + error);
            Assert(Contains(events, GameEventKinds.Kill), "增量报文未推导出击杀");
            Assert(adapter.StateSummary.Contains("de_dust2"), "合并后丢失了原有状态：" + adapter.StateSummary);
        });

        Check("GSI: 非法报文返回错误且不崩溃", () =>
        {
            var adapter = new Cs2GsiAdapter(_logger);
            var broken = adapter.ProcessPayload("{ not json", out var error1);
            Assert(broken.Count == 0 && error1 is not null, "非法 JSON 应返回错误");
            var wrongGame = adapter.ProcessPayload("""{"provider":{"appid":570}}""", out var error2);
            Assert(wrongGame.Count == 0 && error2 is not null && error2.Contains("570"), "非 CS2 负载应被拒绝");
            var empty = adapter.ProcessPayload("", out var error3);
            Assert(empty.Count == 0 && error3 is not null, "空负载应返回错误");
        });
    }

    private void EventRuleTests()
    {
        Check("规则: 关闭事件类型时不保存并给出原因", () =>
        {
            var settings = EnabledSettings();
            settings.GetGameConfig(GameCatalog.Cs2GameId).EventKinds[GameEventKinds.Kill] = false;
            var engine = new EventEngine(_logger, () => settings, () => true);
            var decision = engine.Evaluate(TestEvent(GameEventKinds.Kill, 1.0));
            Assert(decision.Outcome == EventOutcome.SkippedKindDisabled, $"判定不正确：{decision.Outcome}");
            Assert(decision.Message.Contains("已关闭"), "缺少原因说明：" + decision.Message);
            Assert(!decision.WantsClip, "关闭的事件不应触发保存");
            Assert(engine.SuppressedCount == 1, "抑制计数不正确");
        });

        Check("规则: 置信度低于阈值时跳过", () =>
        {
            var settings = EnabledSettings();
            settings.GetGameConfig(GameCatalog.Cs2GameId).MinConfidence = 0.8;
            var engine = new EventEngine(_logger, () => settings, () => true);
            var decision = engine.Evaluate(TestEvent(GameEventKinds.Kill, 0.4));
            Assert(decision.Outcome == EventOutcome.SkippedLowConfidence, $"判定不正确：{decision.Outcome}");
        });

        Check("规则: 满足条件时保存并触发 SaveRequested", () =>
        {
            var settings = EnabledSettings();
            var engine = new EventEngine(_logger, () => settings, () => true);
            var requested = 0;
            engine.SaveRequested += (_, _) => requested++;
            var decision = engine.Evaluate(TestEvent(GameEventKinds.Kill, 1.0));
            Assert(decision.Outcome == EventOutcome.Saved, $"判定不正确：{decision.Outcome}");
            Assert(decision.WantsClip, "应触发保存");
            Assert(requested == 1, $"SaveRequested 次数不正确：{requested}");
            Assert(decision.PreSeconds == 10 && decision.PostSeconds == 5, "事件前后时长取错");
            Assert(engine.History.Count == 1, "历史记录未写入");
        });

        Check("规则: 冷却期内同组事件不再保存", () =>
        {
            var settings = EnabledSettings();
            var config = settings.GetGameConfig(GameCatalog.Cs2GameId);
            config.CooldownSeconds = 30;
            config.MergeWindowSeconds = 0;
            var engine = new EventEngine(_logger, () => settings, () => true);
            var requested = 0;
            engine.SaveRequested += (_, _) => requested++;
            var first = engine.Evaluate(TestEvent(GameEventKinds.Kill, 1.0));
            engine.MarkSaved(GameCatalog.Cs2GameId, first.Event.Kind, DateTime.UtcNow);
            var second = engine.Evaluate(TestEvent(GameEventKinds.Kill, 1.0));
            Assert(second.Outcome == EventOutcome.SkippedCooldown, $"判定不正确：{second.Outcome}");
            Assert(requested == 1, $"冷却期内不应再次请求保存：{requested}");
        });

        Check("规则: 合并窗口内同组事件合并进上一段", () =>
        {
            var settings = EnabledSettings();
            var config = settings.GetGameConfig(GameCatalog.Cs2GameId);
            config.MergeWindowSeconds = 12;
            config.CooldownSeconds = 0;
            var engine = new EventEngine(_logger, () => settings, () => true);
            var requested = 0;
            engine.SaveRequested += (_, _) => requested++;
            engine.Evaluate(TestEvent(GameEventKinds.BombPlanted, 1.0));
            var merged = engine.Evaluate(TestEvent(GameEventKinds.BombExploded, 1.0));
            Assert(merged.Outcome == EventOutcome.Merged, $"判定不正确：{merged.Outcome}");
            Assert(requested == 1, $"合并后不应再次请求保存：{requested}");
        });

        Check("规则: 只加书签模式不保存但保留补存入口", () =>
        {
            var settings = EnabledSettings();
            settings.GetGameConfig(GameCatalog.Cs2GameId).AutoSaveEvents = false;
            var engine = new EventEngine(_logger, () => settings, () => true);
            var requested = 0;
            engine.SaveRequested += (_, _) => requested++;
            var decision = engine.Evaluate(TestEvent(GameEventKinds.Kill, 1.0));
            Assert(decision.Outcome == EventOutcome.Bookmarked, $"判定不正确：{decision.Outcome}");
            Assert(requested == 0, "书签模式不应自动保存");
            Assert(engine.History[0].CanSaveFromCache, "书签记录应允许从缓存补存");
        });

        Check("规则: 捕获未运行或总开关关闭时只记录不保存", () =>
        {
            var settings = EnabledSettings();
            var engine = new EventEngine(_logger, () => settings, () => false);
            var notReady = engine.Evaluate(TestEvent(GameEventKinds.Kill, 1.0));
            Assert(notReady.Outcome == EventOutcome.Bookmarked, $"捕获未运行时应加书签：{notReady.Outcome}");

            settings.SmartRecordingEnabled = false;
            var off = engine.Evaluate(TestEvent(GameEventKinds.Kill, 1.0));
            Assert(off.Outcome == EventOutcome.Skipped, $"总开关关闭时应跳过：{off.Outcome}");
            Assert(off.Message.Contains("总开关"), "缺少总开关说明：" + off.Message);
        });

        Check("规则: 历史记录与保存结果回写", () =>
        {
            var settings = EnabledSettings();
            var engine = new EventEngine(_logger, () => settings, () => true);
            var updated = 0;
            engine.RecordUpdated += _ => updated++;
            var decision = engine.Evaluate(TestEvent(GameEventKinds.Multikill, 1.0));
            var record = engine.History[0];
            var clipPath = Path.Combine(_root, "smart-clip-" + Guid.NewGuid().ToString("N")[..6] + ".mp4");
            File.WriteAllBytes(clipPath, new byte[] { 1, 2, 3, 4 });
            engine.AttachClip(record, clipPath, Path.GetFileName(clipPath), true, "已保存");
            Assert(record.HasClip, "保存结果未回写");
            Assert(record.IsSaved, "记录状态不是已保存");
            Assert(updated == 1, $"更新事件次数不正确：{updated}");
            engine.SetFalsePositive(record, true);
            Assert(record.FalsePositive && record.Feedback is not null, "误触发反馈未记录");
            Assert(decision.Event.Kind == GameEventKinds.Multikill, "事件类型被改动");
        });
    }

    private void GsiConfigTests()
    {
        Check("GSI 配置: 生成内容包含端口、来源地址与可逆标记", () =>
        {
            var content = GsiConfigInstaller.BuildConfigContent(48123);
            Assert(content.Contains("\"http://127.0.0.1:48123/cs2\""), "缺少 uri：" + content);
            Assert(content.Contains(GsiConfigInstaller.Marker), "缺少本程序标记");
            Assert(content.Contains("\"player_state\"") && content.Contains("\"round\""), "缺少数据字段订阅");
            Assert(GsiConfigInstaller.BuildConfigContent(50000).Contains(":50000/"), "端口未写入");
        });

        Check("GSI 配置: 写入→识别→卸载真实往返", () =>
        {
            var directory = Path.Combine(_root, "gsi-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(directory);
            var definition = GameCatalog.Find(GameCatalog.Cs2GameId)!;
            var installer = new GsiConfigInstaller(_logger);

            var install = installer.Install(definition, 48123, directory);
            Assert(install.Success, "写入失败：" + install.Message);
            Assert(File.Exists(install.Path), "文件未落盘");

            var status = installer.Query(definition, directory);
            Assert(status.Exists && status.OwnedByGameMoments, "状态识别不正确：" + status.Detail);
            Assert(status.ConfiguredPort == 48123, $"端口解析不正确：{status.ConfiguredPort}");

            var remove = installer.Remove(definition, directory);
            Assert(remove.Success, "卸载失败：" + remove.Message);
            Assert(!File.Exists(install.Path), "卸载后文件仍存在");
        });

        Check("GSI 配置: 不覆盖他人同名文件，也不删除他人文件", () =>
        {
            var directory = Path.Combine(_root, "gsi-foreign-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(directory);
            var definition = GameCatalog.Find(GameCatalog.Cs2GameId)!;
            var installer = new GsiConfigInstaller(_logger);
            var path = Path.Combine(directory, GameCatalog.GsiConfigFileName);
            var foreign = "\"SteelSeries\"\n{\n    \"uri\" \"http://127.0.0.1:1234/\"\n}\n";
            File.WriteAllText(path, foreign);

            var install = installer.Install(definition, 48123, directory);
            Assert(!install.Success, "不应覆盖他人文件");
            Assert(File.ReadAllText(path) == foreign, "他人文件内容被改动");

            var remove = installer.Remove(definition, directory);
            Assert(!remove.Success, "不应删除他人文件");
            Assert(File.Exists(path), "他人文件被删除");
        });

        Check("GSI 配置: 端口变更时重写自己的文件", () =>
        {
            var directory = Path.Combine(_root, "gsi-port-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(directory);
            var definition = GameCatalog.Find(GameCatalog.Cs2GameId)!;
            var installer = new GsiConfigInstaller(_logger);
            Assert(installer.Install(definition, 48123, directory).Success, "首次写入失败");
            var second = installer.Install(definition, 48200, directory);
            Assert(second.Success, "端口变更写入失败：" + second.Message);
            Assert(installer.Query(definition, directory).ConfiguredPort == 48200, "端口未更新");
        });

        Check("游戏目录: 支持矩阵与事件源标注一致", () =>
        {
            var cs2 = GameCatalog.Find(GameCatalog.Cs2GameId)!;
            Assert(cs2.SupportsAutomaticEvents, "CS2 应支持自动事件识别");
            Assert(cs2.SupportsGsiConfig, "CS2 应支持安装 GSI 配置");
            Assert(cs2.SupportText.Contains("事件"), "CS2 支持说明应包含事件：" + cs2.SupportText);

            foreach (var definition in GameCatalog.BuiltIn.Where(d => d.Id != GameCatalog.Cs2GameId))
            {
                Assert(!definition.SupportsAutomaticEvents, $"{definition.DisplayName} 不应声称支持自动事件");
                Assert(definition.CaptureOnlyText.Contains("不支持自动事件识别"), $"{definition.DisplayName} 缺少不支持说明");
            }

            var custom = GameCatalog.CustomDefinition("mygame", "我的游戏");
            Assert(!custom.SupportsAutomaticEvents, "自定义游戏不应声称支持自动事件");
            Assert(custom.DisplayName == "我的游戏", "自定义游戏显示名不正确");
        });
    }

    private AppSettings EnabledSettings()
    {
        var settings = new AppSettings
        {
            SmartRecordingEnabled = true,
            SaveFolder = Path.Combine(_root, "clips"),
        };
        settings.Normalize(_root);
        var config = settings.GetGameConfig(GameCatalog.Cs2GameId);
        config.Enabled = true;
        config.Mode = GameModes.Events;
        config.AutoSaveEvents = true;
        config.CooldownSeconds = 0;
        config.MergeWindowSeconds = 0;
        config.MinConfidence = 0.8;
        config.PreSeconds = 10;
        config.PostSeconds = 5;
        config.EnsureEventKinds(GameEventKinds.UserConfigurable);
        foreach (var kind in GameEventKinds.UserConfigurable) config.EventKinds[kind] = true;
        return settings;
    }

    private static GameEvent TestEvent(string kind, double confidence) =>
        new(
            GameCatalog.Cs2GameId,
            "Counter-Strike 2",
            kind,
            GameEventKinds.Label(kind),
            GameEventKinds.Priority(kind),
            confidence,
            DateTime.UtcNow,
            "GSI",
            "de_dust2");

    private static bool Contains(IReadOnlyList<GameEvent> events, string kind) =>
        events.Any(e => e.Kind == kind);

    /// <summary>构造一份接近真实的 CS2 GSI 全量报文（字段名与官方文档一致）。</summary>
    private static string Cs2Payload(
        int kills,
        int killhs,
        string roundPhase,
        string mapPhase,
        string? winTeam = null,
        string? bomb = null,
        int assists = 0,
        int deaths = 0,
        string team = "CT")
    {
        var round = new JsonObject
        {
            ["phase"] = roundPhase,
        };
        if (winTeam is not null) round["win_team"] = winTeam;
        if (bomb is not null) round["bomb"] = bomb;

        var payload = new JsonObject
        {
            ["provider"] = new JsonObject
            {
                ["name"] = "Counter-Strike: Global Offensive",
                ["appid"] = 730,
                ["version"] = 14000,
            },
            ["player"] = new JsonObject
            {
                ["name"] = "TestPlayer",
                ["team"] = team,
                ["state"] = new JsonObject
                {
                    ["health"] = 100,
                    ["armor"] = 100,
                    ["round_kills"] = kills,
                    ["round_killhs"] = killhs,
                },
                ["match_stats"] = new JsonObject
                {
                    ["kills"] = kills,
                    ["assists"] = assists,
                    ["deaths"] = deaths,
                },
            },
            ["round"] = round,
            ["map"] = new JsonObject
            {
                ["name"] = "de_dust2",
                ["mode"] = "competitive",
                ["phase"] = mapPhase,
                ["round"] = 3,
                ["team_ct"] = new JsonObject { ["score"] = 2 },
                ["team_t"] = new JsonObject { ["score"] = 1 },
            },
        };

        return payload.ToJsonString();
    }
}
