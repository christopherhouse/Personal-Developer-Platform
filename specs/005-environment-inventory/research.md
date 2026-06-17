# Phase 0 Research — Environment Inventory

All clarifications were resolved in `/speckit-clarify` (spec §Clarifications, 2026-06-17), so there
are **no open `NEEDS CLARIFICATION` items**. This document records the technical decisions that
back the plan, grounded in the Microsoft Learn MCP (Azure SDK surfaces are version-sensitive — not
answered from memory).

---

## §1 — Source of truth: Azure Resource Graph, resource-group granularity

**Decision**: Query the ARG **`ResourceContainers`** table for resource groups, not the `Resources`
table. Filter is `type =~ 'microsoft.resources/subscriptions/resourcegroups'`. Inventory classifies
at **resource-group** granularity (clarify Q4); individual resources inside RGs are not enumerated.

**Rationale**: The mandatory `pdp-*` tag schema lives on **resource groups** (`docs/conventions.md`
§2). RG-level rows answer all three headline questions and keep the query small/fast (SC-005).
`ResourceContainers` also holds `microsoft.resources/subscriptions` rows, useful for joining the
subscription display name.

**Alternatives considered**: Querying `Resources` and grouping by RG — rejected: far larger result
set, redundant (tags are RG-scoped), and would tempt resource-level scope creep that Q4 closed.

**Core query (managed RGs)** — shape, not final code:

```kusto
ResourceContainers
| where type =~ 'microsoft.resources/subscriptions/resourcegroups'
| where tags['pdp-managed'] =~ 'true'
| project id, name, subscriptionId, location,
          managed=tags['pdp-managed'], deployedBy=tags['pdp-deployed-by'],
          fabric=tags['pdp-fabric'], spoke=tags['pdp-spoke'],
          workload=tags['pdp-workload'], env=tags['pdp-env']
```

**Invisible-drift query (looks-managed-but-untagged, FR-014)** — a *separate* projection so a
missing/`!= true` `pdp-managed` is caught rather than filtered away:

```kusto
ResourceContainers
| where type =~ 'microsoft.resources/subscriptions/resourcegroups'
| where name startswith 'rg-pdp-'
| where isnull(tags['pdp-managed']) or tags['pdp-managed'] !~ 'true'
| project id, name, subscriptionId, location
```

The seed backend `RG-TF` does **not** match `rg-pdp-*`, so it is excluded by construction (FR-018);
the classifier additionally hard-excludes the `RG-TF`/`cmhtfstatesa` identifiers as a belt-and-braces
guard.

---

## §2 — .NET SDK surface (pinned facts)

**Decision**: Use `Azure.ResourceManager.ResourceGraph` (**v1.1.0**, stable) for queries and
`Azure.ResourceManager` (**v1.14.x**) for subscription discovery; authenticate through an injected
`Azure.Core.TokenCredential`.

- **Query**: `TenantResource.GetResources(ResourceQueryContent)` /
  `GetResourcesAsync(...)`. `ResourceQueryContent` carries the KQL string, the **`Subscriptions`**
  scope list, and `ResourceQueryRequestOptions` (`ResultFormat`, `SkipToken`, etc.).
- **Pagination**: response `ResourceQueryResult.SkipToken` → feed into the next request's
  `Options.SkipToken`; loop until null. Page cap is **1000** records. `resultTruncated` /
  `$skipToken` semantics per the ARG pagination guidance.
- **Subscription discovery**: `ArmClient.GetSubscriptions()` returns the
  `SubscriptionCollection` the credential can see — this satisfies "discover every subscription"
  (FR-001) with no hardcoding.
- **Credential**: `new ArmClient(TokenCredential)`; the demo injects `DefaultAzureCredential`
  (owner's `az login`), spec 006 injects the control-plane managed identity. Component never
  constructs a credential itself (FR-019).

**Rationale**: First-party, GA SDK; `Subscriptions` scoping + `allowPartialScopes` give honest
coverage; `SkipToken` is the documented paging contract.

**Alternatives considered**: `az graph query` shell-out — rejected (not typed, not embeddable in the
006 control plane); the legacy `Microsoft.Azure.Management.ResourceGraph` — rejected (track-1,
superseded by `Azure.ResourceManager.*`, which `docs/tech-stack.md` already names for inventory).

---

## §3 — Cross-subscription scope & coverage honesty

**Decision**: Enumerate accessible subscriptions first (`GetSubscriptions()`), then pass their IDs as
the ARG query **`Subscriptions`** scope. Track each subscription as covered or **skipped/inaccessible**
and include that in the result (FR-011 / SC-008). Use `allowPartialScopes` so one unreadable
subscription never fails the whole sweep.

**Rationale**: Explicit scoping + per-subscription coverage is what makes "none silently omitted"
testable. ARG accepts up to 1000 subscription scopes per call; batch only in the (unlikely) event of
more.

**Alternatives considered**: Pure tenant-scope query (`UseTenantScope`) — rejected as the primary
path because it blurs which subscriptions were actually reachable, weakening coverage honesty; kept
as a possible fallback noted in the adapter.

---

## §4 — Classification & drift rules (the pure engine)

**Decision**: A deterministic classifier maps each managed RG row to exactly one outcome, and a drift
detector emits findings. Rules (full table in `data-model.md`):

- **Scope tag count** decides type: exactly one of `pdp-fabric` / `pdp-spoke` / `pdp-workload` →
  fabric / spoke / workload. **Zero** scope tags → **orphan** drift. **More than one** → **ambiguous**
  drift.
- **Environment grouping**: workloads group by `pdp-env`; a workload RG **missing** `pdp-env` is a
  **conformance** finding (still listed, ungrouped).
- **Conformance** validation against `docs/conventions.md` §2: `pdp-fabric` must be a known Azure
  region; `pdp-spoke`/`pdp-workload` match `[a-z0-9-]{1,24}`; `pdp-env` matches `[a-z0-9-]{1,16}`;
  `pdp-deployed-by` ∈ {`github-actions`, `control-plane`, `owner`}; universal tags
  (`pdp-managed`, `pdp-deployed-by`) present.
- **Region** comes from the RG **`location`** (spokes carry no region tag — spec 004); a `pdp-fabric`
  value that disagrees with `location` is a conformance finding.
- **Invisible** drift from the §1 second query; **seed backend** excluded.
- **Drift is informational** (Q5 / FR-016): findings live in the result; the service always
  succeeds.

**Rationale**: Keeping this logic pure (row in → typed verdict out) makes every branch unit-testable
with fabricated rows and no Azure — the seam (`IResourceGraphReader`) is the only Azure boundary.

**Alternatives considered**: Doing classification inside the KQL (e.g., computed columns) — rejected:
harder to unit-test, harder to evolve the convention rules, and couples drift logic to query strings.

---

## §5 — Result shape, rendering, and glossary

**Decision**: Return an immutable `InventorySnapshot` record graph (environments, fabrics, spokes,
workloads, drift findings, subscription coverage). The demo harness renders it two ways from the same
object: a human table and `--json` (`System.Text.Json`), proving FR-010 / SC-006. **Drift category
vocabulary** (orphan / conformance / invisible / ambiguous) is documented in `data-model.md`;
**follow-up**: add a one-line **"Drift"** entry to `docs/glossary.md` (constitution Article X) since
the term will recur in specs 006/007/010 — noted here, applied when those specs land or as a small
docs PR.

**Rationale**: One typed graph, two renderers, is the cleanest way to serve both humans and the
future programmatic consumers without a display/data split.

**Alternatives considered**: Returning formatted strings — rejected (FR-010 forbids consumers parsing
text). A full OpenAPI/JSON-schema contract now — deferred to spec 006 where the verb layer defines
the external contract; this spec ships the in-process typed model it will wrap.

---

## §6 — Performance approach

**Decision**: One subscription-discovery call + one (paged) ARG query for managed RGs + one (paged)
ARG query for invisible-drift candidates. Classification/grouping/drift are in-memory over the
returned rows. Target P95 < 5 s full sweep, < 2 s scoped (SC-005).

**Rationale**: ARG is purpose-built for fast cross-subscription reads; two projections over
`ResourceContainers` return only RG rows (small). No per-resource fan-out (Q4) keeps latency flat as
resource counts grow.

**Alternatives considered**: Per-subscription sequential RG listing via the ARM RG API — rejected:
N round-trips, slower, and not the Article III "Resource Graph" path.

---

## §7 — Testing strategy

**Decision**: Unit-test the pure engine (classifier, drift detector, grouping, tag-schema
validation, and `InventoryService` orchestration over a **faked `IResourceGraphReader`**) with
xUnit + NSubstitute + Shouldly. **No Testcontainers** (no Postgres in this spec). Live SCs are
proven by the quickstart running `Pdp.Inventory.Demo` against the deployed `westus3` fabric + spec-004
spokes, including a deliberately mis-tagged RG to exercise drift (SC-003).

**Rationale**: The Article III behavior (classification/drift) is deterministic over rows and must be
exhaustively tested offline; the live run proves the ARG adapter and the latency budget against real
Azure, the same shape spec 004 used for its live verification.

**Alternatives considered**: Mocking the Azure SDK types directly — rejected (sealed/awkward to fake);
the `IResourceGraphReader` seam is the supported abstraction boundary.
