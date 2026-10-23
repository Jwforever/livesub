using System.Text;
using Whisper.net;

namespace LiveSub.Services;

public sealed class WhisperAsrService : IDisposable
{
    private readonly WhisperFactory _factory;
    private readonly int _threads;
    private readonly string _language;

    public WhisperAsrService(string modelPath, string language = "en")
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("Whisper model not found", modelPath);
        _factory = WhisperFactory.FromPath(modelPath);
        _threads = Math.Clamp(Environment.ProcessorCount / 2, 4, 8);
        _language = SourceLanguages.Get(language).Code;
        AppPaths.Log($"Whisper model loaded: {modelPath} ({_threads} threads, language={_language})");
    }

    public async Task<string> TranscribeAsync(float[] samples, CancellationToken ct)
    {
        using var processor = _factory.CreateBuilder()
            .WithLanguage(_language)
            .WithThreads(_threads)
            .WithNoContext()
            .Build();

        var text = new StringBuilder();
        await foreach (var segment in processor.ProcessAsync(samples, ct))
        {
            var part = segment.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(part)) text.Append(part).Append(' ');
        }

        var result = text.ToString().Trim();
        AppPaths.Log($"ASR: {result}");
        return result;
    }

    public void Dispose()
    {
        try { _factory.Dispose(); } catch { }
    }
}
