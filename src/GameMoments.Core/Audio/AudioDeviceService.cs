using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GameMoments.Core.Audio;

public enum AudioDeviceKind
{
    Render,
    Capture,
}

public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault, AudioDeviceKind Kind)
{
    public string DisplayName => IsDefault ? $"{Name}（默认）" : Name;
}

/// <summary>用 NAudio 枚举系统音频端点；默认设备排在最前。</summary>
public static class AudioDeviceService
{
    public static IReadOnlyList<AudioDeviceInfo> Enumerate(AudioDeviceKind kind)
    {
        var flow = kind == AudioDeviceKind.Render ? DataFlow.Render : DataFlow.Capture;
        var list = new List<AudioDeviceInfo>();
        string? defaultId = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (enumerator.HasDefaultAudioEndpoint(flow, Role.Multimedia))
            {
                using var defaultDevice = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
                defaultId = defaultDevice.ID;
            }

            foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                using (device)
                {
                    list.Add(new AudioDeviceInfo(device.ID, device.FriendlyName, device.ID == defaultId, kind));
                }
            }
        }
        catch
        {
            // 没有音频设备或 COM 不可用时返回空列表，由上层提示。
        }

        return list
            .OrderByDescending(d => d.IsDefault)
            .ThenBy(d => d.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    public static AudioDeviceInfo? GetDefault(AudioDeviceKind kind) => Enumerate(kind).FirstOrDefault(d => d.IsDefault);

    /// <summary>按 Id 解析端点；Id 为空或找不到时回退到默认设备。</summary>
    public static MMDevice? Resolve(AudioDeviceKind kind, string? id, out string? error)
    {
        error = null;
        var flow = kind == AudioDeviceKind.Render ? DataFlow.Render : DataFlow.Capture;
        try
        {
            var enumerator = new MMDeviceEnumerator();
            if (!string.IsNullOrWhiteSpace(id))
            {
                try
                {
                    return enumerator.GetDevice(id);
                }
                catch (Exception ex)
                {
                    error = $"已配置的音频设备不可用（{ex.Message}），改用默认设备。";
                }
            }

            if (enumerator.HasDefaultAudioEndpoint(flow, Role.Multimedia))
            {
                return enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
            }

            error = "系统中没有可用的音频端点。";
            return null;
        }
        catch (Exception ex)
        {
            error = $"音频设备初始化失败：{ex.Message}";
            return null;
        }
    }

    public static IWaveIn? CreateCapture(AudioDeviceKind kind, MMDevice? device)
    {
        if (device is null) return null;
        if (kind == AudioDeviceKind.Render)
        {
            return new WasapiLoopbackCapture(device);
        }

        try
        {
            return new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: 100);
        }
        catch
        {
            return new WasapiCapture(device);
        }
    }
}
