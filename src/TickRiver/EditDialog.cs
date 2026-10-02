using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using TickRiver.Core;

namespace TickRiver;

// Small modal forms keep editing on one screen without introducing task management.
internal sealed class EditDialog : Window
{
    private readonly StackPanel _fields;
    private readonly TextBlock _error;
    public EditDialog(Window owner, string title, Action save)
    {
        Owner = owner; Title = title; Width = 530; SizeToContent = SizeToContent.Height; MaxHeight = 850;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.NoResize;
        Background = System.Windows.Media.Brushes.White;
        var root = new StackPanel { Margin = new Thickness(26) };
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        root.Children.Add(new TextBlock { Text = title, FontSize = 23, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 18) });
        _fields = new StackPanel(); root.Children.Add(_fields);
        _error = new TextBlock { Foreground = System.Windows.Media.Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 8) }; root.Children.Add(_error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true }; buttons.Children.Add(cancel);
        var ok = new Button { Content = "Save", IsDefault = true, Style = (Style)FindResource("Primary") };
        ok.Click += (_, _) => { try { save(); DialogResult = true; } catch (Exception error) { _error.Text = error.Message; } };
        buttons.Children.Add(ok); root.Children.Add(buttons);
    }
    public T Field<T>(string label, T control) where T : FrameworkElement
    {
        _fields.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 9, 0, 6), Foreground = System.Windows.Media.Brushes.DimGray });
        _fields.Children.Add(control); System.Windows.Automation.AutomationProperties.SetName(control, label); return control;
    }
    public void Note(string text) => _fields.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.DimGray, FontSize = 12, Margin = new Thickness(0, 10, 0, 4) });
    public static decimal Rate(string text)
    {
        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out var value) || value < 0 || value > 100000000)
            throw new ArgumentException("Enter a non-negative hourly rate, up to 100,000,000.");
        return value;
    }
    public static bool Client(Window owner, Store store, Client? client = null)
    {
        TextBox name = new() { Text = client?.Name ?? "" }, currency = new() { Text = client?.Currency ?? "USD" }, rate = new() { Text = (client?.Rate ?? 0).ToString(CultureInfo.CurrentCulture) };
        CheckBox archived = new() { Content = "Archive client", IsChecked = client?.Archived ?? false };
        var dialog = new EditDialog(owner, client == null ? "New client" : "Edit client", () => store.SaveClient(client?.Id ?? 0, name.Text, currency.Text, Rate(rate.Text), archived.IsChecked == true));
        dialog.Field("Client / Company", name); dialog.Field("Currency (USD, EUR…)", currency); dialog.Field("Default hourly rate", rate); dialog.Field("Status", archived);
        dialog.Note("Rate and currency changes apply to future entries. Existing entries retain their recorded rate and currency. Archiving retains all history.");
        return dialog.ShowDialog() == true;
    }
    public static long? Project(Window owner, Store store, Project? project = null)
    {
        long? saved = null;
        var clients = store.Clients().Where(c => !c.Archived || c.Id == project?.ClientId).ToList();
        var client = new ComboBox { ItemsSource = clients, DisplayMemberPath = "Name", SelectedItem = clients.FirstOrDefault(c => c.Id == project?.ClientId) ?? clients.FirstOrDefault(), IsEnabled = project == null };
        TextBox name = new() { Text = project?.Name ?? "" }, rate = new() { Text = project?.RateOverride?.ToString(CultureInfo.CurrentCulture) ?? "" };
        CheckBox inherit = new() { Content = "Use client rate", IsChecked = project?.RateOverride == null }, billable = new() { Content = "Billable by default", IsChecked = project?.Billable ?? true }, archived = new() { Content = "Archive project", IsChecked = project?.Archived ?? false };
        var dialog = new EditDialog(owner, project == null ? "New project" : "Edit project", () => {
            if (client.SelectedItem is not Client selected) throw new ArgumentException("Create or select a client first.");
            saved = store.SaveProject(project?.Id ?? 0, selected.Id, name.Text, inherit.IsChecked == true ? null : Rate(rate.Text), billable.IsChecked == true, archived.IsChecked == true);
        });
        void RefreshRate() { rate.IsEnabled = inherit.IsChecked != true; inherit.Content = client.SelectedItem is Client selected ? $"Use client rate — {selected.Currency} {selected.Rate:0.00}/hour" : "Use client rate"; }
        inherit.Checked += (_, _) => RefreshRate(); inherit.Unchecked += (_, _) => RefreshRate(); client.SelectionChanged += (_, _) => RefreshRate(); RefreshRate();
        dialog.Field("Client / Company", client); dialog.Field("Project name", name); dialog.Field("Hourly rate", inherit); dialog.Field("Project override", rate); dialog.Field("Billing", billable); dialog.Field("Status", archived);
        dialog.Note("New entries inherit the current client rate unless overridden. Rate changes leave past work unchanged.");
        dialog.ShowDialog(); return saved;
    }
    public static bool Entry(Window owner, Store store, TimeEntry? entry = null, long? selectedProject = null)
    {
        var projects = store.Projects().Where(p => !p.Archived && !p.ClientArchived || p.Id == entry?.ProjectId).ToList();
        var project = new ComboBox { ItemsSource = projects, DisplayMemberPath = "DisplayName", SelectedItem = projects.FirstOrDefault(p => p.Id == (entry?.ProjectId ?? selectedProject)) ?? projects.FirstOrDefault() };
        var now = DateTime.Now;
        TextBox description = new() { Text = entry?.Description ?? "" }, startTime = new() { Text = (entry?.Start.LocalDateTime ?? now.AddHours(-1)).ToString("HH:mm:ss") },
            endTime = new() { Text = (entry?.End?.LocalDateTime ?? now).ToString("HH:mm:ss") }, rate = new() { Text = (entry?.Rate ?? (project.SelectedItem as Project)?.EffectiveRate ?? 0).ToString(CultureInfo.CurrentCulture) };
        DatePicker startDate = new() { SelectedDate = entry?.Start.LocalDateTime.Date ?? now.AddHours(-1).Date }, endDate = new() { SelectedDate = entry?.End?.LocalDateTime.Date ?? now.Date };
        CheckBox billable = new() { Content = "Billable", IsChecked = entry?.Billable ?? (project.SelectedItem as Project)?.Billable ?? true }, invoiced = new() { Content = "Already invoiced", IsChecked = entry?.Invoiced ?? false };
        project.SelectionChanged += (_, _) => { if (project.SelectedItem is Project p) { rate.Text = (entry?.ProjectId == p.Id ? entry.Rate : p.EffectiveRate).ToString(CultureInfo.CurrentCulture); billable.IsChecked = entry?.ProjectId == p.Id ? entry.Billable : p.Billable; } };
        var dialog = new EditDialog(owner, entry == null ? "Add time" : "Edit time entry", () => {
            if (project.SelectedItem is not Project p || startDate.SelectedDate == null || endDate.SelectedDate == null) throw new ArgumentException("Select a project and both dates.");
            store.SaveEntry(entry?.Id ?? 0, p.Id, description.Text, TimeFormat.ParseLocal(startDate.SelectedDate.Value, startTime.Text, entry?.Start), TimeFormat.ParseLocal(endDate.SelectedDate.Value, endTime.Text, entry?.End), Rate(rate.Text), billable.IsChecked == true, invoiced.IsChecked == true, expected: entry);
        });
        dialog.Field("Project · Client", project); dialog.Field("Description (optional)", description);
        var start = new Grid(); start.ColumnDefinitions.Add(new ColumnDefinition()); start.ColumnDefinitions.Add(new ColumnDefinition()); start.Children.Add(startDate); start.Children.Add(startTime); Grid.SetColumn(startTime, 1);
        var end = new Grid(); end.ColumnDefinitions.Add(new ColumnDefinition()); end.ColumnDefinitions.Add(new ColumnDefinition()); end.Children.Add(endDate); end.Children.Add(endTime); Grid.SetColumn(endTime, 1);
        dialog.Field("Start date and time", start); dialog.Field("End date and time", end); dialog.Field("Recorded hourly rate", rate); dialog.Field("Billing", billable); dialog.Field("Invoice status", invoiced);
        dialog.Note("Times use this computer’s local time zone. For overnight entries, set the end date to the following day.");
        return dialog.ShowDialog() == true;
    }
}
