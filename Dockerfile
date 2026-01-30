# ===== Build stage =====
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY CozeFptWebhook/CozeFptWebhook/CozeFptWebhook.csproj CozeFptWebhook/CozeFptWebhook/
RUN dotnet restore CozeFptWebhook/CozeFptWebhook/CozeFptWebhook.csproj

COPY . .
WORKDIR /src/CozeFptWebhook/CozeFptWebhook
RUN dotnet publish -c Release -o /app/publish

# ===== Runtime stage =====
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 10000
ENTRYPOINT ["dotnet", "CozeFptWebhook.dll"]
