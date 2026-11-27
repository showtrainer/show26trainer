using System.Collections.ObjectModel;
using System.Windows;
using ShowTrainer.Core.Session;

namespace ShowTrainer.App;

public sealed class HistoryRow : Notify
{
    public required EditRecord R { get; init; }
    public string Time => R.When.ToString("HH:mm:ss");
    public string Target => R.Target;
    public string Field => R.Field;
    public string Old => R.OldText;
    public string New => R.NewText;
    public string Address => R.Address.ToString("X10");
    private string _note = "";
    public string Note { get => _note; set { if (Set(ref _note, value)) Raise(nameof(State)); } }
    public string State => (R.Undone ? "undone" : "written") + (Note.Length > 0 ? " - " + Note : "");
    public void Update() => Raise(nameof(State));
}

/// <summary>History tab: every write this launch, newest first, with guarded undo.</summary>
public partial class MainWindow
{
    private readonly ObservableCollection<HistoryRow> _history = new();
    private ulong? _historyMain;

    private void InitHistory()
    {
        HistoryGrid.ItemsSource = _history;
        _ctx.Freezer.History.Changed += () => Dispatcher.BeginInvoke(SyncHistory);
    }

    private void SyncHistory()
    {
        var items = _ctx.Freezer.History.Items;
        var known = _history.ToDictionary(h => h.R);
        _history.Clear();
        foreach (var r in items.Reverse()) { var row = known.TryGetValue(r, out var h) ? h : new HistoryRow { R = r }; row.Update(); _history.Add(row); }
    }

    /// <summary>History addresses are only good for one launch: drop them when main moved (relaunch).</summary>
    private void CheckHistoryLaunch()
    {
        var main = _ctx.Layout?.Main?.Va;
        if (_historyMain != null && main != _historyMain && _ctx.Freezer.History.Items.Count > 0)
        {
            _ctx.Freezer.History.Clear();
            HistoryStatus.Text = "The game was relaunched: the old history was cleared (its addresses no longer apply). Use player snapshots to go back further.";
        }
        _historyMain = main;
    }

    private void UndoSelected_Click(object s, RoutedEventArgs e) =>
        Undo(HistoryGrid.SelectedItems.OfType<HistoryRow>().OrderByDescending(h => h.R.When).ToList());

    private void UndoLast_Click(object s, RoutedEventArgs e) =>
        Undo(_history.Where(h => !h.R.Undone).Take(1).ToList());

    private void Undo(List<HistoryRow> rows)
    {
        var l = _ctx.Layout;
        if (l == null) { MessageBox.Show("Game not detected."); return; }
        rows = rows.Where(h => !h.R.Undone).ToList();
        if (rows.Count == 0) { HistoryStatus.Text = "Nothing to undo."; return; }
        int ok = 0;
        foreach (var h in rows)
        {
            // A frozen entry would rewrite the value straight away.
            var frozen = _entries.FirstOrDefault(x => x.Frozen && x.Category == h.R.Target && x.Name == h.R.Field);
            if (frozen != null) { h.Note = "unfreeze it first"; continue; }
            var why = _ctx.Freezer.History.Undo(l, h.R);
            h.Note = why ?? "";
            if (why == null) ok++;
        }
        SyncHistory();
        RefreshEntries(); RefreshRatings();
        HistoryStatus.Text = $"Undid {ok} of {rows.Count}." + (ok < rows.Count ? " See State for the reason the others were left alone." : " Check in-game before saving.");
    }
}
