using GameDeck.Core.Localization;
using System.Diagnostics;

namespace GameDeck.App.Tray;

/// <summary>The app has no main window; it lives in the notification area.</summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly GameDeckService _service;
    private readonly NotifyIcon _tray;
    private readonly SynchronizationContext _ui;
    private PairingForm? _pairingForm;
    private AudioSetupForm? _audioSetup;
    private readonly SceneTriggers _sceneTriggers;

    public TrayContext(GameDeckService service, bool openOnStart = false)
    {
        _service = service;

        _tray = new NotifyIcon
        {
            Icon = Theme.CreateTrayIcon(service),
            Text = L.T("tray.tooltip.idle", service.Port),
            ContextMenuStrip = BuildMenu(),
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => ShowPairing();

        // Same playing status the web controllers see, on the PC itself.
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _service.PlayingChanged += () => _ui.Post(_ => UpdateTooltip(), null);

        // Scene shortcuts and automatic switching follow every scene change, from any device.
        _sceneTriggers = new SceneTriggers(_service);
        // Smart Deck "Browse…": Windows file and folder dialogs on this PC's screen.
        _service.PathPicker = (folder, current) => PathPicker.ShowAsync(_ui, folder, current);
        _service.StateChanged += () => _ui.Post(_ => _sceneTriggers.Sync(), null);

        // Language switched from any device: rebuild the menu text immediately.
        _service.LanguageChanged += () => _ui.Post(_ =>
        {
            var old = _tray.ContextMenuStrip;
            _tray.ContextMenuStrip = BuildMenu();
            old?.Dispose();
            UpdateTooltip();
            _sceneTriggers.Reset();
        }, null);

        // A new logo from Settings, or Windows switching between light and dark.
        _service.LogoChanged += OnLogoChanged;
        _service.Updates.Changed += () => _ui.Post(_ => NotifyUpdate(), null);
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnWindowsPreferenceChanged;

        var virtualConfigured = _service.Store.Read(c => c.Audio.VirtualDeviceId is not null);
        _tray.ShowBalloonTip(4000, L.T("tray.balloon.title"),
            virtualConfigured ? L.T("tray.balloon.connect") : L.T("tray.balloon.setup"),
            ToolTipIcon.Info);

        if (openOnStart)
        {
            OpenLocalDashboard();
            ShowAudioSetup();
        }
    }

    private void OnLogoChanged() => _ui.Post(_ => RefreshIcon(), null);

    private void OnWindowsPreferenceChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category == Microsoft.Win32.UserPreferenceCategory.General) _ui.Post(_ => RefreshIcon(), null);
    }

    private void RefreshIcon()
    {
        if (!_tray.Visible) return;
        var old = _tray.Icon;
        _tray.Icon = Theme.CreateTrayIcon(_service);
        old?.Dispose();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(L.T("tray.connect"), null, (_, _) => ShowPairing());
        menu.Items.Add(L.T("tray.openDashboard"), null, (_, _) => OpenLocalDashboard());
        menu.Items.Add(L.T("tray.audioSetup"), null, (_, _) => ShowAudioSetup());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(L.T("tray.stopAll"), null, (_, _) => _service.StopAll());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(L.T("tray.forgetDevices"), null, (_, _) => RevokeAll());
        menu.Items.Add(L.T("tray.dataFolder"), null, (_, _) => Shell(_service.Store.DataDirectory));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(L.T("tray.exit"), null, (_, _) => ExitThread());
        return menu;
    }

    private void ShowAudioSetup()
    {
        if (_audioSetup is { IsDisposed: false })
        {
            _audioSetup.Activate();
            return;
        }
        _audioSetup = new AudioSetupForm(_service);
        _audioSetup.Show();
    }

    private void UpdateTooltip()
    {
        if (!_tray.Visible) return;
        var playing = _service.Engine.GetPlaying();
        var text = playing.Count switch
        {
            0 => L.T("tray.tooltip.idle", _service.Port),
            1 => L.T("tray.tooltip.one", LabelOf(playing[0].SoundId)),
            _ => L.T("tray.tooltip.many", playing.Count),
        };
        _tray.Text = text.Length > 63 ? text[..63] : text; // NotifyIcon limit
    }

    private string LabelOf(string soundId) =>
        _service.Store.Read(c => c.Sounds.FirstOrDefault(s => s.Id == soundId)?.Label ?? L.T("tray.aSound"));

    private void ShowPairing()
    {
        if (_pairingForm is { IsDisposed: false })
        {
            _pairingForm.Activate();
            return;
        }
        _pairingForm = new PairingForm(_service);
        _pairingForm.Show();
    }

    private string? _notifiedVersion;

    /// <summary>One balloon per new version; installing is done from Settings.</summary>
    private void NotifyUpdate()
    {
        var available = _service.Updates.Snapshot().Available;
        if (available is null || available.Version == _notifiedVersion) return;
        _notifiedVersion = available.Version;
        _tray.ShowBalloonTip(6000, L.T("tray.update.title", available.Version), L.T("tray.update.body"), ToolTipIcon.Info);
    }

    private void OpenLocalDashboard()
    {
        // A fresh single-use code in the URL fragment pairs this PC's browser without typing.
        var code = _service.Pairing.Regenerate();
        Shell($"http://localhost:{_service.Port}/#pair={code}");
    }

    private void RevokeAll()
    {
        var answer = MessageBox.Show(
            L.T("tray.forget.body"),
            L.T("tray.forget.title"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (answer == DialogResult.Yes) _service.RevokeAll();
    }

    private static void Shell(string target) =>
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });

    protected override void ExitThreadCore()
    {
        _pairingForm?.Close();
        _audioSetup?.Close();
        _sceneTriggers.Dispose();
        _service.LogoChanged -= OnLogoChanged;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnWindowsPreferenceChanged;
        _tray.Visible = false;
        _tray.Icon?.Dispose();
        _tray.Dispose();
        base.ExitThreadCore();
    }
}
