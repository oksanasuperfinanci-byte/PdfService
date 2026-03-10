using Microsoft.Extensions.DependencyInjection.Extensions;
using PdfService.Application.Interfaces;
using PdfService.Application.Jobs;
using PdfService.Infrastructure.Services;
using PdfService.Infrastructure.Storage;

namespace PdfService.WebApi.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPdfServices(
        this IServiceCollection services,
        IConfiguration configuration
        )
    {
        services.Configure<FileStorageOptions>(configuration.GetSection(FileStorageOptions.SectionName));

        services.AddSingleton<IFileStorage, LocalFileStorage>();

        services.AddSingleton<ITaskStore, InMemoryTaskStore>();

        services.AddSingleton<IPdfProcessor, PdfProcessor>();

        // Читаем WorkerCount из конфигурации для запуска N параллельных воркеров
        var storageSection = configuration.GetSection(FileStorageOptions.SectionName);
        var workerCount = storageSection.GetValue<int?>("WorkerCount") ?? 2;

        for (int i = 0; i < workerCount; i++)
        {
            var workerId = i;
            services.AddSingleton<IHostedService>(sp =>
                new PdfProcessingWorker(
                    sp.GetRequiredService<ITaskStore>(),
                    sp.GetRequiredService<IPdfProcessor>(),
                    sp.GetRequiredService<ILogger<PdfProcessingWorker>>(),
                    workerId));
        }

        services.AddHostedService<StorageCleanupWorker>();

        return services;
    }

    public static IServiceCollection AddPdfCors( this IServiceCollection services)
    {
        services.AddCors(options =>
        {
            options.AddPolicy("AllowAll", builder =>
            {
                builder
                    .AllowAnyOrigin()
                    .AllowAnyMethod()
                    .AllowAnyHeader();
            });

            options.AddPolicy("Production", builder =>
            {
                builder.WithOrigins(
                    "https://mydomenname.abc",
                    "https://www.mydomenname.abc")
                .AllowAnyMethod()
                .AllowAnyHeader()
                .AllowCredentials();
            });
        });

        return services;
    }
}
