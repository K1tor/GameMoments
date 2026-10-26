namespace GameMoments.Core.Platform;

/// <summary>
/// 日志与诊断输出中的隐私处理：把用户目录替换为 %USERPROFILE%，避免导出日志泄露用户名。
/// </summary>
public static class Privacy
{
    private static readonly Lazy<string[]> Terms = new(BuildTerms, LazyThreadSafetyMode.ExecutionAndPublication);

    private static string[] BuildTerms()
    {
        var list = new List<string>();
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
        {
            list.Add(profile);
            list.Add(profile.Replace('\\', '/'));
        }
        return list
            .Where(t => t.Length > 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(t => t.Length)
            .ToArray();
    }

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        foreach (var term in Terms.Value)
        {
            text = text.Replace(term, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }
        return text;
    }
}
