using Microsoft.Win32;

namespace LiveSub.Services;

public sealed record SourceLanguage(string Code, string DisplayName, string PromptName)
{
    public override string ToString() => DisplayName;
}

public static class SourceLanguages
{
    public static IReadOnlyList<SourceLanguage> All { get; } = new[]
    {
        new SourceLanguage("en", "英语", "English"),
        new SourceLanguage("ja", "日语", "Japanese"),
        new SourceLanguage("ko", "韩语", "Korean"),
    };

    public static SourceLanguage Get(string? code) =>
        All.FirstOrDefault(language => string.Equals(language.Code, code, StringComparison.OrdinalIgnoreCase))
        ?? All[0];

    public static string? GetWindowsCaptionCode()
    {
        var value = Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\LiveCaptions\UI",
            "CaptionLanguage",
            null) as string;
        if (string.IsNullOrWhiteSpace(value)) return null;
        var separator = value.IndexOf('-');
        return (separator > 0 ? value[..separator] : value).ToLowerInvariant();
    }
}
