using System.Windows;

namespace TickRiver;

public partial class ConnectionsDialog : Window
{
    public ConnectionsDialog(Window owner, IReadOnlyList<string> results)
    {
        Owner = owner;
        InitializeComponent();
        Width = Math.Min(Width, SystemParameters.WorkArea.Width - 40);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 40);
        Connections.ItemsSource = results.Select(result => {
            var separator = result.IndexOf(": ", StringComparison.Ordinal);
            var host = separator < 0 ? "TickRiver" : result[..separator];
            var detail = separator < 0 ? result : result[(separator + 2)..];
            var status = "Attention";
            if (detail.StartsWith("not detected.", StringComparison.Ordinal)) status = "Not detected";
            else if (detail.StartsWith("could not connect", StringComparison.Ordinal) || separator < 0) status = "Error";
            else if (detail.StartsWith("TickRiver is disabled", StringComparison.Ordinal)) status = "Disabled";
            else if (detail.StartsWith("TickRiver was removed", StringComparison.Ordinal)) status = "Removed";
            else if (detail.Contains("preserved", StringComparison.Ordinal)) status = "Customized";
            else if (detail.StartsWith("Bundled executable and database verified.", StringComparison.Ordinal)) status = "Verified";
            else if (detail.StartsWith("TickRiver connected", StringComparison.Ordinal)) status = "Configured";
            var success = status is "Verified" or "Configured";
            return new ConnectionRow(host, status, detail, success ? "✓" : status == "Error" ? "✕" : "!",
                success ? "#E6F4EB" : status == "Error" ? "#FCEBEC" : "#FFF3D6",
                success ? "#237345" : status == "Error" ? "#AE3039" : "#8A5A0A");
        }).ToArray();
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    private sealed record ConnectionRow(string Host, string Status, string Detail, string Symbol, string Background, string Foreground);
}
