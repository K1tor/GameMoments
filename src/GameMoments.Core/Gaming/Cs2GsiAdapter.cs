using System.Text.Json;
using System.Text.Json.Nodes;
using GameMoments.Core.Logging;

namespace GameMoments.Core.Gaming;

/// <summary>
/// 可插拔游戏事件源适配器：把某种外部数据源（官方 API、GSI、日志、OCR……）翻译成统一的事件流。
/// 实现者不得使用 DLL 注入、读取游戏内存或绕过反作弊。
/// </summary>
public interface IGameEventAdapter
{
    /// <summary>对应 <see cref="GameCatalog"/> 中的游戏 id。</summary>
    string GameId { get; }

    string DisplayName { get; }

    /// <summary>Steam AppID（非 Steam 游戏为 0）。</summary>
    int AppId { get; }

    /// <summary>事件来源说明，展示在界面上。</summary>
    string SourceLabel { get; }

    /// <summary>收到并成功处理的负载数量。</summary>
    int PayloadCount { get; }

    DateTime? LastPayloadUtc { get; }

    DateTime? LastEventUtc { get; }

    /// <summary>当前对局摘要，用于界面显示。</summary>
    string StateSummary { get; }

    string? LastError { get; }

    /// <summary>处理一份负载，返回推导出的事件（可能为空）。</summary>
    IReadOnlyList<GameEvent> ProcessPayload(string json, out string? error);

    /// <summary>清空累积状态（例如游戏退出后）。</summary>
    void Reset();
}

/// <summary>CS2 的 Game State Integration 适配器：解析官方 GSI JSON 并用前后帧对比推导事件。</summary>
public sealed class Cs2GsiAdapter : IGameEventAdapter
{
    private readonly AppLogger _logger;
    private readonly object _gate = new();
    private JsonNode? _state;
    private int _payloadCount;
    private DateTime? _lastPayloadUtc;
    private DateTime? _lastEventUtc;
    private string _stateSummary = "等待游戏推送";
    private string? _lastError;

    public Cs2GsiAdapter(AppLogger logger)
    {
        _logger = logger;
    }

    public string GameId => GameCatalog.Cs2GameId;

    public string DisplayName => GameCatalog.Find(GameCatalog.Cs2GameId)?.DisplayName ?? "反恐精英 2";

    public int AppId => 730;

    public string SourceLabel => "官方游戏状态集成（GSI）";

    public int PayloadCount
    {
        get
        {
            lock (_gate) return _payloadCount;
        }
    }

    public DateTime? LastPayloadUtc
    {
        get
        {
            lock (_gate) return _lastPayloadUtc;
        }
    }

    public DateTime? LastEventUtc
    {
        get
        {
            lock (_gate) return _lastEventUtc;
        }
    }

    public string StateSummary
    {
        get
        {
            lock (_gate) return _stateSummary;
        }
    }

    public string? LastError
    {
        get
        {
            lock (_gate) return _lastError;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _state = null;
            _payloadCount = 0;
            _lastPayloadUtc = null;
            _lastEventUtc = null;
            _stateSummary = "等待游戏推送";
            _lastError = null;
        }
    }

    public IReadOnlyList<GameEvent> ProcessPayload(string json, out string? error)
    {
        error = null;
        var events = new List<GameEvent>();

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "空负载";
            return events;
        }

        JsonObject incoming;
        try
        {
            var node = JsonNode.Parse(json);
            if (node is not JsonObject obj)
            {
                error = "负载不是 JSON 对象";
                return events;
            }

            incoming = obj;
        }
        catch (JsonException ex)
        {
            error = "JSON 解析失败：" + ex.Message;
            return events;
        }

        var appId = GetInt(incoming, "provider", "appid");
        if (appId is not null && appId != AppId)
        {
            error = $"appid={appId}，不是 CS2（{AppId}）";
            return events;
        }

        lock (_gate)
        {
            var now = DateTime.UtcNow;
            _payloadCount++;
            _lastPayloadUtc = now;

            var previous = _state;
            var merged = MergeDelta(previous, incoming);
            _state = merged;

            if (previous is null)
            {
                events.Add(BuildEvent(GameEventKinds.Connected, "已接入 CS2 游戏状态", now, 1.0, "GSI", "首帧状态"));
            }
            else
            {
                events.AddRange(Diff(previous, merged, now));
            }

            _stateSummary = BuildSummary(merged);
            if (events.Count > 0) _lastEventUtc = now;
            _lastError = null;
        }

        return events;
    }

    private IEnumerable<GameEvent> Diff(JsonNode previous, JsonNode current, DateTime now)
    {
        var events = new List<GameEvent>();
        var mapName = GetString(current, "map", "name") ?? "未知地图";

        // 击杀 / 多杀 / 爆头
        var killsNow = GetInt(current, "player", "state", "round_kills") ?? 0;
        var killsBefore = GetInt(previous, "player", "state", "round_kills") ?? 0;
        if (killsNow > killsBefore && killsNow > 0)
        {
            var added = killsNow - killsBefore;
            for (var i = 0; i < added; i++)
            {
                events.Add(BuildEvent(GameEventKinds.Kill, $"击杀（本回合第 {killsNow} 个）", now, 1.0, "GSI", mapName));
            }

            if (killsNow >= 2)
            {
                var label = killsNow switch
                {
                    2 => "双杀",
                    3 => "三杀",
                    4 => "四杀",
                    _ => "五杀 ACE",
                };
                events.Add(BuildEvent(GameEventKinds.Multikill, label, now, 1.0, "GSI", $"本回合 {killsNow} 杀"));
            }
        }

        var hsNow = GetInt(current, "player", "state", "round_killhs") ?? 0;
        var hsBefore = GetInt(previous, "player", "state", "round_killhs") ?? 0;
        if (hsNow > hsBefore) events.Add(BuildEvent(GameEventKinds.Headshot, "爆头击杀", now, 1.0, "GSI", mapName));

        var assistsNow = GetInt(current, "player", "match_stats", "assists") ?? 0;
        var assistsBefore = GetInt(previous, "player", "match_stats", "assists") ?? 0;
        if (assistsNow > assistsBefore) events.Add(BuildEvent(GameEventKinds.Assist, "助攻", now, 1.0, "GSI", mapName));

        var deathsNow = GetInt(current, "player", "match_stats", "deaths") ?? 0;
        var deathsBefore = GetInt(previous, "player", "match_stats", "deaths") ?? 0;
        if (deathsNow > deathsBefore) events.Add(BuildEvent(GameEventKinds.Death, "阵亡", now, 1.0, "GSI", mapName));

        // 回合结束 / 比赛结束
        var roundPhase = GetString(current, "round", "phase");
        var roundPhaseBefore = GetString(previous, "round", "phase");
        var isRoundOver = string.Equals(roundPhase, "over", StringComparison.OrdinalIgnoreCase);
        var wasRoundOver = string.Equals(roundPhaseBefore, "over", StringComparison.OrdinalIgnoreCase);
        if (isRoundOver && !wasRoundOver)
        {
            var winTeam = GetString(current, "round", "win_team");
            var myTeam = GetString(current, "player", "team");
            var won = winTeam is not null && myTeam is not null
                && string.Equals(winTeam, myTeam, StringComparison.OrdinalIgnoreCase);
            var score = ScoreText(current);
            events.Add(BuildEvent(
                won ? GameEventKinds.RoundWin : GameEventKinds.RoundLoss,
                won ? "回合胜利" : "回合失败",
                now,
                1.0,
                "GSI",
                score));
        }

        var mapPhase = GetString(current, "map", "phase");
        var mapPhaseBefore = GetString(previous, "map", "phase");
        if (string.Equals(mapPhase, "gameover", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(mapPhaseBefore, "gameover", StringComparison.OrdinalIgnoreCase))
        {
            events.Add(BuildEvent(GameEventKinds.MatchEnd, "比赛结束", now, 1.0, "GSI", ScoreText(current)));
        }

        // 炸弹事件
        var bombNow = BombState(current);
        var bombBefore = BombState(previous);
        if (string.Equals(bombNow, "planted", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(bombBefore, "planted", StringComparison.OrdinalIgnoreCase))
        {
            events.Add(BuildEvent(GameEventKinds.BombPlanted, "炸弹已安放", now, 1.0, "GSI", mapName));
        }

        if (string.Equals(bombNow, "defused", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(bombBefore, "defused", StringComparison.OrdinalIgnoreCase))
        {
            events.Add(BuildEvent(GameEventKinds.BombDefused, "炸弹已拆除", now, 1.0, "GSI", mapName));
        }

        if (string.Equals(bombNow, "exploded", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(bombBefore, "exploded", StringComparison.OrdinalIgnoreCase))
        {
            events.Add(BuildEvent(GameEventKinds.BombExploded, "炸弹爆炸", now, 1.0, "GSI", mapName));
        }

        return events;
    }

    private static GameEvent BuildEvent(string kind, string title, DateTime now, double confidence, string source, string? detail) =>
        new(
            GameCatalog.Cs2GameId,
            GameCatalog.Find(GameCatalog.Cs2GameId)?.DisplayName ?? "反恐精英 2",
            kind,
            title,
            GameEventKinds.Priority(kind),
            confidence,
            now,
            source,
            detail);

    private static string ScoreText(JsonNode root)
    {
        var ct = GetInt(root, "map", "team_ct", "score") ?? 0;
        var t = GetInt(root, "map", "team_t", "score") ?? 0;
        return $"比分 {ct}:{t}";
    }

    private string BuildSummary(JsonNode root)
    {
        var mapName = GetString(root, "map", "name") ?? "未知地图";
        var mode = GetString(root, "map", "mode");
        var phase = GetString(root, "map", "phase") ?? "-";
        var round = GetInt(root, "map", "round");
        var ct = GetInt(root, "map", "team_ct", "score") ?? 0;
        var t = GetInt(root, "map", "team_t", "score") ?? 0;
        var health = GetInt(root, "player", "state", "health");
        var kills = GetInt(root, "player", "match_stats", "kills") ?? 0;
        var deaths = GetInt(root, "player", "match_stats", "deaths") ?? 0;
        var name = GetString(root, "player", "name");

        var parts = new List<string> { mapName };
        if (!string.IsNullOrWhiteSpace(mode)) parts.Add(mode!);
        if (round is not null) parts.Add($"第 {round} 回合");
        parts.Add($"{ct}:{t}");
        if (!string.IsNullOrWhiteSpace(phase)) parts.Add(phase!);
        if (health is not null) parts.Add($"生命 {health}");
        if (deaths > 0 || kills > 0) parts.Add($"K/D {kills}/{deaths}");
        if (!string.IsNullOrWhiteSpace(name)) parts.Add(name!);
        return string.Join(" · ", parts);
    }

    private static string? BombState(JsonNode root)
    {
        var node = Get(root, "round", "bomb");
        return node switch
        {
            null => null,
            JsonValue value => value.TryGetValue<string>(out var text) ? text : null,
            JsonObject obj => GetString(obj, "state") ?? GetString(obj, "phase"),
            _ => null,
        };
    }

    /// <summary>把增量负载（含 previously/added 包装）合并进上一帧状态。</summary>
    private static JsonNode MergeDelta(JsonNode? previous, JsonNode incoming)
    {
        if (previous is null) return incoming.DeepClone() ?? new JsonObject();
        return MergeInto(previous.DeepClone(), incoming);
    }

    private static JsonNode MergeInto(JsonNode? baseNode, JsonNode incoming)
    {
        if (incoming is not JsonObject incomingObject) return incoming.DeepClone();

        // 增量包装：{"previously":{...},"added":{...}} —— 只取 added。
        if (incomingObject.ContainsKey("added") && incomingObject.ContainsKey("previously"))
        {
            var addedOnly = incomingObject["added"] as JsonObject;
            if (addedOnly is null) return baseNode ?? new JsonObject();
            return MergeInto(baseNode, addedOnly);
        }

        var target = baseNode as JsonObject ?? new JsonObject();
        foreach (var pair in incomingObject.ToArray())
        {
            var key = pair.Key;
            var value = pair.Value;
            if (value is null)
            {
                target.Remove(key);
                continue;
            }

            if (value is JsonObject childObject && childObject.ContainsKey("previously") && childObject.ContainsKey("added"))
            {
                var inner = childObject["added"];
                if (inner is null) continue;
                var existing = target[key];
                target[key] = existing is null ? inner.DeepClone() : MergeInto(existing, inner);
                continue;
            }

            if (value is JsonObject mergeChild)
            {
                // 普通嵌套对象（例如只推送了 player.state.round_kills 的分段增量）也要递归合并，
                // 否则会用这一小段覆盖上一帧的完整状态。
                var existing = target[key];
                target[key] = existing is null ? mergeChild.DeepClone() : MergeInto(existing, mergeChild);
                continue;
            }

            target[key] = value.DeepClone();
        }

        return target;
    }

    private static JsonNode? Get(JsonNode? root, params string[] path)
    {
        var current = root;
        foreach (var segment in path)
        {
            if (current is not JsonObject obj) return null;
            current = obj[segment];
            if (current is null) return null;
        }

        return current;
    }

    private static string? GetString(JsonNode? root, params string[] path)
    {
        var node = Get(root, path);
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<string>(out var text)) return text;
        return null;
    }

    private static int? GetInt(JsonNode? root, params string[] path)
    {
        var node = Get(root, path);
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<int>(out var number)) return number;
        if (value.TryGetValue<long>(out var longNumber)) return (int)longNumber;
        if (value.TryGetValue<double>(out var d)) return (int)d;
        if (value.TryGetValue<string>(out var text) && int.TryParse(text, out var parsed)) return parsed;
        return null;
    }
}
