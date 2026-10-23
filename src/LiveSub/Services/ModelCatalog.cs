using System.Net.Http;
using System.Text.Json;

namespace LiveSub.Services;

public enum ModelKind
{
    SpeechRecognition,
    Translation,
}

public sealed class ModelDescriptor
{
    public required string Id { get; init; }
    public required ModelKind Kind { get; init; }
    public required string DisplayName { get; init; }
    public required string ShortName { get; init; }
    public required string FileName { get; init; }
    public required string[] Urls { get; init; }
    public required long ApproxBytes { get; init; }
    public required long MinBytes { get; init; }
    public required string SizeLabel { get; init; }
    public required string Latency { get; init; }
    public required string Description { get; init; }

    public string FullPath => Path.Combine(AppPaths.ModelsRoot, FileName);
    public bool IsDownloaded => File.Exists(FullPath) && new FileInfo(FullPath).Length >= MinBytes;
}

public static class ModelCatalog
{
    public const string DefaultAsrId = "whisper-small-en";
    public const string DefaultTranslationId = "qwen2.5-1.5b";

    public static IReadOnlyList<ModelDescriptor> BuiltInAsrModels { get; } = new List<ModelDescriptor>
    {
        new()
        {
            Id = "whisper-tiny-en",
            Kind = ModelKind.SpeechRecognition,
            DisplayName = "Whisper tiny.en",
            ShortName = "tiny.en",
            FileName = Path.Combine("whisper", "ggml-tiny.en.bin"),
            Urls = BuildUrls("https://hf-mirror.com/ggerganov/whisper.cpp/resolve/main/ggml-tiny.en.bin"),
            ApproxBytes = 75_800_000,
            MinBytes = 50_000_000,
            SizeLabel = "75 MB",
            Latency = "约 0.5–1 秒/句 · CPU",
            Description = "最快、最省资源；准确度一般，适合追求速度的场景。",
        },
        new()
        {
            Id = "whisper-small-en",
            Kind = ModelKind.SpeechRecognition,
            DisplayName = "Whisper small.en",
            ShortName = "small.en",
            FileName = Path.Combine("whisper", "ggml-small.en.bin"),
            Urls = BuildUrls("https://hf-mirror.com/ggerganov/whisper.cpp/resolve/main/ggml-small.en.bin"),
            ApproxBytes = 487_614_201,
            MinBytes = 400_000_000,
            SizeLabel = "466 MB",
            Latency = "约 2–4 秒/句 · 8 线程 CPU",
            Description = "英文准确度优先，推荐日常使用；适合会议、视频和网页播放。",
        },
        new()
        {
            Id = "whisper-base-en",
            Kind = ModelKind.SpeechRecognition,
            DisplayName = "Whisper base.en",
            ShortName = "base.en",
            FileName = Path.Combine("whisper", "ggml-base.en.bin"),
            Urls = BuildUrls("https://hf-mirror.com/ggerganov/whisper.cpp/resolve/main/ggml-base.en.bin"),
            ApproxBytes = 147_951_725,
            MinBytes = 100_000_000,
            SizeLabel = "142 MB",
            Latency = "约 1–2 秒/句 · CPU",
            Description = "更快、更省资源，适合低配笔记本；准确度略低于 small.en。",
        },
        new()
        {
            Id = "whisper-base",
            Kind = ModelKind.SpeechRecognition,
            DisplayName = "Whisper base（多语言）",
            ShortName = "base",
            FileName = Path.Combine("whisper", "ggml-base.bin"),
            Urls = BuildUrls("https://hf-mirror.com/ggerganov/whisper.cpp/resolve/main/ggml-base.bin"),
            ApproxBytes = 147_951_725,
            MinBytes = 100_000_000,
            SizeLabel = "142 MB",
            Latency = "约 1–2 秒/句 · CPU",
            Description = "支持英语、日语、韩语等多语言；速度快，适合低配电脑。",
        },
        new()
        {
            Id = "whisper-small",
            Kind = ModelKind.SpeechRecognition,
            DisplayName = "Whisper small（多语言）",
            ShortName = "small",
            FileName = Path.Combine("whisper", "ggml-small.bin"),
            Urls = BuildUrls("https://hf-mirror.com/ggerganov/whisper.cpp/resolve/main/ggml-small.bin"),
            ApproxBytes = 487_601_967,
            MinBytes = 400_000_000,
            SizeLabel = "466 MB",
            Latency = "约 2–4 秒/句 · CPU",
            Description = "支持英语、日语、韩语等多语言；准确度与速度平衡。",
        },
        new()
        {
            Id = "whisper-medium-en",
            Kind = ModelKind.SpeechRecognition,
            DisplayName = "Whisper medium.en",
            ShortName = "medium.en",
            FileName = Path.Combine("whisper", "ggml-medium.en.bin"),
            Urls = BuildUrls("https://hf-mirror.com/ggerganov/whisper.cpp/resolve/main/ggml-medium.en.bin"),
            ApproxBytes = 1_533_774_201,
            MinBytes = 1_200_000_000,
            SizeLabel = "1.5 GB",
            Latency = "约 5–8 秒/句 · CPU",
            Description = "准确度更高，但 CPU 负担明显；较慢的机器不建议使用。",
        },
        new()
        {
            Id = "whisper-large-v3-turbo",
            Kind = ModelKind.SpeechRecognition,
            DisplayName = "Whisper large-v3-turbo",
            ShortName = "large-v3-turbo",
            FileName = Path.Combine("whisper", "ggml-large-v3-turbo.bin"),
            Urls = BuildUrls("https://hf-mirror.com/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo.bin"),
            ApproxBytes = 1_624_000_000,
            MinBytes = 1_400_000_000,
            SizeLabel = "1.6 GB",
            Latency = "约 4–8 秒/句 · CPU",
            Description = "接近 large-v3 的准确度，速度快很多；CPU 可用，独显更佳。",
        },
        new()
        {
            Id = "whisper-large-v3",
            Kind = ModelKind.SpeechRecognition,
            DisplayName = "Whisper large-v3",
            ShortName = "large-v3",
            FileName = Path.Combine("whisper", "ggml-large-v3.bin"),
            Urls = BuildUrls("https://hf-mirror.com/ggerganov/whisper.cpp/resolve/main/ggml-large-v3.bin"),
            ApproxBytes = 3_250_000_000,
            MinBytes = 2_800_000_000,
            SizeLabel = "3.1 GB",
            Latency = "约 10–20 秒/句 · CPU（建议独显）",
            Description = "最高准确度；CPU 上非常慢，建议搭配独显使用。",
        },
    };

    public static IReadOnlyList<ModelDescriptor> BuiltInTranslationModels { get; } = new List<ModelDescriptor>
    {
        new()
        {
            Id = "qwen2.5-1.5b",
            Kind = ModelKind.Translation,
            DisplayName = "Qwen2.5 1.5B Instruct",
            ShortName = "Qwen1.5B",
            FileName = Path.Combine("translation", "qwen2.5-1.5b-instruct-q4_k_m.gguf"),
            Urls = new[]
            {
                "https://hf-mirror.com/bartowski/Qwen2.5-1.5B-Instruct-GGUF/resolve/main/Qwen2.5-1.5B-Instruct-Q4_K_M.gguf",
                "https://modelscope.cn/models/Qwen/Qwen2.5-1.5B-Instruct-GGUF/resolve/master/qwen2.5-1.5b-instruct-q4_k_m.gguf",
                "https://huggingface.co/bartowski/Qwen2.5-1.5B-Instruct-GGUF/resolve/main/Qwen2.5-1.5B-Instruct-Q4_K_M.gguf",
            },
            ApproxBytes = 1_117_320_736,
            MinBytes = 900_000_000,
            SizeLabel = "1.1 GB",
            Latency = "约 1–2 秒/句 · 10 线程 CPU",
            Description = "速度和质量平衡，推荐默认；日常字幕翻译够用。",
        },
        new()
        {
            Id = "qwen2.5-0.5b",
            Kind = ModelKind.Translation,
            DisplayName = "Qwen2.5 0.5B Instruct",
            ShortName = "Qwen0.5B",
            FileName = Path.Combine("translation", "qwen2.5-0.5b-instruct-q4_k_m.gguf"),
            Urls = new[]
            {
                "https://hf-mirror.com/bartowski/Qwen2.5-0.5B-Instruct-GGUF/resolve/main/Qwen2.5-0.5B-Instruct-Q4_K_M.gguf",
                "https://modelscope.cn/models/Qwen/Qwen2.5-0.5B-Instruct-GGUF/resolve/master/qwen2.5-0.5b-instruct-q4_k_m.gguf",
                "https://huggingface.co/bartowski/Qwen2.5-0.5B-Instruct-GGUF/resolve/main/Qwen2.5-0.5B-Instruct-Q4_K_M.gguf",
            },
            ApproxBytes = 397_808_032,
            MinBytes = 300_000_000,
            SizeLabel = "约 400 MB",
            Latency = "约 0.5–1 秒/句 · CPU",
            Description = "更省资源、延迟更低；译文更直，复杂句子的质量一般。",
        },
        new()
        {
            Id = "qwen2.5-3b",
            Kind = ModelKind.Translation,
            DisplayName = "Qwen2.5 3B Instruct",
            ShortName = "Qwen3B",
            FileName = Path.Combine("translation", "qwen2.5-3b-instruct-q4_k_m.gguf"),
            Urls = new[]
            {
                "https://hf-mirror.com/bartowski/Qwen2.5-3B-Instruct-GGUF/resolve/main/Qwen2.5-3B-Instruct-Q4_K_M.gguf",
                "https://modelscope.cn/models/Qwen/Qwen2.5-3B-Instruct-GGUF/resolve/master/qwen2.5-3b-instruct-q4_k_m.gguf",
                "https://huggingface.co/bartowski/Qwen2.5-3B-Instruct-GGUF/resolve/main/Qwen2.5-3B-Instruct-Q4_K_M.gguf",
            },
            ApproxBytes = 1_929_146_464,
            MinBytes = 1_500_000_000,
            SizeLabel = "约 1.9 GB",
            Latency = "约 2–4 秒/句 · CPU",
            Description = "翻译质量更好，资源占用明显更高；建议配合较新的 CPU。",
        },
        new()
        {
            Id = "qwen2.5-7b",
            Kind = ModelKind.Translation,
            DisplayName = "Qwen2.5 7B Instruct",
            ShortName = "Qwen7B",
            FileName = Path.Combine("translation", "qwen2.5-7b-instruct-q4_k_m.gguf"),
            Urls = BuildUrls("https://hf-mirror.com/bartowski/Qwen2.5-7B-Instruct-GGUF/resolve/main/Qwen2.5-7B-Instruct-Q4_K_M.gguf"),
            ApproxBytes = 4_684_000_000,
            MinBytes = 3_800_000_000,
            SizeLabel = "约 4.4 GB",
            Latency = "约 4–8 秒/句 · CPU（较慢）",
            Description = "7B 模型，翻译质量明显更好；CPU 上延迟较高，适合追求质量的场景。",
        },
        new()
        {
            Id = "qwen2.5-14b",
            Kind = ModelKind.Translation,
            DisplayName = "Qwen2.5 14B Instruct",
            ShortName = "Qwen14B",
            FileName = Path.Combine("translation", "qwen2.5-14b-instruct-q4_k_m.gguf"),
            Urls = BuildUrls("https://hf-mirror.com/bartowski/Qwen2.5-14B-Instruct-GGUF/resolve/main/Qwen2.5-14B-Instruct-Q4_K_M.gguf"),
            ApproxBytes = 8_990_000_000,
            MinBytes = 7_000_000_000,
            SizeLabel = "约 9 GB",
            Latency = "约 10–20 秒/句 · CPU（很慢）",
            Description = "14B 模型，质量最高；CPU 上非常慢，只在需要最高质量时使用。",
        },
    };

    public static IReadOnlyList<ModelDescriptor> GetAsrModels()
    {
        var list = new List<ModelDescriptor>(BuiltInAsrModels);
        list.AddRange(CustomModelStore.Load()
            .Where(c => c.Kind != "translation")
            .Select(ToDescriptor));
        return list;
    }

    public static IReadOnlyList<ModelDescriptor> GetTranslationModels()
    {
        var list = new List<ModelDescriptor>(BuiltInTranslationModels);
        list.AddRange(CustomModelStore.Load()
            .Where(c => c.Kind == "translation")
            .Select(ToDescriptor));
        return list;
    }

    public static ModelDescriptor GetAsr(string? id) =>
        GetAsrModels().FirstOrDefault(m => m.Id == id) ?? BuiltInAsrModels[0];

    public static ModelDescriptor GetTranslation(string? id) =>
        GetTranslationModels().FirstOrDefault(m => m.Id == id) ?? BuiltInTranslationModels[0];

    public static IReadOnlyList<ModelDescriptor> MergeAsrWithSource(IReadOnlyList<ModelDescriptor> fromSource)
    {
        var list = new List<ModelDescriptor>();
        foreach (var model in fromSource)
            if (list.All(existing => existing.Id != model.Id))
                list.Add(model);
        foreach (var model in BuiltInAsrModels)
            if (list.All(existing => existing.Id != model.Id))
                list.Add(model);
        foreach (var model in CustomModelStore.Load().Where(c => c.Kind != "translation"))
            if (list.All(existing => existing.Id != model.Id))
                list.Add(ToDescriptor(model));
        return list;
    }

    public static async Task<IReadOnlyList<ModelDescriptor>> LoadAsrFromSourceAsync(CancellationToken ct)
    {
        var files = await FetchWhisperFilesAsync(ct);
        if (files.Count == 0) return BuiltInAsrModels;

        var list = new List<ModelDescriptor>();
        foreach (var (name, size) in files)
        {
            var shortName = name.Replace("ggml-", "").Replace(".bin", "");
            var (latency, description) = Describe(shortName);
            list.Add(new ModelDescriptor
            {
                Id = "whisper-" + shortName,
                Kind = ModelKind.SpeechRecognition,
                DisplayName = "Whisper " + shortName,
                ShortName = shortName,
                FileName = Path.Combine("whisper", name),
                Urls = BuildUrls($"https://hf-mirror.com/ggerganov/whisper.cpp/resolve/main/{name}"),
                ApproxBytes = size,
                MinBytes = Math.Max(1_000_000, size - 2_000_000),
                SizeLabel = FormatBytes(size),
                Latency = latency,
                Description = description,
            });
        }
        return list;
    }

    private static readonly HttpClient CatalogClient = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly string CatalogCachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LiveSub", "asr-catalog.json");

    private static async Task<List<(string Name, long Size)>> FetchWhisperFilesAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));

        // The two endpoints fail independently; race them so one slow API does not add 20 s per host.
        var pending = new[]
        {
            FetchWhisperSourceAsync("https://hf-mirror.com", timeout.Token),
            FetchWhisperSourceAsync("https://huggingface.co", timeout.Token),
        }.ToList();
        while (pending.Count > 0)
        {
            var completed = await Task.WhenAny(pending);
            pending.Remove(completed);
            try
            {
                var files = await completed;
                if (files.Count > 0)
                {
                    SaveCatalogCache(files);
                    return files;
                }
            }
            catch (Exception ex)
            {
                AppPaths.Log($"Fetch whisper model list failed: {ex.Message}");
            }
        }

        var cached = LoadCatalogCache();
        if (cached.Count > 0)
        {
            AppPaths.Log($"Using cached whisper model list ({cached.Count} files).");
            return cached;
        }
        return new List<(string, long)>();
    }

    private static async Task<List<(string, long)>> FetchWhisperSourceAsync(string baseUrl, CancellationToken ct)
    {
        var json = await CatalogClient.GetStringAsync(
            $"{baseUrl}/api/models/ggerganov/whisper.cpp/tree/main", ct);
        using var document = JsonDocument.Parse(json);
        var result = new List<(string, long)>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.TryGetProperty("type", out var typeElement) && typeElement.GetString() != "file")
                continue;
            if (!item.TryGetProperty("path", out var pathElement)) continue;
            var path = pathElement.GetString();
            if (path == null || !path.StartsWith("ggml-", StringComparison.Ordinal) ||
                !path.EndsWith(".bin", StringComparison.Ordinal))
                continue;
            var size = item.TryGetProperty("size", out var sizeElement) ? sizeElement.GetInt64() : 0;
            result.Add((path, size));
        }
        return result;
    }

    private static void SaveCatalogCache(List<(string Name, long Size)> files)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CatalogCachePath)!);
            var payload = files.Select(file => new CatalogCacheEntry { Name = file.Name, Size = file.Size }).ToArray();
            File.WriteAllText(CatalogCachePath, JsonSerializer.Serialize(payload));
        }
        catch (Exception ex)
        {
            AppPaths.Log("Save model list cache failed: " + ex.Message);
        }
    }

    private static List<(string Name, long Size)> LoadCatalogCache()
    {
        try
        {
            if (!File.Exists(CatalogCachePath)) return new List<(string, long)>();
            var entries = JsonSerializer.Deserialize<CatalogCacheEntry[]>(File.ReadAllText(CatalogCachePath));
            return entries == null
                ? new List<(string, long)>()
                : entries.Select(entry => (entry.Name, entry.Size)).ToList();
        }
        catch (Exception ex)
        {
            AppPaths.Log("Load model list cache failed: " + ex.Message);
            return new List<(string, long)>();
        }
    }

    private sealed class CatalogCacheEntry
    {
        public string Name { get; set; } = "";
        public long Size { get; set; }
    }

    private static (string Latency, string Description) Describe(string shortName)
    {
        if (shortName.Contains("large", StringComparison.OrdinalIgnoreCase))
            return ("约 8–15 秒/句 · CPU（建议独显）", "最高准确度，CPU 上很慢，建议配合独显使用。");
        if (shortName.Contains("medium", StringComparison.OrdinalIgnoreCase))
            return ("约 5–8 秒/句 · CPU", "准确度更高，但 CPU 负担明显。");
        if (shortName.Contains("small", StringComparison.OrdinalIgnoreCase))
            return ("约 2–4 秒/句 · 8 线程 CPU", "准确度与速度平衡，日常推荐。");
        if (shortName.Contains("base", StringComparison.OrdinalIgnoreCase))
            return ("约 1–2 秒/句 · CPU", "更快、更省资源；准确度略低。");
        if (shortName.Contains("tiny", StringComparison.OrdinalIgnoreCase))
            return ("约 0.5–1 秒/句 · CPU", "最快、最省资源，适合追求速度；准确度一般。");
        return ("速度未知 · CPU", "来自 ggerganov/whisper.cpp 的量化模型。");
    }

    private static ModelDescriptor ToDescriptor(CustomModel custom)
    {
        var kind = custom.Kind == "translation" ? ModelKind.Translation : ModelKind.SpeechRecognition;
        var approx = custom.ApproxBytes > 0 ? custom.ApproxBytes : 0;
        return new ModelDescriptor
        {
            Id = custom.Id,
            Kind = kind,
            DisplayName = custom.DisplayName,
            ShortName = custom.DisplayName,
            FileName = custom.FileName,
            Urls = BuildUrls(custom.Url),
            ApproxBytes = approx,
            MinBytes = custom.MinBytes > 0 ? custom.MinBytes : 1_000_000,
            SizeLabel = approx > 0 ? FormatBytes(approx) : "未知大小",
            Latency = "自定义模型 · CPU",
            Description = custom.Url,
        };
    }

    private static string[] BuildUrls(string url)
    {
        if (url.Contains("huggingface.co/", StringComparison.OrdinalIgnoreCase))
            return new[] { url.Replace("https://huggingface.co/", "https://hf-mirror.com/"), url };
        if (url.Contains("hf-mirror.com/", StringComparison.OrdinalIgnoreCase))
            return new[] { url, url.Replace("https://hf-mirror.com/", "https://huggingface.co/") };
        return new[] { url, url };
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "未知大小";
        string[] units = { "B", "KB", "MB", "GB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }
}
