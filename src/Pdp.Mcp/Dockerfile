# syntax=docker/dockerfile:1
# pdp-mcp (spec 007) — the chatops MCP server. BUILD CONTEXT = repository root (multi-project solution
# with ProjectReferences + central package management). CI builds + pushes this to ACR over OIDC (T024).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/ src/
RUN dotnet restore src/Pdp.Mcp/Pdp.Mcp.csproj
RUN dotnet publish src/Pdp.Mcp/Pdp.Mcp.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./
# Non-root (the aspnet image defines APP_UID).
USER $APP_UID
ENTRYPOINT ["dotnet", "pdp-mcp.dll"]
