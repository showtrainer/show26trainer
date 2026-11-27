using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using ShowTrainer.Core.Resolve;

namespace ShowTrainer.App;

public sealed class PlayerItem
{
    public required PlayerRef P { get; init; }
    public byte? Overall { get; init; }
    public string Label => $"{P.Name}  ({P.Position}{(Overall is byte o ? $" {o}" : "")})";
}

public sealed class RatingRow : Notify
{
    public required RatingField Field { get; init; }
    public string Group => Field.Group;
    public string Name => Field.Name;
    public string Note => Field.Offset == 0 ? "card overall at the primary position; stored, the game does not recalculate it right away"
        : Field.Name.StartsWith("Reaction part") ? "card Reaction = average of parts 1-4, rounded down; raise all four"
        : Field.Confirmed ? "matches the in-game card" : "not confirmed: one player's card showed a different value";
    private string _live = "";
    public string Live { get => _live; set => Set(ref _live, value); }
    private string _edit = "";
    public string EditValue { get => _edit; set => Set(ref _edit, value); }
}

/// <summary>One control in the player settings panel (a checkbox for flags, a drop-down for choices).</summary>
internal sealed class SettingControl
{
    public required EntryField F { get; init; }
    public required Control C { get; init; }
    /// <summary>The live value the control was last loaded from; the control shows a change when its value differs.</summary>
    public int? Loaded { get; set; }

    public int? UiValue => C switch
    {
        CheckBox cb => cb.IsChecked == true ? 1 : 0,
        ComboBox co => co.SelectedIndex >= 0 && co.SelectedIndex <= F.MaxValue ? co.SelectedIndex : Loaded,
        _ => Loaded,
    };
    public bool Changed => Loaded != null && UiValue != Loaded;
}

/// <summary>Players tab: look up any player in the league table, edit their ratings record and their entry settings.</summary>
public partial class MainWindow
{
    private readonly ObservableCollection<RatingRow> _ratings = new();
    private readonly List<SettingControl> _settings = new();
    private readonly HashSet<string> _autoSnapped = new();
    private PlayerRef? _player;
    private bool _playerWarned, _loadingSettings;

    private static string SnapshotDir => Path.Combine(DataDir, "snapshots");

    private void InitPlayers()
    {
        RatingsGrid.ItemsSource = _ratings;
        FillGroup.ItemsSource = new[] { "All ratings" }.Concat(PlayerTable.Fields.Select(f => f.Group).Distinct().Where(g => g != "Overall")).Append("Reaction parts").ToList();
        FillGroup.SelectedIndex = 0;
        PositionFilter.ItemsSource = new[] { "All positions" }.Concat(PlayerRef.Positions).ToList();
        PositionFilter.SelectedIndex = 0;
        PlayerSort.ItemsSource = new[] { "Sort by name", "Sort by overall" };
        PlayerSort.SelectedIndex = 0;
        BuildSettingsPanel();
    }

    private readonly TextBlock _infoText = new() { FontFamily = new FontFamily("Consolas"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 2, 4, 2) };

    private void BuildSettingsPanel()
    {
        SettingsPanel.Children.Add(new GroupBox
        {
            Header = "Info (read-only)", Margin = new Thickness(0, 2, 0, 4), Content = _infoText,
            ToolTip = "Decoded from the player entry and matched against 22 real players. Team is not found yet.",
        });
        foreach (var g in PlayerTable.EntryFields.GroupBy(f => f.Group))
        {
            var box = new GroupBox { Header = g.Key, Margin = new Thickness(0, 2, 0, 4) };
            Panel host = g.Key == "Positions" ? new StackPanel() : new WrapPanel();
            foreach (var f in g)
            {
                Control c;
                if (f.Choices != null)
                {
                    var combo = new ComboBox { Width = 90, Margin = new Thickness(4, 1, 4, 1) };
                    combo.SelectionChanged += (_, _) => SettingTouched();
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
                    row.Children.Add(new TextBlock { Text = f.Name, Width = 120, VerticalAlignment = VerticalAlignment.Center });
                    row.Children.Add(combo);
                    host.Children.Add(row);
                    c = combo;
                }
                else
                {
                    var cb = new CheckBox { Content = f.Name, Margin = new Thickness(4, 2, 8, 2), MinWidth = g.Key == "Quirks" ? 175 : 50 };
                    cb.Click += (_, _) => SettingTouched();
                    host.Children.Add(cb);
                    c = cb;
                }
                c.IsEnabled = false;
                c.ToolTip = (f.ProvenOnUser ? "Proven on your RTTS player (survived save + restart)." : "Shown on the card after a write, or decoded only; not save-tested.")
                    + (f.Name == "Primary position" ? " Not write-tested on anyone. Changing your own primary position also makes the Targets tab refuse writes (its guards expect P)." : "")
                    + (f.Name == "Pitching role" ? " Pitchers only: 0 SP, 1 RP, 2 CP. Fielders hold other codes, shown as 'other'." : "");
                _settings.Add(new SettingControl { F = f, C = c });
            }
            box.Content = host;
            SettingsPanel.Children.Add(box);
        }
    }

    private void PlayerSearch_KeyDown(object s, KeyEventArgs e) { if (e.Key == Key.Enter) PlayerFind_Click(s, e); }

    private void PlayerFind_Click(object s, RoutedEventArgs e)
    {
        var l = _ctx.Layout;
        if (l == null) { PlayerStatus.Text = "Game not detected."; return; }
        if (_session.CheckBuild(l) is string warn) { PlayerStatus.Text = warn; return; }
        const int Max = 500;
        var found = PlayerTable.Find(l, PlayerSearch.Text);
        if (PositionFilter.SelectedIndex > 0) found = found.Where(p => p.Position == (string)PositionFilter.SelectedItem).ToList();
        var ovr = PlayerTable.Fields[0];
        var items = found.Select(p => new PlayerItem { P = p, Overall = PlayerTable.Read(l, p, ovr) });
        items = PlayerSort.SelectedIndex == 1 ? items.OrderByDescending(i => i.Overall ?? 0).ThenBy(i => i.P.Name) : items.OrderBy(i => i.P.Name);
        PlayerList.ItemsSource = items.Take(Max).ToList();
        PlayerStatus.Text = found.Count == 0 ? "No players found (is your career loaded and on the hub?)" : $"{found.Count} found" + (found.Count > Max ? $", showing {Max}" : "");
        if (found.Count == 1) PlayerList.SelectedIndex = 0;
    }

    private void PlayerList_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        _ratings.Clear();
        _player = (PlayerList.SelectedItem as PlayerItem)?.P;
        foreach (var sc in _settings) { sc.Loaded = null; sc.C.IsEnabled = false; }
        if (_player == null) return;
        foreach (var f in PlayerTable.Fields) _ratings.Add(new RatingRow { Field = f });
        var l = _ctx.Layout;
        bool me = l != null && PlayerTable.IsUser(l, _player);
        SettingsNote.Text = (me ? "This is your RTTS player. Settings with a proven tooltip survived save + restart." :
            "Another player: these settings use the same layout as yours, but writes to other players' settings are not save-tested yet.")
            + " Changed settings show in blue until you apply them.";
        RefreshRatings();
    }

    private void RefreshRatings()
    {
        var l = _ctx.Layout;
        if (_player == null || l == null || _ratings.Count == 0) return;
        bool ok = PlayerTable.Recheck(l, _player, out var why) != null;
        foreach (var r in _ratings) r.Live = ok ? PlayerTable.Read(l, _player, r.Field)?.ToString() ?? "?" : "?";
        if (!ok) PlayerStatus.Text = $"{_player.Name}: {why}";
        RefreshSettings(ok ? l : null);
    }

    /// <summary>Loads live values into untouched controls; controls the user changed keep their pending value.</summary>
    private void RefreshSettings(Core.Guest.GuestLayout? l)
    {
        var info = l != null && _player != null ? PlayerTable.ReadInfo(l, _player) : null;
        _infoText.Text = info == null ? "" : string.Join("\n", info.Chunk(2).Select(c => string.Join("   ", c.Select(x => $"{x.Name + ":",-10} {x.Value,-7}"))));
        _loadingSettings = true;
        try
        {
            foreach (var sc in _settings)
            {
                int? live = l != null && _player != null ? PlayerTable.Read(l, _player, sc.F) : null;
                sc.C.IsEnabled = live != null;
                if (live == null || (sc.Changed && sc.Loaded != null)) continue;
                if (sc.Loaded == live && sc.UiValue == live) continue;
                sc.Loaded = live;
                SetUi(sc, live.Value);
            }
        }
        finally { _loadingSettings = false; }
        StyleSettings();
    }

    private static void SetUi(SettingControl sc, int v)
    {
        if (sc.C is CheckBox cb) cb.IsChecked = v != 0;
        else if (sc.C is ComboBox co)
        {
            var items = sc.F.Choices!.ToList();
            if (v > sc.F.MaxValue) items.Add(sc.F.Describe(v)); // unknown code: shown, and selecting it means "leave as is"
            co.ItemsSource = items;
            co.SelectedIndex = Math.Min(v, items.Count - 1);
        }
    }

    private void SettingTouched() { if (!_loadingSettings) StyleSettings(); }

    private void StyleSettings()
    {
        var blue = new SolidColorBrush(Color.FromRgb(0x1D, 0x4E, 0xD8));
        foreach (var sc in _settings)
        {
            sc.C.FontWeight = sc.Changed ? FontWeights.Bold : FontWeights.Normal;
            sc.C.Foreground = sc.Changed ? blue : SystemColors.ControlTextBrush;
        }
    }

    private void PlayerRevertSettings_Click(object s, RoutedEventArgs e)
    {
        foreach (var sc in _settings) sc.Loaded = null;
        RefreshSettings(_ctx.Layout);
    }

    // ------------------------------------------------------------------ writing

    /// <summary>Common gate for Players-tab writes: build check, backup, a one-time notice, and an automatic snapshot of the player.</summary>
    private bool PlayerWriteGate(PlayerRef p)
    {
        var l = _ctx.Layout;
        if (l == null) { MessageBox.Show("Game not detected."); return false; }
        if (_session.CheckBuild(l) is string warn) { MessageBox.Show(warn); return false; }
        if (!EnsureBackup()) return false;
        if (!_playerWarned && MessageBox.Show("Edits to players are saved with your career (proven for ratings and for your own settings). The game can still change ratings later through progression, streaks or slumps.\n\n" +
                "Before the first edit to each player, the trainer saves a snapshot you can restore from (Compare / restore...).\n\nContinue?",
                "Editing players", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) return false;
        _playerWarned = true;
        if (_autoSnapped.Add($"{p.Entry:X}") && PlayerSnapshot.Take(l, p) is { } snap)
        {
            var path = Path.Combine(SnapshotDir, $"{PlayerSnapshot.SafeFileName(p.Name)}_{DateTime.Now:yyyyMMdd_HHmmss}_auto.json");
            snap.Save(path);
            Core.Infra.Log.Info($"Auto snapshot before first edit: {path}");
        }
        return true;
    }

    /// <summary>Shared backup prompt (Targets and Players). False if the user backs out.</summary>
    private bool EnsureBackup()
    {
        if (BackupAck.IsChecked == true) return true;
        var res = MessageBox.Show("You have not backed up your save in this session.\n\nYes = back up now and continue\nNo = continue without a backup\nCancel = don't write",
            "Back up your save", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        if (res == MessageBoxResult.Cancel || (res == MessageBoxResult.Yes && !MakeBackup())) return false;
        BackupAck.IsChecked = true;
        return true;
    }

    /// <summary>"85" sets 85; "+5" / "-5" move the live value. Null if the text is not a valid rating.</summary>
    private static int? ParseRating(string text, string live)
    {
        text = text.Trim();
        bool rel = text.StartsWith('+') || (text.StartsWith('-') && text.Length > 1);
        if (!int.TryParse(rel ? text[1..] : text, out var n)) return null;
        int v = !rel ? n : int.TryParse(live, out var cur) ? (text[0] == '+' ? cur + n : cur - n) : -1;
        return v is >= 0 and <= 99 ? v : null;
    }

    private void PlayerApplyRow_Click(object s, RoutedEventArgs e)
    {
        if (s is FrameworkElement { DataContext: RatingRow r }) ApplyRatings(new List<RatingRow> { r });
    }

    private void PlayerApplyAll_Click(object s, RoutedEventArgs e) => ApplyRatings(_ratings.Where(r => !string.IsNullOrWhiteSpace(r.EditValue)).ToList());

    private void ApplyRatings(List<RatingRow> rows)
    {
        var l = _ctx.Layout;
        if (l == null || _player == null) { MessageBox.Show("Pick a player first."); return; }
        rows = rows.Where(r => !string.IsNullOrWhiteSpace(r.EditValue)).ToList();
        if (rows.Count == 0) return;
        var values = new Dictionary<RatingRow, int>();
        foreach (var r in rows)
        {
            if (ParseRating(r.EditValue, r.Live) is not int v)
            { MessageBox.Show($"{r.Name}: '{r.EditValue}' does not give a rating 0..99 (now {r.Live}). Nothing was written."); return; }
            values[r] = v;
        }
        if (!PlayerWriteGate(_player)) return;
        int n = 0;
        foreach (var r in rows)
        {
            if (PlayerTable.Write(l, _player, r.Field, values[r], _ctx.Freezer.History) is string why)
            { MessageBox.Show($"Nothing was written to {r.Name}: {why}." + (n > 0 ? $" {n} earlier rating(s) were written (see History)." : "")); RefreshRatings(); return; }
            r.EditValue = "";
            n++;
        }
        RefreshRatings();
        PlayerStatus.Text = $"Applied {n} rating(s) to {_player.Name}. Reopen the player's card in-game to check before saving."
            + (rows.Any(r => r.Field.Offset is >= 0x01 and <= 0x2F) && !rows.Any(r => r.Field.Offset == 0) ? " The card's overall is stored separately (Overall row)." : "");
    }

    private void PlayerFill_Click(object s, RoutedEventArgs e)
    {
        var text = FillValue.Text.Trim();
        if (_ratings.Count == 0 || text.Length == 0) return;
        var group = FillGroup.SelectedItem as string ?? "All ratings";
        var rows = _ratings.Where(r => r.Field.Offset != 0 && (group == "All ratings" || r.Group == group || (group == "Reaction parts" && r.Name.StartsWith("Reaction part")))).ToList();
        // Pitching ratings are 0 for most hitters; filling "All" leaves them alone unless the player has any.
        if (group == "All ratings" && rows.Where(r => r.Group == "Pitching").All(r => r.Live == "0")) rows = rows.Where(r => r.Group != "Pitching").ToList();
        foreach (var r in rows)
        {
            if (ParseRating(text, r.Live) is not int v) { MessageBox.Show($"'{text}' does not give a rating 0..99 for {r.Name} (now {r.Live})."); return; }
            r.EditValue = v.ToString();
        }
        PlayerStatus.Text = $"Typed {rows.Count} value(s). Nothing is written until you press Apply all typed ratings.";
    }

    private void PlayerCopyFrom_Click(object s, RoutedEventArgs e)
    {
        var l = _ctx.Layout;
        if (l == null || _player == null) { MessageBox.Show("Pick the player to copy TO first."); return; }
        var name = Prompt.Ask(this, "Copy ratings", $"Full name of the player whose ratings to type into {_player.Name}'s New value column:");
        if (string.IsNullOrWhiteSpace(name)) return;
        var src = PlayerTable.All(l).Where(p => p.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (src.Count != 1) { MessageBox.Show(src.Count == 0 ? $"No player named {name}." : $"{src.Count} players are named {name}; can't tell which."); return; }
        if (PlayerTable.Recheck(l, src[0], out var why) == null) { MessageBox.Show(why); return; }
        int n = 0;
        foreach (var r in _ratings)
            if (PlayerTable.Read(l, src[0], r.Field) is byte v && v.ToString() != r.Live && v <= 99) { r.EditValue = v.ToString(); n++; }
        PlayerStatus.Text = $"Typed {n} value(s) from {src[0].Name}. Review them (clear any you don't want, e.g. Overall or Pitching), then Apply all typed ratings.";
    }

    private void PlayerClearTyped_Click(object s, RoutedEventArgs e) { foreach (var r in _ratings) r.EditValue = ""; }

    private void PlayerApplySettings_Click(object s, RoutedEventArgs e)
    {
        var l = _ctx.Layout;
        if (l == null || _player == null) { MessageBox.Show("Pick a player first."); return; }
        var changes = _settings.Where(sc => sc.Changed && sc.UiValue is int).ToList();
        if (changes.Count == 0) { PlayerStatus.Text = "No setting changed."; return; }
        bool me = PlayerTable.IsUser(l, _player);
        var lines = changes.Select(sc => $"{sc.F.Label}: {sc.F.Describe(sc.Loaded!.Value)} -> {sc.F.Describe(sc.UiValue!.Value)}" +
            (me && sc.F.ProvenOnUser ? "" : "   (not save-tested)")).ToList();
        var extra = me && changes.Any(sc => sc.F.Name == "Primary position")
            ? "\n\nYou are changing your own primary position. The Targets tab guards expect P, so they will refuse writes until it is P again." : "";
        if (MessageBox.Show($"Write these to {_player.Name}?\n\n" + string.Join("\n", lines) + extra, "Apply settings",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (!PlayerWriteGate(_player)) return;
        int n = 0;
        foreach (var sc in changes)
        {
            if (PlayerTable.Write(l, _player, sc.F, sc.UiValue!.Value, _ctx.Freezer.History) is string why)
            { MessageBox.Show($"{sc.F.Label} was not written: {why}." + (n > 0 ? $" {n} earlier setting(s) were written (see History)." : "")); break; }
            sc.Loaded = null; // reload from memory
            n++;
        }
        RefreshRatings();
        PlayerStatus.Text = $"Applied {n} setting(s) to {_player.Name}. Reopen the player's card in-game to check before saving.";
    }

    // ------------------------------------------------------------------ snapshots

    private void PlayerSnapshot_Click(object s, RoutedEventArgs e)
    {
        var l = _ctx.Layout;
        if (l == null || _player == null) { MessageBox.Show("Pick a player first."); return; }
        if (PlayerTable.Recheck(l, _player, out var why) == null) { MessageBox.Show(why); return; }
        if (PlayerSnapshot.Take(l, _player) is not { } snap) { MessageBox.Show("Could not read the player."); return; }
        var path = Path.Combine(SnapshotDir, $"{PlayerSnapshot.SafeFileName(_player.Name)}_{DateTime.Now:yyyyMMdd_HHmmss}.json");
        snap.Save(path);
        PlayerStatus.Text = $"Snapshot saved: {path}";
    }

    private void PlayerRestore_Click(object s, RoutedEventArgs e)
    {
        var l = _ctx.Layout;
        if (l == null || _player == null) { MessageBox.Show("Pick a player first."); return; }
        Directory.CreateDirectory(SnapshotDir);
        var dlg = new OpenFileDialog
        {
            InitialDirectory = SnapshotDir, Filter = "Player snapshot (*.json)|*.json",
            FileName = PlayerSnapshot.SafeFileName(_player.Name) + "_*.json",
        };
        if (dlg.ShowDialog(this) != true) return;
        PlayerSnapshot snap;
        try { snap = PlayerSnapshot.Load(dlg.FileName); } catch (Exception ex) { MessageBox.Show(ex.Message, "Not a snapshot"); return; }
        if (!snap.Name.Equals(_player.Name, StringComparison.OrdinalIgnoreCase)) { MessageBox.Show($"This snapshot is for {snap.Name}, not {_player.Name}."); return; }
        if (PlayerTable.Recheck(l, _player, out var why) == null) { MessageBox.Show(why); return; }
        var diff = snap.Compare(l, _player);
        if (diff.Count == 0) { MessageBox.Show($"{_player.Name} matches the snapshot from {snap.Taken:g}.", "Compare"); return; }
        var text = string.Join("\n", diff.Take(40).Select(d => $"{d.Field}: now {d.NowText}, snapshot {d.ThenText}")) + (diff.Count > 40 ? $"\n... and {diff.Count - 40} more" : "");
        if (MessageBox.Show($"{diff.Count} difference(s) from the snapshot of {snap.Taken:g}:\n\n{text}\n\nRestore the snapshot values?", "Compare / restore",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (!PlayerWriteGate(_player)) return;
        var err = snap.Restore(l, _player, _ctx.Freezer.History, out int written);
        foreach (var sc in _settings) sc.Loaded = null;
        RefreshRatings();
        PlayerStatus.Text = err == null ? $"Restored {written} value(s) to {_player.Name}. Check the card in-game before saving." : $"Stopped after {written} value(s): {err}";
        if (err != null) MessageBox.Show(PlayerStatus.Text);
    }
}
