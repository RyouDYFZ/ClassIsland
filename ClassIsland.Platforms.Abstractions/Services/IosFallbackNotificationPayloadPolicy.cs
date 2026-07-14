namespace ClassIsland.Platforms.Abstraction.Services;

/// <summary>
/// 一条提醒请求中可用于 iOS 本地通知的文本。
/// </summary>
public sealed record IosFallbackNotificationTextEntry(
    string? MaskText,
    string? OverlayText);

/// <summary>
/// iOS 即时本地通知的纯文本载荷。
/// </summary>
public sealed record IosFallbackNotificationPayload(
    string Title,
    string Body);

/// <summary>
/// 将一条 ClassIsland 提醒链折叠为一条 iOS 即时本地通知。
/// </summary>
public static class IosFallbackNotificationPayloadPolicy
{
    private const string DefaultTitle = "ClassIsland 提醒";
    private const string DefaultBody = "你有一条新提醒。";

    /// <summary>
    /// 按提醒链顺序生成标题和正文，并去除重复文本。
    /// </summary>
    public static IosFallbackNotificationPayload Create(
        string? providerName,
        IEnumerable<IosFallbackNotificationTextEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var normalizedEntries = entries
            .Select(x => new IosFallbackNotificationTextEntry(
                Normalize(x.MaskText),
                Normalize(x.OverlayText)))
            .ToArray();
        var normalizedProviderName = Normalize(providerName);
        var title = normalizedEntries
                        .Select(x => x.MaskText)
                        .FirstOrDefault(x => x is not null) ??
                    normalizedProviderName ??
                    normalizedEntries
                        .Select(x => x.OverlayText)
                        .FirstOrDefault(x => x is not null) ??
                    DefaultTitle;

        var bodyParts = new List<string>();
        var bodyTexts = new HashSet<string>(StringComparer.Ordinal) { title };
        foreach (var entry in normalizedEntries)
        {
            AddBodyPart(entry.MaskText);
            AddBodyPart(entry.OverlayText);
        }

        if (bodyParts.Count == 0)
        {
            if (normalizedProviderName is not null &&
                bodyTexts.Add(normalizedProviderName))
            {
                bodyParts.Add(normalizedProviderName);
            }
            else
            {
                bodyParts.Add(DefaultBody);
            }
        }

        return new IosFallbackNotificationPayload(
            title,
            string.Join(Environment.NewLine, bodyParts));

        void AddBodyPart(string? text)
        {
            if (text is not null && bodyTexts.Add(text))
            {
                bodyParts.Add(text);
            }
        }
    }

    /// <summary>
    /// 仅在全局允许声音且链中至少一条提醒启用声音时播放系统通知声音。
    /// </summary>
    public static bool ShouldPlaySound(
        bool allowNotificationSound,
        IEnumerable<bool> notificationSoundEnabledStates)
    {
        ArgumentNullException.ThrowIfNull(notificationSoundEnabledStates);
        return allowNotificationSound && notificationSoundEnabledStates.Any(x => x);
    }

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Replace("\0", "", StringComparison.Ordinal).Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
