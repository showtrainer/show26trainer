using System.Collections.Concurrent;

namespace ShowTrainer.Core.Infra;

/// <summary>Simple thread-safe logger: keeps recent lines in memory (for the UI) and appends to logs/trainer.log.</summary>
public static class Log
{
    private static readonly object FileLock = new();
    private static readonly ConcurrentQueue<string> Recent = new();
    public static event Action<string>? LineAdded;
    public static string FilePath { get; set; } = Path.Combine(AppContext.BaseDirectory, "logs", "trainer.log");
    public static bool EchoToConsole { get; set; }

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg) => Write("ERR ", msg);
    public static void Debug(string msg) => Write("DBG ", msg);

    public static IReadOnlyCollection<string> Snapshot() => Recent.ToArray();

    private static void Write(string level, string msg)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {level} {msg}";
        Recent.Enqueue(line);
        while (Recent.Count > 5000 && Recent.TryDequeue(out _)) { }
        if (EchoToConsole) Console.Error.WriteLine(line);
        try
        {
            lock (FileLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
        }
        catch { /* logging must never throw */ }
        LineAdded?.Invoke(line);
    }
}
