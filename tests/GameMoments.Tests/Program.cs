using System.Text;
using GameMoments.Core.Audio;
using GameMoments.Core.Capture;
using GameMoments.Core.Clips;
using GameMoments.Core.Configuration;
using GameMoments.Core.Hotkeys;
using GameMoments.Core.Logging;
using GameMoments.Core.Media;
using GameMoments.Core.Platform;

namespace GameMoments.Tests;

/// <summary>
/// 无外部依赖的测试运行器（此环境的 dotnet test/testhost 无法启动，
/// 因此用控制台程序 + 退出码代替 xunit）。
/// </summary>
internal static class Program
{
    private static int Main()
    {
        var suite = new TestSuite();
        suite.RunAll();
        return suite.Failures == 0 ? 0 : 1;
    }
}

internal sealed class TestSuite
{
    private int _passed;
    private int _failed;
    private readonly string _root;
    private readonly AppLogger _logger;

    public TestSuite()
    {
        _root = Path.Combine(Environment.GetEnvironmentVariable("GAMEMOMENTS_TEST_DIR") ?? Path.Combine(AppContext.BaseDirectory, ".tests"), "gm-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        _logger = new AppLogger(Path.Combine(_root, "logs"));
    }

    public int Failures => _failed;

    public void RunAll()
    {
        HotkeyTests();
        SettingsTests();
        WavTests();
        SegmentRingTests();
        ClipIndexTests();
        FfmpegCommandTests();
        MiscTests();

        Console.WriteLine($"通过 {_passed}，失败 {_failed}");
        try { Directory.Delete(_root, true); } catch { /* 测试临时目录清理失败不影响结果 */ }
    }

    private void HotkeyTests()
    {
        Check("hotkey: Ctrl+Alt+F10 解析与格式化", () =>
        {
            Assert(HotkeyGesture.TryParse("Ctrl+Alt+F10", out var g, out var error), error ?? "解析失败");
            Assert(g!.VirtualKey == 0x79, $"VK 应为 0x79，实际 0x{g.VirtualKey:X}");
            Assert(g.Modifiers == (HotkeyModifiers.Control | HotkeyModifiers.Alt), "修饰键不正确");
            Assert(g.ToString() == "Ctrl+Alt+F10", $"格式化结果 {g}");
        });

        Check("hotkey: 缺少修饰键被拒绝", () =>
        {
            var ok = HotkeyGesture.TryParse("F10", out _, out var error);
            Assert(!ok && !string.IsNullOrWhiteSpace(error), "应拒绝无修饰键的快捷键");
        });

        Check("hotkey: 多个主键被拒绝", () =>
        {
            var ok = HotkeyGesture.TryParse("Ctrl+A+B", out _, out var error);
            Assert(!ok && !string.IsNullOrWhiteSpace(error), "应拒绝多个主键");
        });

        Check("hotkey: 四修饰键与 Win 键", () =>
        {
            Assert(HotkeyGesture.TryParse("Ctrl+Alt+Shift+Win+F5", out var g, out _), "应可解析");
            Assert(g!.NativeModifiers() == (0x0001u | 0x0002u | 0x0004u | 0x0008u), "Win32 修饰位不正确");
            Assert(g.ToString() == "Ctrl+Alt+Shift+Win+F5", $"格式化结果 {g}");
        });

        Check("hotkey: 大小写与别名", () =>
        {
            Assert(HotkeyGesture.TryParse("control + alt + f10", out var g, out _), "应可解析");
            Assert(g!.VirtualKey == 0x79, "F10 虚拟键错误");
        });
    }

    private void SettingsTests()
    {
        Check("settings: Normalize 收敛越界值", () =>
        {
            var settings = new AppSettings
            {
                ReplaySeconds = 45,
                PostTriggerSeconds = 7,
                Fps = 144,
                QualityPreset = "ultra",
                SystemAudioVolume = 999,
                MicrophoneVolume = -5,
                SaveFolder = "",
                SaveHotkey = "bad",
                ToggleRecordingHotkey = "Ctrl+Alt+F9",
                MaxCacheSizeGb = 9999,
                MaxQueuedSaves = 0,
            };

            var fallback = Path.Combine(_root, "fallback");
            var warnings = settings.Normalize(fallback);

            Assert(settings.ReplaySeconds == 30, $"回放时长 {settings.ReplaySeconds}");
            Assert(settings.PostTriggerSeconds == 5, $"按键后时长 {settings.PostTriggerSeconds}");
            Assert(settings.Fps == 60, $"帧率 {settings.Fps}");
            Assert(settings.QualityPreset == "medium", $"画质 {settings.QualityPreset}");
            Assert(settings.SystemAudioVolume == 150, $"系统音量 {settings.SystemAudioVolume}");
            Assert(settings.MicrophoneVolume == 0, $"麦克风音量 {settings.MicrophoneVolume}");
            Assert(settings.SaveFolder == fallback, "保存目录应回退");
            Assert(settings.SaveHotkey == "Ctrl+Alt+F10", $"保存快捷键 {settings.SaveHotkey}");
            Assert(settings.MaxQueuedSaves == 1, $"队列上限 {settings.MaxQueuedSaves}");
            Assert(settings.MaxCacheSizeGb <= 200, $"缓存上限 {settings.MaxCacheSizeGb}");
            Assert(warnings.Count > 0, "应返回修复提示");
        });

        Check("settings: 合法值保持不变", () =>
        {
            var settings = new AppSettings
            {
                ReplaySeconds = 60,
                PostTriggerSeconds = 10,
                Fps = 30,
                QualityPreset = "low",
                SaveHotkey = "Ctrl+Shift+F8",
                ToggleRecordingHotkey = "Ctrl+Shift+F9",
                SaveFolder = Path.Combine(_root, "clips"),
            };
            settings.Normalize(Path.Combine(_root, "fallback"));
            Assert(settings.ReplaySeconds == 60 && settings.PostTriggerSeconds == 10 && settings.Fps == 30, "合法值被改动");
            Assert(settings.QualityPreset == "low", "画质被改动");
        });

        Check("settings: 文件名清理", () =>
        {
            var name = AppSettings.SanitizeFileName("a:b/c*?\"<>|.mp4");
            Assert(name.Length > 0, "文件名不应为空");
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                Assert(!name.Contains(c), $"仍包含非法字符 {c}");
            }
            Assert(AppSettings.SanitizeFileName("  ").Length > 0, "空白名应有回退");
        });

        Check("settings: 画质预设映射", () =>
        {
            Assert(QualityPresets.Get("high").BitrateKbps == 24000, "high 码率");
            Assert(QualityPresets.Get("unknown").Id == "medium", "未知画质应回退 medium");
            Assert(QualityPresets.NearestBitrateKbps("low") == 8000, "low 码率");
        });
    }

    private void WavTests()
    {
        Check("wav: 写入与读取往返", () =>
        {
            var path = Path.Combine(_root, "one.wav");
            var samples = MakeRamp(WavIO.SampleRate);
            WavIO.WriteFile(path, samples);

            var data = WavIO.Read(path);
            Assert(data.SampleRate == WavIO.SampleRate, $"采样率 {data.SampleRate}");
            Assert(data.Channels == WavIO.Channels, $"声道 {data.Channels}");
            Assert(data.Samples.Length == samples.Length, $"样本数 {data.Samples.Length}");
            Assert(data.Samples[100] == samples[100], "样本内容不一致");
        });

        Check("wav: 按时间裁剪与静音补齐", () =>
        {
            var dir = Path.Combine(_root, "chunks");
            Directory.CreateDirectory(dir);
            var chunk0 = Path.Combine(dir, "a_00000.wav");
            var chunk1 = Path.Combine(dir, "a_00001.wav");
            var first = MakeRamp(WavIO.SampleRate);
            var second = MakeRamp(WavIO.SampleRate);
            WavIO.WriteFile(chunk0, first);
            WavIO.WriteFile(chunk1, second);

            var output = Path.Combine(dir, "out.wav");
            WavIO.Extract(new[] { chunk0, chunk1 }, output, 0.5, 1.0);
            var data = WavIO.Read(output);
            Assert(data.Samples.Length == WavIO.SampleRate * 2, $"裁剪长度 {data.Samples.Length}");
            Assert(data.Samples[0] == first[WavIO.SampleRate / 2 * 2], "起始样本应对齐到 0.5 秒");

            var shortOutput = Path.Combine(dir, "short.wav");
            WavIO.Extract(new[] { chunk0 }, shortOutput, 0.75, 2.0);
            var shortData = WavIO.Read(shortOutput);
            Assert(shortData.Samples.Length == WavIO.SampleRate * 2 * 2, $"补齐长度 {shortData.Samples.Length}");
        });
    }

    private void SegmentRingTests()
    {
        Check("ring: 扫描、关闭状态与回放时长", () =>
        {
            var dir = NewDir("ring-ok");
            var start = DateTime.UtcNow.AddSeconds(-10);
            for (var i = 0; i < 5; i++)
            {
                var path = Path.Combine(dir, $"seg_{i:00000}.mp4");
                File.WriteAllBytes(path, new byte[2048]);
                File.SetCreationTimeUtc(path, start.AddSeconds(i));
            }

            var ring = new SegmentRing(dir) { ReplaySeconds = 30 };
            Assert(ring.Scan(), "首次扫描应有变化");
            Assert(ring.SegmentCount == 5, $"分段数 {ring.SegmentCount}");
            Assert(Math.Abs(ring.CachedSeconds - 5) < 0.01, $"缓存时长 {ring.CachedSeconds}");
            var snapshot = ring.Snapshot();
            Assert(snapshot[^1].IsClosed == false, "最新分段不应标记为关闭");
            Assert(snapshot[0].IsClosed, "旧分段应标记为关闭");
        });

        Check("ring: 超出窗口自动清理最旧分段", () =>
        {
            var dir = NewDir("ring-prune");
            var start = DateTime.UtcNow.AddSeconds(-10);
            for (var i = 0; i < 6; i++)
            {
                var path = Path.Combine(dir, $"seg_{i:00000}.mp4");
                File.WriteAllBytes(path, new byte[1024]);
                File.SetCreationTimeUtc(path, start.AddSeconds(i));
            }

            var ring = new SegmentRing(dir) { ReplaySeconds = 1, MaxBytes = 1024L * 1024 * 1024 };
            ring.Scan();
            Assert(ring.SegmentCount <= 4, $"应保留少量分段，实际 {ring.SegmentCount}");
            Assert(Directory.GetFiles(dir, "seg_*.mp4").Length == ring.SegmentCount, "磁盘与索引数量应一致");
        });

        Check("ring: 保存中的分段被 pin 后不清理", () =>
        {
            var dir = NewDir("ring-pin");
            var start = DateTime.UtcNow.AddSeconds(-10);
            for (var i = 0; i < 6; i++)
            {
                var path = Path.Combine(dir, $"seg_{i:00000}.mp4");
                File.WriteAllBytes(path, new byte[1024]);
                File.SetCreationTimeUtc(path, start.AddSeconds(i));
            }

            var ring = new SegmentRing(dir) { ReplaySeconds = 1 };
            ring.Scan();
            var newest = ring.Snapshot()[^1];
            using (ring.Pin(new[] { newest }))
            {
                ring.Scan();
                Assert(File.Exists(newest.Path), "被 pin 的分段不应被删除");
            }
        });

        Check("ring: 按时间范围挑选分段", () =>
        {
            var dir = NewDir("ring-select");
            var start = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            for (var i = 0; i < 5; i++)
            {
                var path = Path.Combine(dir, $"seg_{i:00000}.mp4");
                File.WriteAllBytes(path, new byte[10]);
                File.SetCreationTimeUtc(path, start.AddSeconds(i));
            }

            var ring = new SegmentRing(dir);
            ring.Scan();
            var selected = ring.Select(start.AddSeconds(1.5), start.AddSeconds(3.2));
            Assert(selected.Count >= 2, $"选中 {selected.Count} 个分段");
            Assert(selected.All(s => s.IsClosed), "所选分段应已关闭");
        });
    }

    private void ClipIndexTests()
    {
        Check("clips: 索引写入与读取", () =>
        {
            var store = new ClipIndexStore(Path.Combine(_root, "index", "clips.json"), _logger);
            var clips = new List<ClipRecord>
            {
                new()
                {
                    Id = "a", FilePath = Path.Combine(_root, "a.mp4"), FileName = "a.mp4",
                    CreatedAt = DateTime.Now, SavedAtUtc = DateTime.UtcNow, DurationSeconds = 12.5,
                    Width = 1920, Height = 1080, Fps = 60, FileSize = 1234, HasAudio = true,
                },
                new()
                {
                    Id = "b", FilePath = Path.Combine(_root, "b.mp4"), FileName = "b.mp4",
                    CreatedAt = DateTime.Now, SavedAtUtc = DateTime.UtcNow, DurationSeconds = 30,
                    Width = 1920, Height = 1080, Fps = 60, FileSize = 5678, HasAudio = false,
                },
            };
            store.Save(clips);
            var loaded = store.Load();
            Assert(loaded.Count == 2, $"读取 {loaded.Count} 条");
            Assert(loaded[0].Id == "a" && Math.Abs(loaded[0].DurationSeconds - 12.5) < 0.001, "内容不一致");
        });

        Check("clips: 索引损坏时回退并备份", () =>
        {
            var dir = NewDir("corrupt-index");
            var path = Path.Combine(dir, "clips.json");
            File.WriteAllText(path, "{ this is not json", Encoding.UTF8);
            var store = new ClipIndexStore(path, _logger);
            var loaded = store.Load();
            Assert(loaded.Count == 0, "损坏索引应返回空列表");
            Assert(Directory.GetFiles(dir, "*.corrupt-*.bak").Length > 0, "应保留损坏文件备份");
        });
    }

    private void FfmpegCommandTests()
    {
        Check("ffmpeg: ddagrab + NVENC 分段命令", () =>
        {
            var display = new DisplayInfo(0, @"\\.\DISPLAY1", "Test", new PixelRect(0, 0, 1920, 1080), true);
            var settings = new AppSettings { Fps = 60, QualityPreset = "high" };
            var profile = new CaptureProfile(CaptureSource.Ddagrab, "h264_nvenc", 60);
            var args = FfmpegCommands.Record(profile, settings, display, Path.Combine(_root, "seg"));
            var text = string.Join(" ", args);
            Assert(text.Contains("ddagrab=framerate=60"), "缺少 ddagrab 输入");
            Assert(text.Contains("h264_nvenc"), "缺少 NVENC 编码器");
            Assert(text.Contains("-f segment"), "缺少分段输出");
            Assert(text.Contains("seg_%05d.mp4"), "分段文件名不正确");
            Assert(!text.Contains("-sc_threshold"), "NVENC 不应出现 x264 参数");
        });

        Check("ffmpeg: gdigrab + libx264 回退命令", () =>
        {
            var display = new DisplayInfo(1, @"\\.\DISPLAY2", "Test", new PixelRect(1920, 0, 2560, 1440), false);
            var settings = new AppSettings { Fps = 30, QualityPreset = "low" };
            var profile = new CaptureProfile(CaptureSource.Gdigrab, "libx264", 30);
            var args = FfmpegCommands.Record(profile, settings, display, Path.Combine(_root, "seg"));
            var text = string.Join(" ", args);
            Assert(text.Contains("gdigrab"), "缺少 gdigrab 输入");
            Assert(text.Contains("-offset_x 1920") && text.Contains("-offset_y 0"), "多显示器偏移不正确");
            Assert(text.Contains("libx264"), "缺少软件编码器");
            Assert(text.Contains("-sc_threshold 0"), "x264 应关闭场景切换");
        });

        Check("ffmpeg: 双路音频混音与拼接列表转义", () =>
        {
            var output = Path.Combine(_root, "out.mp4");
            var mux = FfmpegCommands.Mux(Path.Combine(_root, "v.mp4"), new List<AudioInput>
            {
                new(Path.Combine(_root, "s.wav"), 1.0f),
                new(Path.Combine(_root, "m.wav"), 0.8f),
            }, output, "标题");
            var text = string.Join(" ", mux);
            Assert(text.Contains("amix") && text.Contains("normalize=0"), "缺少双路混音");
            Assert(text.Contains("aac") && text.Contains("192k"), "音频编码参数不正确");

            var list = FfmpegCommands.ConcatListContent(new[] { @"C:\a b\o'neil.mp4" });
            Assert(list.Contains("file '"), "拼接列表格式不正确");
            Assert(list.Contains("o'\\''neil"), "单引号应转义");
            Assert(list.Contains("/"), "路径应使用正斜杠");
        });
    }

    private void MiscTests()
    {
        Check("platform: 隐私脱敏与容量格式化", () =>
        {
            var profile = AppPaths.UserProfileDirectory();
            var redacted = Privacy.Redact(Path.Combine(profile, "Videos", "GameMoments", "x.mp4"));
            Assert(!redacted.Contains(profile, StringComparison.OrdinalIgnoreCase), "用户目录未脱敏");
            Assert(redacted.Contains("%USERPROFILE%"), "缺少占位符");
            Assert(AppPaths.FormatBytes(1536).Contains("KB"), "容量格式化不正确");
        });

        Check("audio: 线性重采样输出帧数符合比例", () =>
        {
            var resampler = new LinearResampler(44100, 48000);
            var input = new float[44100 * 2];
            for (var i = 0; i < input.Length; i++) input[i] = 0.25f;
            var output = new float[48000 * 2 + 8];
            var written = resampler.Process(input, output);
            Assert(Math.Abs(written - 48000) <= 8, $"输出帧数 {written}");
            Assert(output[0] > 0.2f, "输出样本异常");
        });

        Check("audio: PCM 转 16 位并削波", () =>
        {
            var source = new[] { -2f, -1f, 0f, 0.5f, 2f };
            var target = new short[source.Length];
            PcmConverter.ConvertToPcm16(source, target);
            Assert(target[0] == (short)-32767, "负向削波");
            Assert(target[4] == short.MaxValue, "正向削波");
            Assert(Math.Abs(target[3] - 16384) <= 2, "0.5 映射");
        });
    }

    private string NewDir(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static short[] MakeRamp(int frames)
    {
        var samples = new short[frames * WavIO.Channels];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)(i % 4000 - 2000);
        }
        return samples;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private void Check(string name, Action action)
    {
        try
        {
            action();
            _passed++;
            Console.WriteLine($"PASS  {name}");
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine($"FAIL  {name}: {ex.Message}");
        }
    }
}



