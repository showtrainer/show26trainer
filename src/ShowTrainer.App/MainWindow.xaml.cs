using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using ShowTrainer.Core;
using ShowTrainer.Core.Infra;
using ShowTrainer.Core.Native;
using ShowTrainer.Core.Scan;
using ShowTrainer.Core.Session;

namespace ShowTrainer.App;

public sealed record ProcessItem(EmulatorCandidate C)
{
    public string Display => $"{C.Pid}  {C.Name}  {C.WindowTitle}";
}

public partial class MainWindow : Window
{
    private const int MaxDisplayedResults = 2000;
    private readonly TrainerContext _ctx = new();
    private readonly ObservableCollection<ResultRow> _results = new();
    private readonly ObservableCollection<EntryRow> _entries = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private ScanResults? _scan;
    private CancellationTokenSource? _cts;
    private TrainerSession _session = new();
    private string? _sessionPath;

    public static string DataDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShowTrainer");

    public MainWindow()
    {
        InitializeComponent();
        Log.FilePath = Path.Combine(DataDir, "logs", "trainer.log");
        KindCombo.ItemsSource = Enum.GetValues<ValueKind>(); KindCombo.SelectedItem = ValueKind.Int32;
        CompareCombo.ItemsSource = new[] { ScanCompare.Exact, ScanCompare.Between, ScanCompare.Unknown };
        CompareCombo.SelectedIndex = 0;
        ScopeCombo.ItemsSource = Enum.GetValues<ScanScope>(); ScopeCombo.SelectedItem = ScanScope.AllGuest;
        EntryKindColumn.ItemsSource = Enum.GetValues<ValueKind>();
        EntryCopyColumn.ItemsSource = Enum.GetValues<CopyKind>();
        ResultsGrid.ItemsSource = _results;
        EntriesGrid.ItemsSource = _entries;
        InitTargets();
        foreach (var l in Log.Snapshot()) LogBox.AppendText(l + "\n");
        Log.LineAdded += l => Dispatcher.BeginInvoke(() => { LogBox.AppendText(l + "\n"); LogBox.ScrollToEnd(); });
        InitPlayers();
        InitHistory();
        InitMemory();
        _timer.Tick += (_, _) => { Tick(); RefreshRatings(); };
        _timer.Start();
        RefreshProcesses();
        if (ProcessCombo.Items.Count > 0) Attach();
    }

    // ------------------------------------------------------------------ attach / status

    private void RefreshProcesses()
    {
        ProcessCombo.ItemsSource = HostProcess.FindCandidates().Select(c => new ProcessItem(c)).ToList();
        ProcessCombo.SelectedIndex = ProcessCombo.Items.Count > 0 ? 0 : -1;
    }

    private void RefreshProcesses_Click(object s, RoutedEventArgs e) => RefreshProcesses();
    private void Attach_Click(object s, RoutedEventArgs e) => Attach();
    private void Detach_Click(object s, RoutedEventArgs e) { _ctx.Detach(); UpdateStatus(); }

    private async void Redetect_Click(object s, RoutedEventArgs e)
    {
        if (_ctx.Process == null) { Attach(); return; }
        StatusText.Text = "Detecting guest memory...";
        await Task.Run(() => _ctx.Redetect());
        AfterDetect();
    }

    private async void Attach()
    {
        if (ProcessCombo.SelectedItem is not ProcessItem p) { StatusText.Text = "No emulator process selected"; return; }
        StatusText.Text = "Attaching and detecting guest memory...";
        try { await Task.Run(() => _ctx.Attach(p.C.Pid)); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Attach failed"); }
        AfterDetect();
    }

    private void AfterDetect()
    {
        UpdateStatus();
        var l = _ctx.Layout;
        if (l == null) { StatusText.Text = "Guest memory not found - is the game booted? See Detection / Log."; return; }
        StatusText.Text = l.GameDetected ? "Game detected" : "Guest memory found but main module not identified";
        LoadProfileIfAvailable();
        CheckHistoryLaunch();
        var warn = _session.CheckBuild(l);
        if (warn != null) MessageBox.Show(warn, "Build mismatch", MessageBoxButton.OK, MessageBoxImage.Warning);
        // Addresses move between launches: re-resolve, and never restore a freeze automatically.
        _ctx.Freezer.Clear();
        foreach (var r in _entries)
        {
            r.Frozen = false;
            bool ok = ShowTrainer.Core.Resolve.PointerResolver.Resolve(l, r.Entry, warn == null);
            r.Location = ok ? l.FormatAddress(r.Entry.Address) : "UNRESOLVED - re-scan";
            r.RefreshAddress();
        }
    }

    private void UpdateStatus()
    {
        bool alive = _ctx.IsAttached;
        ProcStatus.Text = alive ? $"Attached: {_ctx.Process!.Name} (pid {_ctx.Process.Pid})" : "Not attached";
        ProcStatus.Foreground = alive ? Brushes.LightGreen : new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
        var l = _ctx.Layout;
        bool game = alive && l?.GameDetected == true;
        bool isMlb = game && (l!.GameTitle?.Contains("MLB", StringComparison.OrdinalIgnoreCase) ?? false);
        GameStatus.Text = game ? $"Game: {(isMlb ? "YES - " : "")}{l!.GameTitle ?? "unknown title"}" : "Game: not detected";
        GameStatus.Foreground = isMlb ? Brushes.LightGreen : game ? Brushes.Gold : new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
        LayoutStatus.Text = l == null ? "" :
            $"title {l.TitleId ?? "?"}  build {(l.BuildId != null ? l.BuildId[..16] : "?")}  main {l.Main?.Va:X}  heap {l.HeapBase:X} ({l.HeapSize >> 20} MB)  [{l.Memory.Mode}]";
    }

    private void Tick()
    {
        if (_ctx.Process != null && !_ctx.IsAttached)
        {
            Log.Warn("Emulator process exited");
            _ctx.Detach();
            UpdateStatus();
            return;
        }
        var mem = _ctx.Layout?.Memory;
        if (mem == null || _scan == null) { RefreshEntries(); return; }
        // Refresh only rows currently in view-ish (first N); cheap enough at 2000.
        int vs = _scan.ValueSize;
        var buf = new byte[vs];
        foreach (var r in _results)
            r.Current = mem.Read(r.Address, buf) ? ValueCodec.Format(_scan.Kind, buf) : "?";
        RefreshEntries();
    }

    private void RefreshEntries()
    {
        var mem = _ctx.Layout?.Memory;
        foreach (var r in _entries)
        {
            if (mem == null) { r.Live = ""; r.Status = "game not detected"; continue; }
            if (PointerOk(r, out var status) == false) { r.Live = "?"; r.Status = status; continue; }
            r.Status = r.Frozen ? "frozen" : "ok";
            var b = mem.ReadBytes(r.Entry.Address, ValueCodec.SizeOf(r.Kind));
            r.Live = b != null ? ValueCodec.Format(r.Kind, r.Entry.Own(b)) : "?";
        }
    }

    /// <summary>Re-checks an entry live, so loading screens and moved records show up in the UI.</summary>
    private bool PointerOk(EntryRow r, out string status)
    {
        var l = _ctx.Layout!;
        if (_session.CheckBuild(l) != null) { status = "wrong game build"; return false; }
        if (ShowTrainer.Core.Resolve.PointerResolver.CheckForWrite(l, r.Entry, out status) is not ulong at) return false;
        if (at != r.Entry.Address) { r.Entry.Address = at; r.Location = l.FormatAddress(at); r.RefreshAddress(); }
        r.Entry.Resolved = true;
        return true;
    }

    // ------------------------------------------------------------------ scanning

    private void CompareCombo_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        var c = CompareCombo.SelectedItem as ScanCompare?;
        bool two = c == ScanCompare.Between;
        Value2Box.Visibility = Value2Label.Visibility = two ? Visibility.Visible : Visibility.Collapsed;
        Value1Box.IsEnabled = c is not (ScanCompare.Unknown or ScanCompare.Changed or ScanCompare.Unchanged or ScanCompare.Increased or ScanCompare.Decreased);
    }

    private ScanRequest BuildRequest()
    {
        var c = (ScanCompare)CompareCombo.SelectedItem;
        double? tol = string.IsNullOrWhiteSpace(TolBox.Text) ? null : double.Parse(TolBox.Text, CultureInfo.InvariantCulture);
        string? v1 = Value1Box.IsEnabled ? Value1Box.Text : null, v2 = c == ScanCompare.Between ? Value2Box.Text : null;
        if (Value1Box.IsEnabled && string.IsNullOrWhiteSpace(v1)) throw new InvalidOperationException("Enter a value.");
        return new ScanRequest(c, v1, v2, tol);
    }

    private async void FirstScan_Click(object s, RoutedEventArgs e)
    {
        if (_ctx.Scanner == null) { MessageBox.Show("Attach to Eden with the game running first."); return; }
        ScanRequest req;
        try { req = BuildRequest(); } catch (Exception ex) { MessageBox.Show(ex.Message); return; }
        var kind = (ValueKind)KindCombo.SelectedItem; var scope = (ScanScope)ScopeCombo.SelectedItem;
        int align = int.TryParse(AlignBox.Text, out var a) ? a : 0;
        await RunScan(ct => _ctx.Scanner.FirstScan(kind, req, align, scope, ProgressSink(), ct));
    }

    private async void NextScan_Click(object s, RoutedEventArgs e)
    {
        if (_ctx.Scanner == null || _scan == null) return;
        ScanRequest req;
        try { req = BuildRequest(); } catch (Exception ex) { MessageBox.Show(ex.Message); return; }
        var prev = _scan;
        await RunScan(ct => _ctx.Scanner.NextScan(prev, req, ProgressSink(), ct));
    }

    private void NewScan_Click(object s, RoutedEventArgs e)
    {
        _scan = null; _results.Clear(); ResultCount.Text = "";
        KindCombo.IsEnabled = true; NextScanBtn.IsEnabled = false;
        CompareCombo.ItemsSource = new[] { ScanCompare.Exact, ScanCompare.Between, ScanCompare.Unknown };
        CompareCombo.SelectedIndex = 0;
    }

    private void Cancel_Click(object s, RoutedEventArgs e) => _cts?.Cancel();

    private IProgress<double> ProgressSink() => new Progress<double>(p => Progress.Value = p);

    private async Task RunScan(Func<CancellationToken, ScanResults> work)
    {
        _cts = new CancellationTokenSource();
        FirstScanBtn.IsEnabled = NextScanBtn.IsEnabled = false; CancelBtn.IsEnabled = true;
        StatusText.Text = "Scanning...";
        try
        {
            var ct = _cts.Token;
            var res = await Task.Run(() => work(ct), ct);
            _scan = res;
            ShowResults();
            KindCombo.IsEnabled = false;
            var cur = CompareCombo.SelectedItem;
            CompareCombo.ItemsSource = Enum.GetValues<ScanCompare>().Where(c => c != ScanCompare.Unknown).ToArray();
            CompareCombo.SelectedItem = cur is ScanCompare sc && sc != ScanCompare.Unknown ? sc : ScanCompare.Changed;
            StatusText.Text = $"Scan #{res.ScanNumber} done";
        }
        catch (OperationCanceledException) { StatusText.Text = "Scan cancelled"; }
        catch (Exception ex) { Log.Error(ex.ToString()); MessageBox.Show(ex.Message, "Scan failed"); }
        finally
        {
            FirstScanBtn.IsEnabled = true; NextScanBtn.IsEnabled = _scan != null; CancelBtn.IsEnabled = false; Progress.Value = 0;
        }
    }

    private void ShowResults()
    {
        _results.Clear();
        var l = _ctx.Layout!;
        if (_scan == null) return;
        ResultCount.Text = $"{_scan.Count:N0} results{(_scan.Truncated ? " (truncated)" : "")}{(_scan.IsSnapshot ? " - snapshot taken; now change the value in-game and use Changed/Increased/..." : "")}";
        if (_scan.IsSnapshot) return;
        int n = Math.Min(MaxDisplayedResults, _scan.Addresses.Length);
        for (int i = 0; i < n; i++)
        {
            var va = _scan.Addresses[i];
            _results.Add(new ResultRow
            {
                Address = va,
                MainOffset = l.MainOffset(va) is long m && m >= 0 && m < 0x1000_0000 ? $"main+{m:X}" : "",
                HeapOffset = l.HeapOffset(va) is long h && h >= 0 && (ulong)h < l.HeapSize ? $"heap+{h:X}" : "",
                ScanValue = ValueCodec.Format(_scan.Kind, _scan.ValueAt(i)),
            });
        }
        if (_scan.Addresses.Length > n) ResultCount.Text += $" - showing first {n:N0}";
    }

    private void ResultsGrid_MouseDoubleClick(object s, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ResultsGrid.SelectedItem is ResultRow r) AddEntry(r.Address, _scan?.Kind ?? ValueKind.Int32);
    }

    private void AddSelectedResults_Click(object s, RoutedEventArgs e)
    {
        foreach (var r in ResultsGrid.SelectedItems.OfType<ResultRow>().ToList()) AddEntry(r.Address, _scan?.Kind ?? ValueKind.Int32);
    }

    private void AddEntry(ulong va, ValueKind kind, string? name = null)
    {
        var l = _ctx.Layout;
        var e = new AddressEntry { Resolved = true, Address = va, Kind = kind, Name = name ?? $"value {_entries.Count + 1}" };
        if (l != null)
        {
            e.MainOffset = l.Main != null && va >= l.Main.Va && va < l.Main.End ? (long)(va - l.Main.Va) : null;
            e.HeapOffset = l.HeapOffset(va) is long h && h >= 0 && (ulong)h < l.HeapSize ? h : null;
        }
        var row = new EntryRow(e) { Location = l?.FormatAddress(va) ?? "" };
        _entries.Add(row);
        _session.Entries.Add(e);
    }

    private void AddManual_Click(object s, RoutedEventArgs e)
    {
        var text = Prompt.Ask(this, "Add address", "Guest address (hex), e.g. 2105E000E0 or main+1C23060:");
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            text = text.Trim();
            ulong va;
            if (text.StartsWith("main+", StringComparison.OrdinalIgnoreCase))
                va = (_ctx.Layout?.Main?.Va ?? throw new InvalidOperationException("Main module not detected")) + ulong.Parse(text[5..], NumberStyles.HexNumber);
            else if (text.StartsWith("heap+", StringComparison.OrdinalIgnoreCase))
                va = (_ctx.Layout?.HeapBase ?? 0) + ulong.Parse(text[5..], NumberStyles.HexNumber);
            else va = ulong.Parse(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text, NumberStyles.HexNumber);
            AddEntry(va, (ValueKind)KindCombo.SelectedItem);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message); }
    }

    private void RemoveEntry_Click(object s, RoutedEventArgs e)
    {
        foreach (var r in EntriesGrid.SelectedItems.OfType<EntryRow>().ToList())
        {
            _ctx.Freezer.Unfreeze(r.Entry);
            _entries.Remove(r); _session.Entries.Remove(r.Entry);
        }
    }

    // ------------------------------------------------------------------ writing

    /// <summary>Safety gate for every write. Returns false if the user backs out.</summary>
    private bool ConfirmWrite(EntryRow r, string value)
    {
        if (!r.Entry.Resolved) { MessageBox.Show($"'{r.Name}' has no confirmed address for this launch. Re-scan it first."); return false; }
        if (_ctx.Layout is { } lay && _session.CheckBuild(lay) is string mismatch) { MessageBox.Show(mismatch); return false; }
        if (!EnsureBackup()) return false;
        try { ValueCodec.Parse(r.Kind, value); }
        catch { MessageBox.Show($"'{value}' is not a valid {r.Kind}."); return false; }
        double d = ValueCodec.ParseDouble(value);
        if ((r.Entry.Min is double mn && d < mn) || (r.Entry.Max is double mx && d > mx))
        {
            MessageBox.Show($"{value} is outside {r.Entry.Min}..{r.Entry.Max} for {r.Name}. Nothing was written.");
            return false;
        }
        if (r.Copy == CopyKind.Unknown && !r.VerifiedPersists)
            Log.Warn($"Writing '{r.Name}' whose copy type is unknown - verify after save + reload.");
        return true;
    }

    private void ApplySelected_Click(object s, RoutedEventArgs e)
    {
        EntriesGrid.CommitEdit(DataGridEditingUnit.Row, true);
        ApplyRows(EntriesGrid.SelectedItems.OfType<EntryRow>().ToList());
    }

    private void FreezeCheck_Click(object s, RoutedEventArgs e)
    {
        if (s is not CheckBox cb || cb.DataContext is not EntryRow r) return;
        if (cb.IsChecked == true)
        {
            var value = string.IsNullOrWhiteSpace(r.EditValue) ? r.Live : r.EditValue;
            if (string.IsNullOrWhiteSpace(value) || value == "?" || !ConfirmWrite(r, value)
                || (!r.VerifiedPersists && MessageBox.Show($"{r.Name} is not proven to survive a save and reload. Freeze it anyway?", "Untested value", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes))
            { cb.IsChecked = false; return; }
            r.Entry.FreezeValue = value;
            _ctx.Freezer.Freeze(r.Entry, value);
            r.Frozen = true;
        }
        else
        {
            _ctx.Freezer.Unfreeze(r.Entry);
            r.Frozen = false;
        }
    }

    // ------------------------------------------------------------------ sessions

    private void SaveSession_Click(object s, RoutedEventArgs e)
    {
        var l = _ctx.Layout;
        Directory.CreateDirectory(Path.Combine(DataDir, "sessions"));
        var dlg = new SaveFileDialog
        {
            Filter = "Trainer session (*.json)|*.json",
            InitialDirectory = Path.Combine(DataDir, "sessions"),
            FileName = _sessionPath != null ? Path.GetFileName(_sessionPath) : $"{l?.TitleId ?? "session"}_{(l?.BuildId != null ? l.BuildId[..16] : "unknown")}.json",
        };
        if (dlg.ShowDialog(this) != true) return;
        _sessionPath = dlg.FileName;
        _session.Entries = _entries.Select(r => r.Entry).ToList();
        _session.SetScan(_scan, _sessionPath);
        _session.Save(_sessionPath, l);
        StatusText.Text = $"Saved {_sessionPath}";
    }

    private void LoadSession_Click(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Trainer session (*.json)|*.json", InitialDirectory = Path.Combine(DataDir, "sessions") };
        if (dlg.ShowDialog(this) != true) return;
        _session = TrainerSession.Load(dlg.FileName);
        _sessionPath = dlg.FileName;
        _ctx.Freezer.Clear();
        _entries.Clear();
        foreach (var en in _session.Entries) _entries.Add(new EntryRow(en) { Location = _ctx.Layout?.FormatAddress(en.Address) ?? "" });
        _scan = _session.Scan?.ToResults();
        if (_scan != null)
        {
            KindCombo.SelectedItem = _scan.Kind; KindCombo.IsEnabled = false; NextScanBtn.IsEnabled = true;
            CompareCombo.ItemsSource = Enum.GetValues<ScanCompare>().Where(c => c != ScanCompare.Unknown).ToArray();
            CompareCombo.SelectedItem = ScanCompare.Exact;
            if (_ctx.Layout != null) ShowResults();
        }
        if (_ctx.Layout != null) AfterDetect();
        StatusText.Text = $"Loaded {dlg.FileName}";
    }

    private void Window_Closing(object? s, System.ComponentModel.CancelEventArgs e)
    {
        _timer.Stop();
        _ctx.Dispose();
    }
}

/// <summary>Minimal text prompt dialog.</summary>
internal static class Prompt
{
    public static string? Ask(Window owner, string title, string label, string initial = "")
    {
        var w = new Window { Title = title, Owner = owner, Width = 420, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        var sp = new StackPanel { Margin = new Thickness(10) };
        var tb = new TextBox { Text = initial };
        var ok = new Button { Content = "OK", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, Width = 80 };
        string? result = null;
        ok.Click += (_, _) => { result = tb.Text; w.DialogResult = true; };
        sp.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        sp.Children.Add(tb); sp.Children.Add(ok);
        w.Content = sp;
        tb.Focus();
        return w.ShowDialog() == true ? result : null;
    }
}
