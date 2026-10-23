namespace LiveSub.Services;

public static class AppPaths
{
    private static readonly object LogLock = new();

    public static string ModelsRoot
    {
        get
        {
            var custom = Environment.GetEnvironmentVariable("LIVESUB_MODELS");
            if (!string.IsNullOrWhiteSpace(custom)) return Path.GetFullPath(custom);

            var besideExe = Path.Combine(AppContext.BaseDirectory, "models");
            if (Directory.Exists(besideExe)) return besideExe;

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LiveSub", "models");
        }
    }

    public static string WhisperModelPath => ModelCatalog.GetAsr(ModelSelectionStore.Load().AsrId).FullPath;
    public static string TranslationModelPath => ModelCatalog.GetTranslation(ModelSelectionStore.Load().TranslationId).FullPath;

    public static string WhisperModelDirectory => Path.Combine(ModelsRoot, "whisper");
    public static string TranslationModelDirectory => Path.Combine(ModelsRoot, "translation");

    public static string LogFile
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LiveSub");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "live.log");
        }
    }

    public static void Log(string message)
    {
        try
        {
            lock (LogLock)
            {
                File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
