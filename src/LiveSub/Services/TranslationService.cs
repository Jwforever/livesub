using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using LLama;
using LLama.Common;
using LLama.Sampling;

namespace LiveSub.Services;

public sealed class TranslationService : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LLamaWeights? _weights;
    private StatelessExecutor? _executor;
    private int _threads;

    public string SourceLanguage { get; set; } = "en";

    public async Task LoadAsync(string modelPath, CancellationToken ct = default)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("Translation model not found", modelPath);

        // 6–8 threads keeps 0.5B translation responsive while leaving CPU for Windows captions.
        _threads = Math.Clamp(Environment.ProcessorCount / 2, 6, 8);
        await Task.Run(() =>
        {
            var parameters = new ModelParams(modelPath)
            {
                ContextSize = 2048,
                GpuLayerCount = 0,
                BatchSize = 512,
                Threads = _threads,
            };
            _weights = LLamaWeights.LoadFromFile(parameters);
            _executor = new StatelessExecutor(_weights, parameters);
        }, ct);
        AppPaths.Log($"Translation model loaded: {modelPath} ({_threads} threads)");
    }

    public async Task<string> TranslateAsync(
        string text,
        Action<string>? partial = null,
        CancellationToken ct = default)
    {
        if (_executor == null) throw new InvalidOperationException("Translation model is not loaded.");
        await _gate.WaitAsync(ct);
        try
        {
            var prompt = BuildPrompt(text);
            var inference = new InferenceParams
            {
                MaxTokens = Math.Clamp(text.Length, 48, 128),
                AntiPrompts = new[] { "<|im_end|>", "<|endoftext|>" },
                SamplingPipeline = new DefaultSamplingPipeline
                {
                    Temperature = 0.1f,
                    TopP = 0.9f,
                    TopK = 40,
                    RepeatPenalty = 1.05f,
                },
            };

            var output = new StringBuilder();
            var clock = Stopwatch.StartNew();
            var lastShown = 0L;
            await foreach (var token in _executor.InferAsync(prompt, inference, ct))
            {
                output.Append(token);
                if (partial == null || clock.ElapsedMilliseconds - lastShown < 50) continue;
                lastShown = clock.ElapsedMilliseconds;
                var preview = Clean(output.ToString());
                if (HasChinese(preview)) partial(preview);
            }

            var translated = Clean(output.ToString());
            if (!HasChinese(translated) && text.Any(char.IsLetter))
            {
                // Small models sometimes echo an incomplete fragment instead of translating it.
                AppPaths.Log($"MT retry (no Chinese): {translated}");
                output.Clear();
                var retryPrompt = BuildRetryPrompt(text);
                await foreach (var token in _executor.InferAsync(retryPrompt, inference, ct))
                {
                    output.Append(token);
                    if (partial == null || clock.ElapsedMilliseconds - lastShown < 50) continue;
                    lastShown = clock.ElapsedMilliseconds;
                    var preview = Clean(output.ToString());
                    if (HasChinese(preview)) partial(preview);
                }
                translated = Clean(output.ToString());
            }

            if (!HasChinese(translated) && text.Any(char.IsLetter))
                translated = "（未翻译）";

            AppPaths.Log($"MT: {translated}");
            return translated;
        }
        finally
        {
            _gate.Release();
        }
    }

    private string BuildPrompt(string text)
    {
        var source = SourceLanguages.Get(SourceLanguage);
        return "<|im_start|>system\n" +
            $"你是实时字幕翻译。把下面的{source.DisplayName}内容译成简洁、自然、口语化的简体中文；短句或片段也直译，" +
            "不拒答、不解释、不加标签或引号，只输出中文译文。\n" +
            "<|im_end|>\n" +
            "<|im_start|>user\n" + text.Trim() + "\n<|im_end|>\n" +
            "<|im_start|>assistant\n";
    }

    private string BuildRetryPrompt(string text) =>
        "<|im_start|>system\nYou are a subtitle translator. Translate the input into Simplified Chinese and output Chinese only.\n<|im_end|>\n" +
        "<|im_start|>user\n" + text.Trim() + "\n<|im_end|>\n<|im_start|>assistant\n";

    private static bool HasChinese(string value) =>
        value.Any(ch => ch >= '\u4e00' && ch <= '\u9fff');

    private static string Clean(string value)
    {
        var text = value
            .Replace("<|im_end|>", string.Empty)
            .Replace("<|endoftext|>", string.Empty)
            .Replace("</s>", string.Empty)
            .Trim();

        text = Regex.Replace(text, @"^(翻译|译文|Translation)\s*[:：]\s*", string.Empty, RegexOptions.IgnoreCase);
        text = text.Trim('"', '“', '”', '\'', ' ');
        text = Regex.Replace(text, @"\s+", " ");
        return text.Trim();
    }

    public void Dispose()
    {
        try { _executor = null; } catch { }
        try { _weights?.Dispose(); } catch { }
        _gate.Dispose();
    }
}
