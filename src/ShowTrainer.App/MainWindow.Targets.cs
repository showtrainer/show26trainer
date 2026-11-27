using System.ComponentModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using ShowTrainer.Core.Export;
using ShowTrainer.Core.Infra;
using ShowTrainer.Core.Resolve;
using ShowTrainer.Core.Scan;
using ShowTrainer.Core.Session;

namespace ShowTrainer.App;

/// <summary>Targets tab: one section per target, guarded apply/freeze, backups and cheat export.</summary>
public partial class MainWindow
{
    private void InitTargets()
    {
        var view = new ListCollectionView(_entries);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(EntryRow.Category)));
        TargetsGrid.ItemsSource = view;
    }

    /// <summary>Loads the bundled address profile for this exact game build.</summary>
    private void LoadProfileIfAvailable()
    {
        var l = _ctx.Layout;
        if (l?.TitleId == null || l.BuildId == null || _entries.Count > 0) return;
        var path = Path.Combine(AppContext.BaseDirectory, "profiles", $"{l.TitleId}_{l.BuildId[..16]}.json");
        if (!File.Exists(path)) { Log.Warn($"No profile for this game build: {path}"); return; }
        _session = TrainerSession.Load(path);
        foreach (var e in _session.Entries) { e.Frozen = false; _entries.Add(new EntryRow(e)); }
    }

    private void Backup_Click(object s, RoutedEventArgs e) => MakeBackup();

    private bool MakeBackup()
    {
        try
        {
            var title = _ctx.Layout?.TitleId ?? throw new InvalidOperationException("Game not detected.");
            var dir = SaveBackup.Create(_ctx.Process?.ExePath, title, Path.Combine(DataDir, "save-backups"));
            BackupAck.IsChecked = true;
            StatusText.Text = $"Save backed up and verified: {dir}";
            return true;
        }
        catch (Exception ex) { Log.Error("Backup: " + ex.Message); MessageBox.Show(ex.Message, "Backup failed"); return false; }
    }

    private void ApplyRow_Click(object s, RoutedEventArgs e)
    {
        if (s is FrameworkElement { DataContext: EntryRow r }) ApplyRows(new List<EntryRow> { r });
    }

    private void ApplyAllTargets_Click(object s, RoutedEventArgs e) => ApplyRows(_entries.Where(r => !string.IsNullOrWhiteSpace(r.EditValue)).ToList());

    private void ApplyRows(List<EntryRow> rows)
    {
        var l = _ctx.Layout;
        if (l == null) { MessageBox.Show("Game not detected."); return; }
        var risky = rows.Where(r => !r.VerifiedPersists).Select(r => r.Name).ToList();
        if (risky.Count > 0 && MessageBox.Show("Not yet proven to survive a save and reload:\n\n" + string.Join("\n", risky.Take(20)) +
                "\n\nA wrong write can corrupt your save. Continue?", "Untested values", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        foreach (var r in rows)
        {
            if (string.IsNullOrWhiteSpace(r.EditValue)) continue;
            var value = r.EditValue.Trim();
            if (!ConfirmWrite(r, value)) return;

            // Season stats and career totals are stored separately: move the total by the same amount.
            EntryRow? career = null; string careerValue = "";
            var m = Regex.Match(r.Name, @"^20\d\d (.+)$");
            if (LinkCareer.IsChecked == true && m.Success && r.Category == "Stats")
            {
                career = _entries.FirstOrDefault(x => x.Category == "Career" && x.Name == "Career " + m.Groups[1].Value);
                if (career != null && rows.Contains(career)) career = null; // the user typed an explicit total
                if (career == null && !_entries.Any(x => x.Category == "Career" && x.Name == "Career " + m.Groups[1].Value))
                    Log.Warn($"{r.Name} has no linked career total; totals may disagree.");
                if (career != null)
                {
                    var cur = ReadGuarded(r); var tot = ReadGuarded(career);
                    if (cur == null || tot == null) { MessageBox.Show($"{r.Name}: could not read both the season and career value safely. Nothing was written."); return; }
                    var sum = tot.Value + (long)ValueCodec.ParseDouble(value) - cur.Value;
                    careerValue = sum.ToString();
                    if (sum < (career.Entry.Min ?? sum) || sum > (career.Entry.Max ?? sum)) { MessageBox.Show($"{career.Name} would become {sum}. Nothing was written."); return; }
                }
            }

            if (_ctx.Freezer.Write(r.Entry, value) is string why) { MessageBox.Show($"Nothing was written to {r.Name}: {why}."); return; }
            if (r.Frozen) _ctx.Freezer.Freeze(r.Entry, value);
            r.EditValue = "";
            if (career != null)
            {
                if (_ctx.Freezer.Write(career.Entry, careerValue) is string why2)
                { MessageBox.Show($"{r.Name} was written, but {career.Name} was not: {why2}. The two now disagree; set it to {careerValue} before saving."); return; }
                if (career.Frozen) _ctx.Freezer.Freeze(career.Entry, careerValue);
            }
        }
        StatusText.Text = "Applied. Reopen the in-game screen to check it before saving.";
        RefreshEntries();
    }

    private long? ReadGuarded(EntryRow r)
    {
        var l = _ctx.Layout;
        if (l == null || PointerResolver.CheckForWrite(l, r.Entry, out _) is not ulong at) return null;
        var b = l.Memory.ReadBytes(at, ValueCodec.SizeOf(r.Kind));
        return b == null ? null : (long)ValueCodec.ToDouble(r.Kind, r.Entry.Own(b));
    }

    private void UnfreezeAll_Click(object s, RoutedEventArgs e)
    {
        _ctx.Freezer.Clear();
        foreach (var r in _entries) r.Frozen = false;
        StatusText.Text = "All freezes off";
    }

    private void Export_Click(object s, RoutedEventArgs e)
    {
        var l = _ctx.Layout;
        if (l?.BuildId == null) { MessageBox.Show("Game not detected."); return; }
        if (_session.CheckBuild(l) is string warn) { MessageBox.Show(warn); return; }
        var items = _entries.Where(r => r.VerifiedPersists && r.Entry.Pointer != null && r.Entry.Mask == null) // cheats cannot write single bits
            .Select(r => (r.Entry, Value: string.IsNullOrWhiteSpace(r.EditValue) ? r.Live : r.EditValue.Trim(), Shift: PointerResolver.RowShift(l, r.Entry) ?? -1)).ToList();
        if (items.Count == 0 || items.Any(i => i.Value is "" or "?" || i.Shift < 0)) { MessageBox.Show("Values must be readable to export. Load your career first."); return; }
        string text; int words;
        try { text = AtmosphereCheats.Export(l.BuildId, items, out words); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Export failed"); return; }
        var dir = Path.Combine(DataDir, "exports");
        Directory.CreateDirectory(dir);
        var dlg = new SaveFileDialog { InitialDirectory = dir, FileName = AtmosphereCheats.FileName(l.BuildId), Filter = "Cheat file (*.txt)|*.txt" };
        if (dlg.ShowDialog(this) != true) return;
        File.WriteAllText(dlg.FileName, text);
        MessageBox.Show($"{items.Count} cheats ({words} code words).\n\nEnabled cheats keep rewriting the value, like a freeze. They check for null pointers and a plausible record, " +
            "but fewer things than this trainer does. Back up your save and leave them off during loading.", "Cheat file exported");
    }
}
