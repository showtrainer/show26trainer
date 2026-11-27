using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using ShowTrainer.Core.Infra;
using ShowTrainer.Core.Resolve;
using ShowTrainer.Core.Session;

namespace ShowTrainer.App;

public sealed class MemoryRow
{
    public required EditStatus S { get; init; }
    public string Player => S.Edit.Player;
    public string Field => S.Edit.Field;
    public string Wanted => PlayerTable.DescribeField(S.Edit.Field, S.Edit.Value);
    public string Original => PlayerTable.DescribeField(S.Edit.Field, S.Edit.Original);
    public string Now => S.Now is int n ? PlayerTable.DescribeField(S.Edit.Field, n) : "?";
    public string When => S.Edit.When.ToString("MM-dd HH:mm");
    public string State => S.State;
}

public sealed class LeagueRow
{
    public required PlayerSnapshot.LeagueChange C { get; init; }
    public string Player => C.Player;
    public string Field => C.Field;
    public string Then => C.Then;
    public string Now => C.Now;
    public string Delta => int.TryParse(C.Then, out var a) && int.TryParse(C.Now, out var b) ? (b - a).ToString("+0;-0") : "";
}

/// <summary>My edits tab (persistent per-career edit memory) and League tab (league snapshots, changes, CSV).</summary>
public partial class MainWindow
{
    private EditMemory? _memory;
    private static string MemoryDir => Path.Combine(DataDir, "edit-memory");
    private static string LeagueDir => Path.Combine(DataDir, "league");

    private void InitMemory()
    {
        // Every player write and undo goes through the history; mirror it into the career's edit memory.
        _ctx.Freezer.History.Recorded += (r, undone) =>
        {
            if (r.Player == null || r.OldValue is not int o || r.NewValue is not int n) return;
            if (OpenMemory() is not { } m) { Log.Warn($"Edit memory: career not readable, {r.Player} {r.Field} not remembered"); return; }
            m.Remember(r.Player, r.Field, undone ? n : o, undone ? o : n);
        };
    }

    /// <summary>The edit memory for the loaded career (named after the RTTS player). Null while loading.</summary>
    private EditMemory? OpenMemory()
    {
        var l = _ctx.Layout;
        if (l == null || PlayerTable.UserName(l) is not string career) return _memory;
        if (_memory?.Career != career) _memory = EditMemory.Open(MemoryDir, career, l.BuildId);
        return _memory;
    }

    private List<EditStatus>? MemoryStatusNow(out string? why)
    {
        why = null;
        var l = _ctx.Layout;
        if (l == null) { why = "Game not detected."; return null; }
        if (OpenMemory() is not { } m) { why = "Your career is not readable right now (loading?). Go to the hub and try again."; return null; }
        var all = PlayerTable.All(l);
        if (all.Count == 0) { why = "The player table is not readable right now (loading?)."; return null; }
        return m.Status(l, all);
    }

    private void MemoryRefresh_Click(object s, RoutedEventArgs e) => RefreshMemory();

    private void RefreshMemory()
    {
        var st = MemoryStatusNow(out var why);
        if (st == null) { MemoryStatus.Text = why; return; }
        MemoryGrid.ItemsSource = st.Select(x => new MemoryRow { S = x }).ToList();
        int changed = st.Count(x => x.NeedsReapply);
        MemoryStatus.Text = $"{_memory!.Career}: {st.Count} remembered edit(s), {st.Count(x => x.State == "kept")} kept, {changed} changed by the game"
            + (st.Count - changed - st.Count(x => x.State == "kept") is int other and > 0 ? $", {other} not checkable" : "") + $". Checked {DateTime.Now:HH:mm:ss}.";
    }

    private void MemoryReapplyAll_Click(object s, RoutedEventArgs e)
    {
        var st = MemoryStatusNow(out var why);
        if (st == null) { MessageBox.Show(why); return; }
        Reapply(st.Where(x => x.NeedsReapply).ToList());
    }

    private void MemoryReapplySelected_Click(object s, RoutedEventArgs e)
    {
        var picked = MemoryGrid.SelectedItems.OfType<MemoryRow>().Select(r => r.S.Edit).ToHashSet();
        var st = MemoryStatusNow(out var why);
        if (st == null) { MessageBox.Show(why); return; }
        Reapply(st.Where(x => picked.Contains(x.Edit) && x.NeedsReapply).ToList());
    }

    private void Reapply(List<EditStatus> items)
    {
        var l = _ctx.Layout!;
        if (items.Count == 0) { MemoryStatus.Text = "Nothing to re-apply: every selected edit is kept, or can't be checked."; return; }
        if (_session.CheckBuild(l) is string warn) { MessageBox.Show(warn); return; }
        var list = string.Join("\n", items.Take(30).Select(x => $"{x.Edit.Player} {x.Edit.Field}: {PlayerTable.DescribeField(x.Edit.Field, x.Now!.Value)} -> {PlayerTable.DescribeField(x.Edit.Field, x.Edit.Value)}"))
            + (items.Count > 30 ? $"\n... and {items.Count - 30} more" : "");
        if (MessageBox.Show($"Write these {items.Count} value(s) back?\n\n{list}", "Re-apply edits", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (!EnsureBackup()) return;
        var (n, err) = _memory!.Reapply(l, items, _ctx.Freezer.History);
        RefreshMemory(); RefreshRatings();
        MemoryStatus.Text = $"Re-applied {n} of {items.Count}." + (err != null ? $" Stopped: {err}." : " Check the cards in-game before saving.");
        if (err != null) MessageBox.Show(MemoryStatus.Text);
    }

    private void MemoryForget_Click(object s, RoutedEventArgs e)
    {
        var rows = MemoryGrid.SelectedItems.OfType<MemoryRow>().ToList();
        if (rows.Count == 0 || _memory == null) return;
        if (MessageBox.Show($"Stop tracking {rows.Count} edit(s)? The game is not changed.", "Forget edits", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        _memory.Forget(rows.Select(r => r.S.Edit));
        RefreshMemory();
    }

    // ------------------------------------------------------------------ league

    private List<PlayerSnapshot>? TakeLeague()
    {
        var l = _ctx.Layout;
        if (l == null) { MessageBox.Show("Game not detected."); return null; }
        var all = PlayerSnapshot.TakeAll(l);
        if (all.Count == 0) { MessageBox.Show("The player table is not readable right now (loading?). Go to the hub and try again."); return null; }
        return all;
    }

    private void LeagueSnapshot_Click(object s, RoutedEventArgs e)
    {
        if (TakeLeague() is not { } all) return;
        var path = Path.Combine(LeagueDir, $"league_{DateTime.Now:yyyyMMdd_HHmmss}.json");
        PlayerSnapshot.SaveAll(path, all);
        LeagueStatus.Text = $"{all.Count} players saved to {path}. Play or sim, then Compare with snapshot.";
    }

    private void LeagueCompare_Click(object s, RoutedEventArgs e)
    {
        var l = _ctx.Layout;
        if (l == null) { MessageBox.Show("Game not detected."); return; }
        Directory.CreateDirectory(LeagueDir);
        var dlg = new OpenFileDialog { InitialDirectory = LeagueDir, Filter = "League snapshot (*.json)|*.json" };
        if (dlg.ShowDialog(this) != true) return;
        List<PlayerSnapshot> then;
        try { then = PlayerSnapshot.LoadAll(dlg.FileName); } catch (Exception ex) { MessageBox.Show(ex.Message, "Not a league snapshot"); return; }
        var now = PlayerTable.All(l);
        if (now.Count == 0) { MessageBox.Show("The player table is not readable right now (loading?)."); return; }
        var changes = PlayerSnapshot.CompareAll(l, then, now, out int skipped);
        var view = new ListCollectionView(changes.Select(c => new LeagueRow { C = c }).ToList());
        LeagueGrid.ItemsSource = view;
        ApplyLeagueFilter();
        LeagueStatus.Text = $"{changes.Select(c => c.Player).Distinct().Count()} player(s), {changes.Count} value(s) changed since {then.FirstOrDefault()?.Taken:g}. {skipped} not compared (missing now, or a shared name).";
    }

    private void LeagueFilter_TextChanged(object s, TextChangedEventArgs e) => ApplyLeagueFilter();

    private void ApplyLeagueFilter()
    {
        if (LeagueGrid.ItemsSource is not ListCollectionView v) return;
        var f = LeagueFilter.Text.Trim();
        v.Filter = f.Length == 0 ? null : o => o is LeagueRow r && (r.Player.Contains(f, StringComparison.OrdinalIgnoreCase) || r.Field.Contains(f, StringComparison.OrdinalIgnoreCase));
    }

    private void LeagueCsv_Click(object s, RoutedEventArgs e)
    {
        if (TakeLeague() is not { } all) return;
        Directory.CreateDirectory(LeagueDir);
        var dlg = new SaveFileDialog { InitialDirectory = LeagueDir, FileName = $"ratings_{DateTime.Now:yyyyMMdd_HHmm}.csv", Filter = "CSV (*.csv)|*.csv" };
        if (dlg.ShowDialog(this) != true) return;
        File.WriteAllText(dlg.FileName, PlayerSnapshot.ToCsv(all), new System.Text.UTF8Encoding(true));
        LeagueStatus.Text = $"{all.Count} players exported to {dlg.FileName}.";
    }
}
