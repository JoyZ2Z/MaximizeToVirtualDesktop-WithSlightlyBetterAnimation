using System.Diagnostics;
using System.Runtime.InteropServices;
using MaximizeToVirtualDesktop.Interop;

namespace MaximizeToVirtualDesktop;

/// <summary>
/// Wraps the COM virtual desktop APIs. All methods are defensive — they catch COM
/// exceptions and return success/failure rather than throwing.
/// </summary>
internal sealed class VirtualDesktopService : IDisposable
{
    private DesktopManagerAdapter? _managerInternal;
    private IVirtualDesktopManager? _manager;
    private IApplicationViewCollection? _viewCollection;
    private IVirtualDesktopPinnedApps? _pinnedApps;
    // A host HWND can repeatedly fire foreground/state events while its
    // IApplicationView lives on a different thumbnail/root HWND. The cache is
    // always revalidated before use, so HWND reuse or a recreated UWP host
    // cannot target a stale view.
    private readonly Dictionary<IntPtr, IntPtr> _indirectViewCache = new();
    private readonly HashSet<IntPtr> _reportedViewIdentities = new();
    private int _buildNumber;
    private bool _disposed;

    public bool IsInitialized => _managerInternal != null && _manager != null;

    public bool Initialize(int windowsBuildNumber)
    {
        _buildNumber = windowsBuildNumber;
        IServiceProvider10? shell = null;
        try
        {
            shell = (IServiceProvider10)Activator.CreateInstance(
                Type.GetTypeFromCLSID(ComGuids.CLSID_ImmersiveShell)!)!;

            var mgrInternalGuid = ComGuids.IID_VirtualDesktopManagerInternal;
            var mgrInternalRaw = shell.QueryService(
                ref Unsafe.AsRef(ComGuids.CLSID_VirtualDesktopManagerInternal), ref mgrInternalGuid);

            _managerInternal = DesktopManagerAdapter.Create(mgrInternalRaw, windowsBuildNumber);

            _manager = (IVirtualDesktopManager)Activator.CreateInstance(
                Type.GetTypeFromCLSID(ComGuids.CLSID_VirtualDesktopManager)!)!;

            var viewCollGuid = typeof(IApplicationViewCollection).GUID;
            _viewCollection = (IApplicationViewCollection)shell.QueryService(
                ref viewCollGuid, ref viewCollGuid);

            // Pin support — query IVirtualDesktopPinnedApps
            try
            {
                var pinnedGuid = typeof(IVirtualDesktopPinnedApps).GUID;
                _pinnedApps = (IVirtualDesktopPinnedApps)shell.QueryService(
                    ref Unsafe.AsRef(ComGuids.CLSID_VirtualDesktopPinnedApps), ref pinnedGuid);
            }
            catch
            {
                Trace.WriteLine("VirtualDesktopService: IVirtualDesktopPinnedApps not available.");
            }

            Trace.WriteLine("VirtualDesktopService: COM initialized successfully.");
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"VirtualDesktopService: COM initialization failed: {ex.Message}");
            ReleaseComObjects();
            return false;
        }
        finally
        {
            if (shell != null) Marshal.ReleaseComObject(shell);
        }
    }

    /// <summary>
    /// Reinitialize COM objects (e.g. after Explorer restart).
    /// </summary>
    public bool Reinitialize()
    {
        ReleaseComObjects();
        return Initialize(_buildNumber);
    }

    public Guid? GetCurrentDesktopId()
    {
        IVirtualDesktop? desktop = null;
        try
        {
            desktop = _managerInternal?.GetCurrentDesktop();
            return desktop?.GetId();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"VirtualDesktopService: GetCurrentDesktopId failed: {ex.Message}");
            return null;
        }
        finally
        {
            if (desktop != null) Marshal.ReleaseComObject(desktop);
        }
    }

    public Guid? GetDesktopIdForWindow(IntPtr hwnd)
    {
        try
        {
            return _manager?.GetWindowDesktopId(hwnd);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"VirtualDesktopService: GetDesktopIdForWindow failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// True if the window is on the currently visible virtual desktop.
    /// UWP windows report unreliable showCmd/desktop-id values when they are on
    /// a non-current desktop, so this is used to gate state checks for them.
    /// </summary>
    public bool IsWindowOnCurrentDesktop(IntPtr hwnd)
    {
        try
        {
            return _manager?.IsWindowOnCurrentVirtualDesktop(hwnd) ?? false;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"VirtualDesktopService: IsWindowOnCurrentDesktop failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Creates a new virtual desktop. Returns its ID, or null on failure.
    /// </summary>
    public (IVirtualDesktop? desktop, Guid? id) CreateDesktop()
    {
        try
        {
            var desktop = _managerInternal!.CreateDesktop();
            var id = desktop.GetId();
            Trace.WriteLine($"VirtualDesktopService: Created desktop {id}");
            return (desktop, id);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"VirtualDesktopService: CreateDesktop failed: {ex.Message}");
            return (null, null);
        }
    }

    /// <summary>
    /// Moves a window to the specified desktop. Uses MoveViewToDesktop for cross-process windows.
    /// </summary>
    public bool MoveWindowToDesktop(IntPtr hwnd, IVirtualDesktop desktop)
    {
        IApplicationView? view = null;
        try
        {
            // For cross-process windows, we must use the view-based approach
            _viewCollection?.GetViewForHwnd(hwnd, out view);

            if (view != null)
            {
                _managerInternal!.MoveViewToDesktop(view, desktop);
            }
            else
            {
                // Fallback to the documented API (only works for own-process windows
                // or when the other approach fails)
                var desktopId = desktop.GetId();
                _manager!.MoveWindowToDesktop(hwnd, ref desktopId);
            }

            Trace.WriteLine($"VirtualDesktopService: Moved window {hwnd} to desktop {desktop.GetId()}");
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"VirtualDesktopService: MoveWindowToDesktop failed for hwnd={hwnd}: {ex.GetType().Name}: {ex.Message}");
            if (view != null) { Marshal.ReleaseComObject(view); view = null; }

            // Second attempt: try main window of the process
            IApplicationView? fallbackView = null;
            try
            {
                NativeMethods.GetWindowThreadProcessId(hwnd, out int processId);
                using var process = Process.GetProcessById(processId);
                if (process.MainWindowHandle != IntPtr.Zero && process.MainWindowHandle != hwnd)
                {
                    _viewCollection?.GetViewForHwnd(process.MainWindowHandle, out fallbackView);
                    if (fallbackView != null)
                    {
                        _managerInternal!.MoveViewToDesktop(fallbackView, desktop);
                        Trace.WriteLine($"VirtualDesktopService: Moved main window instead for process {processId}");
                        return true;
                    }
                }
            }
            catch (Exception ex2)
            {
                Trace.WriteLine($"VirtualDesktopService: Fallback move also failed: {ex2.Message}");
            }
            finally
            {
                if (fallbackView != null) Marshal.ReleaseComObject(fallbackView);
            }

            return false;
        }
        finally
        {
            if (view != null) Marshal.ReleaseComObject(view);
        }
    }

    public bool SwitchToDesktop(IVirtualDesktop desktop)
    {
        try
        {
            _managerInternal!.SwitchDesktop(desktop);

            Trace.WriteLine($"VirtualDesktopService: Switched to desktop {desktop.GetId()}");
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"VirtualDesktopService: SwitchToDesktop failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Removes a virtual desktop. Windows on it move to the fallback desktop.
    /// </summary>
    public bool RemoveDesktop(IVirtualDesktop desktop)
    {
        IVirtualDesktop? current = null;
        IVirtualDesktop? adjacent = null;
        try
        {
            // Find a fallback desktop (the current one, or adjacent)
            current = _managerInternal!.GetCurrentDesktop();
            IVirtualDesktop fallback;

            if (current.GetId() == desktop.GetId())
            {
                // We're removing the current desktop — find an adjacent one
                int hr = _managerInternal.GetAdjacentDesktop(desktop, 3, out fallback); // 3 = Left
                if (hr != 0)
                {
                    hr = _managerInternal.GetAdjacentDesktop(desktop, 4, out fallback); // 4 = Right
                    if (hr != 0)
                    {
                        Trace.WriteLine("VirtualDesktopService: No adjacent desktop for fallback, cannot remove.");
                        return false;
                    }
                }
                adjacent = fallback;
            }
            else
            {
                fallback = current;
            }

            _managerInternal.RemoveDesktop(desktop, fallback);
            Trace.WriteLine($"VirtualDesktopService: Removed desktop {desktop.GetId()}");
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"VirtualDesktopService: RemoveDesktop failed: {ex.Message}");
            return false;
        }
        finally
        {
            if (adjacent != null) Marshal.ReleaseComObject(adjacent);
            if (current != null) Marshal.ReleaseComObject(current);
        }
    }

    public bool SetDesktopName(IVirtualDesktop desktop, string name)
    {
        IntPtr hstring = IntPtr.Zero;
        try
        {
            int hr = NativeMethods.WindowsCreateString(name, name.Length, out hstring);
            if (hr != 0) return false;

            _managerInternal!.SetDesktopName(desktop, hstring);
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"VirtualDesktopService: SetDesktopName failed: {ex.Message}");
            return false;
        }
        finally
        {
            if (hstring != IntPtr.Zero) NativeMethods.WindowsDeleteString(hstring);
        }
    }

    public IVirtualDesktop? FindDesktop(Guid desktopId)
    {
        try
        {
            return _managerInternal?.FindDesktop(ref desktopId);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"VirtualDesktopService: FindDesktop failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Returns the IDs of all virtual desktops in physical (left-to-right) order.
    /// </summary>
    public List<Guid> GetAllDesktopIds()
    {
        var result = new List<Guid>();
        IObjectArray? desktops = null;
        try
        {
            _managerInternal!.GetDesktops(out desktops);
            desktops.GetCount(out int count);
            var iid = typeof(IVirtualDesktop).GUID;
            for (int i = 0; i < count; i++)
            {
                desktops.GetAt(i, ref iid, out object obj);
                result.Add(((IVirtualDesktop)obj).GetId());
                Marshal.ReleaseComObject(obj);
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"VirtualDesktopService: GetAllDesktopIds failed: {ex.Message}");
        }
        finally
        {
            if (desktops != null) Marshal.ReleaseComObject(desktops);
        }
        return result;
    }

    /// <summary>
    /// Moves a virtual desktop to the given physical index (0 = leftmost).
    /// </summary>
    public bool MoveDesktopToIndex(Guid desktopId, int index)
    {
        IVirtualDesktop? desktop = null;
        try
        {
            desktop = FindDesktop(desktopId);
            if (desktop == null) return false;
            _managerInternal!.MoveDesktop(desktop, index);
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"VirtualDesktopService: MoveDesktopToIndex failed for {desktopId} -> {index}: {ex.Message}");
            return false;
        }
        finally
        {
            if (desktop != null) Marshal.ReleaseComObject(desktop);
        }
    }

    /// <summary>
    /// Returns true if the window's view is pinned to all virtual desktops.
    /// </summary>
    public bool IsWindowPinned(IntPtr hwnd)
    {
        return TryGetWindowPinnedState(hwnd, out var isPinned) && isPinned;
    }

    /// <summary>Removes a desktop using an explicit fallback desktop.</summary>
    public bool RemoveDesktop(IVirtualDesktop desktop, Guid fallbackDesktopId)
    {
        IVirtualDesktop? fallback = null;
        try
        {
            fallback = FindDesktop(fallbackDesktopId);
            if (fallback == null || fallback.GetId() == desktop.GetId()) return false;
            _managerInternal!.RemoveDesktop(desktop, fallback);
            Trace.WriteLine(
                $"VirtualDesktopService: Removed desktop {desktop.GetId()} with fallback {fallbackDesktopId}");
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"VirtualDesktopService: RemoveDesktop explicit fallback failed: {ex.Message}");
            return false;
        }
        finally
        {
            if (fallback != null) Marshal.ReleaseComObject(fallback);
        }
    }

    /// <summary>
    /// Distinguishes a real unpinned state from a failed application-view query.
    /// Callers that intend to mutate state must not treat query failure as false.
    /// </summary>
    public bool TryGetWindowPinnedState(IntPtr hwnd, out bool isPinned)
    {
        isPinned = false;
        return TryResolveAutoPinView(hwnd, out _, out isPinned);
    }

    /// <summary>
    /// Retrieves the AUMID only for a resolved application view. AutoPin uses
    /// this to recognize the Host/Core pair exposed by one legacy UWP app; a
    /// failed lookup is intentionally non-mutating.
    /// </summary>
    public bool TryGetAppUserModelId(IntPtr hwnd, out string? appUserModelId)
    {
        appUserModelId = null;
        IApplicationView? view = null;
        try
        {
            if (_viewCollection == null) return false;
            _viewCollection.GetViewForHwnd(hwnd, out view);
            if (view == null || view.GetAppUserModelId(out var resolvedId) != 0
                || string.IsNullOrWhiteSpace(resolvedId))
            {
                return false;
            }
            appUserModelId = resolvedId;
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (view != null) Marshal.ReleaseComObject(view);
        }
    }

    /// <summary>
    /// Reads the desktop identity and pin state from the same application view.
    /// AutoPin uses this as an all-or-nothing capability check so a raw HWND with
    /// no usable view can never enter its mutation policy.
    /// </summary>
    public bool TryGetAutoPinWindowState(
        IntPtr hwnd,
        out Guid desktopId,
        out bool isPinned)
    {
        IApplicationView? view = null;
        desktopId = Guid.Empty;
        isPinned = false;
        try
        {
            if (_pinnedApps == null || _viewCollection == null) return false;
            _viewCollection.GetViewForHwnd(hwnd, out view);
            if (view == null) return false;

            var desktopResult = view.GetVirtualDesktopId(out desktopId);
            if (desktopResult != 0 || desktopId == Guid.Empty) return false;
            isPinned = _pinnedApps.IsViewPinned(view);
            return true;
        }
        catch
        {
            // A foreground host/child HWND without an IApplicationView is normal.
            // The resolver below tries same-family views before giving up.
            return false;
        }
        finally
        {
            if (view != null) Marshal.ReleaseComObject(view);
        }
    }

    /// <summary>
    /// Resolves a Win32 foreground HWND to the HWND owned by its application view.
    /// Foreground notifications can report a child or host window which has no
    /// direct IApplicationView, while the view's thumbnail/root window does.
    /// </summary>
    public bool TryResolveForegroundAutoPinWindowState(
        IntPtr foregroundHwnd,
        bool allowSameProcessFallback,
        out IntPtr viewHwnd,
        out Guid desktopId,
        out bool isPinned)
    {
        viewHwnd = IntPtr.Zero;
        desktopId = Guid.Empty;
        isPinned = false;

        if (TryGetAutoPinWindowState(foregroundHwnd, out desktopId, out isPinned))
        {
            viewHwnd = foregroundHwnd;
            return true;
        }

        if (TryGetCachedAutoPinView(foregroundHwnd, out var cachedView)
            && TryGetAutoPinWindowState(cachedView, out desktopId, out isPinned))
        {
            viewHwnd = cachedView;
            return true;
        }

        IObjectArray? views = null;
        var enumeratedAllViews = false;
        try
        {
            if (_pinnedApps == null || _viewCollection == null) return false;
            if (_viewCollection.GetViewsByZOrder(out views) != 0 || views == null) return false;

            views.GetCount(out var count);
            var viewGuid = typeof(IApplicationView).GUID;
            for (var index = 0; index < count; index++)
            {
                IApplicationView? candidate = null;
                try
                {
                    views.GetAt(index, ref viewGuid, out var unknown);
                    candidate = unknown as IApplicationView;
                    if (candidate == null) continue;
                    if (candidate.GetThumbnailWindow(out var candidateHwnd) != 0
                        || candidateHwnd == IntPtr.Zero
                        || !IsSameWindowFamily(foregroundHwnd, candidateHwnd))
                    {
                        continue;
                    }

                    if (candidate.GetVirtualDesktopId(out desktopId) != 0
                        || desktopId == Guid.Empty)
                    {
                        return false;
                    }

                    isPinned = _pinnedApps.IsViewPinned(candidate);
                    viewHwnd = candidateHwnd;
                    ReportIndirectViewResolution(foregroundHwnd, viewHwnd, "foreground-family");
                    return true;
                }
                finally
                {
                    if (candidate != null) Marshal.ReleaseComObject(candidate);
                }
            }
            enumeratedAllViews = true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"VirtualDesktopService: foreground view resolution failed for {foregroundHwnd}: {ex.Message}");
        }
        finally
        {
            if (views != null) Marshal.ReleaseComObject(views);
        }

        // GetViewsByZOrder already enumerates every application view.  If it
        // completed without finding this HWND family, GetViewInFocus can only
        // repeat the same negative lookup (and frequently throws while the
        // shell transitions desktops).  Reserve that fallback for a failed
        // collection enumeration, where it can still recover a valid view.
        if (enumeratedAllViews) return false;

        // Retry the focused view only as another way to retrieve the same Win32
        // window family. GetViewInFocus may already have advanced to the fullscreen
        // anchor, so an unrelated focused view must never stand in for this HWND.
        IApplicationView? focusedView = null;
        IntPtr focusedViewPointer = IntPtr.Zero;
        try
        {
            if (_viewCollection == null || _pinnedApps == null) return false;
            if (_viewCollection.GetViewInFocus(out focusedViewPointer) != 0
                || focusedViewPointer == IntPtr.Zero)
            {
                return false;
            }

            focusedView = (IApplicationView)Marshal.GetObjectForIUnknown(focusedViewPointer);
            NativeMethods.GetWindowThreadProcessId(foregroundHwnd, out var foregroundProcessId);
            if (focusedView.GetThumbnailWindow(out var focusedHwnd) != 0
                || focusedHwnd == IntPtr.Zero
                || !TryGetWindowProcessId(focusedHwnd, out var focusedProcessId)
                || !AutoPinFocusedViewFallbackPolicy.CanAccept(
                    IsSameWindowFamily(foregroundHwnd, focusedHwnd),
                    allowSameProcessFallback,
                    foregroundProcessId != 0 && foregroundProcessId == focusedProcessId)
                || focusedView.GetVirtualDesktopId(out desktopId) != 0
                || desktopId == Guid.Empty)
            {
                return false;
            }

            isPinned = _pinnedApps.IsViewPinned(focusedView);
            viewHwnd = focusedHwnd;
            ReportIndirectViewResolution(foregroundHwnd, viewHwnd, "foreground-focus");
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"VirtualDesktopService: focused-view resolution failed for {foregroundHwnd}: {ex.Message}");
        }
        finally
        {
            if (focusedView != null) Marshal.ReleaseComObject(focusedView);
            if (focusedViewPointer != IntPtr.Zero) Marshal.Release(focusedViewPointer);
        }

        return false;
    }

    private static bool TryGetWindowProcessId(IntPtr hwnd, out int processId)
    {
        NativeMethods.GetWindowThreadProcessId(hwnd, out processId);
        return processId != 0;
    }

    /// <summary>
    /// Resolves a top-level Win32 window to the view that owns its pin state.
    /// This is used by Z-order observation and deliberately does not read a
    /// virtual-desktop ID: AutoPin has no ownership-desktop policy.
    /// </summary>
    public bool TryResolveAutoPinView(IntPtr hwnd, out IntPtr viewHwnd, out bool isPinned)
    {
        viewHwnd = IntPtr.Zero;
        isPinned = false;
        if (TryGetDirectAutoPinViewState(hwnd, out isPinned))
        {
            viewHwnd = hwnd;
            return true;
        }

        if (TryGetCachedAutoPinView(hwnd, out var cachedView)
            && TryGetDirectAutoPinViewState(cachedView, out isPinned))
        {
            viewHwnd = cachedView;
            return true;
        }

        IObjectArray? views = null;
        try
        {
            if (_viewCollection == null || _pinnedApps == null) return false;
            if (_viewCollection.GetViewsByZOrder(out views) != 0 || views == null) return false;
            views.GetCount(out var count);
            var viewGuid = typeof(IApplicationView).GUID;
            for (var index = 0; index < count; index++)
            {
                IApplicationView? candidate = null;
                try
                {
                    views.GetAt(index, ref viewGuid, out var unknown);
                    candidate = unknown as IApplicationView;
                    if (candidate == null || candidate.GetThumbnailWindow(out var candidateHwnd) != 0
                        || candidateHwnd == IntPtr.Zero || !IsSameWindowFamily(hwnd, candidateHwnd))
                    {
                        continue;
                    }

                    isPinned = _pinnedApps.IsViewPinned(candidate);
                    viewHwnd = candidateHwnd;
                    ReportIndirectViewResolution(hwnd, viewHwnd, "window-family");
                    return true;
                }
                finally
                {
                    if (candidate != null) Marshal.ReleaseComObject(candidate);
                }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"VirtualDesktopService: view resolution failed for {hwnd}: {ex.Message}");
        }
        finally
        {
            if (views != null) Marshal.ReleaseComObject(views);
        }

        return false;
    }

    /// <summary>
    /// Resolves a group of top-level HWNDs using at most one application-view
    /// enumeration for the whole group.  AutoPin uses this for its full-window
    /// observation; calling the single-window fallback for every host window
    /// made that path scale with windows times application views.
    /// </summary>
    public IReadOnlyDictionary<IntPtr, AutoPinViewState> ResolveAutoPinViews(
        IEnumerable<IntPtr> hwnds)
    {
        var results = new Dictionary<IntPtr, AutoPinViewState>();
        var unresolved = new HashSet<IntPtr>();
        foreach (var hwnd in hwnds)
        {
            if (results.ContainsKey(hwnd) || !unresolved.Add(hwnd)) continue;
            if (TryGetDirectAutoPinViewState(hwnd, out var isPinned))
            {
                results[hwnd] = new AutoPinViewState(hwnd, isPinned);
                unresolved.Remove(hwnd);
            }
            else if (TryGetCachedAutoPinView(hwnd, out var cachedView)
                && TryGetDirectAutoPinViewState(cachedView, out isPinned))
            {
                results[hwnd] = new AutoPinViewState(cachedView, isPinned);
                unresolved.Remove(hwnd);
            }
        }
        if (unresolved.Count == 0) return results;

        IObjectArray? views = null;
        try
        {
            if (_viewCollection == null || _pinnedApps == null) return results;
            if (_viewCollection.GetViewsByZOrder(out views) != 0 || views == null) return results;
            views.GetCount(out var count);
            var viewGuid = typeof(IApplicationView).GUID;
            for (var index = 0; index < count && unresolved.Count > 0; index++)
            {
                IApplicationView? candidate = null;
                try
                {
                    views.GetAt(index, ref viewGuid, out var unknown);
                    candidate = unknown as IApplicationView;
                    if (candidate == null
                        || candidate.GetThumbnailWindow(out var candidateHwnd) != 0
                        || candidateHwnd == IntPtr.Zero)
                    {
                        continue;
                    }

                    var matchingSources = unresolved
                        .Where(source => IsSameWindowFamily(source, candidateHwnd))
                        .ToArray();
                    if (matchingSources.Length == 0) continue;

                    var isPinned = _pinnedApps.IsViewPinned(candidate);
                    foreach (var source in matchingSources)
                    {
                        results[source] = new AutoPinViewState(candidateHwnd, isPinned);
                        ReportIndirectViewResolution(source, candidateHwnd, "batch-family");
                        unresolved.Remove(source);
                    }
                }
                finally
                {
                    if (candidate != null) Marshal.ReleaseComObject(candidate);
                }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"VirtualDesktopService: batch view resolution failed: {ex.Message}");
        }
        finally
        {
            if (views != null) Marshal.ReleaseComObject(views);
        }

        return results;
    }

    private bool TryGetDirectAutoPinViewState(IntPtr hwnd, out bool isPinned)
    {
        isPinned = false;
        IApplicationView? directView = null;
        try
        {
            if (_viewCollection == null || _pinnedApps == null) return false;
            _viewCollection.GetViewForHwnd(hwnd, out directView);
            if (directView != null)
            {
                ReportViewIdentity(hwnd, directView, hwnd, "direct");
                isPinned = _pinnedApps.IsViewPinned(directView);
                return true;
            }
        }
        catch
        {
            // Host/child HWNDs are expected to miss the direct lookup.
        }
        finally
        {
            if (directView != null) Marshal.ReleaseComObject(directView);
        }
        return false;
    }

    private bool TryGetCachedAutoPinView(IntPtr sourceHwnd, out IntPtr viewHwnd)
    {
        viewHwnd = IntPtr.Zero;
        lock (_indirectViewCache)
        {
            if (!_indirectViewCache.TryGetValue(sourceHwnd, out viewHwnd))
                return false;
        }
        if (NativeMethods.IsWindow(viewHwnd) && IsSameWindowFamily(sourceHwnd, viewHwnd))
            return true;

        lock (_indirectViewCache)
        {
            _indirectViewCache.Remove(sourceHwnd);
        }
        viewHwnd = IntPtr.Zero;
        return false;
    }

    private static bool IsSameWindowFamily(IntPtr foregroundHwnd, IntPtr viewHwnd)
    {
        if (foregroundHwnd == viewHwnd) return true;
        return NativeMethods.GetAncestor(foregroundHwnd, NativeMethods.GA_ROOT) == viewHwnd
            || NativeMethods.GetAncestor(foregroundHwnd, NativeMethods.GA_ROOTOWNER) == viewHwnd
            || NativeMethods.GetAncestor(viewHwnd, NativeMethods.GA_ROOT) == foregroundHwnd
            || NativeMethods.GetAncestor(viewHwnd, NativeMethods.GA_ROOTOWNER) == foregroundHwnd;
    }

    /// <summary>
    /// Pin a window's view to all virtual desktops.
    /// </summary>
    public bool PinWindow(IntPtr hwnd)
    {
        return SetWindowPinnedState(hwnd, pinned: true);
    }

    /// <summary>
    /// Unpin a window's view from all virtual desktops.
    /// </summary>
    public bool UnpinWindow(IntPtr hwnd)
    {
        return SetWindowPinnedState(hwnd, pinned: false);
    }

    /// <summary>
    /// Mutates the same application view used by AutoPin observation. Packaged
    /// applications often expose a host HWND without a direct view; resolving
    /// its thumbnail/root view first keeps read and write paths consistent.
    /// </summary>
    private bool SetWindowPinnedState(IntPtr hwnd, bool pinned)
    {
        IApplicationView? view = null;
        try
        {
            if (_pinnedApps == null || !TryGetApplicationViewForMutation(hwnd, out view, out var viewHwnd))
            {
                Trace.WriteLine($"VirtualDesktopService[UWP-View]: unresolved source={hwnd}; pin mutation skipped.");
                return false;
            }
            var resolvedView = view;
            if (resolvedView == null) return false;
            if (pinned) _pinnedApps.PinView(resolvedView);
            else _pinnedApps.UnpinView(resolvedView);
            Trace.WriteLine($"VirtualDesktopService: {(pinned ? "Pinned" : "Unpinned")} window {hwnd} via view {viewHwnd}");
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine(
                $"VirtualDesktopService: {(pinned ? "PinWindow" : "UnpinWindow")} failed for {hwnd}: {ex.Message}");
            return false;
        }
        finally
        {
            if (view != null) Marshal.ReleaseComObject(view);
        }
    }

    /// <summary>
    /// Returns the actual COM view that owns a source HWND. Unlike a
    /// source-&gt;thumbnail HWND round-trip, this preserves the family-resolved
    /// view object for the following PinView/UnpinView call.
    /// </summary>
    private bool TryGetApplicationViewForMutation(
        IntPtr sourceHwnd, out IApplicationView? view, out IntPtr viewHwnd)
    {
        view = null;
        viewHwnd = IntPtr.Zero;
        try
        {
            if (_viewCollection == null) return false;
            _viewCollection.GetViewForHwnd(sourceHwnd, out view);
            if (view != null)
            {
                viewHwnd = sourceHwnd;
                return true;
            }

            if (TryGetCachedAutoPinView(sourceHwnd, out var cachedView))
            {
                _viewCollection.GetViewForHwnd(cachedView, out view);
                if (view != null)
                {
                    viewHwnd = cachedView;
                    return true;
                }
            }

            IObjectArray? views = null;
            try
            {
                if (_viewCollection.GetViewsByZOrder(out views) != 0 || views == null)
                    return false;
                views.GetCount(out var count);
                var viewGuid = typeof(IApplicationView).GUID;
                for (var index = 0; index < count; index++)
                {
                    IApplicationView? candidate = null;
                    try
                    {
                        views.GetAt(index, ref viewGuid, out var unknown);
                        candidate = unknown as IApplicationView;
                        if (candidate == null
                            || candidate.GetThumbnailWindow(out var candidateHwnd) != 0
                            || candidateHwnd == IntPtr.Zero
                            || !IsSameWindowFamily(sourceHwnd, candidateHwnd))
                        {
                            continue;
                        }
                        view = candidate;
                        viewHwnd = candidateHwnd;
                        candidate = null;
                        ReportIndirectViewResolution(sourceHwnd, viewHwnd, "mutation-family");
                        return true;
                    }
                    finally
                    {
                        if (candidate != null) Marshal.ReleaseComObject(candidate);
                    }
                }
            }
            finally
            {
                if (views != null) Marshal.ReleaseComObject(views);
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine(
                $"VirtualDesktopService[UWP-View]: mutation view resolution failed for {sourceHwnd}: {ex.Message}");
        }

        if (view != null)
        {
            Marshal.ReleaseComObject(view);
            view = null;
        }
        viewHwnd = IntPtr.Zero;
        return false;
    }

    private void ReportIndirectViewResolution(IntPtr sourceHwnd, IntPtr viewHwnd, string route)
    {
        if (sourceHwnd == IntPtr.Zero || viewHwnd == IntPtr.Zero || sourceHwnd == viewHwnd) return;
        lock (_indirectViewCache)
        {
            if (_indirectViewCache.TryGetValue(sourceHwnd, out var previous)
                && previous == viewHwnd)
            {
                return;
            }
            _indirectViewCache[sourceHwnd] = viewHwnd;
        }
        Trace.WriteLine(
            $"VirtualDesktopService[UWP-View]: route={route}; "
            + $"{WindowStateHelper.DescribeWindowForDiagnostics(sourceHwnd)}; "
            + $"viewHwnd={viewHwnd}.");
    }

    private void ReportViewIdentity(
        IntPtr sourceHwnd, IApplicationView view, IntPtr viewHwnd, string route)
    {
        lock (_reportedViewIdentities)
        {
            if (!_reportedViewIdentities.Add(viewHwnd)) return;
        }
        var appUserModelId = "<unavailable>";
        try
        {
            if (view.GetAppUserModelId(out var resolvedId) == 0
                && !string.IsNullOrWhiteSpace(resolvedId))
            {
                appUserModelId = resolvedId;
            }
        }
        catch
        {
            // View identity diagnostics must not affect application behavior.
        }
        Trace.WriteLine(
            $"VirtualDesktopService[UWP-View]: route={route}; "
            + $"{WindowStateHelper.DescribeWindowForDiagnostics(sourceHwnd)}; "
            + $"viewHwnd={viewHwnd}; aumid={appUserModelId}.");
    }

    private void ReleaseComObjects()
    {
        if (_managerInternal != null) { _managerInternal.Dispose(); _managerInternal = null; }
        if (_manager != null) { Marshal.ReleaseComObject(_manager); _manager = null; }
        if (_viewCollection != null) { Marshal.ReleaseComObject(_viewCollection); _viewCollection = null; }
        if (_pinnedApps != null) { Marshal.ReleaseComObject(_pinnedApps); _pinnedApps = null; }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            ReleaseComObjects();
            _disposed = true;
        }
    }
}

internal readonly record struct AutoPinViewState(IntPtr ViewHwnd, bool IsPinned);

// Helper to pass readonly Guid fields by ref to COM
internal static class Unsafe
{
    internal static ref Guid AsRef(in Guid guid)
    {
        // We need to pass a readonly static Guid by ref to COM QueryService.
        // This is safe because COM only reads the value.
        unsafe
        {
            fixed (Guid* p = &guid)
            {
                return ref *p;
            }
        }
    }
}
