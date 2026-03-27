FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY src/DiscoveryAgent.Core/ DiscoveryAgent.Core/
COPY src/DiscoveryAgent/ DiscoveryAgent/
COPY config/ config/

RUN dotnet publish DiscoveryAgent/DiscoveryAgent.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# Copy published app
COPY --from=build /app .

# Copy web UI into wwwroot for static file serving
COPY web/ wwwroot/

# Copy config (instructions.md)
COPY config/ config/

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "DiscoveryAgent.dll"]
