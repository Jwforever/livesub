using System.Text.Json;

namespace LiveSub.Services;

public sealed class CustomModel
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "asr";
    public string DisplayName { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Url { get; set; } = "";
    public long ApproxBytes { get; set; }
    public long MinBytes { get; set; }
}

public static class CustomModelStore
{
    private static readonly object Lock = new();
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LiveSub", "customModels.json");

    private static List<CustomModel>? _cached;

    public static IReadOnlyList<CustomModel> Load()
    {
        lock (Lock)
        {
            EnsureLoaded();
            return _cached!.ToList();
        }
    }

    public static void Save(IReadOnlyList<CustomModel> models)
    {
        lock (Lock)
        {
            _cached = models.ToList();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(_cached, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                AppPaths.Log("Custom model save failed: " + ex.Message);
            }
        }
    }

    public static void Add(CustomModel model)
    {
        var list = Load().ToList();
        list.Add(model);
        Save(list);
    }

    private static void EnsureLoaded()
    {
        if (_cached != null) return;
        _cached = new List<CustomModel>();
        try
        {
            if (!File.Exists(FilePath)) return;
            var json = File.ReadAllText(FilePath);
            var parsed = JsonSerializer.Deserialize<List<CustomModel>>(json);
            if (parsed != null) _cached = parsed;
        }
        catch (Exception ex)
        {
            AppPaths.Log("Custom model load failed: " + ex.Message);
        }
    }
}
