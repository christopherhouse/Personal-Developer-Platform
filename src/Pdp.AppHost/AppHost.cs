// Pdp.AppHost — .NET Aspire local dev orchestration (spec 006): Postgres + Api + Ingress, one F5.
//
// Stands up Postgres with a persistent volume and the shared platform database, migrates it via the CLI's
// host-light `migrate` command, then runs the internal control-plane Api (verb layer + Wolverine
// outbox/inbox + reconciler + webhook handler) behind the public YARP Ingress — the full US5 tracking
// topology locally, host-ready for the spec-007 ACA deployment.
var builder = DistributedApplication.CreateBuilder(args);

// Postgres holds all three schemas (ipam, registry, wolverine) in one database — one connection,
// enabling the atomic allocate-record-dispatch transaction (research §6). The data volume persists
// the schema/data across F5 runs.
var postgres = builder.AddPostgres("pdp-postgres")
    .WithDataVolume();

var pdpDb = postgres.AddDatabase("pdp");

// DB-prep on F5: apply the ipam + registry EF Core schemas via the CLI's host-light `migrate` command
// once the database is reachable. The CLI reads the Aspire-injected ConnectionStrings:pdp. Idempotent.
var migrate = builder.AddProject<Projects.Pdp_Cli>("pdp-migrate")
    .WithReference(pdpDb)
    .WaitFor(pdpDb)
    .WithArgs("migrate");

// The internal Api: AddControlPlaneVerbs + Wolverine durable inbox/outbox + the recurring reconciler +
// the workflow_run webhook endpoint. It binds the Aspire-injected ConnectionStrings:pdp and starts only
// after the schema is migrated (no migrations run in the Api host).
var api = builder.AddProject<Projects.Pdp_ControlPlane_Api>("control-plane-api")
    .WithReference(pdpDb)
    .WaitForCompletion(migrate);

// The public YARP Ingress (Article IX): forwards POST /webhooks/github to the Api. Inject the Api's
// resolved endpoint as the route's destination so YARP targets the right port without service-discovery
// wiring (the Ingress takes no project reference — it stays a pure proxy).
builder.AddProject<Projects.Pdp_ControlPlane_Ingress>("ingress")
    .WithEnvironment(
        "ReverseProxy__Clusters__control-plane-api__Destinations__api__Address",
        api.GetEndpoint("http"))
    .WaitFor(api);

builder.Build().Run();
