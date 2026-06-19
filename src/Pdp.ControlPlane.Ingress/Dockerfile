# syntax=docker/dockerfile:1
# Pdp.ControlPlane.Ingress (spec 006, hosted by spec 007) — the YARP reverse proxy and the ONE public
# surface (Article IX). External ingress; routes /webhooks/github -> internal api and /mcp (+ PRM) ->
# internal mcp. No business logic. BUILD CONTEXT = repository root. CI builds + pushes to ACR (T024).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/ src/
RUN dotnet restore src/Pdp.ControlPlane.Ingress/Pdp.ControlPlane.Ingress.csproj
RUN dotnet publish src/Pdp.ControlPlane.Ingress/Pdp.ControlPlane.Ingress.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./
USER $APP_UID
ENTRYPOINT ["dotnet", "Pdp.ControlPlane.Ingress.dll"]
