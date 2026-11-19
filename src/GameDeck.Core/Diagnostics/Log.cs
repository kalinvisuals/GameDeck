namespace GameDeck.Core.Diagnostics;

/// <summary>Minimal rolling text log in the data directory. Never throws.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _path;

    public static void Initialize(string dataDirectory)
    {
        var dir = Path.Combine(dataDirectory, "logs");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "gamedeck.log");
        try
        {
            if (File.Exists(_path) && new FileInfo(_path).Length > 2_000_000)
                File.Move(_path, _path + ".1", overwrite: true);
        }
        catch { /* best effort */ }
    }

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warn(string message, Exception? ex = null) => Write("WARN", message, ex);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        if (ex is not null) line += $" :: {ex.GetType().Name}: {ex.Message}";
        System.Diagnostics.Debug.WriteLine(line);
        if (_path is null) return;
        try
        {
            lock (Gate) File.AppendAllText(_path, line + Environment.NewLine);
        }
        catch { /* logging must never break audio */ }
    }
}
