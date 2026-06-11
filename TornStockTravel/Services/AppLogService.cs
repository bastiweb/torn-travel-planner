using System.IO;

namespace TornStockTravel.Services;

public static class AppLogService
{
    private const long MaximumLogSizeBytes = 1_000_000;
    private static readonly object SyncRoot = new();

    public static string LogDirectory
    {
        get
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "TornStockTravel", "logs");
        }
    }

    public static string LogPath => Path.Combine(LogDirectory, "app.log");

    public static void Info(string message)
    {
        Write("INFO", message);
    }

    public static void Warning(string message)
    {
        Write("WARN", message);
    }

    public static void Error(string message, Exception? exception = null)
    {
        string text = exception is null
            ? message
            : $"{message}{Environment.NewLine}{exception}";
        Write("ERROR", text);
    }

    private static void Write(string level, string message)
    {
        try
        {
            lock (SyncRoot)
            {
                Directory.CreateDirectory(LogDirectory);
                RotateIfNeeded();

                string line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {level} {message}{Environment.NewLine}";
                File.AppendAllText(LogPath, line);
            }
        }
        catch
        {
            // Logging should never interrupt the app.
        }
    }

    private static void RotateIfNeeded()
    {
        if (!File.Exists(LogPath))
        {
            return;
        }

        FileInfo fileInfo = new(LogPath);
        if (fileInfo.Length < MaximumLogSizeBytes)
        {
            return;
        }

        string archivePath = Path.Combine(LogDirectory, "app.previous.log");
        if (File.Exists(archivePath))
        {
            File.Delete(archivePath);
        }

        File.Move(LogPath, archivePath);
    }
}
