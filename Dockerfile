FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY src/PdfService.Application/PdfService.Application.csproj PdfService.Application/
COPY src/PdfService.Infrastructure/PdfService.Infrastructure.csproj PdfService.Infrastructure/
COPY src/PdfService.WebApi/PdfService.WebApi.csproj PdfService.WebApi/
RUN dotnet restore PdfService.WebApi/PdfService.WebApi.csproj

COPY src/ .
RUN dotnet publish PdfService.WebApi/PdfService.WebApi.csproj -c Release -o /app/publish --no-restore

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .

# Создаём директорию для temp-storage
RUN mkdir -p /app/temp-storage

ENTRYPOINT ["dotnet", "PdfService.WebApi.dll"]
