using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TaskManager.Data;
using TaskManager.Mcp;
using TaskManager.Services;

// stdio MCP server over the TaskManager database. Usage: TaskManager.Mcp.exe [--data <folder>]
var dataIndex = Array.IndexOf(args, "--data");
var paths = dataIndex >= 0 && dataIndex + 1 < args.Length ? new AppPaths(args[dataIndex + 1]) : AppPaths.Default();

var builder = Host.CreateApplicationBuilder(args);
// stdout carries the protocol; all logs go to stderr.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.Services.AddSingleton(paths);
builder.Services.AddSingleton(_ => new Db(paths.Database));
builder.Services.AddSingleton<TaskService>();
builder.Services
    .AddMcpServer(o =>
    {
        o.ServerInfo = new() { Name = "taskmanager", Title = "TaskManager", Version = "1.0.0" };
        o.ServerInstructions = """
            Local task tracker of a solo developer (Linear-like). Issues have keys like TM-12 (project key + number),
            statuses backlog/todo/in_progress/done/canceled and priorities urgent/high/medium/low/none.
            Descriptions are Markdown. Changes appear in the desktop app immediately.
            When you start working on an issue set it to in_progress; when finished set it to done and
            add a short summary with update_issue(append_description=...).
            The knowledge base (kb_* tools) is the user's folders of markdown notes; #words in them are tags.
            To reference an entry from an issue, put kb_read's link_markdown into the description and/or call link_entry.
            """;
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
