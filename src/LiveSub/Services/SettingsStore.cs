using System.Text.Json;

namespace LiveSub.Services;

public sealed class OverlaySettings
{
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double BackgroundOpacity { get; set; } = 0.86;
    public double TextOpacity { get; set; } = 1.0;
    public double FontScale { get; set; } = 50;
    public bool ShowEnglish { get; set; } = true;
    public string AsrEngine { get; set; } = "whisper";
    public string SourceLanguage { get; set; } = "en";
}

public static class SettingsStore
{
    private static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LiveSub", "settings.json");

    public static OverlaySettings Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                var json = File.ReadAllText(Path);
                return JsonSerializer.Deserialize<OverlaySettings>(json) ?? new OverlaySettings();
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log("Settings load failed: " + ex.Message);
        }
        return new OverlaySettings();
    }

    public static void Save(OverlaySettings settings)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            AppPaths.Log("Settings save failed: " + ex.Message);
        }
    }
}
