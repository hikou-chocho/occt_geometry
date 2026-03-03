using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// Log to stderr so stdout stays clean for MCP JSON-RPC messages
builder.Logging.AddConsole(opts =>
{
	opts.LogToStandardErrorThreshold = LogLevel.Trace;
});

builder.Services
	.AddMcpServer()
	.WithStdioServerTransport()
	.WithToolsFromAssembly();

await builder.Build().RunAsync();
