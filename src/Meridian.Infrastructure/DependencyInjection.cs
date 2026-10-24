using Meridian.Infrastructure.Features.PdfImports.Contracts;
using Meridian.Infrastructure.Features.PdfImports.Processing;
using Meridian.Infrastructure.Features.PdfImports.Services;
using Meridian.Infrastructure.BackgroundJobs;
using Meridian.Infrastructure.Features.Imports.Contracts;
using Meridian.Infrastructure.Features.Imports.Services;
using Meridian.Infrastructure.Features.Imports.Processing;
using Meridian.Infrastructure.Features.Conflicts.Contracts;
using Meridian.Infrastructure.Features.Conflicts.Services;
using Meridian.Infrastructure.Features.Votes.Contracts;
using Meridian.Infrastructure.Features.Votes.Services;
using Meridian.Infrastructure.Features.Snapshots.Contracts;
using Meridian.Infrastructure.Features.Snapshots.Services;
using Meridian.Infrastructure.Identity;
using Meridian.Infrastructure.Persistence;
using Meridian.Infrastructure.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Meridian.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddMeridianInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Meridian")
            ?? throw new InvalidOperationException("ConnectionStrings:Meridian is required.");
        services.AddSingleton<NpgsqlDataSource>(_ => NpgsqlDataSource.Create(connectionString));
        services.AddSingleton<IImportFileStorage>(_ => ImportFileStorageRegistration.Create(configuration));
        services.AddSingleton(provider => new DatabaseBootstrapper(connectionString, provider.GetRequiredService<ILogger<DatabaseBootstrapper>>()));
        services.AddSingleton<JobProgressReporter>();
        services.AddSingleton<CsvImportValidator>();
        services.AddSingleton<DemoUserService>();
        services.AddSingleton<IImportService, ImportService>();
        services.AddSingleton<IConflictService, ConflictService>();
        services.AddSingleton<IVoteService, VoteService>();
        services.AddSingleton<ISnapshotService, SnapshotService>();
        services.AddSingleton<IDocumentAnalysisClient>(_ => new AzureDocumentAnalysisClient(new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromSeconds(30) }, configuration));
        services.AddSingleton<PdfLayoutParser>();
        services.AddSingleton<PdfDraftValidator>();
        services.AddSingleton<IPdfImportService, PdfImportService>();
        return services;
    }
}
