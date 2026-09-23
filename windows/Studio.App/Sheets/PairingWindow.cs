using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QRCoder;

namespace Lernheft.Studio.App.Sheets;

/// <summary>
/// iPad koppeln: QR-Code mit der Kamera von Lernheft Pad scannen – oder den sechsstelligen Code
/// abtippen. Danach verbindet sich das iPad von allein wieder, sobald beide im selben WLAN sind.
/// </summary>
public sealed class PairingWindow : SheetWindow
{
    private readonly Image _qr = new() { Width = 236, Height = 236, Stretch = Stretch.Uniform };
    private readonly TextBlock _code = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Border _statusDot = new() { Width = 9, Height = 9, CornerRadius = new CornerRadius(4.5), Margin = new Thickness(0, 0, 9, 0) };
    private readonly TextBlock _expires = new();
    private readonly StackPanel _devices = new();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private PairingSession _session;

    public PairingWindow() : base("iPad verbinden", 720, 760)
    {
        RenderOptions.SetBitmapScalingMode(_qr, BitmapScalingMode.NearestNeighbor);
        _session = Services.Pad.StartPairing();

        var left = new StackPanel { Width = 268 };
        var qrFrame = new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = _qr
        };
        left.Children.Add(qrFrame);
        left.Children.Add(Ui.Text("oder Code eingeben", 12, color: "Faint", margin: new Thickness(4, 14, 0, 4)));
        _code.FontFamily = new FontFamily("Cascadia Mono, Consolas, Segoe UI");
        _code.FontSize = 38;
        _code.FontWeight = FontWeights.SemiBold;
        _code.Margin = new Thickness(2, 0, 0, 0);
        left.Children.Add(_code);
        _expires.FontSize = 12;
        _expires.Margin = new Thickness(4, 2, 0, 0);
        _expires.SetResourceReference(TextBlock.ForegroundProperty, "Faint");
        left.Children.Add(_expires);

        var steps = new StackPanel { Margin = new Thickness(28, 0, 0, 0) };
        steps.Children.Add(Step("1", "Öffne auf dem iPad „Lernheft Pad“."));
        steps.Children.Add(Step("2", "Halte die Kamera auf den QR-Code – oder wähle „Code eingeben“ und tippe die sechs Ziffern ab."));
        steps.Children.Add(Step("3", "Fertig. Was du auf dem iPad zeichnest, erscheint sofort in der Notiz, die hier offen ist."));
        steps.Children.Add(Ui.Hint("Beide Geräte müssen im selben WLAN sein. Später verbindet sich das iPad von allein, "
            + "ein neuer Code ist dann nicht nötig.", new Thickness(0, 6, 0, 0)));
        var statusRow = new DockPanel { Margin = new Thickness(0, 22, 0, 0) };
        _statusDot.VerticalAlignment = VerticalAlignment.Top;
        _statusDot.Margin = new Thickness(0, 5, 9, 0);
        DockPanel.SetDock(_statusDot, Dock.Left);
        statusRow.Children.Add(_statusDot);
        statusRow.Children.Add(_status);
        steps.Children.Add(statusRow);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(left);
        Grid.SetColumn(steps, 1);
        grid.Children.Add(steps);
        Body.Children.Add(Ui.Card(grid, new Thickness(22)));

        Body.Children.Add(TroubleCard());
        Body.Children.Add(DevicesCard());

        Refresh();
        _clock.Tick += (_, _) => Tick();
        _clock.Start();
        Services.Bridge.StatusChanged += OnBridge;
        Services.Pad.PairingChanged += OnPairing;
        Closed += (_, _) =>
        {
            _clock.Stop();
            Services.Bridge.StatusChanged -= OnBridge;
            Services.Pad.PairingChanged -= OnPairing;
            Services.Pad.StopPairing();
        };
    }

    private static UIElement Step(string number, string text)
    {
        var badge = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(12),
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = number,
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        badge.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
        ((TextBlock)badge.Child).SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        var dock = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        DockPanel.SetDock(badge, Dock.Left);
        dock.Children.Add(badge);
        dock.Children.Add(Ui.Text(text, 14, wrap: true));
        return dock;
    }

    private UIElement TroubleCard()
    {
        var panel = new StackPanel();
        panel.Children.Add(Ui.CardHeader("Klappt nicht?", Ui.GlyphInfo));
        panel.Children.Add(Ui.Text("• Im Schul-WLAN dürfen sich Geräte oft nicht sehen. Dann schalte am Surface den mobilen Hotspot ein "
            + "(Windows-Einstellungen → Netzwerk → Mobiler Hotspot) und verbinde das iPad damit.", 13, color: "Muted", wrap: true));
        panel.Children.Add(Ui.Text("• Die Windows-Firewall kann eingehende Verbindungen sperren. Das Surface versucht dann von sich aus, "
            + "das iPad zu erreichen – das dauert ein paar Sekunden länger.", 13, color: "Muted", wrap: true, margin: new Thickness(0, 6, 0, 0)));
        var addresses = PadServer.LocalAddresses();
        panel.Children.Add(Ui.Text("Adresse dieses Surface: " + (addresses.Count == 0 ? "kein Netz" : string.Join(", ", addresses)) +
                                   $" · Port {Services.Pad.Port}" + (Services.Pad.Listening ? "" : " (belegt)"),
            12, color: "Faint", wrap: true, margin: new Thickness(0, 10, 0, 0)));
        var firewall = Ui.Button("Firewall-Freigabe einrichten", (_, _) =>
        {
            var ok = Firewall.AddRule();
            Dialogs.Info(this, ok ? "Freigabe eingerichtet" : "Nicht eingerichtet",
                ok ? "Das iPad kann sich jetzt direkt mit dem Surface verbinden."
                   : "Windows hat die Freigabe nicht erlaubt. Das ist nicht schlimm – das Surface verbindet sich dann von sich aus mit dem iPad.");
        }, icon: Ui.GlyphConnect);
        firewall.HorizontalAlignment = HorizontalAlignment.Left;
        firewall.Margin = new Thickness(0, 12, 0, 0);
        panel.Children.Add(firewall);
        return Ui.Card(panel);
    }

    private UIElement DevicesCard()
    {
        var panel = new StackPanel();
        panel.Children.Add(Ui.CardHeader("Gekoppelte iPads", Ui.GlyphTablet));
        panel.Children.Add(_devices);
        FillDevices();
        return Ui.Card(panel);
    }

    private void FillDevices()
    {
        _devices.Children.Clear();
        var devices = Services.Pad.PairedDevices;
        if (devices.Count == 0)
        {
            _devices.Children.Add(Ui.Text("Noch keins.", 13, color: "Faint"));
            return;
        }
        foreach (var device in devices.OrderByDescending(d => d.LastSeen))
        {
            var connected = Services.Bridge.Connected && Services.Pad.Current?.DeviceId == device.DeviceId;
            var remove = Ui.Button("Entfernen", (_, _) =>
            {
                if (!Dialogs.Confirm(this, "iPad entfernen?", $"„{device.Name}“ muss danach neu gekoppelt werden.", "Entfernen", danger: true)) return;
                Services.Pad.Forget(device.DeviceId);
                FillDevices();
            });
            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(Ui.Text(device.Name, 14, FontWeights.SemiBold));
            info.Children.Add(Ui.Text(connected ? "verbunden" : "zuletzt " + AppleTime.ToDateTime(device.LastSeen).ToString("dd.MM. HH:mm"),
                12, color: connected ? "Good" : "Faint"));
            var dock = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            DockPanel.SetDock(remove, Dock.Right);
            dock.Children.Add(remove);
            var icon = Ui.Icon(Ui.GlyphTablet, 18, connected ? "Good" : "Muted");
            icon.Margin = new Thickness(0, 0, 12, 0);
            DockPanel.SetDock(icon, Dock.Left);
            dock.Children.Add(icon);
            dock.Children.Add(info);
            _devices.Children.Add(dock);
        }
    }

    private void Refresh()
    {
        _code.Text = _session.FormattedCode;
        _qr.Source = QrImage(Services.Pad.QrPayload(_session));
        UpdateStatus();
        Tick();
    }

    public static BitmapSource QrImage(string payload)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(12, new byte[] { 0x10, 0x18, 0x26 }, new byte[] { 0xFF, 0xFF, 0xFF }, drawQuietZones: false);
        var image = new BitmapImage();
        using var stream = new MemoryStream(png);
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void Tick()
    {
        var left = _session.Expires - DateTime.UtcNow;
        if (left <= TimeSpan.Zero || Services.Pad.Pairing is null && !Services.Bridge.Connected)
        {
            // Abgelaufen: einfach einen neuen Code würfeln.
            _session = Services.Pad.StartPairing();
            Refresh();
            return;
        }
        _expires.Text = $"gilt noch {(int)left.TotalMinutes}:{left.Seconds:00} Minuten";
    }

    private void UpdateStatus()
    {
        if (Services.Bridge.Connected)
        {
            _status.Text = $"Verbunden mit {Services.Bridge.DeviceName}. Du kannst loszeichnen.";
            _statusDot.SetResourceReference(Border.BackgroundProperty, "Good");
            _status.SetResourceReference(TextBlock.ForegroundProperty, "Good");
        }
        else
        {
            _status.Text = "Wartet auf das iPad …";
            _statusDot.SetResourceReference(Border.BackgroundProperty, "Warn");
            _status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        }
    }

    private void OnBridge()
    {
        UpdateStatus();
        FillDevices();
        if (Services.Bridge.Connected)
        {
            // Kurz zeigen, dass es geklappt hat – dann zu.
            var close = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
            close.Tick += (_, _) =>
            {
                close.Stop();
                if (IsLoaded) Close();
            };
            close.Start();
        }
    }

    private void OnPairing() => Dispatcher.BeginInvoke(FillDevices);
}
