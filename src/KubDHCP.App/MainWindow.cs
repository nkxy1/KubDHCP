using System.Globalization;
using System.Collections.Concurrent;
using System.Net.Sockets;
using KubDHCP.Core;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Ellipse = Microsoft.UI.Xaml.Shapes.Ellipse;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Microsoft.UI.Windowing;
using Windows.ApplicationModel.DataTransfer;

namespace KubDHCP.App;

internal sealed partial class MainWindow : Window
{
    private readonly Grid root = new() { Padding = new Thickness(12, 9, 12, 6), RowSpacing = 9 };
    private readonly Grid layout = new();
    private readonly ColumnDefinition monitorColumn = new() { Width = new GridLength(0) };
    private readonly Border monitorPanel = new() { Visibility = Visibility.Collapsed };
    private readonly ToggleButton monitorButton = new() { Content = new FontIcon { Glyph = "\uE756", FontSize = 16 }, MinWidth = 40 };
    private readonly ListView monitorOutput = new() { SelectionMode = ListViewSelectionMode.None, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly ConcurrentQueue<string> networkMessages = new();
    private readonly DispatcherQueueTimer monitorTimer;
    private ScrollViewer? monitorScroll;
    private readonly ColumnDefinition monitorDivider = new() { Width = new GridLength(0) };
    private readonly HorizontalResizeThumb monitorResize = new() { Visibility = Visibility.Collapsed, Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 55, 75, 65)) };
    private readonly StackPanel monitorState = new() { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button monitorStateButton = new() { Padding = new Thickness(6, 2, 6, 2), Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Right };
    private bool monitorManuallyPaused;
    private readonly List<Button> leaseRemoveButtons = [];
    private readonly FontIcon monitorStateIcon = new() { Glyph = "\uE768", FontSize = 12 };
    private readonly TextBlock monitorStateText = new() { Text = "Live", FontSize = 12, FontFamily = new FontFamily("Consolas") };
    private readonly DispatcherQueueTimer monitorBlinkTimer;
    private double monitorWidth = 430;
    private int monitorAddedWidth;
    private bool monitorFollowing = true, monitorUpdating, monitorAutoScrolling;
    private static readonly FontFamily TerminalFont = new("Consolas");
    private int queuedMessages, droppedMessages;
    private readonly Button removeRange = new() { Content = new FontIcon { Glyph = "\uE711", FontSize = 14 }, IsEnabled = false };
    private readonly Grid fields = new() { ColumnSpacing = 18, RowSpacing = 16 };
    private readonly ComboBox adapters = new() { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = Localizer.Text("LAN-Karte auswählen"), MaxDropDownHeight = 350 };
    private readonly TextBlock adapterDetails = Ui.Text("", 12);
    private readonly Dictionary<string, TextBox> inputs = [];
    private readonly NumberBox duration = new() { Value = 1, Minimum = 1, Maximum = 4294967294, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
    private readonly ComboBox unit = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button toggle = new() { Content = new FontIcon { Glyph = "\uE768", FontSize = 16 }, MinWidth = 40 };
    private readonly Button resetLeases = new() { Content = Localizer.Text("Lease Reset") };
    private readonly Button saveRange = new() { Content = new FontIcon { Glyph = "\uE74E", FontSize = 16 } };
    private readonly ComboBox savedRanges = new() { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = Localizer.Text("Gespeicherten Bereich laden"), MaxDropDownHeight = 300 };
    private readonly StackPanel optionSummary = new() { Spacing = 6, Visibility = Visibility.Collapsed };
    private bool loadingRanges;
    private readonly Button addOption = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly Button adapterSettings = new() { Content = new FontIcon { Glyph = "\uE713", FontSize = 16 } };
    private readonly HashSet<byte> invalidOptions = [];
    private readonly List<Control> optionControls = [];
    private bool adapterSettingsOpened;
    private string? editedAdapterId;
    private ServerConfiguration? adapterDraft;
    private readonly Button refresh = new() { Content = new FontIcon { Glyph = "\uE72C", FontSize = 16 } };
    private readonly TextBlock status = Ui.Text(Localizer.Text("Gestoppt"), 13);
    private readonly Ellipse statusDot = new() { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center };
    private readonly List<Grid> tableRows = [];
    private readonly GridLength[] columnWidths = [new(140), new(145), new(178), new(210), new(180), new(1, GridUnitType.Star), new(250)];
    private readonly TextBlock footer = Ui.Text(Localizer.Text("Bereit"), 12);
    private readonly TextBlock count = Ui.Text(Localizer.Text("0 aktive Leases"), 12);
    private readonly InfoBar message = new() { IsOpen = false, IsClosable = true };
    private readonly ListView table = new() { SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = false, MinWidth = 1303, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock empty = Ui.Text(Localizer.Text("Noch keine bestätigten Adressvergaben."), 14);
    private readonly List<(LeaseRecord Lease, TextBlock Remaining, ProgressBar Progress)> remainingLabels = [];
    private readonly List<FrameworkElement> fieldElements = [];
    private readonly DispatcherQueueTimer timer;
    private readonly DataStore store;
    private readonly DhcpServer server;
    private readonly OuiLookup oui = new();
    private List<ExtraOption> options = [];
    private AdapterInfo? selected;
    private bool busy, closing, initialized;
    private int fieldColumns;
    private string? startupError;

    public MainWindow()
    {
        Title = Program.DemoMode ? "KubDHCP [DEMO]" : "KubDHCP"; SystemBackdrop = new MicaBackdrop();
        store = CreateStore(out var storeNotice);
        using (var icon = typeof(MainWindow).Assembly.GetManifestResourceStream("KubDHCP.App.Icon.ico"))
        {
            if (icon is not null)
            {
                try
                {
                    var iconPath = Path.Combine(store.DirectoryPath, "KubDHCP.ico");
                    using (var output = File.Create(iconPath)) icon.CopyTo(output);
                    AppWindow.SetIcon(iconPath);
                }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        using (var notices = typeof(MainWindow).Assembly.GetManifestResourceStream("KubDHCP.App.ThirdPartyNotices.txt"))
        {
            if (notices is not null)
            {
                using var reader = new StreamReader(notices);
                try { File.WriteAllText(Path.Combine(store.DirectoryPath, "Lizenzen.txt"), reader.ReadToEnd()); }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        server = new(store); BuildWindow();
        var scroll = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        scroll.SizeChanged += (_, e) => root.MinHeight = Math.Max(0, e.NewSize.Height);
        layout.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); layout.ColumnDefinitions.Add(monitorDivider); layout.ColumnDefinitions.Add(monitorColumn);
        layout.Children.Add(scroll); BuildMonitor(); Grid.SetColumn(monitorResize, 1); layout.Children.Add(monitorResize); Grid.SetColumn(monitorPanel, 2); layout.Children.Add(monitorPanel); Content = layout;
        monitorTimer = DispatcherQueue.CreateTimer(); monitorTimer.Interval = TimeSpan.FromMilliseconds(250); monitorTimer.Tick += (_, _) => DrainNetworkMessages(); monitorTimer.Start();
        monitorBlinkTimer = DispatcherQueue.CreateTimer(); monitorBlinkTimer.Interval = TimeSpan.FromMilliseconds(500);
        monitorBlinkTimer.Tick += (_, _) => monitorState.Opacity = monitorState.Opacity < 1 ? 1 : 0.25;
        monitorResize.DragDelta += (_, e) => ResizeMonitor(e.HorizontalChange);
        ToolTipService.SetToolTip(monitorResize, Localizer.Text("Ziehen, um die Monitorbreite anzupassen")); AutomationProperties.SetName(monitorResize, Localizer.Text("Monitorbreite anpassen"));
        layout.SizeChanged += (_, _) => { if (monitorButton.IsChecked == true) ApplyMonitorWidth(); };
        UpdateMonitorState();
        server.NetworkActivity += QueueNetworkMessage;
        QueueNetworkMessage(Localizer.Text("Monitor bereit · protokolliert DHCP-/ARP-Kommunikation dieses Servers. Der Server ist gestoppt."));
        root.Loaded += (_, _) => UpdateTitleBarTheme();
        root.ActualThemeChanged += (_, _) => UpdateTitleBarTheme();
        timer = DispatcherQueue.CreateTimer(); timer.Interval = TimeSpan.FromSeconds(1); timer.Tick += (_, _) => UpdateRemaining();
        server.LeasesChanged += () => DispatcherQueue.TryEnqueue(UpdateLeases);
        server.Status += text => DispatcherQueue.TryEnqueue(() => footer.Text = text);
        server.Failed += text => DispatcherQueue.TryEnqueue(async () => { await server.StopAsync(); SetRunning(); ShowError(text); });
        adapters.SelectionChanged += (_, _) => SelectAdapter(); refresh.Click += (_, _) => LoadAdapters(null);
        adapterSettings.Click += (_, _) => OpenAdapterSettings();
        Activated += (_, e) =>
        {
            UpdateTitleBarTheme();
            if (!adapterSettingsOpened || e.WindowActivationState == WindowActivationState.Deactivated) return;
            adapterSettingsOpened = false; LoadAdapters(adapterDraft, editedAdapterId); adapterDraft = null;
        };
        toggle.Click += async (_, _) => await ToggleAsync();
        addOption.Click += async (_, _) => { var editor = new OptionsDialog(root.XamlRoot, options, server.IsRunning); await editor.ShowAsync(); RenderOptions(); SaveDraft(); };
        resetLeases.Click += (_, _) => ResetLeases();
        saveRange.Click += async (_, _) => await SaveRangeAsync();
        removeRange.Click += (_, _) => RemoveSelectedRange();
        monitorButton.Click += (_, _) => SetMonitorVisible();
        monitorStateButton.Click += (_, _) => ToggleMonitorPlayback();
        savedRanges.SelectionChanged += (_, _) => LoadRange();
        root.SizeChanged += (_, e) => ArrangeFields(e.NewSize.Width);
        Closed += (_, _) => { timer.Stop(); monitorTimer.Stop(); monitorBlinkTimer.Stop(); server.NetworkActivity -= QueueNetworkMessage; };
        AppWindow.Closing += async (_, e) =>
        {
            if (closing) return;
            e.Cancel = true; closing = true; toggle.IsEnabled = false;
            SaveDraft(); await server.StopAsync(); timer.Stop(); Close();
        };
        ServerConfiguration? saved = null;
        try { saved = store.LoadConfiguration(); store.LoadLeases(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { startupError = ex.Message; }
        LoadAdapters(saved);
        RefreshRanges();
        if (startupError is not null) ShowError(startupError); else if (storeNotice is not null) ShowInfo(storeNotice);
        timer.Start(); initialized = true;
        if (Program.DemoMode)
        {
            footer.Text = "DEMO · Synthetic data · Server start disabled";
            root.Loaded += (_, _) => { monitorButton.IsChecked = true; SetMonitorVisible(); foreach (var line in DemoData.MonitorExamples) QueueNetworkMessage(line); };
        }
    }
    private static DataStore CreateStore(out string? notice)
    {
        notice = null;
        if (Program.DemoMode) return DemoData.Create();
        if (Program.TestDataDirectory is { } testDirectory) return new DataStore(testDirectory);
        return UserData.Open();
    }
    private void BuildWindow()
    {
        foreach (var button in new ButtonBase[] { toggle, monitorButton, saveRange, removeRange, adapterSettings, refresh }) Ui.SizeIconButton(button);
        root.Background = Ui.Brush("ApplicationPageBackgroundThemeBrush");
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto }) root.RowDefinitions.Add(new() { Height = height });
        root.Children.Add(message);
        var cardContent = new StackPanel { Spacing = 16 };
        var heading = new Grid { ColumnSpacing = 12 };
        heading.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var caption = Ui.Text(Localizer.Text("DHCP-Konfiguration"), 18); caption.FontWeight = FontWeights.SemiBold; caption.VerticalAlignment = VerticalAlignment.Center; heading.Children.Add(caption);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center }; status.VerticalAlignment = VerticalAlignment.Center; actions.Children.Add(statusDot); actions.Children.Add(status);
        toggle.Style = (Style)Application.Current.Resources["AccentButtonStyle"]; actions.Children.Add(toggle); actions.Children.Add(monitorButton);
        AutomationProperties.SetName(monitorButton, Localizer.Text("Netzwerk-Monitor einblenden")); ToolTipService.SetToolTip(monitorButton, Localizer.Text("Netzwerk-Monitor einblenden"));
        ToolTipService.SetToolTip(resetLeases, Localizer.Text("Alle gespeicherten Leases löschen. Dafür den Server stoppen.")); Grid.SetColumn(actions, 1); heading.Children.Add(actions); cardContent.Children.Add(heading);
        var rangeRow = new Grid { ColumnSpacing = 8 };
        rangeRow.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); rangeRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); rangeRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        AutomationProperties.SetName(savedRanges, Localizer.Text("Gespeicherte Bereiche"));
        AutomationProperties.SetName(saveRange, Localizer.Text("Bereich speichern")); ToolTipService.SetToolTip(saveRange, Localizer.Text("Bereich speichern"));
        rangeRow.Children.Add(savedRanges); Grid.SetColumn(saveRange, 1); rangeRow.Children.Add(saveRange); Grid.SetColumn(removeRange, 2); rangeRow.Children.Add(removeRange);
        AutomationProperties.SetName(removeRange, Localizer.Text("Gespeicherten Bereich entfernen")); ToolTipService.SetToolTip(removeRange, Localizer.Text("Ausgewählten Bereich entfernen"));
        cardContent.Children.Add(Ui.Field(Localizer.Text("Gespeicherte Bereiche"), rangeRow));
        var adapterRow = new Grid { ColumnSpacing = 8 }; adapterRow.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); adapterRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        adapterRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        Grid.SetColumn(adapterSettings, 1); adapterRow.Children.Add(adapterSettings);
        AutomationProperties.SetName(adapterSettings, Localizer.Text("Netzwerkeinstellungen des ausgewählten Adapters öffnen"));
        ToolTipService.SetToolTip(adapterSettings, Localizer.Text("IPv4-Adresse, Subnetzmaske, Gateway und DNS des ausgewählten Adapters in Windows ändern"));
        AutomationProperties.SetName(adapters, Localizer.Text("Netzwerkschnittstelle")); AutomationProperties.SetName(refresh, Localizer.Text("LAN-Karten aktualisieren")); adapterRow.Children.Add(adapters); Grid.SetColumn(refresh, 2); adapterRow.Children.Add(refresh);
        var adapterGroup = Ui.Field(Localizer.Text("Netzwerkschnittstelle"), adapterRow); adapterGroup.Children.Add(adapterDetails); cardContent.Children.Add(adapterGroup);
        foreach (var (key, label) in new[] { ("start", Localizer.Text("Start-IP")), ("end", Localizer.Text("Stop-IP")), ("mask", Localizer.Text("Subnetzmaske")), ("gateway", "Gateway"), ("dns1", Localizer.Text("Primärer DNS-Server")), ("dns2", Localizer.Text("Sekundärer DNS-Server")) })
        {
            var input = new TextBox { PlaceholderText = key is "gateway" or "dns1" or "dns2" ? "Optional" : "", IsSpellCheckEnabled = false, IsTextPredictionEnabled = false };
            AutomationProperties.SetName(input, label); inputs.Add(key, input); fieldElements.Add(Ui.Field(label, input));
        }
        foreach (var label in new[] { Localizer.Text("Sekunden"), Localizer.Text("Minuten"), Localizer.Text("Stunden"), Localizer.Text("Tage") }) unit.Items.Add(label); unit.SelectedIndex = 2;
        AutomationProperties.SetName(duration, Localizer.Text("Lease-Dauer")); AutomationProperties.SetName(unit, Localizer.Text("Einheit der Lease-Dauer"));
        var leaseGrid = new Grid { ColumnSpacing = 8 }; leaseGrid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); leaseGrid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        leaseGrid.Children.Add(duration); Grid.SetColumn(unit, 1); leaseGrid.Children.Add(unit); fieldElements.Add(Ui.Field(Localizer.Text("Lease-Dauer"), leaseGrid));
        var optionContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; optionContent.Children.Add(new FontIcon { Glyph = "\uE710", FontSize = 14 }); optionContent.Children.Add(Ui.Text(Localizer.Text("Option hinzufügen"))); addOption.Content = optionContent; AutomationProperties.SetName(addOption, Localizer.Text("Option hinzufügen"));
        fieldElements.Add(addOption); cardContent.Children.Add(fields); cardContent.Children.Add(optionSummary); ArrangeFields(1100);
        var card = new Border { Background = Ui.Brush("CardBackgroundFillColorDefaultBrush"), BorderBrush = Ui.Brush("CardStrokeColorDefaultBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(20), Child = cardContent }; Grid.SetRow(card, 1); root.Children.Add(card);
        var leasesSection = new Grid { RowSpacing = 12, MinHeight = 230 }; leasesSection.RowDefinitions.Add(new() { Height = GridLength.Auto }); leasesSection.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        var listTitle = new Grid { ColumnSpacing = 12 };
        listTitle.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); listTitle.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); listTitle.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var listHeading = Ui.Text("Leases", 17); listHeading.FontWeight = FontWeights.SemiBold; listTitle.Children.Add(listHeading);
        count.VerticalAlignment = VerticalAlignment.Center; count.HorizontalAlignment = HorizontalAlignment.Right; Grid.SetColumn(count, 1); listTitle.Children.Add(count); Grid.SetColumn(resetLeases, 2); listTitle.Children.Add(resetLeases); leasesSection.Children.Add(listTitle);
        var tableGrid = new Grid { MinWidth = 873 }; tableGrid.RowDefinitions.Add(new() { Height = GridLength.Auto }); tableGrid.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        var tableHeader = RowGrid(); var labels = new[] { Localizer.Text("Zeitstempel"), "IP", Localizer.Text("MAC-Adresse"), Localizer.Text("Client-ID"), "Hostname", Localizer.Text("Gerätehersteller"), Localizer.Text("Rest-Leasezeit") };
        for (var i = 0; i < labels.Length; i++) { var text = Ui.Text(labels[i], 12); text.FontWeight = FontWeights.Bold; text.Margin = new(12, 12, 8, 12); Grid.SetColumn(text, i); tableHeader.Children.Add(text); }
        for (var i = 0; i < labels.Length - 1; i++)
        {
            var column = i;
            var grip = new Thumb { Width = 10, HorizontalAlignment = HorizontalAlignment.Right, Background = new SolidColorBrush(Colors.Transparent) };
            AutomationProperties.SetName(grip, labels[i] + Localizer.Text(" – Spaltenbreite anpassen"));
            ToolTipService.SetToolTip(grip, Localizer.Text("Ziehen, um die Spaltenbreite anzupassen"));
            grip.DragDelta += (_, e) => ResizeColumns(tableHeader, column, e.HorizontalChange);
            Grid.SetColumn(grip, i); tableHeader.Children.Add(grip);
        }
        tableHeader.Background = Ui.Brush("SubtleFillColorSecondaryBrush"); tableGrid.Children.Add(tableHeader);
        var itemStyle = new Style(typeof(ListViewItem)); itemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch)); itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0))); table.ItemContainerStyle = itemStyle;
        Grid.SetRow(table, 1); tableGrid.Children.Add(table); empty.Margin = new(18); Grid.SetRow(empty, 1); tableGrid.Children.Add(empty);
        AutomationProperties.SetName(table, Localizer.Text("DHCP-Geräte: Zeitstempel, IP, MAC-Adresse, Client-ID, Hostname, Hersteller und Rest-Leasezeit"));
        var horizontal = new ScrollViewer { HorizontalScrollMode = ScrollMode.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = tableGrid };
        var tableBorder = new Border { BorderThickness = new Thickness(1), BorderBrush = Ui.Brush("CardStrokeColorDefaultBrush"), Background = Ui.Brush("CardBackgroundFillColorDefaultBrush"), CornerRadius = new CornerRadius(8), Child = horizontal }; Grid.SetRow(tableBorder, 1); leasesSection.Children.Add(tableBorder); Grid.SetRow(leasesSection, 2); root.Children.Add(leasesSection);
        var bottom = new Grid { ColumnSpacing = 16 };
        bottom.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); bottom.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        footer.TextWrapping = TextWrapping.Wrap; bottom.Children.Add(footer);
        var version = typeof(MainWindow).Assembly.GetName().Version!;
        var versionLabel = Ui.Text($"v{version.Major}.{version.Minor}.{version.Build}", 12); versionLabel.HorizontalAlignment = HorizontalAlignment.Right; versionLabel.VerticalAlignment = VerticalAlignment.Bottom;
        Grid.SetColumn(versionLabel, 1); bottom.Children.Add(versionLabel); Grid.SetRow(bottom, 3); root.Children.Add(bottom);
    }
    private void UpdateTitleBarTheme()
    {
        var dark = root.ActualTheme == ElementTheme.Dark;
        var background = dark ? Windows.UI.Color.FromArgb(255, 32, 32, 32) : Windows.UI.Color.FromArgb(255, 243, 243, 243);
        var foreground = dark ? Colors.White : Colors.Black;
        var inactive = dark ? Windows.UI.Color.FromArgb(255, 160, 160, 160) : Windows.UI.Color.FromArgb(255, 96, 96, 96);
        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            var titleBar = AppWindow.TitleBar;
            titleBar.BackgroundColor = titleBar.InactiveBackgroundColor = background;
            titleBar.ForegroundColor = titleBar.ButtonForegroundColor = foreground;
            titleBar.InactiveForegroundColor = titleBar.ButtonInactiveForegroundColor = inactive;
            titleBar.ButtonBackgroundColor = titleBar.ButtonInactiveBackgroundColor = background;
            titleBar.ButtonHoverBackgroundColor = dark ? Windows.UI.Color.FromArgb(255, 55, 55, 55) : Windows.UI.Color.FromArgb(255, 225, 225, 225);
            titleBar.ButtonHoverForegroundColor = titleBar.ButtonPressedForegroundColor = foreground;
            titleBar.ButtonPressedBackgroundColor = dark ? Windows.UI.Color.FromArgb(255, 70, 70, 70) : Windows.UI.Color.FromArgb(255, 210, 210, 210);
        }
        // Keep the native caption in sync too; Windows 10 ignores the newer color attributes.
        var window = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var darkMode = dark ? 1 : 0;
        DwmSetWindowAttribute(window, 20, ref darkMode, sizeof(int));
        var captionColor = background.R | background.G << 8 | background.B << 16;
        var textColor = foreground.R | foreground.G << 8 | foreground.B << 16;
        DwmSetWindowAttribute(window, 35, ref captionColor, sizeof(int));
        DwmSetWindowAttribute(window, 36, ref textColor, sizeof(int));
    }
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
    public void SizeForStartup()
    {
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96d;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min((int)Math.Ceiling(1460 * scale), Math.Max(400, area.Width - (int)(40 * scale)));
        root.Measure(new Windows.Foundation.Size(width / scale, double.PositiveInfinity));
        var height = Math.Min((int)Math.Ceiling(Math.Max(850, root.DesiredSize.Height + 48) * scale), Math.Max(300, area.Height - (int)(40 * scale)));
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);
    private void BuildMonitor()
    {
        var body = new Grid { Padding = new Thickness(14), RowSpacing = 10 };
        body.RowDefinitions.Add(new() { Height = GridLength.Auto }); body.RowDefinitions.Add(new() { Height = GridLength.Auto }); body.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var header = new Grid { ColumnSpacing = 10 };
        header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var heading = Ui.Text(Localizer.Text("Netzwerk-Monitor"), 16); heading.FontFamily = TerminalFont; heading.FontWeight = FontWeights.SemiBold; heading.Foreground = new SolidColorBrush(Colors.White); header.Children.Add(heading);
        monitorState.Children.Add(monitorStateIcon); monitorState.Children.Add(monitorStateText); monitorStateButton.Content = monitorState; Grid.SetColumn(monitorStateButton, 1); header.Children.Add(monitorStateButton); body.Children.Add(header);
        var hint = Ui.Text(Localizer.Text("DHCP & ARP · Hochscrollen pausiert die Live-Ansicht"), 11); hint.FontFamily = TerminalFont; hint.TextWrapping = TextWrapping.Wrap; hint.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 154, 171, 163)); Grid.SetRow(hint, 1); body.Children.Add(hint);
        monitorOutput.FontFamily = TerminalFont;
        monitorOutput.Background = new SolidColorBrush(Colors.Transparent);
        var itemStyle = new Style(typeof(ListViewItem)); itemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch)); itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0))); itemStyle.Setters.Add(new Setter(Control.MinHeightProperty, 0d)); monitorOutput.ItemContainerStyle = itemStyle;
        ScrollViewer.SetHorizontalScrollBarVisibility(monitorOutput, ScrollBarVisibility.Disabled);
        monitorOutput.Loaded += (_, _) => AttachMonitorScroll();
        AutomationProperties.SetName(monitorOutput, Localizer.Text("Live-Protokoll der DHCP- und ARP-Kommunikation"));
        Grid.SetRow(monitorOutput, 2); body.Children.Add(monitorOutput);
        monitorPanel.Child = body; monitorPanel.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 13, 20, 17));
        monitorPanel.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 54, 72, 63)); monitorPanel.BorderThickness = new Thickness(1); monitorPanel.CornerRadius = new CornerRadius(8); monitorPanel.Margin = new Thickness(0, 18, 12, 6);
    }
    private void AttachMonitorScroll()
    {
        var viewer = FindScrollViewer(monitorOutput);
        if (viewer is null || viewer == monitorScroll) return;
        if (monitorScroll is not null) monitorScroll.ViewChanged -= MonitorViewChanged;
        monitorScroll = viewer; monitorScroll.ViewChanged += MonitorViewChanged;
    }
    private static ScrollViewer? FindScrollViewer(DependencyObject element)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            var child = VisualTreeHelper.GetChild(element, i);
            if (child is ScrollViewer viewer) return viewer;
            if (FindScrollViewer(child) is { } nested) return nested;
        }
        return null;
    }
    private void ResizeMonitor(double horizontalChange)
    {
        monitorWidth -= horizontalChange; ApplyMonitorWidth();
    }
    private void ApplyMonitorWidth()
    {
        monitorWidth = Math.Clamp(monitorWidth, 300, Math.Max(300, layout.ActualWidth - 930 - 8));
        if (Math.Abs(monitorColumn.Width.Value - monitorWidth) < 0.1) return;
        var wasUpdating = monitorUpdating; monitorUpdating = true;
        try
        {
            monitorColumn.Width = new GridLength(monitorWidth); layout.UpdateLayout();
            if (monitorFollowing && monitorButton.IsChecked == true) ScrollMonitorToEnd();
        }
        finally { monitorUpdating = wasUpdating; }
    }
    private void SetMonitorVisible()
    {
        var visible = monitorButton.IsChecked == true;
        var action = visible ? Localizer.Text("Netzwerk-Monitor ausblenden") : Localizer.Text("Netzwerk-Monitor einblenden");
        AutomationProperties.SetName(monitorButton, action); ToolTipService.SetToolTip(monitorButton, action);
        monitorPanel.Visibility = monitorResize.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        monitorDivider.Width = new GridLength(visible ? 8 : 0); layout.ColumnSpacing = 0;
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored })
        {
            if (visible)
            {
                var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
                var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96d;
                var available = Math.Max(AppWindow.Size.Width, area.X + area.Width - AppWindow.Position.X - (int)(12 * scale));
                var width = Math.Min(AppWindow.Size.Width + (int)((monitorWidth + 8) * scale), available);
                monitorAddedWidth = width - AppWindow.Size.Width;
                AppWindow.Resize(new SizeInt32(width, AppWindow.Size.Height));
            }
            else if (monitorAddedWidth > 0)
            {
                // Only remove the width we added. Never restore an old position or height.
                var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96d;
                var width = Math.Max((int)(640 * scale), AppWindow.Size.Width - monitorAddedWidth);
                AppWindow.Resize(new SizeInt32(width, AppWindow.Size.Height));
            }
        }
        if (visible) { monitorColumn.Width = new GridLength(monitorWidth); DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, ApplyMonitorWidth); ScrollMonitorToEnd(); }
        else { monitorColumn.Width = new GridLength(0); monitorAddedWidth = 0; }
        monitorManuallyPaused = false; monitorFollowing = true; UpdateMonitorState();
    }
    private void MonitorViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (monitorUpdating || monitorAutoScrolling || monitorButton.IsChecked != true || monitorScroll is null) return;
        if (monitorManuallyPaused) return;
        var following = monitorScroll.VerticalOffset >= monitorScroll.ScrollableHeight - 3;
        if (monitorFollowing != following) { monitorFollowing = following; UpdateMonitorState(); }
    }
    private void ToggleMonitorPlayback()
    {
        if (monitorFollowing)
        {
            monitorManuallyPaused = true; monitorFollowing = false; UpdateMonitorState();
        }
        else
        {
            monitorManuallyPaused = false; monitorFollowing = true; UpdateMonitorState();
            ScrollMonitorToEnd(); DrainNetworkMessages();
        }
    }
    private void UpdateMonitorState()
    {
        var color = monitorFollowing ? Windows.UI.Color.FromArgb(255, 80, 220, 130) : Windows.UI.Color.FromArgb(255, 255, 80, 80);
        monitorStateText.Text = monitorFollowing ? "Live" : "PAUSE";
        monitorStateIcon.Glyph = monitorFollowing ? "\uE768" : "\uE769";
        monitorStateText.Foreground = monitorStateIcon.Foreground = new SolidColorBrush(color);
        monitorState.Opacity = 1;
        AutomationProperties.SetName(monitorStateButton, monitorFollowing ? Localizer.Text("Live-Ausgabe pausieren") : Localizer.Text("Live-Ausgabe fortsetzen"));
        ToolTipService.SetToolTip(monitorStateButton, monitorFollowing ? Localizer.Text("Klicken zum Pausieren") : Localizer.Text("Klicken zum Fortsetzen"));
        monitorBlinkTimer.Stop();
        if (!monitorFollowing && monitorButton.IsChecked == true) monitorBlinkTimer.Start();
    }
    private void ScrollMonitorToEnd()
    {
        var wasUpdating = monitorUpdating; monitorUpdating = true;
        try
        {
            monitorOutput.UpdateLayout(); AttachMonitorScroll();
            if (monitorOutput.Items.Count > 0) monitorOutput.ScrollIntoView(monitorOutput.Items[^1]);
            monitorOutput.UpdateLayout();
            monitorScroll?.ChangeView(null, monitorScroll.ScrollableHeight, null, true);
            monitorOutput.UpdateLayout();
        }
        finally { monitorUpdating = wasUpdating; }
        if (!monitorAutoScrolling)
        {
            monitorAutoScrolling = true;
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                try
                {
                    monitorOutput.UpdateLayout(); AttachMonitorScroll();
                    if (monitorFollowing && monitorButton.IsChecked == true)
                    { monitorScroll?.ChangeView(null, monitorScroll.ScrollableHeight, null, true); monitorOutput.UpdateLayout(); }
                }
                finally { monitorAutoScrolling = false; }
            });
        }
    }
    private void QueueNetworkMessage(string message)
    {
        if (Interlocked.Increment(ref queuedMessages) > 4000)
        { Interlocked.Decrement(ref queuedMessages); Interlocked.Increment(ref droppedMessages); return; }
        networkMessages.Enqueue($"[{DateTimeOffset.Now:HH:mm:ss.fff}] {message}");
    }
    private void DrainNetworkMessages()
    {
        // Hold new messages in the bounded queue while the reader has scrolled up.
        if (!monitorFollowing && monitorButton.IsChecked == true) return;
        monitorUpdating = true;
        try
        {
            var dropped = Interlocked.Exchange(ref droppedMessages, 0);
            if (dropped > 0) AddMonitorLine((Localizer.IsGerman ? $"[{DateTimeOffset.Now:HH:mm:ss.fff}] MONITOR · {dropped} Meldungen wegen hoher Last ausgelassen." : $"[{DateTimeOffset.Now:HH:mm:ss.fff}] MONITOR · {dropped} messages dropped under high load."));
            var added = false;
            for (var i = 0; i < 250 && networkMessages.TryDequeue(out var line); i++)
            { Interlocked.Decrement(ref queuedMessages); AddMonitorLine(line); added = true; }
            if ((added || dropped > 0) && monitorFollowing && monitorButton.IsChecked == true && monitorOutput.Items.Count > 0)
                ScrollMonitorToEnd();
        }
        finally { monitorUpdating = false; }
    }
    private void AddMonitorLine(string line)
    {
        var warning = line.Contains("FEHLER", StringComparison.OrdinalIgnoreCase) || line.Contains("IGNORIERT", StringComparison.Ordinal) || line.Contains(Localizer.Text("KEINE ANTWORT"), StringComparison.Ordinal);
        var text = Ui.Text(line, 12); text.FontFamily = TerminalFont; text.TextWrapping = TextWrapping.Wrap; text.IsTextSelectionEnabled = true; text.Margin = new Thickness(0, 3, 0, 5);
        text.Foreground = new SolidColorBrush(warning ? Windows.UI.Color.FromArgb(255, 242, 190, 109) : Windows.UI.Color.FromArgb(255, 139, 227, 169));
        monitorOutput.Items.Add(text);
        while (monitorOutput.Items.Count > 2000) monitorOutput.Items.RemoveAt(0);
    }
    private void RemoveSelectedRange()
    {
        if (busy || savedRanges.SelectedItem is not SavedRange range) return;
        try
        {
            store.RemoveRange(range.Name); RefreshRanges();
            footer.Text = (Localizer.IsGerman ? $"Bereich „{range.Name}“ entfernt. Die aktuellen Einstellungen bleiben erhalten." : $"Range “{range.Name}” removed. Current settings are retained.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { ShowError(Localizer.Text("Bereich konnte nicht entfernt werden: ") + ex.Message); }
    }
    internal async Task VerifyMonitorStartupAsync(string reportPath)
    {
        if (monitorPanel.Visibility != Visibility.Collapsed || monitorColumn.Width.Value != 0)
            throw new InvalidOperationException("Monitor ist beim Start nicht vollständig ausgeblendet.");
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized })
            throw new InvalidOperationException("Fenster startet unerwartet maximiert.");
        monitorButton.IsChecked = true; SetMonitorVisible();
        if (await Task.Run(() => TransportSelfTest.Run(reportPath + ".network.txt", QueueNetworkMessage)) != 0)
            throw new InvalidOperationException("Loopback-Kommunikation für den Monitor fehlgeschlagen.");
        DrainNetworkMessages();
        var messages = string.Join(" ", monitorOutput.Items.OfType<TextBlock>().Select(t => t.Text));
        foreach (var type in new[] { "DISCOVER", "OFFER", "REQUEST", "ACK" })
            if (!messages.Contains(type, StringComparison.Ordinal)) throw new InvalidOperationException(type + " fehlt im Live-Monitor.");
        if (monitorPanel.Visibility != Visibility.Visible) throw new InvalidOperationException("Monitor wurde nicht eingeblendet.");
        await Task.Delay(100);
        var widthBefore = monitorWidth; ResizeMonitor(-60);
        if (monitorWidth <= widthBefore) throw new InvalidOperationException("Monitorbreite lässt sich nicht vergrößern.");
        for (var i = 0; i < 120; i++) QueueNetworkMessage($"LOOPBACK-TEST · Scrollprüfung {i:D3} · Monitor-Ausgabe zum Nachlesen.");
        DrainNetworkMessages(); monitorOutput.UpdateLayout();
        await Task.Delay(150); AttachMonitorScroll();
        if (monitorScroll is null || monitorScroll.ScrollableHeight <= 0) throw new InvalidOperationException("Monitor ist nicht scrollbar.");
        monitorScroll.ChangeView(null, 0, null, true);
        await Task.Delay(150);
        if (monitorFollowing || monitorStateText.Text != "PAUSE" || !monitorBlinkTimer.IsRunning)
            throw new InvalidOperationException("Hochscrollen aktiviert den blinkenden PAUSE-Status nicht.");
        var countBeforePause = monitorOutput.Items.Count;
        QueueNetworkMessage("LOOPBACK-TEST · NACH-PAUSE-MARKER"); DrainNetworkMessages();
        if (monitorOutput.Items.Count != countBeforePause) throw new InvalidOperationException("Die Ausgabe läuft trotz Lesepause weiter.");
        monitorStateButton.Focus(FocusState.Programmatic);
        var opacity = monitorState.Opacity; await Task.Delay(550);
        if (monitorState.Opacity == opacity) throw new InvalidOperationException("PAUSE-Anzeige blinkt nicht.");
        monitorScroll.ChangeView(null, monitorScroll.ScrollableHeight, null, true);
        await Task.Delay(350); DrainNetworkMessages();
        if (!monitorFollowing || monitorStateText.Text != "Live" || monitorBlinkTimer.IsRunning
            || !monitorOutput.Items.OfType<TextBlock>().Any(t => t.Text.Contains("NACH-PAUSE-MARKER", StringComparison.Ordinal)))
            throw new InvalidOperationException("Live-Ausgabe wird am unteren Ende nicht fortgesetzt.");
        ToggleMonitorPlayback();
        if (monitorFollowing || !monitorManuallyPaused || monitorStateText.Text != "PAUSE") throw new InvalidOperationException("Live-Klick pausiert nicht.");
        monitorScroll.ChangeView(null, 0, null, true); monitorScroll.ChangeView(null, monitorScroll.ScrollableHeight, null, true);
        await Task.Delay(150);
        if (monitorFollowing) throw new InvalidOperationException("Manuelle Pause endet unerwartet durch Scrollen.");
        QueueNetworkMessage("LOOPBACK-TEST · KLICK-FORTSETZUNG"); DrainNetworkMessages();
        ToggleMonitorPlayback(); await Task.Delay(150);
        if (!monitorFollowing || monitorManuallyPaused || monitorStateText.Text != "Live" || !monitorOutput.Items.OfType<TextBlock>().Any(t => t.Text.Contains("KLICK-FORTSETZUNG", StringComparison.Ordinal)))
            throw new InvalidOperationException("PAUSE-Klick setzt die Ausgabe nicht fort.");
        if (monitorOutput.Items.OfType<TextBlock>().Any(t => t.FontFamily.Source != TerminalFont.Source))
            throw new InvalidOperationException("Monitor verwendet keine durchgängige Monospace-Schrift.");
        AppWindow.Move(new PointInt32(AppWindow.Position.X + 8, AppWindow.Position.Y + 8));
        var positionBeforeHide = AppWindow.Position; var heightBeforeHide = AppWindow.Size.Height;
        monitorButton.IsChecked = false; SetMonitorVisible();
        if (AppWindow.Position.X != positionBeforeHide.X || AppWindow.Position.Y != positionBeforeHide.Y || AppWindow.Size.Height != heightBeforeHide)
            throw new InvalidOperationException("Fensterposition oder Höhe springt beim Ausblenden des Monitors.");
        if (monitorPanel.Visibility != Visibility.Collapsed || monitorColumn.Width.Value != 0 || monitorDivider.Width.Value != 0 || layout.ColumnSpacing != 0)
            throw new InvalidOperationException("Monitor wurde nicht vollständig ausgeblendet.");
    }
    private void OpenAdapterSettings()
    {
        if (selected is null || busy || server.IsRunning) return;
        try
        {
            adapterDraft = null;
            try { adapterDraft = ReadConfiguration(); } catch (ArgumentException) { }
            editedAdapterId = selected.Id;
            AdapterSettings.Open(WinRT.Interop.WindowNative.GetWindowHandle(this), selected.Id);
            adapterSettingsOpened = true;
            LoadAdapters(adapterDraft, editedAdapterId);
            footer.Text = Localizer.Text("Windows-Netzwerkeinstellungen geöffnet. Schnittstellenwerte wurden aktualisiert.");
        }
        catch (Exception ex) when (ex is ArgumentException or System.ComponentModel.Win32Exception)
        { ShowError(Localizer.Text("Netzwerkeinstellungen konnten nicht geöffnet werden: ") + ex.Message); }
    }
    private void RenderOptions()
    {
        optionSummary.Children.Clear(); optionControls.Clear(); invalidOptions.Clear();
        optionSummary.Visibility = options.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (options.Count == 0) { UpdateOptionActions(); return; }
        var title = Ui.Text(Localizer.Text("Zusätzliche DHCP-Optionen"), 13); title.FontWeight = FontWeights.SemiBold; optionSummary.Children.Add(title);
        foreach (var option in options.ToArray())
        {
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var editor = new TextBox { Header = $"{option.Code} · {option.Name}" + (option.Enabled ? "" : Localizer.Text(" · deaktiviert")), Text = option.Value,
                AcceptsReturn = option.Format == "Routen", TextWrapping = TextWrapping.Wrap, MaxHeight = 100, IsSpellCheckEnabled = false, IsTextPredictionEnabled = false };
            AutomationProperties.SetName(editor, (Localizer.IsGerman ? $"Wert für Option {option.Code} · {option.Name}" : $"Value for option {option.Code} · {option.Name}"));
            ToolTipService.SetToolTip(editor, Localizer.Text(option.Format));
            var error = Ui.Text("", 12); error.TextWrapping = TextWrapping.Wrap; error.Foreground = Ui.Brush("SystemFillColorCriticalBrush"); error.Visibility = Visibility.Collapsed;
            var group = new StackPanel { Spacing = 4 }; group.Children.Add(editor); group.Children.Add(error); row.Children.Add(group);
            void UpdateValue()
            {
                var index = options.FindIndex(o => o.Code == option.Code); if (index < 0) return;
                var updated = options[index] with { Value = editor.Text.Trim() }; options[index] = updated;
                try { OptionCodec.Encode(updated); invalidOptions.Remove(option.Code); error.Visibility = Visibility.Collapsed; }
                catch (ArgumentException ex) { invalidOptions.Add(option.Code); error.Text = ex.Message; error.Visibility = Visibility.Visible; }
                UpdateOptionActions();
            }
            editor.TextChanged += (_, _) => UpdateValue(); editor.LostFocus += (_, _) => SaveDraft();
            var remove = new Button { Content = new FontIcon { Glyph = "\uE711", FontSize = 14 }, VerticalAlignment = VerticalAlignment.Bottom };
            Ui.SizeIconButton(remove);
            AutomationProperties.SetName(remove, (Localizer.IsGerman ? $"Option {option.Code} entfernen" : $"Option {option.Code} remove"));
            ToolTipService.SetToolTip(remove, Localizer.Text("Option entfernen"));
            remove.Click += (_, _) => { if (busy || server.IsRunning) return; options.RemoveAll(o => o.Code == option.Code); RenderOptions(); SaveDraft(); };
            Grid.SetColumn(remove, 1); row.Children.Add(remove); optionSummary.Children.Add(row);
            optionControls.Add(editor); optionControls.Add(remove);
            editor.IsEnabled = remove.IsEnabled = !server.IsRunning && !busy;
            UpdateValue();
        }
    }
    private void UpdateOptionActions()
    {
        toggle.IsEnabled = !Program.DemoMode && !busy && selected is not null && startupError is null && (server.IsRunning || invalidOptions.Count == 0);
        saveRange.IsEnabled = !busy && selected is not null && invalidOptions.Count == 0;
    }
    private void EnableCopy(TextBlock text)
    {
        ToolTipService.SetToolTip(text, Localizer.Text("Rechtsklick zum Kopieren"));
        text.RightTapped += (_, e) =>
        {
            e.Handled = true;
            try
            {
                var data = new DataPackage(); data.SetText(text.Text); Clipboard.SetContent(data); Clipboard.Flush();
                footer.Text = Localizer.Text("Kopiert: ") + text.Text.Replace('\n', ' ');
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
            { ShowError(Localizer.Text("Kopieren nicht möglich: ") + ex.Message); }
        };
    }
    private void RemoveSingleLease(LeaseRecord lease)
    {
        if (busy) return;
        try
        {
            var removed = server.RemoveLease(lease); UpdateLeases();
            footer.Text = removed ? (Localizer.IsGerman ? $"Lease {lease.Address} · {lease.Mac} gelöscht." : $"Lease {lease.Address} · {lease.Mac} deleted.") : Localizer.Text("Der Lease wurde inzwischen geändert oder entfernt. Die Tabelle wurde aktualisiert.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ShowError(Localizer.Text("Lease konnte nicht gelöscht werden: ") + ex.Message); }
    }
    private void ResetLeases()
    {
        if (busy || server.IsRunning) return;
        try
        {
            store.SaveLeases([]); store.LoadConfiguration(); startupError = null; message.IsOpen = false; UpdateLeases(); SetRunning();
            footer.Text = Localizer.Text("Alle gespeicherten Leases gelöscht. Clients behalten ihre Adresse bis zur Erneuerung oder zum Ablauf.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { ShowError(Localizer.Text("Lease Reset fehlgeschlagen: ") + ex.Message); }
    }
    private void RefreshRanges(string? name = null)
    {
        loadingRanges = true;
        try
        {
            var ranges = store.LoadRanges(); savedRanges.Items.Clear();
            foreach (var range in ranges) savedRanges.Items.Add(range);
            savedRanges.SelectedItem = ranges.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { ShowError(Localizer.Text("Bereiche konnten nicht geladen werden: ") + ex.Message); }
        finally { loadingRanges = false; removeRange.IsEnabled = !busy && savedRanges.SelectedItem is SavedRange; }
    }
    private void LoadRange()
    {
        removeRange.IsEnabled = !busy && savedRanges.SelectedItem is SavedRange;
        if (loadingRanges || busy || server.IsRunning || savedRanges.SelectedItem is not SavedRange range) return;
        var configuration = range.Configuration;
        var match = adapters.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag is AdapterInfo a && a.Id == configuration.AdapterId && a.Address == configuration.ServerAddress && a.Mask == configuration.Mask);
        if (match?.Tag is not AdapterInfo adapter) { ShowError(Localizer.Text("Die Netzwerkschnittstelle für diesen Bereich ist nicht verfügbar oder ihre IPv4-Konfiguration wurde geändert.")); return; }
        try
        {
            configuration.Validate(adapter); adapters.SelectedItem = match; Apply(configuration); store.SaveConfiguration(configuration);
            message.IsOpen = false; footer.Text = (Localizer.IsGerman ? $"Bereich „{range.Name}“ geladen." : $"Range “{range.Name}” loaded.");
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { ShowError(Localizer.Text("Bereich konnte nicht geladen werden: ") + ex.Message); }
    }
    private async Task SaveRangeAsync()
    {
        ServerConfiguration configuration;
        try { configuration = ReadConfiguration(); configuration.Validate(selected!); }
        catch (ArgumentException ex) { ShowError(ex.Message); return; }
        var name = new TextBox { Header = Localizer.Text("Name des Bereichs"), Text = (savedRanges.SelectedItem as SavedRange)?.Name ?? $"{configuration.StartAddress} – {configuration.EndAddress}", MinWidth = 380 };
        var explanation = Ui.Text(Localizer.Text("Ein vorhandener Bereich mit demselben Namen wird aktualisiert."), 12); explanation.TextWrapping = TextWrapping.Wrap;
        var error = new InfoBar { IsOpen = false, Severity = InfoBarSeverity.Error };
        var body = new StackPanel { Spacing = 12 }; body.Children.Add(name); body.Children.Add(explanation); body.Children.Add(error);
        var dialog = new ContentDialog { XamlRoot = root.XamlRoot, Title = Localizer.Text("Bereich speichern"), Content = body, PrimaryButtonText = Localizer.Text("Speichern"), CloseButtonText = Localizer.Text("Abbrechen"), DefaultButton = ContentDialogButton.Primary };
        dialog.PrimaryButtonClick += (_, e) =>
        {
            try { store.SaveRange(name.Text, configuration); store.SaveConfiguration(configuration); }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            { e.Cancel = true; error.Message = ex.Message; error.IsOpen = true; }
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        { RefreshRanges(name.Text.Trim()); footer.Text = (Localizer.IsGerman ? $"Bereich „{name.Text.Trim()}“ gespeichert." : $"Range “{name.Text.Trim()}” saved."); }
    }
    private void ArrangeFields(double width)
    {
        var columns = width < 500 ? 1 : width < 930 ? 2 : 3; if (columns == fieldColumns) return; fieldColumns = columns;
        fields.Children.Clear(); fields.ColumnDefinitions.Clear(); fields.RowDefinitions.Clear();
        for (var i = 0; i < columns; i++) fields.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        for (var i = 0; i < (fieldElements.Count + columns - 1) / columns; i++) fields.RowDefinitions.Add(new() { Height = GridLength.Auto });
        for (var i = 0; i < fieldElements.Count; i++) { Grid.SetRow(fieldElements[i], i / columns); Grid.SetColumn(fieldElements[i], i % columns); fields.Children.Add(fieldElements[i]); }
    }
    private void LoadAdapters(ServerConfiguration? saved, string? preferredAdapterId = null)
    {
        if (server.IsRunning) return; var previousKey = selected?.Key; adapters.Items.Clear();
        try
        {
            foreach (var adapter in Program.DemoMode ? new[] { DemoData.Adapter } : AdapterInfo.Enumerate()) adapters.Items.Add(new ComboBoxItem { Content = adapter.DisplayName, Tag = adapter });
            var match = adapters.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag is AdapterInfo a && (saved is not null ? a.Id == saved.AdapterId && a.Address == saved.ServerAddress && a.Mask == saved.Mask : a.Key == previousKey));
            adapters.SelectedItem = match ?? adapters.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag is AdapterInfo a && a.Id == preferredAdapterId) ?? adapters.Items.OfType<ComboBoxItem>().FirstOrDefault(); if (saved is not null && match is not null) Apply(saved);
            if (adapters.Items.Count == 0) { selected = null; footer.Text = Localizer.Text("Keine aktive IPv4-LAN-Karte gefunden. Netzwerk verbinden und aktualisieren."); }
        }
        catch (Exception ex) when (ex is System.Net.NetworkInformation.NetworkInformationException or ArgumentException) { ShowError(ex.Message); }
        SetRunning();
    }
    private void SelectAdapter()
    {
        selected = (adapters.SelectedItem as ComboBoxItem)?.Tag as AdapterInfo; if (selected is null) return;
        var defaults = ServerConfiguration.ForAdapter(selected) with { Options = options.ToList() }; Apply(defaults);
        adapterDetails.Text = $"{selected.Address} · {selected.Mask}" + (selected.DhcpEnabled ? Localizer.Text(" · Adapteradresse per DHCP") : Localizer.Text(" · Statische Adapteradresse"));
        footer.Text = (Localizer.IsGerman ? $"Werte für {selected.Name} vorbelegt. Der Server ist gestoppt." : $"Defaults for {selected.Name} filled in. The server is stopped."); UpdateLeases(); SetRunning();
    }
    private void Apply(ServerConfiguration configuration)
    {
        inputs["start"].Text = configuration.StartAddress; inputs["end"].Text = configuration.EndAddress; inputs["mask"].Text = configuration.Mask; inputs["gateway"].Text = configuration.Gateway; inputs["dns1"].Text = configuration.PrimaryDns; inputs["dns2"].Text = configuration.SecondaryDns;
        var seconds = configuration.LeaseSeconds;
        if (seconds % 86400 == 0 && seconds != 0) { duration.Value = seconds / 86400; unit.SelectedIndex = 3; }
        else if (seconds % 3600 == 0 && seconds != 0) { duration.Value = seconds / 3600; unit.SelectedIndex = 2; }
        else if (seconds % 60 == 0 && seconds != 0) { duration.Value = seconds / 60; unit.SelectedIndex = 1; }
        else { duration.Value = seconds; unit.SelectedIndex = 0; }
        options.Clear(); options.AddRange(configuration.Options); RenderOptions();
    }
    private ServerConfiguration ReadConfiguration()
    {
        if (selected is null) throw new ArgumentException(Localizer.Text("Bitte eine LAN-Karte auswählen."));
        if (invalidOptions.Count != 0) throw new ArgumentException(Localizer.Text("Bitte die markierten Werte der Zusatzoptionen korrigieren."));
        var multiplier = new[] { 1d, 60, 3600, 86400 }[Math.Max(0, unit.SelectedIndex)]; var seconds = duration.Value * multiplier;
        if (!double.IsFinite(seconds) || duration.Value != Math.Floor(duration.Value) || seconds is < 1 or > 4294967294) throw new ArgumentException(Localizer.Text("Die Lease-Dauer muss eine positive ganze Zahl sein und darf 4.294.967.294 Sekunden nicht überschreiten."));
        return new() { AdapterId = selected.Id, ServerAddress = selected.Address, StartAddress = inputs["start"].Text.Trim(), EndAddress = inputs["end"].Text.Trim(), Mask = inputs["mask"].Text.Trim(), Gateway = inputs["gateway"].Text.Trim(), PrimaryDns = inputs["dns1"].Text.Trim(), SecondaryDns = inputs["dns2"].Text.Trim(), LeaseSeconds = (uint)seconds, Options = options.ToList() };
    }
    private async Task ToggleAsync()
    {
        if (Program.DemoMode) return;
        if (busy) return; busy = true; SetRunning(); message.IsOpen = false;
        try
        {
            if (server.IsRunning) { await server.StopAsync(); footer.Text = Localizer.Text("Server gestoppt. Bereits vergebene Leases bleiben bis zu ihrem Ablauf gültig."); }
            else { if (startupError is not null) throw new InvalidOperationException(startupError); var configuration = ReadConfiguration(); configuration.Validate(selected!); await Task.Run(() => server.Start(selected!, configuration)); UpdateLeases(); }
        }
        catch (SocketException ex) { ShowError(ex.SocketErrorCode == SocketError.AddressAlreadyInUse ? Localizer.Text("UDP-Port 67 ist bereits belegt. Einen anderen DHCP-Server oder die Windows-Internetfreigabe prüfen.") : (Localizer.IsGerman ? $"Netzwerkfehler: {ex.Message} (Windows-Code {ex.NativeErrorCode}). Firewallfreigabe für diese EXE und UDP 67/68 prüfen." : $"Network error: {ex.Message} (Windows error {ex.NativeErrorCode}). Check firewall access for this executable and UDP 67/68.")); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException) { ShowError(ex.Message); }
        finally { busy = false; SetRunning(); }
    }
    private void SetRunning()
    {
        var running = server.IsRunning; ((FontIcon)toggle.Content).Glyph = running ? "\uE71A" : "\uE768";
        var action = busy ? Localizer.Text("Bitte warten…") : running ? Localizer.Text("Server stoppen") : Localizer.Text("Server starten");
        AutomationProperties.SetName(toggle, action); ToolTipService.SetToolTip(toggle, action); toggle.IsEnabled = !Program.DemoMode && !busy && selected is not null && startupError is null; status.Text = running ? Localizer.Text("Läuft") : Localizer.Text("Gestoppt");
        statusDot.Fill = new SolidColorBrush(running ? Windows.UI.Color.FromArgb(255, 46, 204, 113) : Windows.UI.Color.FromArgb(255, 239, 68, 68));
        adapters.IsEnabled = !running && !busy; refresh.IsEnabled = !running && !busy; foreach (var field in inputs.Values) field.IsEnabled = !running && !busy;
        duration.IsEnabled = unit.IsEnabled = !running && !busy; addOption.IsEnabled = !busy;
        resetLeases.IsEnabled = !running && !busy; savedRanges.IsEnabled = !running && !busy; saveRange.IsEnabled = !busy && selected is not null;
        adapterSettings.IsEnabled = !Program.DemoMode && !running && !busy && selected is not null;
        foreach (var button in leaseRemoveButtons) button.IsEnabled = !busy;
        foreach (var control in optionControls) control.IsEnabled = !running && !busy;
        removeRange.IsEnabled = !busy && savedRanges.SelectedItem is SavedRange;
        UpdateOptionActions();
    }
    private void SaveDraft()
    {
        if (!initialized || selected is null || busy) return;
        try { var configuration = ReadConfiguration(); configuration.Validate(selected); store.SaveConfiguration(configuration); }
        catch (ArgumentException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ShowError(Localizer.Text("Einstellungen konnten nicht gespeichert werden: ") + ex.Message); }
    }
    private void UpdateLeases()
    {
        IReadOnlyList<LeaseRecord> records;
        try { records = server.IsRunning ? server.Leases : store.LoadLeases().Where(l => selected is not null && l.Scope == $"{Ipv4.Network(selected.Address, selected.Mask)}/{selected.Mask}" && l.LastConfirmedUtc != default).OrderByDescending(l => l.LastConfirmedUtc).ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ShowError(ex.Message); return; }
        table.Items.Clear(); remainingLabels.Clear(); leaseRemoveButtons.Clear(); if (tableRows.Count > 1) tableRows.RemoveRange(1, tableRows.Count - 1);
        foreach (var lease in records)
        {
            var row = RowGrid(); row.Padding = new Thickness(0, 4, 0, 4);
            var values = new[] { lease.LastConfirmedUtc.ToLocalTime().ToString("dd.MM.yyyy\nHH:mm:ss", CultureInfo.CurrentCulture), lease.Address, lease.Mac, string.IsNullOrEmpty(lease.ClientId) ? "—" : lease.ClientId, string.IsNullOrWhiteSpace(lease.Hostname) ? "—" : lease.Hostname, oui.Find(lease.Mac), "" }; TextBlock? remaining = null;
            for (var i = 0; i < values.Length; i++) { var text = Ui.Text(values[i], i == 0 ? 12 : 13); text.Margin = new(12, 10, 8, 10); text.VerticalAlignment = VerticalAlignment.Center; text.TextWrapping = i is 3 or 4 or 5 ? TextWrapping.Wrap : TextWrapping.NoWrap; if (i is 2 or 3) text.FontFamily = new FontFamily("Consolas"); EnableCopy(text); Grid.SetColumn(text, i); row.Children.Add(text); if (i == 6) remaining = text; }
            row.Children.Remove(remaining!);
            var progress = new ProgressBar { Minimum = 0, Maximum = 100, Width = 64, Height = 5, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 145, 160)) };
            var leaseCell = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(12, 10, 8, 10), VerticalAlignment = VerticalAlignment.Center };
            remaining!.Margin = new Thickness(0); leaseCell.Children.Add(remaining); leaseCell.Children.Add(progress);
            var removeLease = new Button { Content = new FontIcon { Glyph = "\uE711", FontSize = 12 }, Padding = new Thickness(6), MinWidth = 28, MinHeight = 28, VerticalAlignment = VerticalAlignment.Center, IsEnabled = !busy };
            Ui.SizeIconButton(removeLease);
            AutomationProperties.SetName(removeLease, (Localizer.IsGerman ? $"Lease {lease.Address} · {lease.Mac} löschen" : $"Lease {lease.Address} · {lease.Mac} delete")); ToolTipService.SetToolTip(removeLease, Localizer.Text("Nur diesen Lease löschen"));
            removeLease.Click += (_, _) => RemoveSingleLease(lease); leaseRemoveButtons.Add(removeLease); leaseCell.Children.Add(removeLease);
            Grid.SetColumn(leaseCell, 6); row.Children.Add(leaseCell);
            remainingLabels.Add((lease, remaining, progress));
            table.Items.Add(new Border { Child = row, BorderThickness = new Thickness(0, table.Items.Count > 0 ? 1 : 0, 0, 0), BorderBrush = Ui.Brush("DividerStrokeColorDefaultBrush") });
        }
        empty.Visibility = records.Count == 0 ? Visibility.Visible : Visibility.Collapsed; UpdateRemaining();
    }
    private void UpdateRemaining()
    {
        var now = DateTimeOffset.UtcNow; var active = 0;
        foreach (var (lease, label, progress) in remainingLabels) { var span = lease.ExpiresUtc - now; label.Text = lease.State == "Released" ? Localizer.Text("Freigegeben") : lease.State == "Declined" ? Localizer.Text("Adresskonflikt") : span <= TimeSpan.Zero ? Localizer.Text("Abgelaufen") : (span.Days > 0 ? $"{span.Days} d " : "") + span.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture); var total = (lease.ExpiresUtc - lease.LastConfirmedUtc).TotalSeconds;
            progress.Value = lease.State == "Active" && total > 0 ? Math.Clamp(span.TotalSeconds / total * 100, 0, 100) : 0;
            AutomationProperties.SetName(progress, Localizer.Text("Verbleibende Leasezeit: ") + label.Text);
            if (lease.State == "Active" && span > TimeSpan.Zero) active++; }
        count.Text = active == 1 ? Localizer.Text("1 aktiver Lease") : (Localizer.IsGerman ? $"{active} aktive Leases" : $"{active} active leases");
    }
    private Grid RowGrid()
    {
        var grid = new Grid();
        foreach (var width in columnWidths) grid.ColumnDefinitions.Add(new() { Width = width, MinWidth = 80 });
        tableRows.Add(grid); return grid;
    }
    private void ResizeColumns(Grid header, int column, double delta)
    {
        var left = header.ColumnDefinitions[column].ActualWidth;
        var right = header.ColumnDefinitions[column + 1].ActualWidth;
        delta = Math.Clamp(delta, 80 - left, right - 80);
        columnWidths[column] = new(left + delta); columnWidths[column + 1] = new(right - delta);
        foreach (var row in tableRows)
        {
            row.ColumnDefinitions[column].Width = columnWidths[column];
            row.ColumnDefinitions[column + 1].Width = columnWidths[column + 1];
        }
    }
    private void ShowError(string text) { message.Severity = InfoBarSeverity.Error; message.Title = ""; message.Message = text; message.IsOpen = true; }
    private void ShowInfo(string text) { message.Severity = InfoBarSeverity.Informational; message.Message = text; message.IsOpen = true; }
}

internal static class Ui
{
    public static void SizeIconButton(ButtonBase button)
    {
        button.Width = button.MinWidth = 40; button.Height = button.MinHeight = 32;
        button.Padding = new Thickness(0); button.HorizontalContentAlignment = HorizontalAlignment.Center; button.VerticalContentAlignment = VerticalAlignment.Center;
        if (button.Content is FontIcon icon) icon.FontSize = 16;
    }
    public static TextBlock Text(string value, double size = 14) => new() { Text = value, FontSize = size };
    public static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
    public static StackPanel Field(string label, FrameworkElement input) { var panel = new StackPanel { Spacing = 6 }; panel.Children.Add(Text(label, 13)); panel.Children.Add(input); return panel; }
}

internal sealed class HorizontalResizeThumb : ContentControl
{
    public event DragDeltaEventHandler? DragDelta;
    public HorizontalResizeThumb()
    {
        ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast);
        HorizontalContentAlignment = HorizontalAlignment.Stretch; VerticalContentAlignment = VerticalAlignment.Stretch;
        var thumb = new Thumb { Background = new SolidColorBrush(Colors.Transparent), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        thumb.DragDelta += (_, e) => DragDelta?.Invoke(this, e); Content = thumb;
    }
}
