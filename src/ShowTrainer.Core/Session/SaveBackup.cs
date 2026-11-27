using System.Text.RegularExpressions;
using ShowTrainer.Core.Guest;
using ShowTrainer.Core.Infra;

namespace ShowTrainer.Core.Session;

/// <summary>Copies the game's Eden save folders and verifies the copy byte for byte.</summary>
public static class SaveBackup
{
    public static string Create(string? exePath, string titleId, string backupRoot)
    {
        var user = TitleInfo.FindUserDir(exePath) ?? throw new InvalidOperationException("Eden user folder not found.");
        var nand = Path.Combine(user, "nand");
        var cfg = Path.Combine(user, "config", "qt-config.ini");
        if (File.Exists(cfg))
        {
            var m = Regex.Match(File.ReadAllText(cfg), @"^nand_directory=(.+)$", RegexOptions.Multiline);
            if (m.Success && Directory.Exists(m.Groups[1].Value.Trim())) nand = m.Groups[1].Value.Trim();
        }
        var root = Path.Combine(nand, "user", "save");
        // <nand>/user/save/<16 hex>/<profile>/<title id>
        var sources = Directory.Exists(root)
            ? Directory.GetDirectories(root).SelectMany(Directory.GetDirectories).SelectMany(Directory.GetDirectories)
                .Where(d => Path.GetFileName(d).Equals(titleId, StringComparison.OrdinalIgnoreCase)).ToList()
            : new List<string>();
        if (sources.Count == 0) throw new InvalidOperationException($"No save folder for {titleId} under {root}.");
        var dest = Path.Combine(backupRoot, DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        int n = 0;
        foreach (var src in sources)
            foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            {
                var to = Path.Combine(dest, Path.GetRelativePath(root, f));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(f, to);
                if (!File.ReadAllBytes(f).AsSpan().SequenceEqual(File.ReadAllBytes(to))) throw new IOException($"Backup of {f} does not match.");
                n++;
            }
        if (n == 0) throw new InvalidOperationException("The save folder is empty.");
        Log.Info($"Save backup verified: {n} files -> {dest}");
        return dest;
    }
}
