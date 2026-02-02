# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy csproj and restore
COPY CozeFptWebhook/CozeFptWebhook/CozeFptWebhook.csproj CozeFptWebhook/CozeFptWebhook/
RUN dotnet restore CozeFptWebhook/CozeFptWebhook/CozeFptWebhook.csproj

# Copy everything else and publish
COPY . .
RUN dotnet publish CozeFptWebhook/CozeFptWebhook/CozeFptWebhook.csproj -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# Render thường dùng port 10000 (hoặc bạn set trong Render)
ENV ASPNETCORE_URLS=http://+:10000
EXPOSE 10000

ENTRYPOINT ["dotnet", "CozeFptWebhook.dll"]
