# syntax=docker/dockerfile:1
# Pdp.ControlPlane.Api (spec 006, hosted by spec 007) — the always-on run-tracking host: GitHub
# workflow_run webhook sink + Wolverine durable inbox/outbox + the polling reconciler. Internal ingress.
# BUILD CONTEXT = repository root. CI builds + pushes this to ACR over OIDC (T024).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/ src/
RUN dotnet restore src/Pdp.ControlPlane.Api/Pdp.ControlPlane.Api.csproj
RUN dotnet publish src/Pdp.ControlPlane.Api/Pdp.ControlPlane.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./
USER $APP_UID
ENTRYPOINT ["dotnet", "Pdp.ControlPlane.Api.dll"]
