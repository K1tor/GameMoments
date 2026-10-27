namespace GameMoments.Core.Gaming;

/// <summary>随剪辑一起保存的游戏/事件元数据。</summary>
public sealed record GameClipTag(string GameName, string EventKind, string? EventSource)
{
    public static GameClipTag Manual(string? gameName = null) =>
        new(gameName ?? "手动录制", "manual", null);
}
