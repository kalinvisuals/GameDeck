using GameDeck.Core.Localization;
using GameDeck.Core.Security;
using QRCoder;

namespace GameDeck.App.Tray;

/// <summary>Shows the LAN address, a QR code with a one-time pairing link, and the 6-digit code.</summary>
internal sealed class PairingForm : Form
{
    private readonly GameDeckService _service;
    private readonly PictureBox _qr = new() { Size = new Size(232, 232), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White };
    private readonly Label _code = new() { AutoSize = true, Font = new Font("Consolas", 30f, FontStyle.Bold), ForeColor = Theme.Text };
    private readonly Label _expiry = new() { AutoSize = true, ForeColor = Theme.Muted, Font = new Font("Segoe UI", 9f) };
    private readonly LinkLabel _url = new() { AutoSize = true, Font = new Font("Segoe UI Semibold", 11f), LinkColor = Theme.Accent, ActiveLinkColor = Theme.Text };
    private readonly Label _otherUrls = new() { AutoSize = true, ForeColor = Theme.Muted, Font = new Font("Segoe UI", 8.5f), MaximumSize = new Size(300, 0) };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private string _shownCode = "";
    private string _baseUrl = "";

    public PairingForm(GameDeckService service)
    {
        _service = service;
        Icon = Theme.CreateWindowIcon(service);
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 9.5f);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(24);

        BuildUi();

        _url.LinkClicked += (_, _) => { if (_baseUrl.Length > 0) Clipboard.SetText(_baseUrl); };
        _service.Pairing.CodeChanged += OnCodeChanged;
        _service.LanguageChanged += OnLanguageChanged;
        _timer.Tick += (_, _) => RefreshView();
        _timer.Start();
        RefreshView();
    }

    private void OnLanguageChanged()
    {
        if (IsHandleCreated && !IsDisposed) BeginInvoke(() => { BuildUi(); RefreshView(); });
    }

    /// <summary>(Re)creates all text-bearing controls in the current language.</summary>
    private void BuildUi()
    {
        SuspendLayout();
        foreach (Control c in Controls) c.Controls.Clear();
        Controls.Clear();
        Text = L.T("pair.window");

        var layout = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            WrapContents = false,
            BackColor = Theme.Background,
        };

        layout.Controls.Add(new Label
        {
            Text = L.T("pair.title"),
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 15f),
            Margin = new Padding(0, 0, 0, 4),
        });
        layout.Controls.Add(new Label
        {
            Text = L.T("pair.subtitle"),
            AutoSize = true,
            ForeColor = Theme.Muted,
            Margin = new Padding(0, 0, 0, 16),
        });
        layout.Controls.Add(_qr);
        _qr.Margin = new Padding(0, 0, 0, 16);
        layout.Controls.Add(_url);
        layout.Controls.Add(_otherUrls);
        layout.Controls.Add(new Label { Text = L.T("pair.code"), AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(0, 16, 0, 0) });
        layout.Controls.Add(_code);
        layout.Controls.Add(_expiry);

        var buttons = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 16, 0, 0), BackColor = Theme.Background };
        var refresh = Theme.MakeButton(L.T("pair.newCode"));
        refresh.Click += (_, _) => _service.Pairing.Regenerate();
        var copy = Theme.MakeButton(L.T("pair.copy"));
        copy.Click += (_, _) => { if (_baseUrl.Length > 0) Clipboard.SetText(_baseUrl); };
        var close = Theme.MakeButton(L.T("pair.done"), primary: true);
        close.Click += (_, _) => Close();
        buttons.Controls.AddRange([refresh, copy, close]);
        layout.Controls.Add(buttons);

        layout.Controls.Add(new Label
        {
            Text = L.T("pair.lanOnly"),
            AutoSize = true,
            ForeColor = Theme.Muted,
            Font = new Font("Segoe UI", 8.5f),
            Margin = new Padding(0, 16, 0, 0),
        });

        Controls.Add(layout);
        ResumeLayout();
    }

    private void OnCodeChanged()
    {
        if (IsHandleCreated && !IsDisposed) BeginInvoke(RefreshView);
    }

    private void RefreshView()
    {
        var (code, expires) = _service.Pairing.GetCurrentCode();
        var addresses = NetworkGuard.GetLanAddresses();
        var port = _service.Port;
        _baseUrl = addresses.Count > 0 ? $"http://{addresses[0]}:{port}" : $"http://localhost:{port}";

        if (code != _shownCode)
        {
            _shownCode = code;
            _code.Text = $"{code[..3]} {code[3..]}";
            SetQr($"{_baseUrl}/#pair={code}");
        }

        _url.Text = _baseUrl;
        _otherUrls.Text = addresses.Count > 1
            ? L.T("pair.otherAddresses", string.Join(", ", addresses.Skip(1).Select(a => $"{a}:{port}")))
            : addresses.Count == 0 ? L.T("pair.noNetwork") : "";
        var remaining = expires - DateTime.UtcNow;
        _expiry.Text = L.T("pair.expiry", Math.Max(0, (int)remaining.TotalMinutes), Math.Max(0, remaining.Seconds));
    }

    private void SetQr(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(8);
        using var ms = new MemoryStream(png);
        using var image = Image.FromStream(ms);
        var old = _qr.Image;
        _qr.Image = new Bitmap(image);
        old?.Dispose();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _service.Pairing.CodeChanged -= OnCodeChanged;
        _service.LanguageChanged -= OnLanguageChanged;
        _timer.Dispose();
        _qr.Image?.Dispose();
        base.OnFormClosed(e);
    }
}
