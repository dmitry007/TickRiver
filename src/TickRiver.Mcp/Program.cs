using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using TickRiver.Core;
using TickRiver.Mcp;

var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TickRiver", "tickriver.sqlite");
if (args.Length == 2 && args[0] == "--database") path = Path.GetFullPath(args[1]);
else if (args.Length != 0) { Console.Error.WriteLine("Usage: TickRiver.Mcp.exe [--database <sqlite-path>]"); return 2; }
var builder = Host.CreateApplicationBuilder();
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddSingleton(new Store(path, readOnly: true));
builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<TimeTools>().WithTools<TimerTools>();
await builder.Build().RunAsync();
return 0;
