using System.ComponentModel;
using System.Runtime.CompilerServices;
using ShowTrainer.Core.Scan;
using ShowTrainer.Core.Session;

namespace ShowTrainer.App;

public abstract class Notify : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One row of the scan result grid.</summary>
public sealed class ResultRow : Notify
{
    public ulong Address { get; init; }
    public string AddressText => Address.ToString("X10");
    public string MainOffset { get; init; } = "";
    public string HeapOffset { get; init; } = "";
    public string ScanValue { get; init; } = "";
    private string _current = "";
    public string Current { get => _current; set => Set(ref _current, value); }
}

/// <summary>Address-list row wrapping a persisted <see cref="AddressEntry"/>.</summary>
public sealed class EntryRow : Notify
{
    public AddressEntry Entry { get; }
    public EntryRow(AddressEntry e) { Entry = e; }

    public string Name { get => Entry.Name; set { Entry.Name = value; Raise(nameof(Name)); } }
    public string Category { get => Entry.Category; set { Entry.Category = value; Raise(nameof(Category)); } }
    public ValueKind Kind { get => Entry.Kind; set { Entry.Kind = value; Raise(nameof(Kind)); } }
    public CopyKind Copy { get => Entry.Copy; set { Entry.Copy = value; Raise(nameof(Copy)); } }
    public bool VerifiedPersists { get => Entry.VerifiedPersists; set { Entry.VerifiedPersists = value; Raise(nameof(VerifiedPersists)); } }
    public string Notes { get => Entry.Notes; set { Entry.Notes = value; Raise(nameof(Notes)); } }
    public string AddressText => Entry.Address.ToString("X10");
    private string _location = "";
    public string Location { get => _location; set => Set(ref _location, value); }

    private string _status = "";
    public string Status { get => _status; set => Set(ref _status, value); }

    private string _live = "";
    public string Live { get => _live; set => Set(ref _live, value); }

    private string _edit = "";
    /// <summary>Value typed by the user, applied on "Apply" or used for freezing.</summary>
    public string EditValue { get => _edit; set => Set(ref _edit, value); }

    public bool Frozen { get => Entry.Frozen; set { Entry.Frozen = value; Raise(nameof(Frozen)); } }

    public void RefreshAddress() { Raise(nameof(AddressText)); }
}
