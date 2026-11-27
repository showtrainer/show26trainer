using System.Text.RegularExpressions;
using ShowTrainer.Core.Native;

namespace ShowTrainer.Core.Guest;

/// <summary>Finds the title ID / game name from the Eden window title and log file.</summary>
public static class TitleInfo
{
    private static readonly Regex TitleIdRx = new(@"\b(01[0-9A-Fa-f]{14})\b");

    /// <summary>Eden user dir: portable "user" folder next to the exe, else %APPDATA%\eden.</summary>
    public static string? FindUserDir(string? exePath)
    {
        if (exePath != null)
        {
            var portable = Path.Combine(Path.GetDirectoryName(exePath)!, "user");
            if (Directory.Exists(portable)) return portable;
        }
        foreach (var name in new[] { "eden", "Eden", "yuzu", "citron", "suyu", "sudachi", "torzu" })
        {
            var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), name);
            if (Directory.Exists(d)) return d;
        }
        return null;
    }

    public static void Fill(GuestLayout layout, HostProcess proc, Action<string> L)
    {
        var title = proc.CurrentWindowTitle();
        L($"Window title: \"{title}\"");
        // Eden title: "Eden | <version> | <Game Name> | ..." (format varies by build)
        var parts = title.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        layout.GameTitle = parts.FirstOrDefault(p => p.Contains("MLB", StringComparison.OrdinalIgnoreCase))
                           ?? parts.Skip(2).FirstOrDefault(p => !p.Contains("GNU") && !p.Contains("MSVC") && !p.Contains("clang", StringComparison.OrdinalIgnoreCase));

        var user = FindUserDir(proc.ExePath);
        L($"Eden user dir: {user ?? "(not found)"}");
        if (user == null) return;
        var logFile = Path.Combine(user, "log", "eden_log.txt");
        if (!File.Exists(logFile)) logFile = Directory.Exists(Path.Combine(user, "log"))
            ? Directory.GetFiles(Path.Combine(user, "log"), "*.txt").OrderByDescending(File.GetLastWriteTime).FirstOrDefault() ?? logFile : logFile;
        if (!File.Exists(logFile)) { L("Eden log not found; title ID must be entered manually"); return; }
        try
        {
            string text;
            using (var fs = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs)) text = sr.ReadToEnd();
            // Prefer lines that talk about the program being loaded.
            string? tid = null;
            foreach (var line in text.Split('\n').Reverse())
            {
                if (!(line.Contains("program", StringComparison.OrdinalIgnoreCase) || line.Contains("title", StringComparison.OrdinalIgnoreCase)
                      || line.Contains("Booting", StringComparison.OrdinalIgnoreCase) || line.Contains("Loading", StringComparison.OrdinalIgnoreCase))) continue;
                var m = TitleIdRx.Match(line);
                if (m.Success && !m.Value.StartsWith("0100000000000", StringComparison.Ordinal)) { tid = m.Value.ToUpperInvariant(); L($"Title ID {tid} from log line: {line.Trim()}"); break; }
            }
            layout.TitleId = tid;
            if (tid == null) L($"No title ID found in {logFile}");
        }
        catch (Exception ex) { L($"Could not read Eden log: {ex.Message}"); }
    }
}
