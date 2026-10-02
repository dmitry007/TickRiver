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
        window.Close();
        File.WriteAllText(Path.Combine(directory, "result.txt"), "PASS: timer recovery, Start/Stop UI, external timer synchronization, repeated month/quarter/year navigation, all screens, PNG and Excel exports.");
    }
    private static void Capture(MainWindow window, string path)
    {
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
}
