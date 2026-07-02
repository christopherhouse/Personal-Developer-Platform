# Ralph Progress Log

Feature: 008-workload-archetypes
Started: 2026-07-02 13:43:13

## Codebase Patterns

- Central package management: pins live in `Directory.Packages.props` ItemGroups labeled
  by spec; csproj files carry bare `PackageReference` with a why-comment.
- Dockerfiles build from repo ROOT context; api runtime stage now COPYs
  `archetypes/catalog.json` (missing until T013 — api image unbuildable until then).

---

## 2026-07-02 — Phase 1 (Setup) complete: T001–T004

- T001: JsonSchema.Net pinned **7.4.0** (latest stable 7.x); referenced by Verbs + Registry; build green.
- T002: `archetypes/README.md` — authoring conventions, release runbook (catalog edit + `archetype/<name>/<version>` tag in same PR), schema conventions.
- T003: glossary — Managed unit / env_id now include workloads; Archetype catalog rewritten to repo-managed-file-projected-to-Postgres model.
- T004: `controlplane-host-images.yml` paths += `archetypes/catalog.json`; api Dockerfile COPYs the catalog into the runtime image.

---

