using Microsoft.Extensions.DependencyInjection;
using PdfService.WebApi.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddPdfCors();

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        //
        options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    });

builder.Services.AddPdfServices(builder.Configuration);

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 500 * 1024 * 1024;
});

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "PDF Service v1");
    options.RoutePrefix = "swagger";
});

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler("/error");
}

app.UseCors("AllowAll");

app.Use(async (context, next) =>
{
    var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();

    logger.LogInformation(
        "Request: {Method} {Path}",
        context.Request.Method,
        context.Request.Path
        );

    await next();

    logger.LogInformation(
        "Response: {StatusCode}",
        context.Response.StatusCode
        );
});

app.MapControllers();
app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    timestamp = DateTime.UtcNow
}));

app.MapGet("/", () => Results.Ok(new
{
    name = "PDF Service",
    version = "1.0.0",
    documentation = "/swagger",
    endpoints = new
    {
        merge = "POST /api/pdf/merge",
        split = "POST /api/pdf/split",
        rotate = "POST /api/pdf/rotate",
        extract = "POST /api/pdf/extract",
        htmlToPdf = "POST /api/pdf/html-to-pdf",
        compressPdf = "POST /api/pdf/compress-pdf",
        officeToPdf = "POST /api/pdf/office-to-pdf",
        taskStatus = "GET /api/pdf/tasks/{taskId}",
        download = "GET /api/pdf/download/{taskId}"
    }
}));

app.UseHttpsRedirection();

app.Run();
