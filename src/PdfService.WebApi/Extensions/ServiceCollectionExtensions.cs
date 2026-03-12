using PdfService.Application.Interfaces;
using PdfService.Application.Jobs;
using PdfService.Infrastructure.Services;
using PdfService.Infrastructure.Storage;
using StackExchange.Redis;

namespace PdfService.WebApi.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPdfServices(
        this IServiceCollection services,
        IConfiguration configuration
        )
    {
        // ── Настройки ──────────────────────────────────────────────
        services.Configure<FileStorageOptions>(configuration.GetSection(FileStorageOptions.SectionName));
        services.Configure<GotenbergOptions>(configuration.GetSection(GotenbergOptions.SectionName));

        // ── Файловое хранилище ─────────────────────────────────────
        services.AddSingleton<IFileStorage, LocalFileStorage>();

        // ── Task Store: Redis или InMemory ─────────────────────────
        var redisConnection = configuration.GetSection("Redis")
            .GetValue<string>("ConnectionString");

        if (!string.IsNullOrEmpty(redisConnection))
        {
            // Redis доступен — distributed mode
            // IConnectionMultiplexer — singleton, потокобезопасный, мультиплексирует соединения.
            // Создаём один экземпляр на всё приложение.
            services.AddSingleton<IConnectionMultiplexer>(sp =>
            {
                var logger = sp.GetRequiredService<ILogger<RedisTaskStore>>();
                logger.LogInformation("Connecting to Redis: {Connection}", redisConnection);

                var options = ConfigurationOptions.Parse(redisConnection);
                options.AbortOnConnectFail = false;  // Не падаем при старте, если Redis ещё не готов
                options.ConnectRetry = 3;            // 3 попытки подключения
                options.ConnectTimeout = 5000;       // 5 сек на подключение

                return ConnectionMultiplexer.Connect(options);
            });

            services.AddSingleton<ITaskStore, RedisTaskStore>();
        }
        else
        {
            // Redis не настроен — InMemory fallback для локальной разработки
            services.AddSingleton<ITaskStore, InMemoryTaskStore>();
        }

        // ── PDF Processor: Gotenberg или локальный ─────────────────
        var gotenbergSection = configuration.GetSection(GotenbergOptions.SectionName);
        var gotenbergUrl = gotenbergSection.GetValue<string>("BaseUrl");

        // PdfProcessor регистрируется всегда — GotenbergPdfProcessor делегирует ему
        // локальные операции (Merge, Split, Rotate, ExtractPages), избегая дублирования кода.
        services.AddSingleton<PdfProcessor>();

        if (!string.IsNullOrEmpty(gotenbergUrl))
        {
            var timeoutSeconds = gotenbergSection.GetValue<int?>("TimeoutSeconds") ?? 180;

            services.AddHttpClient<IPdfProcessor, GotenbergPdfProcessor>(client =>
            {
                client.BaseAddress = new Uri(gotenbergUrl);
                client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
            });
        }
        else
        {
            services.AddSingleton<IPdfProcessor>(sp => sp.GetRequiredService<PdfProcessor>());
        }

        // ── Background Workers ─────────────────────────────────────
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
