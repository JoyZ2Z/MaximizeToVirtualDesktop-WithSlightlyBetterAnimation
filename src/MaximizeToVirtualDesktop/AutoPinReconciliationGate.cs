namespace MaximizeToVirtualDesktop;

/// <summary>
/// Thread-safe write gate. After a transition it opens only when two consecutive
/// observations have the same desktop and fingerprint and that desktop is current.
/// </summary>
internal sealed class AutoPinReconciliationGate
{
    private readonly object _sync = new();
    private bool _closed;
    private Guid? _candidateDesktop;
    private string? _candidateFingerprint;

    public bool IsClosed
    {
        get { lock (_sync) return _closed; }
    }

    public void Open()
    {
        lock (_sync)
        {
            _closed = false;
            ClearCandidate();
        }
    }

    public void Close()
    {
        lock (_sync)
        {
            _closed = true;
            ClearCandidate();
        }
    }

    public void InvalidateObservation()
    {
        lock (_sync)
        {
            _closed = true;
            ClearCandidate();
        }
    }

    public bool TryOpenAfterStableObservation(
        Guid observedDesktop,
        string fingerprint,
        Guid? currentDesktop)
    {
        lock (_sync)
        {
            if (!_closed) return currentDesktop == observedDesktop;
            if (currentDesktop != observedDesktop)
            {
                ClearCandidate();
                return false;
            }

            if (_candidateDesktop == observedDesktop
                && _candidateFingerprint == fingerprint)
            {
                _closed = false;
                ClearCandidate();
                return true;
            }

            _candidateDesktop = observedDesktop;
            _candidateFingerprint = fingerprint;
            return false;
        }
    }

    private void ClearCandidate()
    {
        _candidateDesktop = null;
        _candidateFingerprint = null;
    }
}
