using System.Diagnostics;
using System.Runtime.InteropServices;
using MaximizeToVirtualDesktop.Interop;

namespace MaximizeToVirtualDesktop;

/// <summary>
/// Low-level mouse hook that intercepts maximize button clicks and title-bar double-clicks.
/// Suppresses the Windows maximize and triggers virtual-desktop maximize instead.
/// </summary>
internal sealed class MaximizeButtonHook : IDisposable
{
    private readonly FullScreenManager _manager;
    private readonly Control _syncControl;
    private readonly AppSettings _settings;
    private readonly TitleBarDoubleClickTracker _doubleClickTracker = new();
    private IntPtr _hookHandle;
    private bool _disposed;

    // Must be stored as a field to prevent GC collection
    private readonly NativeMethods.LowLevelHookProc _hookProc;

    public MaximizeButtonHook(FullScreenManager manager, Control syncControl, AppSettings settings)
    {
        _manager = manager;
        _syncControl = syncControl;
        _settings = settings;
        _hookProc = HookCallback;
    }

    public void Install()
    {
        if (_hookHandle != IntPtr.Zero) return;

        _hookHandle = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_MOUSE_LL,
            _hookProc,
            NativeMethods.GetModuleHandle(null),
            0);

        if (_hookHandle == IntPtr.Zero)
        {
            Trace.WriteLine("MaximizeButtonHook: Failed to install mouse hook.");
        }
        else
        {
            Trace.WriteLine("MaximizeButtonHook: Installed.");
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= NativeMethods.HC_ACTION && wParam == (IntPtr)NativeMethods.WM_LBUTTONDOWN)
        {
            var hookStruct = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
            var hwnd = NativeMethods.WindowFromPoint(hookStruct.pt);
            var topLevel = hwnd == IntPtr.Zero ? IntPtr.Zero : GetTopLevelWindow(hwnd);

            if (topLevel != IntPtr.Zero
                && IsTriggerActive()
                && IsHitTest(topLevel, hookStruct.pt, NativeMethods.HTMAXBUTTON))
            {
                _doubleClickTracker.Reset();
                PostToggle(topLevel);
                return (IntPtr)1;
            }

            if (topLevel != IntPtr.Zero
                && IsHitTest(topLevel, hookStruct.pt, NativeMethods.HTCAPTION)
                && _doubleClickTracker.Observe(
                    topLevel,
                    hookStruct.pt.X,
                    hookStruct.pt.Y,
                    hookStruct.time,
                    NativeMethods.GetDoubleClickTime(),
                    NativeMethods.GetSystemMetrics(NativeMethods.SM_CXDOUBLECLK),
                    NativeMethods.GetSystemMetrics(NativeMethods.SM_CYDOUBLECLK)))
            {
                if (_manager.IsTracked(topLevel) || IsTriggerActive())
                {
                    PostToggle(topLevel);
                    return (IntPtr)1;
                }
            }
        }

        return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private bool IsTriggerActive()
    {
        bool shiftHeld = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_SHIFT) & 0x8000) != 0;
        return _settings.InvertShiftClick ? !shiftHeld : shiftHeld;
    }

    private void PostToggle(IntPtr topLevel)
    {
        try
        {
            if (!_syncControl.IsDisposed && _syncControl.IsHandleCreated)
            {
                _syncControl.BeginInvoke(() => _manager.Toggle(topLevel));
            }
        }
        catch (ObjectDisposedException)
        {
            // App is shutting down
        }
    }

    private static bool IsHitTest(IntPtr hwnd, NativeMethods.POINT pt, int expectedHit)
    {
        try
        {
            IntPtr lParam = (IntPtr)((pt.Y << 16) | (pt.X & 0xFFFF));
            IntPtr result = NativeMethods.SendMessageTimeout(
                hwnd, NativeMethods.WM_NCHITTEST, IntPtr.Zero, lParam,
                NativeMethods.SMTO_ABORTIFHUNG, 100, out IntPtr hitResult);
            return result != IntPtr.Zero && hitResult == (IntPtr)expectedHit;
        }
        catch
        {
            return false;
        }
    }

    private static IntPtr GetTopLevelWindow(IntPtr hwnd)
    {
        // Walk up the parent chain to find the top-level window
        IntPtr current = hwnd;
        IntPtr parent;
        while ((parent = GetParent(current)) != IntPtr.Zero)
        {
            current = parent;
        }
        return current;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hookHandle != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
            Trace.WriteLine("MaximizeButtonHook: Uninstalled.");
        }
    }
}
