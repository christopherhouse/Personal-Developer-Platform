# Pdp.Cli (`pdp`)

Control plane (spec 006). The `pdp` CLI (System.CommandLine 2.0 GA) — the owner's one command for the
whole platform: `pdp spoke|fabric|ipam|inventory|env|run`, each in human and `--json` form. It hosts
the verb layer in-process under the owner's `DefaultAzureCredential`; a mutating command dispatches a
workflow then polls the registry/GitHub correlation to terminal (or returns immediately with
`--no-wait`, leaving completion to the Api host's durable reconciler).

> The MCP server surfacing these same verbs = **spec 007**.
