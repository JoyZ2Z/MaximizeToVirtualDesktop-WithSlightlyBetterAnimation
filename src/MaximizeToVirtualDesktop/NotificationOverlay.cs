using System.Drawing.Drawing2D;
using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace MaximizeToVirtualDesktop;

/// <summary>
/// Translucent overlay notification that replaces balloon tips.
/// Shows centered on the active monitor,
/// fades out after 1.5 seconds. Click-through and non-activating.
/// </summary>
internal sealed class NotificationOverlay : Form
{
    private const int ShowDurationMs = 1500;
    private const int FadeStepMs = 20;
    private const double FadeStepAmount = 0.06;
    private const double InitialOpacity = 0.92;
    private const int PaddingH = 32;
    private const int MinWidth = 300;
    private const int MaxWidth = 520;

    private readonly System.Windows.Forms.Timer _hideTimer;
    private readonly System.Windows.Forms.Timer _fadeTimer;
    private readonly bool _darkTheme;
    private string _title = "";
    private string _subtitle = "";

    private static NotificationOverlay? _instance;

    private NotificationOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        _darkTheme = IsDarkModeEnabled();
        BackColor = _darkTheme ? Color.FromArgb(30, 30, 46) : Color.FromArgb(245, 245, 250);
        ForeColor = _darkTheme ? Color.White : Color.FromArgb(24, 24, 28);
        DoubleBuffered = true;
        Size = new Size(MinWidth, 80);

        _fadeTimer = new System.Windows.Forms.Timer { Interval = FadeStepMs };
        _hideTimer = new System.Windows.Forms.Timer { Interval = ShowDurationMs };

        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            _fadeTimer.Start();
        };

        _fadeTimer.Tick += (_, _) =>
        {
            if (Opacity <= FadeStepAmount)
            {
                _fadeTimer.Stop();
                Hide();
                Opacity = InitialOpacity;
            }
            else
            {
                Opacity -= FadeStepAmount;
            }
        };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00000080;  // WS_EX_TOOLWINDOW — hide from Alt+Tab
            cp.ExStyle |= 0x08000000;  // WS_EX_NOACTIVATE — don't steal focus
            cp.ExStyle |= 0x00000020;  // WS_EX_TRANSPARENT — click-through
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        // Win11 rounded corners
        int cornerPref = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(int));

        // Match border rendering to system theme
        int darkMode = _darkTheme ? 1 : 0;
        DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Fill with a solid theme-aware color for consistent rendering across displays.
        using var brush = new SolidBrush(BackColor);
        e.Graphics.FillRectangle(brush, ClientRectangle);
    }

    // Prefer Segoe UI Variable Display (Win11 native), fall back to Segoe UI
    private static readonly string FontFamily = IsFontInstalled("Segoe UI Variable Display")
        ? "Segoe UI Variable Display"
        : "Segoe UI";

    private static bool IsFontInstalled(string name)
    {
        using var f = new Font(name, 10f);
        return f.Name.Equals(name, StringComparison.OrdinalIgnoreCase);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        using var titleFont = new Font(FontFamily, 15f, FontStyle.Bold);
        using var subtitleFont = new Font(FontFamily, 10.5f);

        var hasSubtitle = !string.IsNullOrEmpty(_subtitle);
        var maxTextWidth = Width - PaddingH * 2;
        var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                  | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;

        var titleSize = TextRenderer.MeasureText(g, _title, titleFont);
        var subtitleSize = hasSubtitle
            ? TextRenderer.MeasureText(g, _subtitle, subtitleFont)
            : Size.Empty;

        int totalHeight = titleSize.Height + (hasSubtitle ? subtitleSize.Height + 2 : 0);
        int y = (Height - totalHeight) / 2;

        TextRenderer.DrawText(g, _title, titleFont,
            new Rectangle(PaddingH, y, maxTextWidth, titleSize.Height),
            ForeColor, flags);

        if (hasSubtitle)
        {
            var subtitleColor = _darkTheme
                ? Color.FromArgb(180, 180, 180)
                : Color.FromArgb(88, 88, 96);
            TextRenderer.DrawText(g, _subtitle, subtitleFont,
                new Rectangle(PaddingH, y + titleSize.Height + 2, maxTextWidth, subtitleSize.Height),
                subtitleColor, flags);
        }
    }

    /// <summary>
    /// Show a notification overlay centered on the monitor containing the given window (or cursor).
    /// </summary>
    public static void ShowNotification(string title, string subtitle = "", IntPtr hwnd = default)
    {
        if (_instance == null || _instance.IsDisposed)
            _instance = new NotificationOverlay();

        _instance._title = title;
        _instance._subtitle = subtitle;
        _instance.FitToContent(title, subtitle);
        _instance.PositionOnScreen(hwnd);
        _instance.Opacity = InitialOpacity;
        _instance._fadeTimer.Stop();
        _instance._hideTimer.Stop();
        _instance.Invalidate();
        _instance.Visible = true;
        _instance._hideTimer.Start();
    }

    private void FitToContent(string title, string subtitle)
    {
        using var g = CreateGraphics();
        using var titleFont = new Font(FontFamily, 15f, FontStyle.Bold);
        using var subtitleFont = new Font(FontFamily, 10.5f);

        var titleWidth = TextRenderer.MeasureText(g, title, titleFont).Width;
        var subtitleWidth = string.IsNullOrEmpty(subtitle)
            ? 0
            : TextRenderer.MeasureText(g, subtitle, subtitleFont).Width;

        int needed = Math.Max(titleWidth, subtitleWidth) + PaddingH * 2;
        Width = Math.Clamp(needed, MinWidth, MaxWidth);
    }

    private void PositionOnScreen(IntPtr hwnd)
    {
        var screen = hwnd != IntPtr.Zero
            ? Screen.FromHandle(hwnd)
            : Screen.FromPoint(Cursor.Position);

        // Upper third of screen — visible but not blocking center content
        Location = new Point(
            screen.WorkingArea.Left + (screen.WorkingArea.Width - Width) / 2,
            screen.WorkingArea.Top + screen.WorkingArea.Height / 4 - Height / 2);
    }

    private static bool IsDarkModeEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var appsUseLightTheme = key?.GetValue("AppsUseLightTheme");
            if (appsUseLightTheme is int value)
            {
                return value == 0;
            }
        }
        catch
        {
            // Ignore and use dark theme by default.
        }

        return true;
    }

    // --- DWM interop (overlay-specific, not shared with NativeMethods) ---

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
