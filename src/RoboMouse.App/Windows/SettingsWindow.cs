using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using RoboMouse.Core;
using RoboMouse.Core.Configuration;
using RoboMouse.Core.Network;

namespace RoboMouse.App.Windows;

/// <summary>
/// Settings window: a header with the live status, a navigation rail on the left and one page per
/// area (General, Network, Peers, Layout).
/// </summary>
public sealed class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly RoboMouseService _service;

    // Header
    private TextBlock _statusLabel = null!;
    private Ellipse _statusDot = null!;
    private Color _statusColor = Ui.Grey;

    // Navigation
    private readonly List<(Button button, Border accent, Control page)> _pages = new();
    private Panel _content = null!;
    private StackPanel _rail = null!;

    // General
    private TextBox _machineNameTextBox = null!;
    private CheckBox _startWithWindowsCheck = null!;
    private CheckBox _startMinimizedCheck = null!;
    private TextBox _hotkeyTextBox = null!;
    private CheckBox _clipboardEnabledCheck = null!;
    private CheckBox _shareFilesCheck = null!;
    private ComboBox _highlightCombo = null!;
#if DEBUG
    private CheckBox _debugPanelCheck = null!;
#endif

    // Network
    private NumericUpDown _portNumeric = null!;
    private NumericUpDown _discoveryPortNumeric = null!;
    private TextBox _pairingCodeTextBox = null!;

    // Peers
    private ListBox _peersList = null!;
    private ListBox _discoveredList = null!;
    private Button _addDiscoveredButton = null!;
    private Button _editButton = null!;
    private Button _removeButton = null!;
    private Button _enableButton = null!;
    private bool _suppressItemCheck;

    // Layout
    private ScreenLayoutControl _layoutPanel = null!;
    private Control _layoutPage = null!;
    private readonly DispatcherTimer _statusTimer;

    private const double NameColumn = 170;
    private const double AddressColumn = 150;
    private const double PositionColumn = 80;
    private const double ScreenColumn = 110;

    public SettingsWindow(AppSettings settings, RoboMouseService service)
    {
        _settings = settings;
        _service = service;

        Ui.Style(this);
        Title = "RoboMouse";
        Width = 940;
        Height = 720;
        MinWidth = 800;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        CanMaximize = false;

        var saveButton = Ui.PrimaryButton("Save", 100);
        saveButton.Click += OnSaveClick;
        var cancelButton = Ui.Button("Cancel", 100);
        cancelButton.IsCancel = true;
        cancelButton.Click += (s, e) => Close();

        var root = new DockPanel { LastChildFill = true };
        var header = CreateHeader();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(Ui.ActionBar(saveButton, cancelButton));

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition(168, GridUnitType.Pixel));
        body.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        var rail = CreateNavRail();
        body.Children.Add(rail);
        _content = new Panel { Margin = new Thickness(Ui.Pad + 4, Ui.Pad, Ui.Pad, Ui.Pad) };
        Grid.SetColumn(_content, 1);
        body.Children.Add(_content);
        root.Children.Add(body);
        Content = root;

        AddPage("General", CreateGeneralPage());
        AddPage("Network", CreateNetworkPage());
        AddPage("Peers", CreatePeersPage());
        _layoutPage = CreateLayoutPage();
        AddPage("Layout", _layoutPage);
        ShowPage(0);

        LoadSettings();

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (s, e) =>
        {
            RefreshStatus();
            RefreshPeerStatus();
            RefreshDiscoveredList();
        };
        _statusTimer.Start();
        Closed += (s, e) => _statusTimer.Stop();
    }

    // ------------------------------------------------------------------ chrome

    private Control CreateHeader()
    {
        var grid = new Grid { Height = 72, Margin = new Thickness(Ui.Pad, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        var logo = new Image { Source = Ui.AppImage(), Width = 40, Height = 40, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        grid.Children.Add(logo);

        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = "RoboMouse", FontSize = Ui.TitleSize, FontWeight = FontWeight.SemiBold, Foreground = Ui.TextBrush });
        titles.Children.Add(new TextBlock
        {
            Text = $"Version {typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3)}  ·  {_settings.MachineName}",
            FontSize = Ui.SmallSize,
            Foreground = Ui.MutedBrush,
            Margin = new Thickness(2, 2, 0, 0)
        });
        Grid.SetColumn(titles, 1);
        grid.Children.Add(titles);

        // Status pill, right-aligned.
        var pill = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _statusDot = Ui.Dot(_statusColor);
        _statusDot.Margin = new Thickness(0, 0, 8, 0);
        _statusLabel = new TextBlock { Text = "Not connected", Foreground = Ui.MutedBrush, VerticalAlignment = VerticalAlignment.Center };
        pill.Children.Add(_statusDot);
        pill.Children.Add(_statusLabel);
        Grid.SetColumn(pill, 2);
        grid.Children.Add(pill);

        return new Border
        {
            Background = Ui.CardBrush,
            BorderBrush = Ui.BorderBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = grid
        };
    }

    private Control CreateNavRail()
    {
        _rail = new StackPanel { Orientation = Orientation.Vertical, Spacing = 2 };
        return new Border
        {
            Background = Ui.SidebarBrush,
            BorderBrush = Ui.BorderBrush,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Padding = new Thickness(12, 16, 12, 12),
            Child = _rail
        };
    }

    private void AddPage(string name, Control page)
    {
        var index = _pages.Count;

        var accent = new Border { Width = 3, Background = Ui.AccentBrush, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 8, 0, 8), IsVisible = false };
        var label = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        var content = new DockPanel();
        content.Children.Add(accent);
        content.Children.Add(label);

        var button = new Button
        {
            Content = content,
            Height = 36,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = Ui.SidebarBrush,
            Foreground = Ui.TextBrush,
            Cursor = new Cursor(StandardCursorType.Hand),
            Focusable = false
        };
        button.Resources["ButtonBackground"] = new SolidColorBrush(Ui.Sidebar);
        button.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(Ui.SidebarHover);
        button.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(Ui.SidebarHover);
        button.Resources["ButtonForegroundPointerOver"] = new SolidColorBrush(Ui.Text);
        button.Resources["ButtonForegroundPressed"] = new SolidColorBrush(Ui.Text);
        button.Click += (s, e) => ShowPage(index);

        page.IsVisible = false;
        _content.Children.Add(page);
        _rail.Children.Add(button);
        _pages.Add((button, accent, page));
    }

    private void ShowPage(int index)
    {
        for (var i = 0; i < _pages.Count; i++)
        {
            var (button, accent, page) = _pages[i];
            var active = i == index;
            page.IsVisible = active;
            accent.IsVisible = active;
            if (active && page == _layoutPage)
                _layoutPanel.Reload();
            var background = active ? Ui.Card : Ui.Sidebar;
            button.Background = new SolidColorBrush(background);
            button.Resources["ButtonBackground"] = new SolidColorBrush(background);
            button.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(active ? Ui.Card : Ui.SidebarHover);
            button.FontWeight = active ? FontWeight.SemiBold : FontWeight.Normal;
        }
    }

    /// <summary>A settings page: scrolls vertically when its sections do not fit the window.</summary>
    private static ScrollViewer Page(params Control[] sections)
    {
        var stack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 0, Ui.Pad, 0) };
        foreach (var section in sections)
            stack.Children.Add(section);
        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = stack
        };
    }

    private void RefreshStatus()
    {
        var connected = _service.ConnectedPeers.Count;
        string text;
        Color color;
        if (!_service.Enabled) { text = "Sharing off"; color = Ui.Grey; }
        else if (_service.IsControllingRemote) { text = $"Controlling {_service.ActivePeer?.Name}"; color = Ui.Accent; }
        else if (_service.IsControlledByRemote) { text = "Being controlled"; color = Ui.Orange; }
        else if (connected == 0) { text = "Not connected"; color = Ui.Grey; }
        else if (connected == 1) { text = $"Connected to {_service.ConnectedPeers.First().PeerName}"; color = Ui.Green; }
        else { text = $"Connected to {connected} peers"; color = Ui.Green; }

        if (_statusLabel.Text != text)
            _statusLabel.Text = text;
        if (_statusColor != color)
        {
            _statusColor = color;
            _statusDot.Fill = new SolidColorBrush(color);
        }
    }

    // ------------------------------------------------------------------ General

    private Control CreateGeneralPage()
    {
        var identity = Ui.FormGrid();
        _machineNameTextBox = Ui.TextBox(260);
        Ui.Row(identity, "Machine name", _machineNameTextBox);
        Ui.Row(identity, "", Ui.Hint("How this computer appears on the other machines."));

        var startup = Ui.Stack();
        _startWithWindowsCheck = Ui.Check("Start with Windows");
        _startMinimizedCheck = Ui.Check("Start minimized to the tray");
        startup.Children.Add(_startWithWindowsCheck);
        startup.Children.Add(_startMinimizedCheck);

        var hotkey = Ui.FormGrid();
        _hotkeyTextBox = Ui.TextBox(200);
        Ui.Row(hotkey, "Toggle hotkey", _hotkeyTextBox);
        Ui.Row(hotkey, "", Ui.Hint("For example Ctrl+Alt+M. Releases control of another screen if you are stuck there; otherwise turns sharing on or off.", 460));

        var clipboard = Ui.Stack();
        _clipboardEnabledCheck = Ui.Check("Share text and images");
        _shareFilesCheck = Ui.Check("Share copied files (paste them in Explorer on the other machine)");
        clipboard.Children.Add(_clipboardEnabledCheck);
        clipboard.Children.Add(_shareFilesCheck);
        clipboard.Children.Add(Ui.Hint("Files transfer only when you paste, straight from the machine you copied them on.", 460));

        var display = Ui.Stack();
        var highlightRow = Ui.Inline();
        highlightRow.Children.Add(Ui.Label("When the mouse arrives on this screen"));
        _highlightCombo = Ui.Combo(260);
        _highlightCombo.ItemsSource = new[]
        {
            new HighlightChoice(EdgeHighlightStyle.None, "Show nothing"),
            new HighlightChoice(EdgeHighlightStyle.Border, "Flash a border around the screen"),
            new HighlightChoice(EdgeHighlightStyle.Fade, "Glow along the edge it came in on"),
        };
        highlightRow.Children.Add(_highlightCombo);
        display.Children.Add(highlightRow);
#if DEBUG
        _debugPanelCheck = Ui.Check("Show the debug panel while controlling another screen");
        display.Children.Add(_debugPanelCheck);
#endif

        var legend = Ui.Stack();
        legend.Children.Add(Ui.Hint("The border colour of the tray icon shows what RoboMouse is doing:"));
        foreach (var (color, text) in new[]
        {
            (Ui.Grey, "No peers connected"),
            (Ui.Green, "Connected"),
            (Ui.Accent, "Controlling another screen"),
            (Ui.Orange, "Being controlled"),
        })
        {
            var row = Ui.Inline();
            row.Margin = new Thickness(0, 0, 0, 4);
            row.Children.Add(Ui.Dot(color));
            row.Children.Add(new TextBlock { Text = text, Foreground = Ui.TextBrush, VerticalAlignment = VerticalAlignment.Center });
            legend.Children.Add(row);
        }
        legend.Children.Add(Ui.Hint("A faded icon means sharing is switched off."));

        return Page(
            Ui.SectionHeading("This computer", first: true), identity,
            Ui.SectionHeading("Startup"), startup,
            Ui.SectionHeading("Hotkey"), hotkey,
            Ui.SectionHeading("Clipboard"), clipboard,
            Ui.SectionHeading("Display"), display,
            Ui.SectionHeading("Tray icon"), legend);
    }

    // ------------------------------------------------------------------ Network

    private Control CreateNetworkPage()
    {
        var pairing = Ui.Stack();
        var codeRow = Ui.Inline();
        _pairingCodeTextBox = Ui.TextBox(230);
        _pairingCodeTextBox.FontFamily = Ui.MonoFont;
        _pairingCodeTextBox.FontSize = 14;
        codeRow.Children.Add(_pairingCodeTextBox);
        var copyCodeButton = Ui.Button("Copy");
        copyCodeButton.Margin = new Thickness(Ui.Gap, 3, 0, 3);
        copyCodeButton.Click += async (s, e) =>
        {
            try { if (Clipboard != null) await Clipboard.SetTextAsync(_pairingCodeTextBox.Text ?? ""); } catch { }
        };
        codeRow.Children.Add(copyCodeButton);
        var newCodeButton = Ui.Button("Generate new");
        newCodeButton.Margin = new Thickness(Ui.Gap, 3, 0, 3);
        newCodeButton.Click += async (s, e) =>
        {
            if (await Dialogs.ConfirmAsync(this, "Generate a new pairing code? Every other machine will need the new code before it can connect again."))
                _pairingCodeTextBox.Text = SecureChannel.GeneratePairingCode();
        };
        codeRow.Children.Add(newCodeButton);
        pairing.Children.Add(codeRow);
        pairing.Children.Add(Ui.Hint(
            "Enter the same code on every machine. It authenticates peers and encrypts all traffic; a machine with a " +
            "different code cannot connect. Existing connections keep their session until they reconnect.", 480));

        var ports = Ui.FormGrid();
        _portNumeric = Ui.Number(1024, 65535);
        Ui.Row(ports, "Listen port", _portNumeric);
        _discoveryPortNumeric = Ui.Number(1024, 65535);
        Ui.Row(ports, "Discovery port", _discoveryPortNumeric);
        var idTextBox = Ui.TextBox(300);
        idTextBox.Text = _settings.MachineId;
        idTextBox.IsReadOnly = true;
        idTextBox.Foreground = Ui.MutedBrush;
        Ui.Row(ports, "Machine ID", idTextBox);
        Ui.Row(ports, "", Ui.Hint("Port changes take effect after RoboMouse is restarted."));

        var firewall = Ui.Stack();
        var firewallButton = Ui.Button("Allow RoboMouse through Windows Firewall…");
        firewallButton.Click += OnFirewallClick;
        firewall.Children.Add(firewallButton);
        firewall.Children.Add(Ui.Hint(
            "Adds inbound rules for the ports above from any address, on all network profiles. Needed when the other " +
            "machine is on a different subnet: the rule Windows creates automatically is often limited to the local " +
            "subnet. Requires administrator approval.", 480));
        firewall.Children.Add(Ui.Hint("Automatic discovery uses broadcast and never crosses subnets; add such peers by IP address.", 480));

        return Page(
            Ui.SectionHeading("Pairing code", first: true), pairing,
            Ui.SectionHeading("Ports"), ports,
            Ui.SectionHeading("Firewall"), firewall);
    }

    private async void OnFirewallClick(object? sender, EventArgs e)
    {
        var tcp = (int)(_portNumeric.Value ?? _settings.LocalPort);
        var udp = (int)(_discoveryPortNumeric.Value ?? _settings.DiscoveryPort);

        // One elevated cmd that replaces any previous RoboMouse rules.
        var script =
            $"netsh advfirewall firewall delete rule name=\"RoboMouse (TCP)\" & " +
            $"netsh advfirewall firewall delete rule name=\"RoboMouse (UDP)\" & " +
            $"netsh advfirewall firewall add rule name=\"RoboMouse (TCP)\" dir=in action=allow protocol=TCP localport={tcp} remoteip=any profile=any & " +
            $"netsh advfirewall firewall add rule name=\"RoboMouse (UDP)\" dir=in action=allow protocol=UDP localport={udp} remoteip=any profile=any";

        try
        {
            var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c " + script,
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            });
            if (process != null)
                await Task.Run(() => process.WaitForExit(15000));

            if (process?.ExitCode == 0)
                await Dialogs.InfoAsync(this, $"Firewall rules added for TCP {tcp} and UDP {udp}.");
            else
                await Dialogs.WarnAsync(this, "The firewall command did not complete. You can add the rules manually in Windows Defender Firewall with Advanced Security.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // User declined the elevation prompt.
        }
        catch (Exception ex)
        {
            await Dialogs.ErrorAsync(this, $"Could not update the firewall: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ Peers

    private Control CreatePeersPage()
    {
        // Configured peers: the list takes the spare height, everything else is auto-sized.
        var grid = new Grid { Margin = new Thickness(0, 0, Ui.Pad, 0) };
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));           // heading
        grid.RowDefinitions.Add(new RowDefinition(62, GridUnitType.Star));      // peers list
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));           // toolbar
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));           // hint
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));           // discovered heading
        grid.RowDefinitions.Add(new RowDefinition(38, GridUnitType.Star));      // discovered list
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));           // discovered toolbar

        void Place(Control control, int row)
        {
            Grid.SetRow(control, row);
            grid.Children.Add(control);
        }

        Place(Ui.SectionHeading("Configured peers", first: true), 0);

        _peersList = List();
        _peersList.SelectionChanged += (s, e) => UpdatePeerButtons();
        _peersList.DoubleTapped += (s, e) => OnEditPeerClick(s, EventArgs.Empty);
        Place(ListWithHeader(_peersList, ("", 28), ("Name", NameColumn), ("Address", AddressColumn), ("Position", PositionColumn), ("Status", 0)), 1);

        var toolbar = Ui.Inline();
        toolbar.Margin = new Thickness(0, Ui.Gap, 0, 0);
        var addButton = Ui.Button("Add…");
        addButton.Click += OnAddPeerClick;
        _editButton = Ui.Button("Edit…");
        _editButton.Click += OnEditPeerClick;
        _enableButton = Ui.Button("Disable");
        _enableButton.Click += OnToggleEnabledClick;
        _removeButton = Ui.DangerButton("Remove");
        _removeButton.Click += OnRemovePeerClick;
        foreach (var b in new[] { addButton, _editButton, _enableButton, _removeButton })
            toolbar.Children.Add(b);
        Place(toolbar, 2);

        Place(Ui.Hint("Untick a peer to switch it off: it keeps its settings but is never connected to and cannot take control of this screen. " +
                      "Change which edge a peer sits on under Layout, or by editing it.", 560), 3);

        Place(Ui.SectionHeading("Found on this network"), 4);

        _discoveredList = List();
        _discoveredList.SelectionChanged += (s, e) => _addDiscoveredButton.IsEnabled = _discoveredList.SelectedItem != null;
        _discoveredList.DoubleTapped += (s, e) => OnAddDiscoveredClick(s, EventArgs.Empty);
        Place(ListWithHeader(_discoveredList, ("Name", NameColumn), ("Address", AddressColumn), ("Screen", ScreenColumn)), 5);

        var discoveredBar = Ui.Inline();
        discoveredBar.Margin = new Thickness(0, Ui.Gap, 0, 0);
        _addDiscoveredButton = Ui.Button("Add selected…");
        _addDiscoveredButton.IsEnabled = false;
        _addDiscoveredButton.Click += OnAddDiscoveredClick;
        discoveredBar.Children.Add(_addDiscoveredButton);
        var discoveredHint = Ui.Hint("Machines running RoboMouse on this subnet that are not configured yet. Peers elsewhere must be added by IP address.", 420);
        discoveredHint.VerticalAlignment = VerticalAlignment.Center;
        discoveredHint.Margin = new Thickness(0);
        discoveredBar.Children.Add(discoveredHint);
        Place(discoveredBar, 6);

        return grid;
    }

    /// <summary>A clean details list: flat, full-row select, shared font.</summary>
    private static ListBox List() => new()
    {
        Background = Ui.CardBrush,
        BorderThickness = new Thickness(0),
        SelectionMode = SelectionMode.Single,
        Padding = new Thickness(0)
    };

    /// <summary>Wraps a list in a bordered card with a column header row above it.</summary>
    private static Control ListWithHeader(ListBox list, params (string title, double width)[] columns)
    {
        var header = new Grid { Margin = new Thickness(8, 6, 8, 6) };
        foreach (var (title, width) in columns)
        {
            var column = width > 0 ? new ColumnDefinition(width, GridUnitType.Pixel) : new ColumnDefinition(1, GridUnitType.Star);
            header.ColumnDefinitions.Add(column);
            var text = new TextBlock { Text = title, FontSize = Ui.SmallSize, FontWeight = FontWeight.SemiBold, Foreground = Ui.MutedBrush };
            Grid.SetColumn(text, header.ColumnDefinitions.Count - 1);
            header.Children.Add(text);
        }

        var dock = new DockPanel { LastChildFill = true };
        var headerBorder = new Border { Child = header, BorderBrush = Ui.BorderBrush, BorderThickness = new Thickness(0, 0, 0, 1), Background = Ui.CardBrush };
        DockPanel.SetDock(headerBorder, Dock.Top);
        dock.Children.Add(headerBorder);
        dock.Children.Add(list);

        return new Border
        {
            BorderBrush = Ui.BorderBrush,
            BorderThickness = new Thickness(1),
            Background = Ui.CardBrush,
            CornerRadius = new CornerRadius(3),
            Child = dock
        };
    }

    private static Grid RowGrid(params double[] widths)
    {
        var grid = new Grid { Margin = new Thickness(8, 2, 8, 2) };
        foreach (var width in widths)
            grid.ColumnDefinitions.Add(width > 0 ? new ColumnDefinition(width, GridUnitType.Pixel) : new ColumnDefinition(1, GridUnitType.Star));
        return grid;
    }

    private static TextBlock Cell(Grid grid, int column, string text, IBrush? foreground = null)
    {
        var block = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Foreground = foreground ?? Ui.TextBrush, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(block, column);
        grid.Children.Add(block);
        return block;
    }

    // ------------------------------------------------------------------ Layout

    private Control CreateLayoutPage()
    {
        var grid = new Grid { Margin = new Thickness(0, 0, Ui.Pad, 0) };
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));

        var heading = Ui.SectionHeading("Screen layout", first: true);
        grid.Children.Add(heading);
        var hint = Ui.Hint("Drag a peer screen to the edge of this screen where that computer sits. Its position along the edge is kept too. " +
                           "Faded screens are disabled peers. Changes are applied when you click Save.", 560);
        Grid.SetRow(hint, 1);
        grid.Children.Add(hint);

        _layoutPanel = new ScreenLayoutControl(_settings) { Margin = new Thickness(0, Ui.Gap, 0, 0) };
        var canvas = new Border { Child = _layoutPanel, CornerRadius = new CornerRadius(6), ClipToBounds = true };
        Grid.SetRow(canvas, 2);
        grid.Children.Add(canvas);

        return grid;
    }

    // ------------------------------------------------------------------ discovered peers

    private void RefreshDiscoveredList()
    {
        var configuredIds = _settings.Peers.Select(p => p.Id).ToHashSet();
        var configuredAddresses = _settings.Peers.Select(p => p.Address).ToHashSet();
        var peers = _service.DiscoveredPeers
            .Where(p => p.MachineId != _settings.MachineId
                        && !configuredIds.Contains(p.MachineId)
                        && !configuredAddresses.Contains(p.Address.ToString()))
            .OrderBy(p => p.MachineName)
            .ToList();

        var current = _discoveredList.Items.OfType<ListBoxItem>().Select(i => (i.Tag as DiscoveredPeer)?.MachineId).ToList();
        if (current.SequenceEqual(peers.Select(p => p.MachineId)))
            return;

        var selectedId = (SelectedDiscovered)?.MachineId;
        _discoveredList.Items.Clear();
        foreach (var peer in peers)
        {
            var row = RowGrid(NameColumn, AddressColumn, ScreenColumn);
            Cell(row, 0, peer.MachineName);
            Cell(row, 1, $"{peer.Address}:{peer.Port}");
            Cell(row, 2, $"{peer.ScreenWidth} × {peer.ScreenHeight}");
            var item = new ListBoxItem { Content = row, Tag = peer };
            _discoveredList.Items.Add(item);
            if (peer.MachineId == selectedId)
                _discoveredList.SelectedItem = item;
        }
        _addDiscoveredButton.IsEnabled = _discoveredList.SelectedItem != null;
    }

    private DiscoveredPeer? SelectedDiscovered => (_discoveredList.SelectedItem as ListBoxItem)?.Tag as DiscoveredPeer;

    private async void OnAddDiscoveredClick(object? sender, EventArgs e)
    {
        if (SelectedDiscovered is not DiscoveredPeer discovered)
            return;

        var draft = new PeerConfig
        {
            Id = discovered.MachineId,
            Name = discovered.MachineName,
            Address = discovered.Address.ToString(),
            Port = discovered.Port,
            ScreenWidth = discovered.ScreenWidth,
            ScreenHeight = discovered.ScreenHeight,
            Position = PeerPositions.All.FirstOrDefault(pos => _settings.Peers.All(p => p.Position != pos), ScreenPosition.Right)
        };

        var dialog = new PeerSetupWindow(draft, _service, _settings);
        var result = await dialog.ShowDialog<PeerConfig?>(this);
        if (result == null)
            return;

        _settings.Peers.Add(result);
        _settings.Save();
        RefreshPeersList();
        RefreshDiscoveredList();

        if (!result.Enabled)
            return;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await _service.ConnectToPeerAsync(result, cts.Token);
            _settings.Save();
        }
        catch (Exception ex)
        {
            await Dialogs.WarnAsync(this, $"Added {result.Name}, but could not connect yet: {ex.Message}");
        }
        RefreshPeersList();
    }

    // ------------------------------------------------------------------ configured peers

    private sealed class PeerRow
    {
        public required PeerConfig Peer { get; init; }
        public required ListBoxItem Item { get; init; }
        public required CheckBox Check { get; init; }
        public required TextBlock Name { get; init; }
        public required TextBlock Address { get; init; }
        public required TextBlock Position { get; init; }
        public required TextBlock Status { get; init; }
    }

    private PeerConfig? SelectedPeer => (_peersList.SelectedItem as ListBoxItem)?.Tag is PeerRow row ? row.Peer : null;

    private IEnumerable<PeerRow> PeerRows => _peersList.Items.OfType<ListBoxItem>().Select(i => i.Tag).OfType<PeerRow>();

    private void RefreshPeersList()
    {
        var selected = SelectedPeer;
        _suppressItemCheck = true;
        _peersList.Items.Clear();
        foreach (var peer in _settings.Peers)
        {
            var grid = RowGrid(28, NameColumn, AddressColumn, PositionColumn, 0);
            var check = new CheckBox { IsChecked = peer.Enabled, VerticalAlignment = VerticalAlignment.Center, MinWidth = 0, Padding = new Thickness(0) };
            grid.Children.Add(check);
            var row = new PeerRow
            {
                Peer = peer,
                Item = new ListBoxItem { Content = grid },
                Check = check,
                Name = Cell(grid, 1, peer.Name),
                Address = Cell(grid, 2, $"{peer.Address}:{peer.Port}"),
                Position = Cell(grid, 3, PeerPositions.Describe(peer.Position)),
                Status = Cell(grid, 4, string.Empty)
            };
            row.Item.Tag = row;
            check.IsCheckedChanged += (s, e) =>
            {
                if (_suppressItemCheck)
                    return;
                _ = SetPeerEnabledAsync(peer, check.IsChecked == true);
            };
            _peersList.Items.Add(row.Item);
            if (peer == selected)
                _peersList.SelectedItem = row.Item;
        }
        _suppressItemCheck = false;
        _layoutPanel?.Reload();
        RefreshPeerStatus();
        UpdatePeerButtons();
    }

    private void RefreshPeerStatus()
    {
        foreach (var row in PeerRows)
        {
            var peer = row.Peer;
            var connection = _service.GetConnection(peer.Id);
            string status;
            Color color;
            if (!peer.Enabled) { status = "Disabled"; color = Ui.Grey; }
            else if (connection == null) { status = "Not connected"; color = Ui.Muted; }
            else if (connection.RoundTripMs >= 0) { status = $"Connected · {connection.RoundTripMs} ms"; color = Ui.Green; }
            else { status = "Connected"; color = Ui.Green; }

            if (row.Status.Text != status)
            {
                row.Status.Text = status;
                row.Status.Foreground = new SolidColorBrush(color);
                var rowBrush = peer.Enabled ? Ui.TextBrush : new SolidColorBrush(Ui.Grey);
                row.Name.Foreground = rowBrush;
                row.Address.Foreground = rowBrush;
                row.Position.Foreground = rowBrush;
            }
            var position = PeerPositions.Describe(peer.Position);
            if (row.Position.Text != position)
                row.Position.Text = position;
            if (row.Check.IsChecked != peer.Enabled)
            {
                _suppressItemCheck = true;
                row.Check.IsChecked = peer.Enabled;
                _suppressItemCheck = false;
            }
        }
    }

    private void UpdatePeerButtons()
    {
        var peer = SelectedPeer;
        var has = peer != null;
        _editButton.IsEnabled = has;
        _removeButton.IsEnabled = has;
        _enableButton.IsEnabled = has;
        _enableButton.Content = peer is { Enabled: false } ? "Enable" : "Disable";
    }

    private void OnToggleEnabledClick(object? sender, EventArgs e)
    {
        if (SelectedPeer is PeerConfig peer)
            _ = SetPeerEnabledAsync(peer, !peer.Enabled);
    }

    private async Task SetPeerEnabledAsync(PeerConfig peer, bool enabled)
    {
        try
        {
            await _service.SetPeerEnabledAsync(peer, enabled);
        }
        catch (Exception ex)
        {
            await Dialogs.ErrorAsync(this, $"Could not {(enabled ? "enable" : "disable")} {peer.Name}: {ex.Message}");
        }
        RefreshPeerStatus();
        UpdatePeerButtons();
    }

    private sealed record HighlightChoice(EdgeHighlightStyle Style, string Text)
    {
        public override string ToString() => Text;
    }

    // ------------------------------------------------------------------ load / save

    private void LoadSettings()
    {
        _machineNameTextBox.Text = _settings.MachineName;
        _portNumeric.Value = _settings.LocalPort;
        _discoveryPortNumeric.Value = _settings.DiscoveryPort;
        _pairingCodeTextBox.Text = _settings.PairingCode;
        _clipboardEnabledCheck.IsChecked = _settings.Clipboard.Enabled;
        _shareFilesCheck.IsChecked = _settings.Clipboard.SyncFiles;
        var choices = (HighlightChoice[])_highlightCombo.ItemsSource!;
        _highlightCombo.SelectedIndex = Math.Max(0, Array.FindIndex(choices, c => c.Style == _settings.EdgeHighlight));
#if DEBUG
        _debugPanelCheck.IsChecked = _settings.DebugPanelEnabled;
#endif
        _startWithWindowsCheck.IsChecked = _settings.StartWithWindows;
        _startMinimizedCheck.IsChecked = _settings.StartMinimized;
        _hotkeyTextBox.Text = _settings.ToggleHotkey ?? "";

        RefreshStatus();
        RefreshPeersList();
    }

    private async void OnSaveClick(object? sender, EventArgs e)
    {
        var code = (_pairingCodeTextBox.Text ?? "").Trim().ToUpperInvariant();
        if (code.Replace("-", "").Replace(" ", "").Length < 8)
        {
            await Dialogs.WarnAsync(this, "The pairing code must be at least 8 characters.");
            return;
        }
        _settings.PairingCode = code;

        var hotkeyText = _hotkeyTextBox.Text ?? "";
        if (!string.IsNullOrWhiteSpace(hotkeyText) && RoboMouse.Core.Input.Hotkey.Parse(hotkeyText) == null)
        {
            await Dialogs.WarnAsync(this, "The hotkey must be a key with at least one modifier, for example Ctrl+Alt+M.");
            return;
        }

        _settings.MachineName = _machineNameTextBox.Text ?? _settings.MachineName;
        _settings.LocalPort = (int)(_portNumeric.Value ?? _settings.LocalPort);
        _settings.DiscoveryPort = (int)(_discoveryPortNumeric.Value ?? _settings.DiscoveryPort);
        _settings.Clipboard.Enabled = _clipboardEnabledCheck.IsChecked == true;
        _settings.Clipboard.SyncFiles = _shareFilesCheck.IsChecked == true;
        _settings.EdgeHighlight = (_highlightCombo.SelectedItem as HighlightChoice)?.Style ?? EdgeHighlightStyle.Fade;
#if DEBUG
        _settings.DebugPanelEnabled = _debugPanelCheck.IsChecked == true;
#endif
        _settings.StartWithWindows = _startWithWindowsCheck.IsChecked == true;
        _settings.StartMinimized = _startMinimizedCheck.IsChecked == true;
        _settings.ToggleHotkey = string.IsNullOrWhiteSpace(hotkeyText) ? null : hotkeyText;
        _layoutPanel.SaveLayout();

        _settings.Save();
        _service.ApplyClipboardSetting();
        _service.ApplyHotkeySetting();

        _ = StartupRegistration.ApplyAsync(_settings.StartWithWindows);

        Close();
    }

    private async void OnAddPeerClick(object? sender, EventArgs e)
    {
        var dialog = new PeerSetupWindow(null, _service, _settings);
        var result = await dialog.ShowDialog<PeerConfig?>(this);
        if (result != null)
        {
            _settings.Peers.Add(result);
            _settings.Save();
            RefreshPeersList();
        }
    }

    private async void OnEditPeerClick(object? sender, EventArgs e)
    {
        if (SelectedPeer is PeerConfig peer)
        {
            var wasEnabled = peer.Enabled;
            var dialog = new PeerSetupWindow(peer, _service, _settings);
            var result = await dialog.ShowDialog<PeerConfig?>(this);
            if (result != null)
            {
                _settings.Save();
                if (wasEnabled != peer.Enabled)
                {
                    // The dialog wrote the flag directly; put it back and go through the service so the
                    // connection follows the new state.
                    var wanted = peer.Enabled;
                    peer.Enabled = wasEnabled;
                    _ = SetPeerEnabledAsync(peer, wanted);
                }
                RefreshPeersList();
            }
        }
    }

    private async void OnRemovePeerClick(object? sender, EventArgs e)
    {
        if (SelectedPeer is PeerConfig peer)
        {
            if (!await Dialogs.ConfirmAsync(this, $"Remove {peer.Name}?"))
                return;
            if (_service.IsPeerConnected(peer.Id))
            {
                try { await _service.DisconnectFromPeerAsync(peer.Id); } catch { }
            }
            _settings.Peers.Remove(peer);
            _settings.Save();
            RefreshPeersList();
        }
    }
}
