# Service-owned policies and affected-service verification

Status: explicit-baseline coordination implemented. Version-two policies, commands,
migration steps and supported limits are documented in `README.md`. Automatic
local baseline selection remains a follow-up.

## Objective

Allow each service or shared library in a monorepo to own its `fdull.json`.
Running FDull from the repository root discovers those policies and verifies
the services affected by a change, including consumers of changed dependencies.
No root policy or central list of services is required.

```text
repo/
  global.json
  Directory.Build.props
  services/
    billing/fdull.json
    orders/fdull.json
  libraries/
    contracts/fdull.json
```

A service is a policy-owned workspace, potentially containing several projects.
Verification initially selects whole services, rather than individual projects
within a service. This preserves exact inventories and unused-contract checks.

## Existing constraints

- `WorkspacePolicy.readWithSourceDrift` loads one `<root>/fdull.json` and requires
  project, source, reference, and input paths to remain inside that workspace.
- `WorkspaceAudit.validate` requires the policy to account for all F# projects
  and sources beneath its root. It also inventories protected build inputs.
- F# project references must resolve within the policy's graph. Reviewed external
  implementation references currently cover C# and Visual Basic, not another
  policy's F# projects.
- `WorkspaceEngine` independently reads the policy and checks its digest and
  inputs. Host-side discovery alone cannot change the worker's trust model.
- `Project.verifyTimed` restores, builds, analyzes, rebuilds, and finally checks
  the workspace's inputs. Rebuilding a dependency can invalidate a consumer's
  previously accepted reference hashes.
- Shared ancestor build files are outside a service-root inventory. Simply
  invoking the existing verifier in every service would not protect them.
- Verification bypasses the local analysis cache. Selection history must not
  silently weaken that guarantee.

## Proposed command behavior

```sh
fdull verify . --since <base-commit>  # Verify affected services
fdull verify . --all                 # Verify all discovered services
fdull verify services/billing        # Explicitly verify one service
```

- Preserve existing behavior when the supplied directory contains a single
  existing policy and no nested policies.
- When the supplied root has no policy, discover descendant policies and use
  repository coordination mode. No policies is an error, not a passing result.
- In coordination mode, bare `verify .` verifies all services initially. A later
  automatic-baseline feature may select affected services only when it has an
  explicitly defined baseline source. Never silently interpret a clean working
  tree as an unchanged branch.
- `--since` accepts a commit resolved once to an immutable commit ID. Compare that
  revision with the current working snapshot, including committed, staged,
  unstaged, and relevant untracked changes. CI supplies the intended comparison
  commit, such as its computed merge base; FDull does not guess a remote branch.
- An invalid or unavailable explicit baseline blocks the run. Do not silently
  replace it with another revision or an empty change set.
- `--all` and `--since` are mutually exclusive. The initial affected mode requires
  Git; full verification remains available without Git.
- An explicit service invocation must eventually prepare and verify required
  cross-policy dependencies, but need not verify unrelated reverse consumers.
- Leave `init <service>` as an explicit local operation. Root initialization must
  not create or overwrite policies for all discovered services automatically.

The first release supports explicit affected selection. Making a bare root
invocation automatically incremental is a follow-up with a separate baseline
contract, not a prerequisite for distributed policies.

## Policy ownership and paths

Introduce separate concepts for repository root, policy root, and project owner.
Keep service-local source and capability paths relative to their policy root.
Normalize identities to repository-relative paths inside the coordinator.

- Every included F# project has exactly one policy owner. All F# source must be
  accounted for by an owner or an existing reviewed project exclusion.
- Reject overlapping or nested policy roots in the first implementation, including
  a root policy combined with child policies. Define delegation explicitly before
  supporting that arrangement.
- Preserve bounded discovery, deterministic ordering, generated-directory
  exclusions, and protection against symlink escapes. Canonical aliases must not
  create multiple owners for the same project or policy.
- Scan repository ownership even when only one service is affected. A new project
  without a policy or a deleted policy with surviving sources blocks selection.
- Keep external classifications and excluded projects scoped to their owner.
  Report repository-level non-F# content outside service scopes as outside the
  verification claim; do not imply that discovery certifies it.
- Separate structural policy loading for planning from full fingerprint and
  semantic validation for execution. Reading metadata for every service must not
  require restoring or exporting every project.

Version any policy extensions explicitly. Existing version-one documents retain
their current meaning. Do not synthesize a merged policy that broadens permissions
or allows one service's capabilities to approve another service's implementation.

## Shared inputs

Add reviewed shared-input pins to service policies using an unambiguous
repository-relative representation. Do not enable arbitrary `../` paths in
existing local fields. Shared paths must remain inside the repository.

Account for ancestor configuration and repository-local imported build controls,
including `global.json`, NuGet configuration, `Directory.Build.props`,
`Directory.Build.targets`, and `Directory.Packages.props`. Build an input-to-owner
index. Changed shared controls select every consumer; when the dependency cannot
be established safely, select all services or block unsupported build behavior.

Selection and fingerprint validation are separate: selecting a service because
a shared file changed does not approve its new contents. Every affected policy's
reviewed pin must still match. The host and worker must bind shared inputs to the
same snapshot and recheck them before accepting verification.

Audit evaluated repository-local imports against the reviewed inputs during
selected-service verification. Unsupported dynamic inputs must remain explicit
limitations or blockers, not become implied complete coverage.

## Change selection

Construct the service graph from reviewed project references and ownership.
Validate literal declarations and, during execution, require agreement with the
evaluated compiler graph as today.

1. Snapshot the baseline identity, current policy inventory, ownership, and
   selection-relevant inputs.
2. Read changed paths without executing historical repository code. Handle
   filenames safely and include both sides of renames and deletions.
3. Consult ownership and dependency metadata from both the baseline and current
   tree so removed edges and moved projects do not hide affected consumers.
4. Select owners of changed sources, policies, scope documents, project files,
   locks, protected inputs, and other files within their service directories.
   Conservative whole-directory selection is acceptable initially.
5. Add transitive reverse consumers of changed services and shared-input owners.
6. Add the dependency closure needed to establish the selected services' build
   and verification evidence. Distinguish affected services from dependencies
   included to support execution.
7. Recheck selection inputs before accepting the aggregate result. Concurrent
   changes to skipped-service ownership, graphs, or relevant files invalidate
   the plan; they must not produce a false successful result.

Use conservative fallback when impact is uncertain. Unreadable or inconsistent
metadata that prevents a sound plan blocks the run. A deliberately removed
service is valid only when its remaining sources and consumers are accounted for.

Documentation-only changes outside protected inputs may yield an empty selection.
Report this as “no affected services,” with the baseline and scope, rather than
as a full repository verification pass. Incremental results rely on external CI
policy for the status of unchanged services; Git ancestry alone is not evidence
that a baseline passed FDull.

## Cross-policy verification

Extend project references to identify an exact project owned by another discovered
policy. A cross-policy edge is not an external-code exemption.

- Enforce existing profile-direction rules across ownership boundaries.
- Reject unknown targets, cycles, duplicate ownership, and references to excluded
  F# projects.
- Define how domain and constructor contracts apply across policy boundaries
  before enabling these references. Never union them globally by convenience.
- Keep worker requests bound to the owning policy and every dependency-policy
  input needed for the check. Workers independently validate those bindings.
- Use one combined project scheduler and one global `--jobs` budget. Do not run
  independent per-service restore/build loops against shared dependency outputs.
- Preserve sequential restores where closures overlap, shared-output-directory
  serialization, dependency-first analysis/rebuild, and a final validation pass
  after all scheduled builds finish.
- Do not apply the existing per-workspace project limit accidentally to the entire
  monorepo. Define separate bounded repository discovery and graph limits.

## Reports and exit behavior

Add a repository report containing the comparison commit, selection mode,
discovered policies, affected services, dependency-only selections, skipped
services, reasons, per-service results, and aggregate completeness.

Preserve exit meanings: `0` for the successfully completed requested scope,
`1` for completed checks with violations, `2` for invalid usage, and `3` for
blocked or incomplete work. Any incomplete required service blocks the aggregate
result. Do not mark skipped services as verified or cached passes.

Initially retain text output for `verify`. Define repository JSON/SARIF schemas
when adding coordination to `lint` and `review`; do not silently change existing
single-workspace report schemas. Keep advisory review distinct from verification.

## Implementation sequence

- [x] **1. Define contracts and fixtures.** Finalize ownership, baseline semantics,
  shared-input schema, report types, and compatibility behavior. Add independent
  services and shared-library fixture layouts.
- [x] **2. Discover and plan.** Add compiled F# discovery and change-selection
  modules with bounded Git process handling. Separate structural policy reads
  from execution validation. Expose selection reasons for testing and inspection.
- [x] **3. Verify independent services.** Add root coordination, reviewed shared
  inputs, `--all`, and `--since`. Keep unsupported cross-policy F# references as
  explicit blockers. Complete host/worker input binding before claiming shared
  configuration support.
- [x] **4. Support shared F# projects.** Implement versioned cross-policy edges,
  contract visibility, a combined scheduler, and final aggregate input checks.
  This stage is required for the complete monorepo use case.
- [x] **5. Integrate workflows.** Extend `lint`, `review`, initialization guidance,
  timings, and reports as appropriate. Document the explicit-baseline CI workflow,
  full verification, migration, and limits.
- [ ] **6. Consider automatic local selection.** Specify prior-run state,
  tool-version invalidation, branch switching, dirty snapshots, and failure
  recovery. Local history must not become authoritative verification evidence.

The initial implementation coordinates text `verify`; `lint` and advisory
`review` retain their single-policy schemas. Cross-policy edges do not share
capabilities, data approvals, domain contracts or constructor approvals. Domain
and constructor contracts on either side of a cross-policy edge are blocked
until a separate visibility contract is introduced. Authored imports must be
literal, repository-local and pinned; SDK property-driven custom import hooks
and arbitrary target-time file reads remain outside the dependency model.

Executable monorepo regressions are in `tests/FDull.Tests/RepositoryTests.fs`.
They cover actual CLI and combined-scheduler execution, transitive and removed
edges, staged/unstaged/committed/untracked changes, branch switching, skipped
snapshot drift, shared pins, actual custom imports, case-sensitive configuration
names, ignored input trees, invalid ownership/profiles, exclusions and cycles.
`WorkspaceTests.fs` also exercises version-one documents without `Repository`.

Likely implementation areas are `WorkspacePolicy.fs`, `WorkspaceAudit.fs`,
`Workspace.fs`, `WorkspaceEngine.fs`, `WorkspaceGenerated.fs`, `Project.fs`,
the CLI parser, and the worker protocol. Add dedicated repository planning types
and modules rather than overloading every use of the current workspace `root`.

## Validation and acceptance

Executable regression coverage must demonstrate:

- Root execution without a root policy; unchanged single-policy behavior.
- One changed independent service leaves unrelated services unbuilt and unanalyzed.
- Shared-library changes select transitive consumers; shared build-input changes
  select every relevant owner and still require reviewed fingerprints.
- Staged, unstaged, committed, and untracked changes; unusual filenames;
  additions, deletions, renames, removed references, and branch switching.
- Missing baselines, shallow-history gaps, no policies, invalid policies, deleted
  configs with surviving projects, orphan sources, ambiguous ownership, and
  symlink escapes fail explicitly.
- Cross-policy profile violations, excluded targets, cycles, and conflicting
  contract visibility cannot pass.
- Dependency preparation does not multiply the jobs budget or race shared output
  directories. Final evidence survives the complete build schedule.
- Concurrent changes to policies, shared inputs, dependency outputs, ownership,
  or selection inputs prevent acceptance of stale results.
- Empty selections and partial results report their actual scope and preserve
  failure exits. Verification never relies on the analysis cache.

After implementation, review exact authored contracts and regression evidence,
remove unused capabilities, and intentionally update source/build fingerprints.
Never generate permissions from diagnostics or add suppressions. Run the existing
formatting check, locked restore, Release build, tests, and CLI self-verification
commands documented in `README.md`. Packaging changes additionally require local
tool installation and an independent monorepo consumer smoke check.

Completion means independent and shared-library monorepos can run affected-service
verification from the root, without a central policy, while retaining exact
service ownership, reviewed inputs, dependency ordering, and honest scope reports.
