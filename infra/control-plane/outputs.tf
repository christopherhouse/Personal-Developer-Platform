# Outputs are populated in T011 once main.tf creates the resources: the Postgres server
# name + FQDN and the VNet/subnet ids the spec-006 runtime binds to. Intentionally empty
# during Phase 1 scaffolding so `tofu validate` stays green with no resources defined.
