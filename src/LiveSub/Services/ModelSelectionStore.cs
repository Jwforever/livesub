using System.Text.Json;

namespace LiveSub.Services;

public sealed class ModelSelection
{
    public string AsrId { get; set; } = ModelCatalog.DefaultAsrId;
    public string TranslationId { get; set; } = ModelCatalog.DefaultTranslationId;
}

public static class ModelSelectionStore
{
    private static readonly object Lock = new();
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LiveSub", "models.json");

    private static ModelSelection? _cached;

    public static ModelSelection Load()
    {
        lock (Lock)
        {
            EnsureLoaded();
            return new ModelSelection
            {
                AsrId = _cached!.AsrId,
                TranslationId = _cached.TranslationId,
            };
        }
    }

    public static void Save(ModelSelection selection)
    {
        var normalized = new ModelSelection
        {
            AsrId = ModelCatalog.GetAsr(selection.AsrId).Id,
            TranslationId = ModelCatalog.GetTranslation(selection.TranslationId).Id,
        };

        lock (Lock)
        {
            _cached = normalized;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(normalized, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                AppPaths.Log("Model selection save failed: " + ex.Message);
            }
        }
    }

    public static void Select(ModelDescriptor model)
    {
        var selection = Load();
        if (model.Kind == ModelKind.SpeechRecognition) selection.AsrId = model.Id;
        else selection.TranslationId = model.Id;
        Save(selection);
    }

    private static void EnsureLoaded()
    {
        if (_cached != null) return;

        _cached = new ModelSelection();
        try
        {
            if (!File.Exists(FilePath)) return;
            var json = File.ReadAllText(FilePath);
            var parsed = JsonSerializer.Deserialize<ModelSelection>(json);
            if (parsed == null) return;
            _cached.AsrId = ModelCatalog.GetAsr(parsed.AsrId).Id;
            _cached.TranslationId = ModelCatalog.GetTranslation(parsed.TranslationId).Id;
        }
        catch (Exception ex)
        {
            AppPaths.Log("Model selection load failed: " + ex.Message);
        }
    }
}
