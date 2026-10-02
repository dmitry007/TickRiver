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
        var window = new MainWindow(new Store(store.Path), false) { ShowActivated = false };
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
        window.ClientsNav.IsChecked = true; window.UpdateLayout(); Capture(window, Path.Combine(directory, "clients.png"));
        window.ProjectsNav.IsChecked = true; window.UpdateLayout(); Capture(window, Path.Combine(directory, "projects.png"));
        var report = window.Chart.Report!;
        HoursChart.SavePng(report, Path.Combine(directory, "invoice-chart.png"));
        ExcelExport.Save(report, Path.Combine(directory, "invoice-hours.xlsx"));
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
        window.Close();
        File.WriteAllText(Path.Combine(directory, "result.txt"), "PASS: window lifecycle, timer controls, external historical additions and edits in tracker/reports, stale desktop edit rejection, calendar navigation, all screens, PNG and Excel exports.");
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
