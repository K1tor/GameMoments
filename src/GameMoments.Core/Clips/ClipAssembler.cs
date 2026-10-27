using System.Globalization;
using GameMoments.Core.Audio;
using GameMoments.Core.Capture;
using GameMoments.Core.Configuration;
using GameMoments.Core.Logging;
using GameMoments.Core.Media;
using GameMoments.Core.Platform;

namespace GameMoments.Core.Clips;

/// <summary>一路要混入片段的音频：来源分块、相对片段起点的偏移、增益。</summary>
public sealed record AudioMixSource(
    string Label,
    IReadOnlyList<string> ChunkPaths,
    double StartOffsetSeconds,
    double DurationSeconds,
    double Volume);

public sealed record AssemblyRequest(
    IReadOnlyList<SegmentInfo> Segments,
    DateTime ClipStartUtc,
    double DurationSeconds,
    IReadOnlyList<AudioMixSource> Audio,
    bool ManualRecording,
    string? Title,
    GameMoments.Core.Gaming.GameClipTag? Tag = null);

/// <summary>
/// 把已关闭的 1 秒视频分段拼成完整 MP4，裁剪并混合对应时间段的音频，
/// 生成缩略图，最后用 ffprobe 验证文件真实可读后才返回记录。
/// </summary>
public sealed class ClipAssembler
{
    private readonly FfmpegTools _tools;
    private readonly AppPaths _paths;
    private readonly AppLogger _logger;

    public ClipAssembler(FfmpegTools tools, AppPaths paths, AppLogger logger)
    {
        _tools = tools;
        _paths = paths;
        _logger = logger;
    }

    public async Task<ClipRecord> AssembleAsync(
        AssemblyRequest request,
        AppSettings settings,
        CaptureProfile profile,
        CancellationToken cancellationToken = default)
    {
        if (request.Segments.Count == 0)
        {
            throw new InvalidOperationException("没有可拼接的视频分段。");
        }

        var saveFolder = string.IsNullOrWhiteSpace(settings.SaveFolder) ? _paths.DefaultSaveDirectory : settings.SaveFolder;
        Directory.CreateDirectory(saveFolder);

        var free = AppPaths.GetFreeBytes(saveFolder);
        if (free is long bytes && bytes < 256L * 1024 * 1024)
        {
            throw new InvalidOperationException($"保存目录剩余空间不足（{AppPaths.FormatBytes(bytes)}），请清理磁盘后重试。");
        }

        var workDirectory = Path.Combine(_paths.TempDirectory, "assemble", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDirectory);
        var id = Guid.NewGuid().ToString("N");
        var clipStartLocal = request.ClipStartUtc.ToLocalTime();

        try
        {
            // 1) 拼接视频（-c copy，不重新编码）。
            var listPath = Path.Combine(workDirectory, "segments.txt");
            await File.WriteAllTextAsync(listPath, FfmpegCommands.ConcatListContent(request.Segments.Select(s => s.Path)), cancellationToken);

            var videoPath = Path.Combine(workDirectory, "video.mp4");
            var concat = await Task.Run(
                () => ProcessRunner.Run(_tools.FfmpegPath, FfmpegCommands.Concat(listPath, videoPath), 120_000),
                cancellationToken);
            if (!concat.Success || !File.Exists(videoPath) || new FileInfo(videoPath).Length < 4096)
            {
                throw new InvalidOperationException($"视频拼接失败：{FirstLine(concat.OutputTail(900))}");
            }

            var videoInfo = MediaProbe.Probe(_tools.FfprobePath, videoPath, _logger);
            if (videoInfo is null || !videoInfo.HasVideo || videoInfo.DurationSeconds <= 0)
            {
                throw new InvalidOperationException("拼接后的视频无法被 ffprobe 正确读取。");
            }

            var duration = request.DurationSeconds > 0 ? request.DurationSeconds : videoInfo.DurationSeconds;

            // 2) 从 WAV 分块中裁剪出片段对应时间段。
            var audioInputs = new List<AudioInput>();
            var mixSources = request.Audio.Where(a => a.Volume > 0.01 && a.ChunkPaths.Count > 0).ToList();
            foreach (var source in mixSources)
            {
                var wavPath = Path.Combine(workDirectory, $"audio_{audioInputs.Count}.wav");
                var offset = Math.Max(0, source.StartOffsetSeconds);
                if (source.StartOffsetSeconds < -0.05)
                {
                    _logger.Warn("组装", $"{source.Label} 的起始时间晚于片段起点 {(-source.StartOffsetSeconds):0.00}s，该段以静音补齐。");
                }

                WavIO.Extract(source.ChunkPaths, wavPath, offset, source.DurationSeconds > 0 ? source.DurationSeconds : duration);
                if (!File.Exists(wavPath) || new FileInfo(wavPath).Length <= 44)
                {
                    _logger.Warn("组装", $"{source.Label} 的音频裁剪结果为空，已跳过。");
                    continue;
                }
                audioInputs.Add(new AudioInput(wavPath, (float)Math.Clamp(source.Volume, 0, 1.5)));
            }

            // 3) 封装到保存目录。
            var outputPath = BuildOutputPath(saveFolder, settings.FileNamePrefix, request.Title, clipStartLocal);
            var muxTitle = string.IsNullOrWhiteSpace(request.Title) ? "精彩时刻" : request.Title!.Trim();
            if (audioInputs.Count == 0)
            {
                File.Move(videoPath, outputPath);
            }
            else
            {
                var mux = await Task.Run(
                    () => ProcessRunner.Run(_tools.FfmpegPath, FfmpegCommands.Mux(videoPath, audioInputs, outputPath, muxTitle), 180_000),
                    cancellationToken);
                if (!mux.Success || !File.Exists(outputPath))
                {
                    TryDelete(outputPath);
                    throw new InvalidOperationException($"音视频封装失败：{FirstLine(mux.OutputTail(900))}");
                }
            }

            // 4) 真正验证最终文件，而不是假设成功。
            var finalInfo = MediaProbe.Probe(_tools.FfprobePath, outputPath, _logger);
            if (finalInfo is null || !finalInfo.HasVideo || finalInfo.DurationSeconds < 0.2)
            {
                TryDelete(outputPath);
                throw new InvalidOperationException("导出的片段无法通过 ffprobe 验证，已删除残留文件。");
            }

            // 5) 缩略图（失败不影响片段本身）。
            string? thumbnailPath = null;
            try
            {
                var thumbnail = Path.Combine(_paths.ThumbnailDirectory, id + ".jpg");
                Directory.CreateDirectory(_paths.ThumbnailDirectory);
                var at = Math.Clamp(finalInfo.DurationSeconds * 0.25, 0.1, Math.Max(0.1, finalInfo.DurationSeconds - 0.2));
                var result = await Task.Run(
                    () => ProcessRunner.Run(_tools.FfmpegPath, FfmpegCommands.Thumbnail(outputPath, thumbnail, at), 30_000),
                    cancellationToken);
                if (result.Success && File.Exists(thumbnail) && new FileInfo(thumbnail).Length > 512)
                {
                    thumbnailPath = thumbnail;
                }
                else
                {
                    _logger.Warn("组装", "缩略图生成失败，片段仍可使用。", result.OutputTail(400));
                }
            }
            catch (Exception ex)
            {
                _logger.Warn("组装", $"缩略图生成异常：{ex.Message}");
            }

            var record = new ClipRecord
            {
                Id = id,
                FilePath = outputPath,
                FileName = Path.GetFileName(outputPath),
                CreatedAt = clipStartLocal,
                SavedAtUtc = DateTime.UtcNow,
                DurationSeconds = finalInfo.DurationSeconds,
                Width = finalInfo.Width > 0 ? finalInfo.Width : videoInfo.Width,
                Height = finalInfo.Height > 0 ? finalInfo.Height : videoInfo.Height,
                Fps = finalInfo.Fps > 0 ? finalInfo.Fps : profile.Fps,
                FileSize = new FileInfo(outputPath).Length,
                HasAudio = finalInfo.HasAudio && audioInputs.Count > 0,
                AudioSummary = BuildAudioSummary(request.Audio),
                ThumbnailPath = thumbnailPath,
                ManualRecording = request.ManualRecording,
                GameName = request.Tag?.GameName,
                EventKind = request.Tag?.EventKind,
                EventSource = request.Tag?.EventSource,
            };

            _logger.Info("组装", $"片段已生成：{record.FileName}",
                $"{record.DurationText} · {record.ResolutionText} · {record.FpsText} · {record.SizeText} · 音频：{record.AudioSummary}");
            return record;
        }
        finally
        {
            try
            {
                if (Directory.Exists(workDirectory)) Directory.Delete(workDirectory, recursive: true);
            }
            catch (Exception ex)
            {
                _logger.Warn("组装", $"清理临时目录失败：{ex.Message}", workDirectory);
            }
        }
    }

    private string BuildOutputPath(string folder, string prefix, string? title, DateTime clipStartLocal)
    {
        var baseName = string.IsNullOrWhiteSpace(title)
            ? $"{prefix}_{clipStartLocal:yyyyMMdd_HHmmss}"
            : AppSettings.SanitizeFileName(title!.Trim());
        if (baseName.Length == 0) baseName = $"Moments_{clipStartLocal:yyyyMMdd_HHmmss}";
        if (baseName.Length > 90) baseName = baseName[..90];

        var candidate = Path.Combine(folder, baseName + ".mp4");
        var suffix = 1;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(folder, $"{baseName}_{suffix}.mp4");
            suffix++;
        }
        return candidate;
    }

    private static string BuildAudioSummary(IReadOnlyList<AudioMixSource> sources)
    {
        var active = sources.Where(s => s.Volume > 0.01).Select(s => $"{s.Label} {s.Volume * 100:0}%").ToList();
        return active.Count == 0 ? "无音频" : string.Join(" + ", active);
    }

    private static string FirstLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "（无输出）";
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim();
        return string.IsNullOrEmpty(line) ? "（无输出）" : line;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 忽略清理失败。
        }
    }
}
