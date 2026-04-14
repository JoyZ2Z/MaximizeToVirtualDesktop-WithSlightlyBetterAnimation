using MaximizeToVirtualDesktop.Interop;

namespace MaximizeToVirtualDesktop;

/// <summary>
/// Modal settings dialog for configuring hotkeys and maximize-button behavior.
/// Call <see cref="ApplyToSettings"/> after <c>ShowDialog</c> returns <c>DialogResult.OK</c>.
/// </summary>
internal sealed class SettingsDialog : Form
{
    private readonly AppSettings _settings;

    private readonly CheckBox _chkHotkeyCtrl, _chkHotkeyAlt, _chkHotkeyShift, _chkHotkeyWin;
    private readonly ComboBox _cmbHotkeyKey;

    private readonly CheckBox _chkPinCtrl, _chkPinAlt, _chkPinShift, _chkPinWin;
    private readonly ComboBox _cmbPinKey;

    private readonly CheckBox _chkInvertShiftClick;
    private Button _btnStartup = null!;

    // (display name, VK code) pairs for the key combo boxes
    private static readonly (string Name, uint Vk)[] SupportedKeys =
    [
        .. Enumerable.Range('A', 26).Select(c => (((char)c).ToString(), (uint)c)),
        .. Enumerable.Range('0', 10).Select(c => (((char)c).ToString(), (uint)c)),
        .. Enumerable.Range(1, 12).Select(i => ($"F{i}", (uint)(0x70 + i - 1))),
    ];

    public SettingsDialog(AppSettings settings)
    {
        _settings = settings;

        SuspendLayout();

        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;

        Text = "Settings — Maximize to Virtual Desktop";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

        int margin = 14;
        int grpW = 460;
        int y = margin + 4;

        // Maximize hotkey group
        var grpMaximize = new GroupBox { Text = "Maximize Hotkey", Location = new Point(margin, y), Size = new Size(grpW, 72) };
        Controls.Add(grpMaximize);
        (_chkHotkeyCtrl, _chkHotkeyAlt, _chkHotkeyShift, _chkHotkeyWin, _cmbHotkeyKey) =
            AddHotkeyRow(grpMaximize, settings.HotkeyModifiers, settings.HotkeyKey);
        y += grpMaximize.Height + 12;

        // Pin hotkey group
        var grpPin = new GroupBox { Text = "Pin Hotkey", Location = new Point(margin, y), Size = new Size(grpW, 72) };
        Controls.Add(grpPin);
        (_chkPinCtrl, _chkPinAlt, _chkPinShift, _chkPinWin, _cmbPinKey) =
            AddHotkeyRow(grpPin, settings.PinHotkeyModifiers, settings.PinHotkeyKey);
        y += grpPin.Height + 12;

        // Behavior group
        var grpBehavior = new GroupBox { Text = "Behavior", Location = new Point(margin, y), Size = new Size(grpW, 80) };
        Controls.Add(grpBehavior);
        _chkInvertShiftClick = new CheckBox
        {
            Text = "Always maximize to virtual desktop on click\r\n" +
                   "(Shift+Click performs a normal maximize instead)",
            AutoSize = true,
            Checked = settings.InvertShiftClick,
            Location = new Point(10, 28),
        };
        grpBehavior.Controls.Add(_chkInvertShiftClick);
        y += grpBehavior.Height + 12;

        // Manage Startup group
        var grpStartup = new GroupBox { Text = "Manage Startup", Location = new Point(margin, y), Size = new Size(grpW, 70) };
        Controls.Add(grpStartup);
        _btnStartup = new Button
        {
            Text = StartupManager.IsStartupEnabled() ? "Remove from Startup" : "Add to Startup",
            Size = new Size(150, 28),
            Location = new Point(10, 25),
            FlatStyle = FlatStyle.System,
        };
        _btnStartup.Click += (_, _) => ToggleStartup();
        grpStartup.Controls.Add(_btnStartup);
        y += grpStartup.Height + 16;

        // Buttons — uniform height, Reset Defaults right-aligned
        int btnW = 90;
        int btnResetW = 130;
        int btnH = 30;
        var btnOk = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Location = new Point(margin, y),
            Size = new Size(btnW, btnH),
            FlatStyle = FlatStyle.System,
        };
        var btnCancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(margin + btnW + 8, y),
            Size = new Size(btnW, btnH),
            FlatStyle = FlatStyle.System,
        };
        var btnReset = new Button
        {
            Text = "Reset Defaults",
            Location = new Point(margin + grpW - btnResetW, y),
            Size = new Size(btnResetW, btnH),
            FlatStyle = FlatStyle.System,
        };
        btnReset.Click += (_, _) => ResetDefaults();
        btnOk.Click += (_, e) =>
        {
            var error = ValidateHotkeys();
            if (error != null)
            {
                MessageBox.Show(error, "Invalid Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
            }
        };

        Controls.AddRange(new Control[] { btnOk, btnCancel, btnReset });
        AcceptButton = btnOk;
        CancelButton = btnCancel;

        ClientSize = new Size(margin + grpW + margin, y + btnH + margin);

        ResumeLayout(false);
        PerformLayout();
    }

    private static (CheckBox ctrl, CheckBox alt, CheckBox shift, CheckBox win, ComboBox key)
        AddHotkeyRow(GroupBox grp, uint modifiers, uint vk)
    {
        // FlowLayoutPanel inside the group box handles DPI scaling for the row
        var row = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Location = new Point(6, 26),
        };

        var chkCtrl  = new CheckBox { Text = "Ctrl",  Checked = (modifiers & NativeMethods.MOD_CONTROL) != 0, AutoSize = true, Margin = new Padding(2, 2, 4, 0) };
        var chkAlt   = new CheckBox { Text = "Alt",   Checked = (modifiers & NativeMethods.MOD_ALT)     != 0, AutoSize = true, Margin = new Padding(2, 2, 4, 0) };
        var chkShift = new CheckBox { Text = "Shift", Checked = (modifiers & NativeMethods.MOD_SHIFT)   != 0, AutoSize = true, Margin = new Padding(2, 2, 4, 0) };
        var chkWin   = new CheckBox { Text = "Win",   Checked = (modifiers & NativeMethods.MOD_WIN)     != 0, AutoSize = true, Margin = new Padding(2, 2, 10, 0) };
        var lblKey   = new Label    { Text = "Key:",  AutoSize = true, Margin = new Padding(2, 5, 2, 0) };
        var cmb = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 60,
            Margin = new Padding(2, 2, 0, 0),
        };

        foreach (var (name, _) in SupportedKeys)
            cmb.Items.Add(name);

        var idx = Array.FindIndex(SupportedKeys, k => k.Vk == vk);
        cmb.SelectedIndex = Math.Max(0, idx);

        row.Controls.AddRange(new Control[] { chkCtrl, chkAlt, chkShift, chkWin, lblKey, cmb });
        grp.Controls.Add(row);
        return (chkCtrl, chkAlt, chkShift, chkWin, cmb);
    }

    private void ResetDefaults()
    {
        _chkHotkeyCtrl.Checked  = true;
        _chkHotkeyAlt.Checked   = true;
        _chkHotkeyShift.Checked = true;
        _chkHotkeyWin.Checked   = false;
        _cmbHotkeyKey.SelectedIndex = Array.FindIndex(SupportedKeys, k => k.Vk == NativeMethods.VK_X);

        _chkPinCtrl.Checked  = true;
        _chkPinAlt.Checked   = true;
        _chkPinShift.Checked = true;
        _chkPinWin.Checked   = false;
        _cmbPinKey.SelectedIndex = Array.FindIndex(SupportedKeys, k => k.Vk == NativeMethods.VK_P);

        _chkInvertShiftClick.Checked = false;
    }

    private void ToggleStartup()
    {
        if (StartupManager.IsStartupEnabled())
        {
            if (StartupManager.DisableStartup())
            {
                _btnStartup.Text = "Add to Startup";
                MessageBox.Show("App removed from startup.", "Startup Disabled", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show("Failed to remove from startup.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        else
        {
            if (StartupManager.EnableStartup())
            {
                _btnStartup.Text = "Remove from Startup";
                MessageBox.Show("App added to startup.", "Startup Enabled", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show("Failed to add to startup.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    /// <summary>Returns an error message if the current hotkey configuration is invalid, or null if valid.</summary>
    private string? ValidateHotkeys()
    {
        uint hotkeyMod = BuildModifiers(_chkHotkeyCtrl, _chkHotkeyAlt, _chkHotkeyShift, _chkHotkeyWin);
        uint pinMod    = BuildModifiers(_chkPinCtrl, _chkPinAlt, _chkPinShift, _chkPinWin);

        if (hotkeyMod == 0)
            return "Maximize hotkey needs at least one modifier (Ctrl, Alt, Shift, or Win).";
        if (pinMod == 0)
            return "Pin hotkey needs at least one modifier (Ctrl, Alt, Shift, or Win).";
        if (_cmbHotkeyKey.SelectedIndex < 0)
            return "Please select a key for the maximize hotkey.";
        if (_cmbPinKey.SelectedIndex < 0)
            return "Please select a key for the pin hotkey.";

        uint hotkeyVk = SupportedKeys[_cmbHotkeyKey.SelectedIndex].Vk;
        uint pinVk    = SupportedKeys[_cmbPinKey.SelectedIndex].Vk;

        if (hotkeyMod == pinMod && hotkeyVk == pinVk)
            return "Maximize and Pin hotkeys cannot be the same combination.";

        return null;
    }

    /// <summary>Writes the dialog's current values back into the <see cref="AppSettings"/> object.</summary>
    public void ApplyToSettings()
    {
        _settings.HotkeyModifiers    = BuildModifiers(_chkHotkeyCtrl, _chkHotkeyAlt, _chkHotkeyShift, _chkHotkeyWin);
        _settings.HotkeyKey          = _cmbHotkeyKey.SelectedIndex >= 0 ? SupportedKeys[_cmbHotkeyKey.SelectedIndex].Vk : NativeMethods.VK_X;
        _settings.PinHotkeyModifiers = BuildModifiers(_chkPinCtrl, _chkPinAlt, _chkPinShift, _chkPinWin);
        _settings.PinHotkeyKey       = _cmbPinKey.SelectedIndex >= 0 ? SupportedKeys[_cmbPinKey.SelectedIndex].Vk : NativeMethods.VK_P;
        _settings.InvertShiftClick   = _chkInvertShiftClick.Checked;
    }

    private static uint BuildModifiers(CheckBox ctrl, CheckBox alt, CheckBox shift, CheckBox win)
    {
        uint m = 0;
        if (ctrl.Checked)  m |= NativeMethods.MOD_CONTROL;
        if (alt.Checked)   m |= NativeMethods.MOD_ALT;
        if (shift.Checked) m |= NativeMethods.MOD_SHIFT;
        if (win.Checked)   m |= NativeMethods.MOD_WIN;
        return m;
    }
}
