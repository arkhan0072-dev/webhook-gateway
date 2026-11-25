FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY src/WebhookGateway/WebhookGateway.csproj src/WebhookGateway/
RUN dotnet restore src/WebhookGateway/WebhookGateway.csproj
COPY src/ src/
RUN dotnet publish src/WebhookGateway/WebhookGateway.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app .
# Runs as the image's built-in non-root user.
USER app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "WebhookGateway.dll"]
