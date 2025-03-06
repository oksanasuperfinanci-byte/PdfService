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

        services.AddHostedService<PdfProcessingWorker>();

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
