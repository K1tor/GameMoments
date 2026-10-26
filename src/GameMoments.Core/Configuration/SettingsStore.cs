using System.Text.Encodings.Web;
using System.Text.Json;
using GameMoments.Core.Logging;
using GameMoments.Core.Platform;

namespace GameMoments.Core.Configuration;

/// <summary>设置文件的读写：容错解析、原子写入、损坏备份与默认值修复。</summary>
public sealed class SettingsStore
{
    private readonly string _path;
    private readonly AppLogger _logger;
    private readonly string _fallbackSaveFolder;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public SettingsStore(string path, string fallbackSaveFolder, AppLogger logger)
    {
        _path = path;
        _backupPath = path + ".bak";
        _fallbackSaveFolder = fallbackSaveFolder;
        _logger = logger;
    }

    private readonly string _backupPath;

    public AppSettings Current { get; private set; } = new();

    public string FilePath => _path;

    public IReadOnlyList<string> Load()
    {
        var warnings = new List<string>();
        AppSettings? loaded = null;

        if (File.Exists(_path))
        {
            try
            {
                var text = File.ReadAllText(_path);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    loaded = JsonSerializer.Deserialize<AppSettings>(text, _json);
                }
                if (loaded is null) warnings.Add("设置文件内容为空，已使用默认设置。");
            }
            catch (Exception ex)
            {
                warnings.Add("设置文件损坏，已备份为 .bak 并使用默认设置。");
                _logger.Error("设置", "读取设置文件失败", ex);
                TryBackupCorruptFile();
            }
        }
        else
        {
            warnings.Add("首次运行：已创建默认设置。");
        }

        Current = loaded ?? new AppSettings();
        var normalizeWarnings = Current.Normalize(_fallbackSaveFolder);
        warnings.AddRange(normalizeWarnings);

        if (warnings.Count > 0)
        {
            _logger.Warn("设置", "设置加载时存在需要修复的内容", string.Join("；", warnings));
        }

        Save(Current);
        return warnings;
    }

    public void Save(AppSettings settings)
    {
        try
        {
            var warnings = settings.Normalize(_fallbackSaveFolder);
            if (warnings.Count > 0)
            {
                _logger.Warn("设置", "保存前修正了设置", string.Join("；", warnings));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, _json));
            if (File.Exists(_path))
            {
                try
                {
                    File.Copy(_path, _backupPath, overwrite: true);
                }
                catch
                {
                    // 备份失败不影响保存。
                }
            }
            File.Move(temp, _path, overwrite: true);
            Current = settings;
            _logger.Info("设置", "设置已保存", _path);
        }
        catch (Exception ex)
        {
            _logger.Error("设置", "保存设置失败", ex);
            throw;
        }
    }

    public AppSettings ResetToDefaults()
    {
        var defaults = new AppSettings { SaveFolder = _fallbackSaveFolder };
        defaults.Normalize(_fallbackSaveFolder);
        Save(defaults);
        _logger.Info("设置", "设置已恢复默认值");
        return defaults;
    }

    private void TryBackupCorruptFile()
    {
        try
        {
            var backup = _path + $".corrupt-{DateTime.Now:yyyyMMddHHmmss}.bak";
            File.Copy(_path, backup, overwrite: true);
        }
        catch
        {
            // 忽略。
        }
    }
}
