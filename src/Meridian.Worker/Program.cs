using Meridian.Infrastructure.Features.PdfImports.Processing;
using Meridian.Infrastructure.Configuration;
using Meridian.Infrastructure;
using Meridian.Infrastructure.Features.Imports.Processing;
using Meridian.Infrastructure.Features.Snapshots.Processing;
using Meridian.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
builder.Configuration.AddKeyVaultIfConfigured(builder.Configuration);
builder.Services.AddMeridianInfrastructure(builder.Configuration);
builder.Services.AddSingleton<CsvImportJobProcessor>();
builder.Services.AddSingleton<RankingSnapshotJobProcessor>();
builder.Services.AddSingleton<PdfExtractionJobProcessor>();
builder.Services.AddHostedService<Worker>();
if (!string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]) || !string.IsNullOrWhiteSpace(builder.Configuration["ApplicationInsights:ConnectionString"]))
    builder.Services.AddApplicationInsightsTelemetryWorkerService();
var host = builder.Build();
await host.RunAsync();
