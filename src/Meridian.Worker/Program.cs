using Meridian.Infrastructure;

var builder = Host.CreateApplicationBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Meridian")
    ?? throw new InvalidOperationException("ConnectionStrings:Meridian is required.");
builder.Services.AddSingleton<DatabaseBootstrapper>(provider => new DatabaseBootstrapper(connectionString, provider.GetRequiredService<ILogger<DatabaseBootstrapper>>()));
builder.Services.AddHostedService<Worker>();
var host = builder.Build();
await host.RunAsync();

public sealed class Worker(ILogger<Worker> logger, DatabaseBootstrapper database, IConfiguration configuration) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await database.InitializeAsync(configuration["Seed:MemberStatesPath"], stoppingToken);
        logger.LogInformation("Background worker is ready to process persistent jobs.");
        while (!stoppingToken.IsCancellationRequested)
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
    }
}


