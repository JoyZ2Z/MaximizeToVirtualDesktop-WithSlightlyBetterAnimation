using System.Diagnostics;

namespace MaximizeToVirtualDesktop;

/// <summary>
/// Owns the single low-cost current-desktop probe used by AutoPin, Snap, and
/// Auto-sort. It intentionally reads only the desktop ID: window enumeration
/// and all pin/unpin writes remain owned by their respective services.
/// </summary>
internal sealed class DesktopTransitionCoordinator : IDisposable
{
    private const int ProbeIntervalMs = 250;

    private readonly VirtualDesktopService _vds;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly DesktopTransitionDetector _detector = new();
    private bool _disposed;

    public DesktopTransitionCoordinator(VirtualDesktopService vds)
    {
        _vds = vds;
        _timer = new System.Windows.Forms.Timer { Interval = ProbeIntervalMs };
        _timer.Tick += (_, _) => Observe();
    }

    public event Action<Guid>? DesktopChanged;
    public event Action<Guid>? DesktopSettled;

    public void Start()
    {
        if (_disposed) return;
        Observe();
        _timer.Start();
    }

    private void Observe()
    {
        var signal = _detector.Observe(_vds.GetCurrentDesktopId());
        if (!signal.DesktopId.HasValue) return;
        if (signal.Changed)
        {
            Trace.WriteLine($"DesktopTransitionCoordinator: detected desktop {signal.DesktopId}.");
            DesktopChanged?.Invoke(signal.DesktopId.Value);
        }
        if (signal.Settled)
        {
            Trace.WriteLine($"DesktopTransitionCoordinator: desktop {signal.DesktopId} settled.");
            DesktopSettled?.Invoke(signal.DesktopId.Value);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Dispose();
    }
}
