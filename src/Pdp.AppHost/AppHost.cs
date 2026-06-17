// Pdp.AppHost — .NET Aspire local dev orchestration (spec 006): Postgres + Api + Ingress, one F5.
//
// Phase 1 scaffold: stands up the Postgres resource. Wiring the Api and Ingress as resources (with
// the registry connection + reconciler) lands in T066 (US5); the project references are already in
// place so the Aspire `Projects.*` metadata is generated.
var builder = DistributedApplication.CreateBuilder(args);

builder.AddPostgres("pdp-postgres");

builder.Build().Run();
