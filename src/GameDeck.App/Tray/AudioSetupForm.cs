using GameDeck.Core.Localization;
using System.Diagnostics;
using GameDeck.App.Web;
using GameDeck.Core.Audio;

namespace GameDeck.App.Tray;

/// <summary>Device selection, volumes, live routing checks and a route test, on the PC itself.</summary>
internal sealed class AudioSetupForm : Form
{
    private readonly GameDeckService _service;
    private readonly ComboBox _headphones = MakeCombo();
    private readonly ComboBox _virtual = MakeCombo();
    private readonly ComboBox _mic = MakeCombo();
    private readonly CheckBox _micEnabled = new() { AutoSize = true, ForeColor = Theme.Text };
    private readonly TrackBar _micVol = MakeTrack(200);
    private readonly TrackBar _soundVol = MakeTrack(150);
    private readonly TrackBar _monitorVol = MakeTrack(150);
    private readonly FlowLayoutPanel _issues = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Theme.Background };
    private readonly Label _testResult = new() { AutoSize = true, MaximumSize = new Size(520, 0), ForeColor = Theme.Muted };
    private readonly Button _testButton = Theme.MakeButton("", primary: true);
    private bool _loading;

    private sealed record Item(string Id, string Text)
    {
        public override string ToString() => Text;
    }

    public AudioSetupForm(GameDeckService service)
    {
        _service = service;
        Icon = Theme.CreateWindowIcon(service);
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 9.5f);
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScroll = true; // taller than small/high-DPI screens; scrolls instead of running off-screen
        Padding = new Padding(24);

        var col = BuildUi();

        // Fit the content, but never beyond the screen's working area.
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        var preferred = col.GetPreferredSize(Size.Empty);
        ClientSize = new Size(preferred.Width + Padding.Horizontal + SystemInformation.VerticalScrollBarWidth,
                              Math.Min(preferred.Height + Padding.Vertical, area.Height - 80));
        MinimumSize = new Size(Width, 320);

        _virtual.SelectionChangeCommitted += (_, _) => Patch(new AudioPatch(null, null, null, Selected(_virtual), null, null, null, null, null));
        _mic.SelectionChangeCommitted += (_, _) => Patch(new AudioPatch(null, null, null, null, null, Selected(_mic), null, null, null));
        _headphones.SelectionChangeCommitted += (_, _) => Patch(new AudioPatch(Selected(_headphones), null, null, null, null, null, null, null, null));
        _micEnabled.CheckedChanged += (_, _) => Patch(new AudioPatch(null, null, null, null, null, null, _micEnabled.Checked, null, null));
        _micVol.ValueChanged += (_, _) => Patch(new AudioPatch(null, null, null, null, null, null, null, _micVol.Value / 100f, null));
        _soundVol.ValueChanged += (_, _) => Patch(new AudioPatch(null, null, null, null, _soundVol.Value / 100f, null, null, null, null));
        _monitorVol.ValueChanged += (_, _) => Patch(new AudioPatch(null, null, _monitorVol.Value / 100f, null, null, null, null, null, null));
        _testButton.Click += async (_, _) => await RunTest();

        _service.StateChanged += OnStateChanged;
        _service.LanguageChanged += OnLanguageChanged;
        Reload();
    }

    private void OnLanguageChanged()
    {
        if (IsHandleCreated && !IsDisposed) BeginInvoke(() => { BuildUi(); _testResult.Text = ""; Reload(); });
    }

    /// <summary>(Re)creates the layout and every label in the current language. Inputs are reused.</summary>
    private FlowLayoutPanel BuildUi()
    {
        SuspendLayout();
        foreach (Control c in Controls) c.Controls.Clear();
        Controls.Clear();
        Text = L.T("setup.window");
        _micEnabled.Text = L.T("setup.mixMic");
        _testButton.Text = L.T("setup.testRouting");

        var col = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Theme.Background };
        col.Controls.Add(Heading(L.T("setup.title"), 15f));
        col.Controls.Add(Muted(L.T("setup.intro"), 520));

        col.Controls.Add(Section(L.T("setup.step1")));
        col.Controls.Add(Muted(L.T("setup.step1.text"), 520));
        var install = new LinkLabel { Text = L.T("setup.openVb"), AutoSize = true, LinkColor = Theme.Accent, ActiveLinkColor = Theme.Text };
        install.LinkClicked += (_, _) => Shell("https://vb-audio.com/Cable/");
        col.Controls.Add(install);
        col.Controls.Add(Row(L.T("setup.virtualOutput"), _virtual));
        col.Controls.Add(Row(L.T("setup.soundsIntoCall"), _soundVol));

        col.Controls.Add(Section(L.T("setup.step2")));
        col.Controls.Add(Row(L.T("setup.microphone"), _mic));
        col.Controls.Add(_micEnabled);
        col.Controls.Add(Row(L.T("setup.micGain"), _micVol));

        col.Controls.Add(Section(L.T("setup.step3")));
        col.Controls.Add(Row(L.T("setup.headphones"), _headphones));
        col.Controls.Add(Row(L.T("setup.soundsInHeadphones"), _monitorVol));
        col.Controls.Add(Muted(L.T("setup.ownVoice"), 520));

        col.Controls.Add(Section(L.T("setup.checks")));
        col.Controls.Add(_issues);
        var buttons = new FlowLayoutPanel { AutoSize = true, BackColor = Theme.Background, Margin = new Padding(0, 8, 0, 0) };
        var sound = Theme.MakeButton(L.T("setup.windowsSound"));
        sound.Click += (_, _) => Shell("mmsys.cpl");
        var close = Theme.MakeButton(L.T("setup.close"));
        close.Click += (_, _) => Close();
        buttons.Controls.AddRange([_testButton, sound, close]);
        col.Controls.Add(buttons);
        col.Controls.Add(Muted(L.T("setup.testInfo"), 520));
        col.Controls.Add(_testResult);

        Controls.Add(col);
        ResumeLayout();
        return col;
    }

    private void OnStateChanged()
    {
        if (IsHandleCreated && !IsDisposed) BeginInvoke(Reload);
    }

    private void Patch(AudioPatch patch)
    {
        if (_loading) return;
        try { _service.UpdateAudio(patch); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "GameDeck", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private void Reload()
    {
        _loading = true;
        try
        {
            var state = _service.BuildState();
            var a = state.Audio;
            var cables = state.Routing.Cables.Select(c => c.Render.Id).ToHashSet();

            Fill(_headphones, L.T("setup.windowsDefault"), state.Devices.Outputs.Where(d => !cables.Contains(d.Id)), a.MonitorDeviceId, _ => false);
            Fill(_virtual, L.T("setup.off"), state.Devices.Outputs.OrderByDescending(d => cables.Contains(d.Id)), a.VirtualDeviceId, d => cables.Contains(d.Id));
            Fill(_mic, L.T("setup.none"), state.Devices.Inputs.Where(d => !state.Routing.Cables.Any(c => c.Capture?.Id == d.Id)), a.MicDeviceId, _ => false);
            _micEnabled.Checked = a.MicEnabled;
            _micVol.Value = Math.Clamp((int)Math.Round(a.MicVolume * 100), 0, _micVol.Maximum);
            _soundVol.Value = Math.Clamp((int)Math.Round(a.VirtualVolume * 100), 0, _soundVol.Maximum);
            _monitorVol.Value = Math.Clamp((int)Math.Round(a.MonitorVolume * 100), 0, _monitorVol.Maximum);

            _issues.SuspendLayout();
            _issues.Controls.Clear();
            var ok = state.Routing.Issues.All(i => i.Level == "info") && state.Status.Virtual.Ok;
            if (ok) _issues.Controls.Add(IssueLabel("ok", L.T("setup.ok"), null));
            foreach (var i in state.Routing.Issues) _issues.Controls.Add(IssueLabel(i.Level, i.Message, i.Fix));
            _issues.ResumeLayout();
            SetTestEnabled(state.Routing.Selected?.Capture is not null && state.Status.Virtual.Ok);
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Flat buttons keep their colours when disabled, so dim it explicitly.</summary>
    private void SetTestEnabled(bool enabled)
    {
        _testButton.Enabled = enabled;
        _testButton.BackColor = enabled ? Theme.Accent : Theme.Surface;
        _testButton.FlatAppearance.BorderColor = enabled ? Theme.Accent : Theme.Border;
    }

    private async Task RunTest()
    {
        SetTestEnabled(false);
        _testResult.ForeColor = Theme.Muted;
        _testResult.Text = L.T("setup.testing");
        try
        {
            var r = await Task.Run(_service.RunRouteTestAsync);
            _testResult.ForeColor = r.Detected ? Color.FromArgb(143, 212, 173) : Color.FromArgb(240, 138, 138);
            _testResult.Text = (r.Detected ? "✓ " : "✗ ") + r.Message;
        }
        catch (Exception ex)
        {
            _testResult.Text = L.T("setup.testFailed", ex.Message);
        }
        finally
        {
            SetTestEnabled(true);
        }
    }

    private static void Fill(ComboBox box, string empty, IEnumerable<AudioDeviceInfo> devices, string? selectedId, Func<AudioDeviceInfo, bool> star)
    {
        var items = new List<Item> { new("", empty) };
        items.AddRange(devices.Select(d => new Item(d.Id, (star(d) ? "â˜… " : "") + d.Name + (d.IsDefault ? "  " + L.T("setup.windowsDefaultTag") : ""))));
        if (selectedId is not null && items.All(i => i.Id != selectedId)) items.Add(new Item(selectedId, L.T("setup.disconnected")));
        box.BeginUpdate();
        box.Items.Clear();
        box.Items.AddRange(items.ToArray());
        box.SelectedItem = items.First(i => i.Id == (selectedId ?? ""));
        box.EndUpdate();
    }

    private static string Selected(ComboBox box) => (box.SelectedItem as Item)?.Id ?? "";

    private static Control IssueLabel(string level, string message, string? fix)
    {
        var color = level switch
        {
            "error" => Color.FromArgb(240, 138, 138),
            "warning" => Color.FromArgb(228, 197, 130),
            "ok" => Color.FromArgb(143, 212, 173),
            _ => Theme.Muted,
        };
        var prefix = level switch { "error" => "✗ ", "warning" => "! ", "ok" => "✓ ", _ => "• " };
        return new Label
        {
            Text = prefix + message + (fix is null ? "" : "\n    → " + fix),
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            ForeColor = color,
            Margin = new Padding(0, 0, 0, 6),
        };
    }

    private static ComboBox MakeCombo() => new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 340,
        FlatStyle = FlatStyle.Flat,
        BackColor = Theme.Surface,
        ForeColor = Theme.Text,
    };

    private static TrackBar MakeTrack(int max) => new()
    {
        Minimum = 0,
        Maximum = max,
        TickFrequency = 25,
        Width = 340,
        BackColor = Theme.Background,
    };

    private static Control Row(string label, Control input)
    {
        var row = new FlowLayoutPanel { AutoSize = true, BackColor = Theme.Background, Margin = new Padding(0, 4, 0, 4) };
        row.Controls.Add(new Label { Text = label, Width = 170, ForeColor = Theme.Text, Margin = new Padding(0, 6, 8, 0) });
        row.Controls.Add(input);
        return row;
    }

    private static Label Heading(string text, float size) =>
        new() { Text = text, AutoSize = true, Font = new Font("Segoe UI Semibold", size), Margin = new Padding(0, 0, 0, 4) };

    private static Label Section(string text) =>
        new() { Text = text, AutoSize = true, Font = new Font("Segoe UI Semibold", 11f), ForeColor = Theme.Text, Margin = new Padding(0, 18, 0, 6) };

    private static Label Muted(string text, int width) =>
        new() { Text = text, AutoSize = true, MaximumSize = new Size(width, 0), ForeColor = Theme.Muted, Margin = new Padding(0, 0, 0, 6) };

    private static void Shell(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "GameDeck"); }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _service.StateChanged -= OnStateChanged;
        _service.LanguageChanged -= OnLanguageChanged;
        base.OnFormClosed(e);
    }
}
