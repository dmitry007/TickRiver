using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using ModelContextProtocol.Protocol;
using TickRiver.Core;
using TickRiver.Mcp;

var path = UserStorage.DatabasePath;
if (args.Length == 2 && args[0] == "--database") path = Path.GetFullPath(args[1]);
else if (args.Length != 0) { Console.Error.WriteLine("Usage: TickRiver.Mcp.exe [--database <sqlite-path>]"); return 2; }
var builder = Host.CreateApplicationBuilder();
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
try
{
    if (string.Equals(path, UserStorage.DatabasePath, StringComparison.OrdinalIgnoreCase)) UserStorage.Validate(path, readOnly: true);
    builder.Services.AddSingleton(new Store(path, readOnly: true));
}
catch (Exception error) { Console.Error.WriteLine($"TickRiver could not open its database at {path}: {error.Message}"); return 1; }
builder.Services.AddMcpServer(options => options.ServerInfo = new Implementation { Name = "TickRiver", Version = typeof(TimeTools).Assembly.GetName().Version!.ToString(3) })
    .WithStdioServerTransport().WithTools<TimeTools>().WithTools<TimerTools>().WithTools<EntryTools>();
using var host = builder.Build();
if (OperatingSystem.IsWindows())
{
    if (InstanceControl.UpdateInProgress) return InstanceControl.UpdateInProgressExitCode;
    using var control = new InstanceControl(() => UpdateShutdown.Request(host.Services.GetRequiredService<IHostApplicationLifetime>()), 1);
    if (InstanceControl.UpdateInProgress) return InstanceControl.UpdateInProgressExitCode;
    await host.RunAsync();
}
else await host.RunAsync();
return 0;
