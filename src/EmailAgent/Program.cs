using EmailAgent.Configuration;
using EmailAgent.Services;
using EmailAgent.Workers;

var builder = Host.CreateApplicationBuilder(args);

// ── Configuration ─────────────────────────────────────────────────────────
builder.Services.Configure<GraphSettings>(
    builder.Configuration.GetSection("Graph"));

builder.Services.Configure<AIFoundrySettings>(
    builder.Configuration.GetSection("AIFoundry"));

builder.Services.Configure<EmailProcessingSettings>(
    builder.Configuration.GetSection("EmailProcessing"));

// ── Services ──────────────────────────────────────────────────────────────
// GraphEmailService is a singleton because it holds a Graph client and
// a folder-ID cache that is safe to reuse across poll cycles.
builder.Services.AddSingleton<IGraphEmailService, GraphEmailService>();

// AIAgentService is a singleton because it lazily initialises the Foundry
// client and ResponsesClient once and reuses them for the lifetime of the
// application.
builder.Services.AddSingleton<IAIAgentService, AIAgentService>();

// ── Background worker ─────────────────────────────────────────────────────
builder.Services.AddHostedService<EmailMonitorWorker>();

var host = builder.Build();
host.Run();
