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
    private readonly ComboBox catalog = new() { HorizontalAlignment = HorizontalAlignment.Stretch, MaxDropDownHeight = 300, PlaceholderText = "IANA-Option auswählen" };
    private readonly NumberBox code = new() { Minimum = 1, Maximum = 254, Value = 15, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
    private readonly ComboBox format = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox value = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 64, MaxHeight = 150, IsSpellCheckEnabled = false };
    private readonly CheckBox always = new() { Content = "Auch ohne Client-Anfrage senden" };
    private readonly TextBlock help = Ui.Text("", 12);
    private readonly TextBlock reference = Ui.Text("", 12);
    private readonly InfoBar error = new() { IsOpen = false, Severity = InfoBarSeverity.Error };
    private readonly Button save = new() { Content = "Hinzufügen", HorizontalAlignment = HorizontalAlignment.Right };
    private readonly bool readOnly;
    private int editing = -1;
    private bool updating;

    public OptionsDialog(XamlRoot xamlRoot, List<ExtraOption> options, bool readOnly)
    {
        XamlRoot = xamlRoot; Title = "Weitere DHCP-Optionen"; CloseButtonText = "Fertig"; DefaultButton = ContentDialogButton.Close;
        this.options = options; this.readOnly = readOnly; Resources["ContentDialogMaxWidth"] = 820d;
        var body = new StackPanel { Spacing = 16, MinWidth = 450, MaxWidth = 760 };
        var introduction = Ui.Text(readOnly ? "Der Server läuft. Die Optionen sind bis zum Stoppen schreibgeschützt." : "Zusätzliche Werte für DHCP-Antworten. Standardmäßig auf Anfrage des Clients.", 13); introduction.TextWrapping = TextWrapping.Wrap; body.Children.Add(introduction);
        body.Children.Add(rows); RenderRows();
        var title = Ui.Text("Option hinzufügen", 16); title.FontWeight = FontWeights.SemiBold; body.Children.Add(title);
        foreach (var definition in definitions) catalog.Items.Add(new ComboBoxItem { Content = definition.Display + (definition.Managed ? " · automatisch / clientseitig" : ""), Tag = definition });
        catalog.Items.Add(new ComboBoxItem { Content = "Eigener Optionscode / Rohdaten", Tag = null });
        body.Children.Add(Ui.Field("IANA-Optionskatalog", catalog));
        var formatRow = new Grid { ColumnSpacing = 12 }; formatRow.ColumnDefinitions.Add(new() { Width = new GridLength(110) }); formatRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        formatRow.Children.Add(Ui.Field("Optionscode", code)); var formatField = Ui.Field("Datenformat", format); Grid.SetColumn(formatField, 1); formatRow.Children.Add(formatField); body.Children.Add(formatRow);
        foreach (var label in OptionCodec.Formats) format.Items.Add(label); format.SelectedItem = "Text (ASCII)";
        body.Children.Add(Ui.Field("Wert", value)); help.TextWrapping = reference.TextWrapping = TextWrapping.Wrap; body.Children.Add(help); body.Children.Add(reference); body.Children.Add(always); body.Children.Add(error); body.Children.Add(save);
        var managed = Ui.Text("Subnetzmaske, Gateway, DNS und Lease-Dauer bleiben im Hauptfenster. Protokolloptionen werden automatisch erzeugt.", 12); managed.TextWrapping = TextWrapping.Wrap; body.Children.Add(managed);
        Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = Math.Max(300, xamlRoot.Size.Height - 210) };
        AutomationProperties.SetName(catalog, "IANA-Optionskatalog"); AutomationProperties.SetName(code, "Optionscode"); AutomationProperties.SetName(format, "Datenformat"); AutomationProperties.SetName(value, "Optionswert");
        save.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        foreach (var control in new Control[] { catalog, code, format, value, always, save }) control.IsEnabled = !readOnly;
        catalog.SelectionChanged += (_, _) =>
        {
            if (updating) return;
            if ((catalog.SelectedItem as ComboBoxItem)?.Tag is OptionDefinition definition)
            {
                updating = true; code.Value = definition.Code; format.SelectedItem = definition.Format; reference.Text = definition.Description + " " + definition.Reference; updating = false;
            }
            UpdateHelp();
        };
        code.ValueChanged += (_, _) =>
        {
            if (updating) return; updating = true;
            var match = catalog.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag is OptionDefinition d && d.Code == code.Value);
            catalog.SelectedItem = match ?? catalog.Items[^1];
            reference.Text = (match?.Tag as OptionDefinition)?.Reference ?? "Eigener Optionscode. Kodierung und Bedeutung anhand der Gerätespezifikation prüfen."; updating = false; UpdateHelp();
        };
        format.SelectionChanged += (_, _) => UpdateHelp();
        save.Click += (_, _) => Save();
        catalog.SelectedItem = catalog.Items.OfType<ComboBoxItem>().First(item => item.Tag is OptionDefinition d && d.Code == 15); UpdateHelp();
    }
    private void UpdateHelp()
    {
        help.Text = (format.SelectedItem as string) switch
        {
            "IPv4-Liste" => "IPv4-Adressen durch Komma trennen, z. B. 192.168.50.10, 192.168.50.11.",
            "Hex" => "Hexbytes, z. B. 01 04 C0 A8 32 FE. Strukturierte Herstellerdaten und Unteroptionen vollständig kodieren.",
            "Domainliste" => "Domainnamen durch Komma trennen; die Anwendung kodiert DNS-Labels gemäß Option 119.",
            "Routen" => "Eine Route je Zeile: 10.20.0.0/16=192.168.50.254. Eine Standardroute: 0.0.0.0/0=192.168.50.254.",
            "Boolean" => "true/false, ja/nein oder 1/0.",
            "UInt8" => "Ganzzahl von 0 bis 255.", "UInt16" => "Ganzzahl von 0 bis 65.535 (Network Byte Order).", "UInt32" => "Ganzzahl von 0 bis 4.294.967.295 (Network Byte Order).",
            "IPv4" => "Eine IPv4-Adresse mit vier dezimalen Oktetten.", _ => "ASCII-Text, z. B. commissioning.example oder firmware.bin."
        };
        if (double.IsFinite(code.Value) && code.Value is >= 0 and <= 255 && OptionCodec.ManagedCodes.Contains((byte)code.Value)) help.Text = "Dieser Code ist protokollgesteuert, clientseitig oder wird im Hauptformular festgelegt. Er kann hier nicht zusätzlich gesendet werden.";
    }
    private void Save()
    {
        try
        {
            if (!double.IsFinite(code.Value) || code.Value != Math.Floor(code.Value) || code.Value is < 1 or > 254) throw new ArgumentException("Bitte einen Optionscode zwischen 1 und 254 eingeben.");
            var optionCode = (byte)code.Value;
            if (options.Where((_, index) => index != editing).Any(o => o.Code == optionCode)) throw new ArgumentException("Dieser Optionscode ist bereits vorhanden. Den bestehenden Eintrag bearbeiten.");
            var definition = definitions.FirstOrDefault(d => d.Code == optionCode);
            var option = new ExtraOption { Code = optionCode, Name = definition?.Name ?? $"Option {optionCode}", Format = format.SelectedItem as string ?? "Hex", Value = value.Text.Trim(), AlwaysSend = always.IsChecked == true };
            OptionCodec.Encode(option);
            if (editing >= 0) options[editing] = option; else options.Add(option);
            editing = -1; value.Text = ""; save.Content = "Hinzufügen"; error.IsOpen = false; RenderRows();
        }
        catch (ArgumentException ex) { error.Message = ex.Message; error.IsOpen = true; }
    }
    private void RenderRows()
    {
        rows.Children.Clear();
        if (options.Count == 0) { rows.Children.Add(Ui.Text("Keine zusätzlichen Optionen konfiguriert.", 13)); return; }
        for (var index = 0; index < options.Count; index++)
        {
            var current = index; var option = options[index]; var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var enabled = new CheckBox { IsChecked = option.Enabled, IsEnabled = !readOnly, MinWidth = 28 }; AutomationProperties.SetName(enabled, $"Option {option.Code} aktivieren"); enabled.Click += (_, _) => options[current] = options[current] with { Enabled = enabled.IsChecked == true }; row.Children.Add(enabled);
            var details = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; var name = Ui.Text($"{option.Code} · {option.Name}", 13); name.FontWeight = FontWeights.SemiBold; details.Children.Add(name); var text = Ui.Text(option.Value, 12); text.TextWrapping = TextWrapping.Wrap; details.Children.Add(text); Grid.SetColumn(details, 1); row.Children.Add(details);
            var edit = new Button { Content = "Bearbeiten", IsEnabled = !readOnly, VerticalAlignment = VerticalAlignment.Center }; edit.Click += (_, _) => { editing = current; code.Value = option.Code; format.SelectedItem = option.Format; value.Text = option.Value; always.IsChecked = option.AlwaysSend; save.Content = "Übernehmen"; }; Grid.SetColumn(edit, 2); row.Children.Add(edit);
            var remove = new Button { Content = new FontIcon { Glyph = "\uE74D", FontSize = 14 }, IsEnabled = !readOnly, VerticalAlignment = VerticalAlignment.Center }; Ui.SizeIconButton(remove); AutomationProperties.SetName(remove, $"Option {option.Code} entfernen"); remove.Click += (_, _) => { options.RemoveAt(current); editing = -1; save.Content = "Hinzufügen"; RenderRows(); }; Grid.SetColumn(remove, 3); row.Children.Add(remove); rows.Children.Add(row);
        }
    }
}
