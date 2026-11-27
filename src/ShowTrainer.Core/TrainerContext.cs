using ShowTrainer.Core.Guest;
using ShowTrainer.Core.Infra;
using ShowTrainer.Core.Native;
using ShowTrainer.Core.Scan;

namespace ShowTrainer.Core;

/// <summary>Ties together the attached process, detected layout, scanner and freezer.</summary>
public sealed class TrainerContext : IDisposable
{
    public HostProcess? Process { get; private set; }
    public GuestLayout? Layout { get; private set; }
    public ScanEngine? Scanner { get; private set; }
    public FreezeService Freezer { get; } = new();

    public bool IsAttached => Process?.IsAlive == true;
    public bool GameDetected => Layout?.GameDetected == true;

    public void Attach(int? pid = null, Action<string>? progress = null)
    {
        Detach();
        if (pid == null)
        {
            var c = HostProcess.FindCandidates();
            if (c.Count == 0) throw new InvalidOperationException("No Eden process found. Start Eden (or pick the process manually if renamed).");
            foreach (var x in c) Log.Info($"Candidate: pid={x.Pid} {x.Name} \"{x.WindowTitle}\" {x.Path}");
            pid = c[0].Pid;
        }
        Process = HostProcess.Open(pid.Value);
        Redetect(progress);
    }

    public void Redetect(Action<string>? progress = null)
    {
        if (Process == null) return;
        Layout = GuestLocator.Locate(Process, progress);
        Scanner = Layout != null ? new ScanEngine(Layout) : null;
        Freezer.Attach(Layout);
    }

    public void Detach()
    {
        Freezer.Attach(null);
        Freezer.Clear();
        Process?.Dispose();
        Process = null; Layout = null; Scanner = null;
    }

    public void Dispose() { Detach(); Freezer.Dispose(); }
}
