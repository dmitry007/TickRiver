using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.Win32;
using Microsoft.Data.Sqlite;
using TickRiver.Core;

namespace TickRiver;

public partial class MainWindow : Window
{
    private readonly Store _store;
    private readonly DispatcherTimer _clock;
    private readonly System.Windows.Forms.NotifyIcon? _tray;
    private readonly System.Drawing.Icon? _trayIcon;
    private bool _ready, _exiting, _changingFilters, _settingRange;
    private int _rangeMonths;
    private long _lastDataVersion;
    private readonly SqliteConnection _changeMonitor;
    private TimeEntry? _running;
    private Report? _report;

    public MainWindow(Store store, bool tray = true)
    {
        _store = store;
        InitializeComponent();
        _changeMonitor = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.Path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        _changeMonitor.Open();
        RangePreset.ItemsSource = new[] { "Today", "Yesterday", "This week", "Last week", "This month", "Last month", "This quarter", "Last quarter", "This year", "Last year", "Custom range" };
        if (tray)
        {
            using var icon = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/TickRiver.ico")).Stream;
            _trayIcon = new System.Drawing.Icon(icon, System.Windows.Forms.SystemInformation.SmallIconSize);
            _tray = new System.Windows.Forms.NotifyIcon { Icon = _trayIcon, Text = "TickRiver", Visible = true };
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("Open TickRiver", null, (_, _) => Dispatcher.Invoke(Restore));
            menu.Items.Add("Start / Stop timer", null, (_, _) => Dispatcher.Invoke(() => { Restore(); TimerClick(this, new RoutedEventArgs()); }));
            menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(RequestExit));
            _tray.ContextMenuStrip = menu;
            _tray.MouseClick += (_, e) => { if (e.Button == System.Windows.Forms.MouseButtons.Left) Dispatcher.Invoke(Restore); };
        }
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) }; _clock.Tick += (_, _) => SynchronizeTimer(); _clock.Start();
        Chart.DayClicked += day => { if (_report == null) return; ReportGrid.ItemsSource = _report.Rows.Where(r => r.Day == day).ToArray(); ReportTabs.SelectedIndex = 1; StatusText.Text = $"Showing {day:MMMM d, yyyy}. Apply the report again to show the full range."; };
        _ready = true; Refresh(); RangePreset.SelectedItem = "This month"; TrackerNav.IsChecked = true;
        Closing += OnClosing;
        Closed += (_, _) => { _clock.Stop(); _tray?.Dispose(); _trayIcon?.Dispose(); _changeMonitor.Dispose(); };
    }

    internal void Restore() { Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Activate(); }
    private void Navigate(object sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not RadioButton button) return;
        TrackerPage.Visibility = ReportsPage.Visibility = ClientsPage.Visibility = ProjectsPage.Visibility = Visibility.Collapsed;
        switch (button.Tag)
        {
            case "Tracker": TrackerPage.Visibility = Visibility.Visible; PageTitle.Text = "Time Tracker"; break;
            case "Reports": ReportsPage.Visibility = Visibility.Visible; PageTitle.Text = "Reports"; Try(RefreshReport); break;
            case "Clients": ClientsPage.Visibility = Visibility.Visible; PageTitle.Text = "Clients"; break;
            case "Projects": ProjectsPage.Visibility = Visibility.Visible; PageTitle.Text = "Projects"; break;
        }
    }

    internal void Refresh()
    {
        _lastDataVersion = ReadDataVersion();
        var projects = _store.Projects(); var clients = _store.Clients(); var entries = _store.Entries();
        var selectedTimer = (TimerProject.SelectedItem as Project)?.Id;
        var clientFilter = (ClientFilter.SelectedItem as Client)?.Id ?? 0;
        var projectFilter = (ProjectFilter.SelectedItem as Project)?.Id ?? 0;
        _running = entries.SingleOrDefault(e => e.End == null);
        TimerProject.ItemsSource = projects.Where(p => !p.Archived && !p.ClientArchived || p.Id == _running?.ProjectId).ToArray();
        TimerProject.SelectedItem = ((IEnumerable<Project>)TimerProject.ItemsSource).FirstOrDefault(p => p.Id == (_running?.ProjectId ?? selectedTimer)) ?? ((IEnumerable<Project>)TimerProject.ItemsSource).FirstOrDefault();
        if (_running != null) DescriptionBox.Text = _running.Description;
        DescriptionBox.IsEnabled = TimerProject.IsEnabled = _running == null;
        TimerButton.Content = _running == null ? "START" : "STOP";
        ClientsGrid.ItemsSource = clients; ProjectsGrid.ItemsSource = projects;
        _changingFilters = true;
        ClientFilter.ItemsSource = new[] { new Client(0, "All clients", "", 0, false) }.Concat(clients).ToArray();
        ClientFilter.SelectedItem = ((IEnumerable<Client>)ClientFilter.ItemsSource).FirstOrDefault(c => c.Id == clientFilter) ?? ((IEnumerable<Client>)ClientFilter.ItemsSource).First();
        SetProjectFilter(projectFilter);
        _changingFilters = false;
        var completed = entries.Where(e => e.End != null).ToList();
        var view = new ListCollectionView(completed);
        var totals = completed.GroupBy(e => e.DateLabel).ToDictionary(g => g.Key, g => g.Sum(e => e.Seconds));
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TimeEntry.DateLabel), new DayTotalConverter(totals)));
        EntriesGrid.ItemsSource = view; TrackerEmpty.Visibility = completed.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var today = DateOnly.FromDateTime(DateTime.Now);
        var monday = today.AddDays(-((int)today.DayOfWeek + 6) % 7);
        WeekTotal.Text = $"This week · {ReportBuilder.Build(entries, monday, today, "").Duration}";
        UpdateClock();
        if (FromDate.SelectedDate != null && ToDate.SelectedDate != null) RefreshReport();
    }
    private void UpdateClock()
    {
        var seconds = _running == null ? 0 : Math.Max(0, (TimeFormat.Now.UtcTicks - _running.Start.UtcTicks) / TimeSpan.TicksPerSecond);
        TimerLabel.Text = TimeFormat.Duration(seconds);
        if (_tray != null) _tray.Text = _running == null ? "TickRiver — ready" : $"TickRiver — {TimerLabel.Text}";
    }
    internal void SynchronizeTimer()
    {
        try
        {
            var current = _store.Entries(runningOnly: true).SingleOrDefault();
            if (current?.Id != _running?.Id || ReadDataVersion() != _lastDataVersion)
            {
                var stopped = _running != null && current == null;
                Refresh(); if (stopped) DescriptionBox.Clear();
                StatusText.Text = current == null ? "Time entries refreshed." : "Timer started. Its start time is saved.";
            }
            else UpdateClock();
        }
        catch (Exception error) { StatusText.Text = "Could not refresh timer: " + error.Message; }
    }
    private long ReadDataVersion()
    {
        using var command = _changeMonitor.CreateCommand(); command.CommandText = "PRAGMA data_version";
        return (long)command.ExecuteScalar()!;
    }
    private void Try(Action action)
    {
        try { action(); }
        catch (Exception error) { StatusText.Text = error.Message; System.Windows.MessageBox.Show(this, error.Message, "TickRiver", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void TimerClick(object sender, RoutedEventArgs e) => Try(() => {
        if (_running != null) { _store.Stop(TimeFormat.Now, _running.Id); DescriptionBox.Clear(); StatusText.Text = "Time entry saved."; }
        else
        {
            if (TimerProject.SelectedItem is not Project project) throw new ArgumentException("Create a client and project before starting your timer.");
            _store.Start(project.Id, DescriptionBox.Text, TimeFormat.Now); StatusText.Text = "Timer running. Its start time is saved on this computer.";
        }
        Refresh();
    });
    private void NewClientClick(object sender, RoutedEventArgs e) => Try(() => { if (EditDialog.Client(this, _store)) { Refresh(); StatusText.Text = "Client saved."; } });
    private void EditClientClick(object sender, RoutedEventArgs e) => Try(() => { if (ClientsGrid.SelectedItem is not Client c) throw new ArgumentException("Select a client to edit."); if (EditDialog.Client(this, _store, c)) Refresh(); });
    private void NewProjectClick(object sender, RoutedEventArgs e) => Try(() => {
        if (!_store.Clients().Any(c => !c.Archived))
        {
            if (!EditDialog.Client(this, _store)) return;
            Refresh();
        }
        var id = EditDialog.Project(this, _store);
        if (id != null) { Refresh(); TimerProject.SelectedItem = ((IEnumerable<Project>)TimerProject.ItemsSource).FirstOrDefault(p => p.Id == id); StatusText.Text = "Project saved."; }
    });
    private void EditProjectClick(object sender, RoutedEventArgs e) => Try(() => { if (ProjectsGrid.SelectedItem is not Project p) throw new ArgumentException("Select a project to edit."); if (EditDialog.Project(this, _store, p) != null) Refresh(); });
    private void AddTimeClick(object sender, RoutedEventArgs e) => Try(() => { if (EditDialog.Entry(this, _store, selectedProject: (TimerProject.SelectedItem as Project)?.Id)) Refresh(); });
    private void EditEntryClick(object sender, RoutedEventArgs e) => Try(() => { if (EntriesGrid.SelectedItem is not TimeEntry entry) return; if (EditDialog.Entry(this, _store, entry)) Refresh(); });
    private void EditReportEntry(object sender, RoutedEventArgs e) => Try(() => { if (ReportGrid.SelectedItem is ReportRow row && EditDialog.Entry(this, _store, row.Entry)) Refresh(); });
    private void ContinueClick(object sender, RoutedEventArgs e) => Try(() => {
        if (_running != null) throw new ArgumentException("Stop the active timer first.");
        if (EntriesGrid.SelectedItem is not TimeEntry entry) throw new ArgumentException("Select an entry to continue.");
        _store.Start(entry.ProjectId, entry.Description, TimeFormat.Now); Refresh(); StatusText.Text = "Started a new session for the selected entry.";
    });
    private void DeleteEntryClick(object sender, RoutedEventArgs e) => Try(() => {
        if (EntriesGrid.SelectedItem is not TimeEntry entry) throw new ArgumentException("Select an entry to delete.");
        if (System.Windows.MessageBox.Show(this, $"Delete this {entry.Duration} entry? This cannot be undone.", "Delete time entry", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _store.DeleteEntry(entry.Id); Refresh();
    });
    private void ClientFilterChanged(object sender, SelectionChangedEventArgs e) { if (_ready && !_changingFilters) SetProjectFilter(0); }
    private void SetProjectFilter(long selected)
    {
        var clientId = (ClientFilter.SelectedItem as Client)?.Id ?? 0;
        var projects = new[] { new Project(0, 0, "All projects", "", "", 0, null, true, false, false) }
            .Concat(_store.Projects().Where(p => clientId == 0 || p.ClientId == clientId)).ToArray();
        ProjectFilter.ItemsSource = projects; ProjectFilter.SelectedItem = projects.FirstOrDefault(p => p.Id == selected) ?? projects[0];
    }
    private void PresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || RangePreset.SelectedItem is not string preset || preset == "Custom range") return;
        var today = DateTime.Today; var monday = today.AddDays(-((int)today.DayOfWeek + 6) % 7);
        var month = new DateTime(today.Year, today.Month, 1); var quarter = new DateTime(today.Year, (today.Month - 1) / 3 * 3 + 1, 1);
        (DateTime from, DateTime to) = preset switch {
            "Today" => (today, today), "Yesterday" => (today.AddDays(-1), today.AddDays(-1)),
            "This week" => (monday, monday.AddDays(6)), "Last week" => (monday.AddDays(-7), monday.AddDays(-1)),
            "This month" => (month, month.AddMonths(1).AddDays(-1)), "Last month" => (month.AddMonths(-1), month.AddDays(-1)),
            "This quarter" => (quarter, quarter.AddMonths(3).AddDays(-1)), "Last quarter" => (quarter.AddMonths(-3), quarter.AddDays(-1)),
            "This year" => (new DateTime(today.Year, 1, 1), new DateTime(today.Year, 12, 31)),
            _ => (new DateTime(today.Year - 1, 1, 1), new DateTime(today.Year - 1, 12, 31)) };
        _rangeMonths = preset.Contains("month", StringComparison.OrdinalIgnoreCase) ? 1 : preset.Contains("quarter", StringComparison.OrdinalIgnoreCase) ? 3 : preset.Contains("year", StringComparison.OrdinalIgnoreCase) ? 12 : 0;
        _settingRange = true;
        try { FromDate.SelectedDate = from; ToDate.SelectedDate = to; }
        finally { _settingRange = false; }
        Try(RefreshReport);
    }
    private void CustomDateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _settingRange) return;
        _rangeMonths = 0; RangePreset.SelectedItem = "Custom range";
    }
    private void PreviousRange(object sender, RoutedEventArgs e) => ShiftRange(-1);
    private void NextRange(object sender, RoutedEventArgs e) => ShiftRange(1);
    private void ShiftRange(int direction) => Try(() => {
        if (FromDate.SelectedDate is not DateTime from || ToDate.SelectedDate is not DateTime to) return;
        if (_rangeMonths > 0) { from = from.AddMonths(direction * _rangeMonths); to = from.AddMonths(_rangeMonths).AddDays(-1); }
        else { var shift = (to - from).Days + 1; from = from.AddDays(shift * direction); to = to.AddDays(shift * direction); }
        RangePreset.SelectedItem = "Custom range";
        _settingRange = true;
        try { FromDate.SelectedDate = from; ToDate.SelectedDate = to; }
        finally { _settingRange = false; }
        RefreshReport();
    });
    private void ApplyReport(object sender, RoutedEventArgs e) => Try(RefreshReport);
    internal void RefreshReport()
    {
        if (FromDate.SelectedDate is not DateTime from || ToDate.SelectedDate is not DateTime to) throw new ArgumentException("Choose a start and end date.");
        var client = ClientFilter.SelectedItem as Client; var project = ProjectFilter.SelectedItem as Project;
        var title = project?.Id > 0 ? project.DisplayName : client?.Id > 0 ? client.Name : "All clients · All projects";
        if (UnbilledFilter.IsChecked == true) title += " · Unbilled work";
        _report = ReportBuilder.Build(_store.Entries(), DateOnly.FromDateTime(from), DateOnly.FromDateTime(to), title,
            client?.Id > 0 ? client.Id : null, project?.Id > 0 ? project.Id : null, UnbilledFilter.IsChecked == true);
        Chart.Report = _report; ChartTitle.Text = title;
        ReportGrid.ItemsSource = _report.Rows; ReportTotal.Text = $"Total {_report.Duration}  ·  {_report.Seconds / 3600m:0.######} hours";
        ReportAmount.Text = $"{_report.RangeLabel}  ·  Billable amount: {(_report.AmountLabel.Length > 0 ? _report.AmountLabel : "0.00")}";
    }
    internal string ExportFileName(string extension)
    {
        var client = ClientFilter.SelectedItem as Client; var project = ProjectFilter.SelectedItem as Project;
        var names = new[] { project?.Id > 0 ? project.ClientName : client?.Id > 0 ? client.Name : null, project?.Id > 0 ? project.Name : null };
        var invalid = Path.GetInvalidFileNameChars();
        var parts = names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => {
            var part = string.Concat(n!.Select(c => invalid.Contains(c) ? '_' : c)).Trim();
            return part.Length <= 80 ? part : part[..(char.IsHighSurrogate(part[79]) ? 79 : 80)];
        });
        var prefix = string.Join("-", new[] { "TickRiver" }.Concat(parts));
        return $"{prefix}-{_report!.From:yyyy-MM-dd}-{_report.To:yyyy-MM-dd}.{extension}";
    }
    private void ExportPng(object sender, RoutedEventArgs e) => Try(() => { RefreshReport(); var dialog = new SaveFileDialog { Filter = "PNG chart|*.png", FileName = ExportFileName("png") }; if (dialog.ShowDialog(this) == true) { HoursChart.SavePng(_report!, dialog.FileName); StatusText.Text = $"Chart saved: {dialog.FileName}"; } });
    private void ExportExcel(object sender, RoutedEventArgs e) => Try(() => { RefreshReport(); var dialog = new SaveFileDialog { Filter = "Excel workbook|*.xlsx", FileName = ExportFileName("xlsx") }; if (dialog.ShowDialog(this) == true) { ExcelExport.Save(_report!, dialog.FileName); StatusText.Text = $"Workbook saved: {dialog.FileName}"; } });
    private void MarkInvoiced(object sender, RoutedEventArgs e) => Try(() => {
        RefreshReport();
        if (_report!.Rows.Count == 0) throw new ArgumentException("There are no entries in this report.");
        if (System.Windows.MessageBox.Show(this, "Mark all billable entries in this report as invoiced? Exporting does not change invoice status.", "Mark invoiced", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        _store.MarkInvoiced(_report, true); Refresh(); StatusText.Text = "Entries marked invoiced. Edit an entry to mark it unbilled again.";
    });
    private void BackupClick(object sender, RoutedEventArgs e) => Try(() => { var dialog = new SaveFileDialog { Filter = "SQLite backup|*.sqlite", FileName = $"TickRiver-backup-{DateTime.Now:yyyy-MM-dd-HHmmss}.sqlite" }; if (dialog.ShowDialog(this) == true) { _store.Backup(dialog.FileName); StatusText.Text = $"Backup saved: {dialog.FileName}"; } });
    private void ExitClick(object sender, RoutedEventArgs e) => RequestExit();
    private async void ConnectionsClick(object sender, RoutedEventArgs e) => await ConnectClients(true);
    internal async System.Threading.Tasks.Task ConnectClients(bool retry)
    {
        try
        {
            var results = await System.Threading.Tasks.Task.Run(() => ClientRegistration.Register(Path.Combine(AppContext.BaseDirectory, "TickRiver.Mcp.exe"),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Path.Combine(Path.GetDirectoryName(_store.Path)!, "ai-connections.json"), Environment.GetEnvironmentVariable("CODEX_HOME"), retry,
                _store.Path, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)));
            StatusText.Text = string.Join("  ", results);
            if (retry) new ConnectionsDialog(this, results).ShowDialog();
        }
        catch (Exception error) { StatusText.Text = "AI connection setup: " + error.Message; }
    }
    private void RequestExit()
    {
        var confirmedTimer = _running;
        if (confirmedTimer != null)
        {
            var choice = System.Windows.MessageBox.Show(this, "Stop the active timer before exiting?\n\nYes: save and stop. No: keep counting until you next open TickRiver. Cancel: stay here.", "Active timer", MessageBoxButton.YesNoCancel);
            if (choice == MessageBoxResult.Cancel) return;
            if (choice == MessageBoxResult.Yes) { try { _store.Stop(TimeFormat.Now, confirmedTimer.Id); } catch (Exception error) { System.Windows.MessageBox.Show(this, error.Message); return; } }
        }
        _exiting = true; Close();
    }
    private void OnClosing(object? sender, CancelEventArgs e) { if (!_exiting && _tray != null) { e.Cancel = true; Hide(); } }

    private sealed class DayTotalConverter(Dictionary<string, long> totals) : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => $"{value}    ·    {TimeFormat.Duration(totals[(string)value])}";
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
