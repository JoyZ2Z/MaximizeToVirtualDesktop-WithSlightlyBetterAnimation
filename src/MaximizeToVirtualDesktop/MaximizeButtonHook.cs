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
    private IntPtr _hookHandle;
    private bool _disposed;

    // Must be stored as a field to prevent GC collection
    private readonly NativeMethods.LowLevelHookProc _hookProc;

    // Double-click tracking
    private long _lastClickTicks;
    private int _lastClickX;
    private int _lastClickY;
    private IntPtr _lastClickHwnd;

    // System metrics (cached)
    private static readonly int DoubleClickTimeMs = (int)NativeMethods.GetDoubleClickTime();
    private static readonly int DoubleClickWidth = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXDOUBLECLK);
    private static readonly int DoubleClickHeight = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYDOUBLECLK);

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
            if (!IsTriggerActive()) return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);

            var hookStruct = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
            var hwnd = NativeMethods.WindowFromPoint(hookStruct.pt);

            if (hwnd != IntPtr.Zero)
            {
                if (IsClickOnMaximizeButton(hwnd, hookStruct.pt))
                {
                    var buttonTopLevel = GetTopLevelWindow(hwnd);
                    if (buttonTopLevel != IntPtr.Zero)
                    {
                        PostToggle(buttonTopLevel);
                        return (IntPtr)1;
                    }
                }

                // Title-bar double-click
                var nowTicks = DateTime.UtcNow.Ticks;
                var elapsedMs = (nowTicks - _lastClickTicks) / TimeSpan.TicksPerMillisecond;
                var topLevel = GetTopLevelWindow(hwnd);
                var lastTopLevel = _lastClickHwnd != IntPtr.Zero
                    ? GetTopLevelWindow(_lastClickHwnd) : IntPtr.Zero;

                if (elapsedMs > 0 && elapsedMs < DoubleClickTimeMs &&
                    Math.Abs(hookStruct.pt.X - _lastClickX) < DoubleClickWidth &&
                    Math.Abs(hookStruct.pt.Y - _lastClickY) < DoubleClickHeight &&
                    topLevel != IntPtr.Zero && topLevel == lastTopLevel)
                {
                    if (IsClickOnCaption(hwnd, hookStruct.pt))
                    {
                        _lastClickTicks = 0;
                        PostToggle(topLevel);
                        return (IntPtr)1;
                    }
                }
                else
                {
                    _lastClickTicks = nowTicks;
                    _lastClickX = hookStruct.pt.X;
                    _lastClickY = hookStruct.pt.Y;
                    _lastClickHwnd = hwnd;
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

    private static bool IsClickOnMaximizeButton(IntPtr hwnd, NativeMethods.POINT pt)
    {
        try
        {
            IntPtr lParam = (IntPtr)((pt.Y << 16) | (pt.X & 0xFFFF));
            IntPtr result = NativeMethods.SendMessageTimeout(
                hwnd, NativeMethods.WM_NCHITTEST, IntPtr.Zero, lParam,
                NativeMethods.SMTO_ABORTIFHUNG, 100, out IntPtr hitResult);
            return result != IntPtr.Zero && hitResult == (IntPtr)NativeMethods.HTMAXBUTTON;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsClickOnCaption(IntPtr hwnd, NativeMethods.POINT pt)
    {
        try
        {
            IntPtr lParam = (IntPtr)((pt.Y << 16) | (pt.X & 0xFFFF));
            IntPtr result = NativeMethods.SendMessageTimeout(
                hwnd, NativeMethods.WM_NCHITTEST, IntPtr.Zero, lParam,
                NativeMethods.SMTO_ABORTIFHUNG, 50, out IntPtr hitResult);
            if (result == IntPtr.Zero) return false;
            var hit = hitResult.ToInt32();
            return hit == NativeMethods.HTCAPTION || hit == NativeMethods.HTSYSMENU || hit == NativeMethods.HTMENU;
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
