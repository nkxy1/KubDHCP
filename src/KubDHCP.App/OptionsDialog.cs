using KubDHCP.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace KubDHCP.App;

internal sealed partial class OptionsDialog : ContentDialog
{
    private readonly List<ExtraOption> options;
    private readonly List<OptionDefinition> definitions = OptionDefinition.Load();
    private readonly StackPanel rows = new() { Spacing = 10 };
    private readonly ComboBox catalog = new() { HorizontalAlignment = HorizontalAlignment.Stretch, MaxDropDownHeight = 300, PlaceholderText = Localizer.Text("IANA-Option auswählen") };
    private readonly NumberBox code = new() { Minimum = 1, Maximum = 254, Value = 15, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
    private readonly ComboBox format = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox value = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 64, MaxHeight = 150, IsSpellCheckEnabled = false };
    private readonly CheckBox always = new() { Content = Localizer.Text("Auch ohne Client-Anfrage senden") };
    private readonly TextBlock help = Ui.Text("", 12);
    private readonly TextBlock reference = Ui.Text("", 12);
    private readonly InfoBar error = new() { IsOpen = false, Severity = InfoBarSeverity.Error };
    private readonly Button save = new() { Content = Localizer.Text("Hinzufügen"), HorizontalAlignment = HorizontalAlignment.Right };
    private readonly bool readOnly;
    private int editing = -1;
    private bool updating;

    public OptionsDialog(XamlRoot xamlRoot, List<ExtraOption> options, bool readOnly)
    {
        XamlRoot = xamlRoot; Title = Localizer.Text("Weitere DHCP-Optionen"); CloseButtonText = Localizer.Text("Fertig"); DefaultButton = ContentDialogButton.Close;
        this.options = options; this.readOnly = readOnly; Resources["ContentDialogMaxWidth"] = 820d;
        var body = new StackPanel { Spacing = 16, MinWidth = 450, MaxWidth = 760 };
        var introduction = Ui.Text(readOnly ? Localizer.Text("Der Server läuft. Die Optionen sind bis zum Stoppen schreibgeschützt.") : Localizer.Text("Zusätzliche Werte für DHCP-Antworten. Standardmäßig auf Anfrage des Clients."), 13); introduction.TextWrapping = TextWrapping.Wrap; body.Children.Add(introduction);
        body.Children.Add(rows); RenderRows();
        var title = Ui.Text(Localizer.Text("Option hinzufügen"), 16); title.FontWeight = FontWeights.SemiBold; body.Children.Add(title);
        foreach (var definition in definitions) catalog.Items.Add(new ComboBoxItem { Content = definition.Display + (definition.Managed ? Localizer.Text(" · automatisch / clientseitig") : ""), Tag = definition });
        catalog.Items.Add(new ComboBoxItem { Content = Localizer.Text("Eigener Optionscode / Rohdaten"), Tag = null });
        body.Children.Add(Ui.Field(Localizer.Text("IANA-Optionskatalog"), catalog));
        var formatRow = new Grid { ColumnSpacing = 12 }; formatRow.ColumnDefinitions.Add(new() { Width = new GridLength(110) }); formatRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        formatRow.Children.Add(Ui.Field(Localizer.Text("Optionscode"), code)); var formatField = Ui.Field(Localizer.Text("Datenformat"), format); Grid.SetColumn(formatField, 1); formatRow.Children.Add(formatField); body.Children.Add(formatRow);
        foreach (var label in OptionCodec.Formats) format.Items.Add(new ComboBoxItem { Content = Localizer.Text(label), Tag = label }); SelectFormat("Text (ASCII)");
        body.Children.Add(Ui.Field(Localizer.Text("Wert"), value)); help.TextWrapping = reference.TextWrapping = TextWrapping.Wrap; body.Children.Add(help); body.Children.Add(reference); body.Children.Add(always); body.Children.Add(error); body.Children.Add(save);
        var managed = Ui.Text(Localizer.Text("Subnetzmaske, Gateway, DNS und Lease-Dauer bleiben im Hauptfenster. Protokolloptionen werden automatisch erzeugt."), 12); managed.TextWrapping = TextWrapping.Wrap; body.Children.Add(managed);
        Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = Math.Max(300, xamlRoot.Size.Height - 210) };
        AutomationProperties.SetName(catalog, Localizer.Text("IANA-Optionskatalog")); AutomationProperties.SetName(code, Localizer.Text("Optionscode")); AutomationProperties.SetName(format, Localizer.Text("Datenformat")); AutomationProperties.SetName(value, Localizer.Text("Optionswert"));
        save.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        foreach (var control in new Control[] { catalog, code, format, value, always, save }) control.IsEnabled = !readOnly;
        catalog.SelectionChanged += (_, _) =>
        {
            if (updating) return;
            if ((catalog.SelectedItem as ComboBoxItem)?.Tag is OptionDefinition definition)
            {
                updating = true; code.Value = definition.Code; SelectFormat(definition.Format); reference.Text = definition.Description + " " + definition.Reference; updating = false;
            }
            UpdateHelp();
        };
        code.ValueChanged += (_, _) =>
        {
            if (updating) return; updating = true;
            var match = catalog.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag is OptionDefinition d && d.Code == code.Value);
            catalog.SelectedItem = match ?? catalog.Items[^1];
            reference.Text = (match?.Tag as OptionDefinition)?.Reference ?? Localizer.Text("Eigener Optionscode. Kodierung und Bedeutung anhand der Gerätespezifikation prüfen."); updating = false; UpdateHelp();
        };
        format.SelectionChanged += (_, _) => UpdateHelp();
        save.Click += (_, _) => Save();
        catalog.SelectedItem = catalog.Items.OfType<ComboBoxItem>().First(item => item.Tag is OptionDefinition d && d.Code == 15); UpdateHelp();
    }
    private string SelectedFormat => (format.SelectedItem as ComboBoxItem)?.Tag as string ?? "Hex";
    private void SelectFormat(string key) => format.SelectedItem = format.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag == key);
    private void UpdateHelp()
    {
        help.Text = SelectedFormat switch
        {
            "IPv4-Liste" => Localizer.Text("IPv4-Adressen durch Komma trennen, z. B. 192.168.50.10, 192.168.50.11."),
            "Hex" => Localizer.Text("Hexbytes, z. B. 01 04 C0 A8 32 FE. Strukturierte Herstellerdaten und Unteroptionen vollständig kodieren."),
            "Domainliste" => Localizer.Text("Domainnamen durch Komma trennen; die Anwendung kodiert DNS-Labels gemäß Option 119."),
            "Routen" => Localizer.Text("Eine Route je Zeile: 10.20.0.0/16=192.168.50.254. Eine Standardroute: 0.0.0.0/0=192.168.50.254."),
            "Boolean" => Localizer.Text("true/false, ja/nein oder 1/0."),
            "UInt8" => Localizer.Text("Ganzzahl von 0 bis 255."), "UInt16" => Localizer.Text("Ganzzahl von 0 bis 65.535 (Network Byte Order)."), "UInt32" => Localizer.Text("Ganzzahl von 0 bis 4.294.967.295 (Network Byte Order)."),
            "IPv4" => Localizer.Text("Eine IPv4-Adresse mit vier dezimalen Oktetten."), _ => Localizer.Text("ASCII-Text, z. B. commissioning.example oder firmware.bin.")
        };
        if (double.IsFinite(code.Value) && code.Value is >= 0 and <= 255 && OptionCodec.ManagedCodes.Contains((byte)code.Value)) help.Text = Localizer.Text("Dieser Code ist protokollgesteuert, clientseitig oder wird im Hauptformular festgelegt. Er kann hier nicht zusätzlich gesendet werden.");
    }
    private void Save()
    {
        try
        {
            if (!double.IsFinite(code.Value) || code.Value != Math.Floor(code.Value) || code.Value is < 1 or > 254) throw new ArgumentException(Localizer.Text("Bitte einen Optionscode zwischen 1 und 254 eingeben."));
            var optionCode = (byte)code.Value;
            if (options.Where((_, index) => index != editing).Any(o => o.Code == optionCode)) throw new ArgumentException(Localizer.Text("Dieser Optionscode ist bereits vorhanden. Den bestehenden Eintrag bearbeiten."));
            var definition = definitions.FirstOrDefault(d => d.Code == optionCode);
            var option = new ExtraOption { Code = optionCode, Name = definition?.Name ?? $"Option {optionCode}", Format = SelectedFormat, Value = value.Text.Trim(), AlwaysSend = always.IsChecked == true };
            OptionCodec.Encode(option);
            if (editing >= 0) options[editing] = option; else options.Add(option);
            editing = -1; value.Text = ""; save.Content = Localizer.Text("Hinzufügen"); error.IsOpen = false; RenderRows();
        }
        catch (ArgumentException ex) { error.Message = ex.Message; error.IsOpen = true; }
    }
    private void RenderRows()
    {
        rows.Children.Clear();
        if (options.Count == 0) { rows.Children.Add(Ui.Text(Localizer.Text("Keine zusätzlichen Optionen konfiguriert."), 13)); return; }
        for (var index = 0; index < options.Count; index++)
        {
            var current = index; var option = options[index]; var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var enabled = new CheckBox { IsChecked = option.Enabled, IsEnabled = !readOnly, MinWidth = 28 }; AutomationProperties.SetName(enabled, (Localizer.IsGerman ? $"Option {option.Code} aktivieren" : $"Option {option.Code} enable")); enabled.Click += (_, _) => options[current] = options[current] with { Enabled = enabled.IsChecked == true }; row.Children.Add(enabled);
            var details = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; var name = Ui.Text($"{option.Code} · {option.Name}", 13); name.FontWeight = FontWeights.SemiBold; details.Children.Add(name); var text = Ui.Text(option.Value, 12); text.TextWrapping = TextWrapping.Wrap; details.Children.Add(text); Grid.SetColumn(details, 1); row.Children.Add(details);
            var edit = new Button { Content = Localizer.Text("Bearbeiten"), IsEnabled = !readOnly, VerticalAlignment = VerticalAlignment.Center }; edit.Click += (_, _) => { editing = current; code.Value = option.Code; SelectFormat(option.Format); value.Text = option.Value; always.IsChecked = option.AlwaysSend; save.Content = Localizer.Text("Übernehmen"); }; Grid.SetColumn(edit, 2); row.Children.Add(edit);
            var remove = new Button { Content = new FontIcon { Glyph = "\uE74D", FontSize = 14 }, IsEnabled = !readOnly, VerticalAlignment = VerticalAlignment.Center }; Ui.SizeIconButton(remove); AutomationProperties.SetName(remove, (Localizer.IsGerman ? $"Option {option.Code} entfernen" : $"Option {option.Code} remove")); remove.Click += (_, _) => { options.RemoveAt(current); editing = -1; save.Content = Localizer.Text("Hinzufügen"); RenderRows(); }; Grid.SetColumn(remove, 3); row.Children.Add(remove); rows.Children.Add(row);
        }
    }
}
