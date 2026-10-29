using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TickRiver.Core;

namespace TickRiver;

internal static class SmokeTest
{
    public static void Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var store = new Store(Path.Combine(directory, "smoke-" + Guid.NewGuid().ToString("N") + ".sqlite"));
        var client = store.SaveClient(0, "Northwind Studio", "USD", 125, false);
        var project = store.SaveProject(0, client, "Website maintenance", null, true, false);
        var second = store.SaveProject(0, client, "Product design", 150, true, false);
        var month = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(-1);
        for (var day = 1; day <= 24; day++)
        {
            var date = month.AddDays(day - 1);
            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || day % 3 == 0) continue;
            var start = TimeFormat.ParseLocal(date, "09:15:00");
            store.SaveEntry(0, day % 4 == 0 ? second : project, day % 4 == 0 ? "Design review" : "Maintenance and client feedback", start, start.AddSeconds(3600 + day * 197), day % 4 == 0 ? 150 : 125, true, false);
        }
        store.Start(project, "Checking timer recovery", TimeFormat.Now.AddSeconds(-125));
        var window = new MainWindow(new Store(store.Path)) { ShowActivated = false };
        window.Show(); window.UpdateLayout();
        if (window.Icon == null) throw new Exception("Application icon missing.");
        window.WindowState = WindowState.Minimized;
        if (!window.IsVisible || !window.ShowInTaskbar) throw new Exception("Minimized window disappeared from the taskbar.");
        window.Restore();
        window.ShowActivated = true; window.WindowState = WindowState.Maximized; window.Hide(); window.Restore();
        if (!window.IsVisible || window.WindowState != WindowState.Maximized) throw new Exception("Restoring a hidden window lost its maximized state.");
        window.WindowState = WindowState.Normal; window.ShowActivated = false; window.Hide();
        var instanceName = "Local\\TickRiver-test-" + Guid.NewGuid().ToString("N");
        using (var first = new SingleInstance(instanceName, () => window.Dispatcher.BeginInvoke(window.Restore)))
        {
            if (!first.IsFirst || first.OwnerProcessId != Environment.ProcessId) throw new Exception("Fresh instance did not publish its owner.");
            using (var secondInstance = new SingleInstance(instanceName, () => { }))
            {
                if (secondInstance.IsFirst) throw new Exception("Second instance was accepted.");
                if (secondInstance.OwnerProcessId != first.OwnerProcessId) throw new Exception("Second instance did not find the original process.");
            }
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (!window.IsVisible && DateTime.UtcNow < deadline)
            {
                System.Threading.Thread.Sleep(10);
                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
            if (!window.IsVisible) throw new Exception("Second launch did not restore the hidden window.");
        }
        var crash = CrashLog.Write(new InvalidOperationException("Diagnostic fixture"), "Smoke test", Path.Combine(directory, "logs"));
        if (crash == null || !File.ReadAllText(crash).Contains("Diagnostic fixture")) throw new Exception("Diagnostic log missing.");
        if (CrashLog.Write(new Exception("Expected log failure"), "Smoke test", store.Path) != null) throw new Exception("Unwritable diagnostic path should be tolerated.");
        if (window.TimerButton.Content.ToString() != "STOP" || window.TimerLabel.Text == "00:00:00") throw new Exception("Persisted timer was not recovered.");
        window.TimerButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (store.Entries().Any(e => e.End == null)) throw new Exception("Stop button failed to save the timer.");
        var external = store.Start(project, "External timer", TimeFormat.Now);
        window.SynchronizeTimer();
        if (window.TimerButton.Content.ToString() != "STOP") throw new Exception("UI did not see external start.");
        store.Stop(TimeFormat.Now, external); window.SynchronizeTimer();
        if (window.TimerButton.Content.ToString() != "START") throw new Exception("UI did not see external stop.");
        var quick = store.Start(project, "Quick external session", TimeFormat.Now); store.Stop(TimeFormat.Now, quick);
        window.SynchronizeTimer();
        if (((System.Windows.Data.ListCollectionView)window.EntriesGrid.ItemsSource).Count != store.Entries().Count) throw new Exception("UI missed an external session between polls.");
        Capture(window, Path.Combine(directory, "tracker.png"));
        window.FromDate.SelectedDate = month; window.ToDate.SelectedDate = month.AddMonths(1).AddDays(-1);
        window.RangePreset.SelectedItem = "Custom range";
        window.ReportsNav.IsChecked = true; window.UpdateLayout();
        if (window.Chart.Report?.Seconds <= 0) throw new Exception("Report not populated.");
        var manualWriter = new Store(store.Path);
        var manualStart = TimeFormat.ParseLocal(month, "13:00:00");
        var manualId = manualWriter.SaveEntry(0, project, "Imported historical time", manualStart, manualStart.AddSeconds(2894), 125, true, false);
        window.SynchronizeTimer();
        if (!((System.Windows.Data.ListCollectionView)window.EntriesGrid.ItemsSource).Cast<TimeEntry>().Any(e => e.Id == manualId)
            || !window.Chart.Report!.Rows.Any(r => r.Entry.Id == manualId && r.Seconds == 2894))
            throw new Exception("UI tracker or report missed an external historical addition.");
        var corrected = store.Entries().First(e => e.End > e.Start && e.Start.LocalDateTime >= month && e.Start.LocalDateTime < month.AddMonths(1));
        var writer = new Store(store.Path);
        writer.SaveEntry(corrected.Id, corrected.ProjectId, "Corrected externally", corrected.Start, corrected.End!.Value, corrected.Rate + 1, corrected.Billable, corrected.Invoiced, expected: corrected);
        window.SynchronizeTimer();
        if (!((System.Windows.Data.ListCollectionView)window.EntriesGrid.ItemsSource).Cast<TimeEntry>().Any(e => e.Id == corrected.Id && e.Description == "Corrected externally")
            || !window.Chart.Report!.Rows.Any(r => r.Entry.Id == corrected.Id && r.Description == "Corrected externally" && r.Entry.Rate == corrected.Rate + 1))
            throw new Exception("UI tracker or report missed an external edit to an existing entry.");
        var desktopSnapshot = store.Entries().Single(e => e.Id == corrected.Id);
        var desktopEditRejected = false;
        Exception? editFailure = null;
        window.Dispatcher.BeginInvoke(() => {
            var dialog = System.Windows.Application.Current.Windows.OfType<EditDialog>().Single();
            try
            {
                writer.SaveEntry(desktopSnapshot.Id, desktopSnapshot.ProjectId, "Concurrent MCP correction", desktopSnapshot.Start, desktopSnapshot.End!.Value,
                    desktopSnapshot.Rate + 2, desktopSnapshot.Billable, true, expected: desktopSnapshot);
                var root = (StackPanel)((ScrollViewer)dialog.Content).Content;
                var buttons = (StackPanel)root.Children[root.Children.Count - 1];
                buttons.Children.OfType<Button>().Single(b => b.IsDefault).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var saved = store.Entries().Single(e => e.Id == desktopSnapshot.Id);
                desktopEditRejected = dialog.IsVisible && saved.Description == "Concurrent MCP correction" && saved.Rate == desktopSnapshot.Rate + 2 && saved.Invoiced;
            }
            catch (Exception error) { editFailure = error; }
            finally { if (dialog.IsVisible) dialog.DialogResult = false; }
        }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        EditDialog.Entry(window, store, desktopSnapshot);
        if (editFailure != null) throw new Exception("Desktop edit concurrency probe failed.", editFailure);
        if (!desktopEditRejected) throw new Exception("A stale desktop dialog overwrote an external correction.");
        window.SynchronizeTimer();
        Capture(window, Path.Combine(directory, "reports.png"));
        window.ReportTabs.SelectedIndex = 1; window.UpdateLayout(); Capture(window, Path.Combine(directory, "details.png"));
        CheckReportClipboard(window, directory, store);
        window.ClientsNav.IsChecked = true; window.UpdateLayout(); Capture(window, Path.Combine(directory, "clients.png"));
        window.ProjectsNav.IsChecked = true; window.UpdateLayout(); Capture(window, Path.Combine(directory, "projects.png"));
        var report = window.Chart.Report!;
        HoursChart.SavePng(report, Path.Combine(directory, "invoice-chart.png"));
        ExcelExport.Save(report, Path.Combine(directory, "invoice-hours.xlsx"));
        var dates = $"{report.From:yyyy-MM-dd}-{report.To:yyyy-MM-dd}";
        if (window.ExportFileName("xlsx") != $"TickRiver-{dates}.xlsx") throw new Exception("Unfiltered export filename changed.");
        window.ClientFilter.SelectedItem = ((IEnumerable<Client>)window.ClientFilter.ItemsSource).Single(c => c.Id == client);
        window.RefreshReport();
        if (window.ExportFileName("xlsx") != $"TickRiver-Northwind Studio-{dates}.xlsx") throw new Exception("Client export filename missing or includes an unselected project.");
        window.ClientFilter.SelectedIndex = 0;
        window.ProjectFilter.SelectedItem = ((IEnumerable<Project>)window.ProjectFilter.ItemsSource).Single(p => p.Id == project);
        window.UnbilledFilter.IsChecked = true; window.RefreshReport();
        if (window.ExportFileName("png") != $"TickRiver-Northwind Studio-Website maintenance-{dates}.png") throw new Exception("Project export filename missing its client or includes the billing title.");
        var namedClient = store.SaveClient(0, "München / R&D", "EUR", 100, false);
        var namedProject = store.SaveProject(0, namedClient, "Website: \"v2\"?", null, true, false);
        window.Refresh();
        window.ClientFilter.SelectedItem = ((IEnumerable<Client>)window.ClientFilter.ItemsSource).Single(c => c.Id == namedClient);
        window.ProjectFilter.SelectedItem = ((IEnumerable<Project>)window.ProjectFilter.ItemsSource).Single(p => p.Id == namedProject);
        window.RefreshReport();
        var exportName = window.ExportFileName("xlsx");
        if (exportName != $"TickRiver-München _ R&D-Website_ _v2__-{dates}.xlsx") throw new Exception("Export filename lost Unicode or contains unsafe name characters.");
        ExcelExport.Save(window.Chart.Report!, Path.Combine(directory, exportName));
        HoursChart.SavePng(window.Chart.Report!, Path.Combine(directory, window.ExportFileName("png")));
        store.SaveClient(namedClient, new string('A', 79) + "🚀" + new string('A', 200), "EUR", 100, false);
        store.SaveProject(namedProject, namedClient, new string('B', 300), null, true, false);
        window.Refresh();
        exportName = window.ExportFileName("xlsx");
        if (exportName != $"TickRiver-{new string('A', 79)}-{new string('B', 80)}-{dates}.xlsx") throw new Exception("Long export names exceed filename limits or split a Unicode character.");
        ExcelExport.Save(window.Chart.Report!, Path.Combine(directory, exportName));
        window.ClientFilter.SelectedIndex = 0; window.UnbilledFilter.IsChecked = false; window.RefreshReport();
        foreach (var preset in new[] { "This month", "This quarter", "This year" })
        {
            window.RangePreset.SelectedItem = preset;
            var initialFrom = window.FromDate.SelectedDate!.Value; var initialTo = window.ToDate.SelectedDate!.Value;
            var months = preset == "This month" ? 1 : preset == "This quarter" ? 3 : 12;
            window.PreviousRangeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.PreviousRangeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (window.FromDate.SelectedDate != initialFrom.AddMonths(-months * 2) || window.ToDate.SelectedDate != initialFrom.AddMonths(-months).AddDays(-1)) throw new Exception("Repeated calendar navigation drifted.");
            window.NextRangeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.NextRangeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (window.FromDate.SelectedDate != initialFrom || window.ToDate.SelectedDate != initialTo) throw new Exception("Calendar range did not return to original.");
        }
        var connections = new ConnectionsDialog(window, [
            "Codex: Bundled executable and database verified.",
            "Claude Code: Bundled executable and database verified.",
            "Claude Desktop: Bundled executable and database verified.",
            "Kiro: Bundled executable and database verified.",
            "Grok Build: not detected."]) { ShowActivated = false };
        connections.Show(); Capture(connections, Path.Combine(directory, "ai-connections.png"));
        connections.CloseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (connections.IsVisible) throw new Exception("Connections dialog did not close.");
        var warnings = new ConnectionsDialog(window, [
            "Codex: TickRiver connected; bundled executable and database updated. Restart the client if needed. Server version 0.1.3; reconnect the client to refresh available tools.",
            "Claude Code: Custom TickRiver executable; user configuration preserved.",
            "Claude Desktop: TickRiver is disabled; user configuration preserved.",
            "Kiro: could not connect — Client config must be a JSON object.",
            "Grok Build: not detected."]) { ShowActivated = false, Width = 560, Height = 480 };
        warnings.Show(); Capture(warnings, Path.Combine(directory, "ai-connections-warnings.png")); warnings.Close();
        var closed = false;
        window.Closed += (_, _) => closed = true;
        var updateTimer = store.Start(project, "Installer shutdown fixture", TimeFormat.Now.AddSeconds(-5));
        using (var control = new InstanceControl(() => window.Dispatcher.BeginInvoke(window.CloseForUpdate), 0))
        {
            var edit = new EditDialog(window, "Installer edit fixture", () => { }) { ShowActivated = false };
            edit.Show();
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            using (var cancelledGate = InstanceControl.CreateUpdateGate())
            {
                var cancelledRequest = System.Threading.Tasks.Task.Run(() => InstanceControl.RequestShutdown(System.Diagnostics.Process.GetCurrentProcess()));
                while ((!cancelledRequest.IsCompleted || !window.StatusText.Text.StartsWith("Finish or cancel")) && deadline.Elapsed < TimeSpan.FromSeconds(5))
                {
                    window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    System.Threading.Thread.Sleep(10);
                }
                if (!cancelledRequest.IsCompletedSuccessfully || !cancelledRequest.Result || !window.IsVisible || !edit.IsVisible
                    || !window.StatusText.Text.StartsWith("Finish or cancel")) throw new Exception("Setup discarded an open edit or failed to acknowledge shutdown.");
            }
            edit.Close();
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            if (!window.IsVisible) throw new Exception("Cancelling setup still closed TickRiver after finishing an edit.");
            using var retryGate = InstanceControl.CreateUpdateGate();
            var request = System.Threading.Tasks.Task.Run(() => InstanceControl.RequestShutdown(System.Diagnostics.Process.GetCurrentProcess()));
            deadline.Restart();
            while ((!closed || !request.IsCompleted) && deadline.Elapsed < TimeSpan.FromSeconds(5))
            {
                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                System.Threading.Thread.Sleep(10);
            }
            if (!request.IsCompletedSuccessfully || !request.Result) throw new Exception("Installer shutdown pipe did not acknowledge the UI request.");
        }
        // Ordinary UI exit disposes an idle listener without pumping the dispatcher.
        using (var idleControl = new InstanceControl(() => { }, 0)) { System.Threading.Thread.Sleep(50); }
        if (!closed) throw new Exception("Closing kept the main window running.");
        if (new Store(store.Path).Entries().Single(entry => entry.Id == updateTimer).End != null) throw new Exception("Installer shutdown stopped the active timer.");
        File.WriteAllText(Path.Combine(directory, "result.txt"), "PASS: window lifecycle, timer controls, external historical additions and edits in tracker/reports, stale desktop edit rejection, calendar navigation, all screens, PNG and Excel exports, client/project filenames including Unicode and unsafe/long names, configurable report copy, HTML byte offsets and escaping, TSV quoting, displayed row ordering and drilldown, saved column selection.");
    }
    private static void CheckReportClipboard(MainWindow window, string directory, Store store)
    {
        var items = window.CopyColumnsMenu.Items.Cast<MenuItem>().ToArray();
        if (items.Where(i => !i.IsChecked).Select(i => (string)i.Tag).SequenceEqual(["ProjectClient", "Billing"]) == false)
            throw new Exception("Invoice copy defaults include project/client or billing, or omit another column.");
        window.CopyColumnsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (!window.CopyColumnsMenu.IsOpen || window.CopyColumnsMenu.PlacementTarget != window.CopyColumnsButton
            || items.Any(i => !i.IsCheckable || !i.StaysOpenOnClick)) throw new Exception("Copy arrow did not open a persistent column checklist.");
        window.CopyColumnsMenu.IsOpen = false;
        var rows = window.ReportGrid.Items.Cast<ReportRow>().ToArray();
        var first = rows[0] with { Entry = rows[0].Entry with { Description = "München & <review> \"quoted\" 🚀\tvalue\r\nSecond line" } };
        var data = ReportClipboard.Create([first], ["Date", "Description", "Duration"]);
        var html = (string)data.GetData(DataFormats.Html)!;
        var bytes = System.Text.Encoding.UTF8.GetBytes(html);
        var offsets = html[..html.IndexOf("<html>", StringComparison.Ordinal)].Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            .Skip(1).Select(s => int.Parse(s[(s.IndexOf(':') + 1)..], System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var fragment = System.Text.Encoding.UTF8.GetString(bytes[offsets[2]..offsets[3]]);
        if (offsets[1] != bytes.Length || !System.Text.Encoding.UTF8.GetString(bytes[offsets[0]..offsets[1]]).StartsWith("<html>")
            || !fragment.StartsWith("<table ") || !fragment.EndsWith("</table>") || fragment.Contains("<review>") || !fragment.Contains("&lt;review&gt;")
            || !fragment.Contains("<br>Second line") || fragment.Contains("Project · Client") || fragment.Contains("Billing"))
            throw new Exception("Clipboard HTML has incorrect byte offsets, escaping, columns, or line breaks.");
        var text = (string)data.GetData(DataFormats.UnicodeText)!;
        if (!text.StartsWith("Date\tDescription\tDuration\r\n") || !text.Contains("\"München & <review> \"\"quoted\"\" 🚀\tvalue\r\nSecond line\""))
            throw new Exception("Clipboard TSV lost Unicode, embedded tabs, quotes, or newlines.");
        File.WriteAllText(Path.Combine(directory, "clipboard.html-format.txt"), html);
        File.WriteAllText(Path.Combine(directory, "clipboard.tsv"), text);
        try { ReportClipboard.Create([], ["Date"]); throw new Exception("Empty report copy was accepted."); }
        catch (ArgumentException) { }
        try { ReportClipboard.Create(rows, []); throw new Exception("Copy with no columns was accepted."); }
        catch (ArgumentException) { }
        foreach (var item in items.Where(i => i.IsChecked)) { item.IsChecked = false; item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); }
        if (window.CopyDataButton.IsEnabled) throw new Exception("Copy remains enabled with no columns.");
        foreach (var item in items.Where(i => (string)i.Tag is "Description" or "Duration")) { item.IsChecked = true; item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); }
        var restored = new MainWindow(new Store(store.Path));
        try
        {
            if (!restored.CopyColumnsMenu.Items.Cast<MenuItem>().Where(i => i.IsChecked).Select(i => (string)i.Tag).SequenceEqual(["Description", "Duration"]))
                throw new Exception("Copy column selection was not restored.");
        }
        finally { restored.Close(); }
        var original = System.Windows.Clipboard.GetDataObject();
        try
        {
            // A sorted day drilldown must copy only its displayed rows in that order.
            window.ReportGrid.ItemsSource = rows.Where(r => r.Day == rows[0].Day).ToArray();
            window.ReportGrid.Items.SortDescriptions.Add(new System.ComponentModel.SortDescription(nameof(ReportRow.Description), System.ComponentModel.ListSortDirection.Ascending));
            var displayed = window.ReportGrid.Items.Cast<ReportRow>().ToArray();
            window.CopyDataButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var expected = ReportClipboard.Create(displayed, ["Description", "Duration"]);
            if (System.Windows.Clipboard.GetText() != (string)expected.GetData(DataFormats.UnicodeText)!
                || !System.Windows.Clipboard.ContainsData(DataFormats.Html) || !window.StatusText.Text.StartsWith($"Copied {displayed.Length} detailed"))
                throw new Exception($"Copy button failed: text matches={System.Windows.Clipboard.GetText() == (string)expected.GetData(DataFormats.UnicodeText)!}; HTML={System.Windows.Clipboard.ContainsData(DataFormats.Html)}; status={window.StatusText.Text}");
        }
        finally
        {
            if (original != null) System.Windows.Clipboard.SetDataObject(original, true); else System.Windows.Clipboard.Clear();
            window.ReportGrid.Items.SortDescriptions.Clear(); window.RefreshReport();
        }
        foreach (var item in items) { item.IsChecked = (string)item.Tag is not ("ProjectClient" or "Billing"); item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); }
    }
    private static void Capture(Window window, string path)
    {
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
}
