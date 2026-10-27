# Incremental workspace analysis: implementation plan

**Status:** In progress. Phase 0 (`--timings`) is implemented.
**Prepared:** 2026-10-27

## Why

`lint` and `review` analyze every included project from scratch, one at a time. Each project costs
two child processes:

- an MSBuild export of the exact compiler invocation (`Workspace.export`, `src/FDull/Workspace.fs`);
- a worker that type-checks the whole project with the F# compiler service
  (`Workspace.analyzeUsing`, run as `fdull-worker --workspace` or `--workspace-review`).

On a 44-project consumer workspace, botf, this measured about 9 seconds per project:

| Command | Time |
|---|---|
| `review` | 6.6 min |
| `lint` | 6.5 min |
| `verify` | 10 min |

A single policy pass needs at least four runs (review, review again after pruning, lint, verify), so
it waits about 30 to 40 minutes on FDull even when the policy work itself is quick. A second
review after a small fix analyzes all 44 projects again, although only a handful changed.

The goal is to make `lint` and `review` cost roughly in proportion to what changed, without
weakening any guarantee `verify` gives.

## What can't change

- **`verify` stays a full analysis from scratch.** It never reads or writes a cache. It is the
  certifying run for CI and for a policy pass's last step.
- **Unused-permission checks still see every project.** A run that analyzes only some projects
  can't report `BUILD005` (unused capability, constructor or data contract) cleanly, so this
  plan reuses earlier results instead of skipping projects.
- **Every result is checked, cached or fresh.** The parent still runs the report-to-request
  check (`BUILD004`) and `WorkspaceProtocol.validate` or `WorkspaceReview.validate` on it.
- **Output is deterministic.** The JSON is byte-identical whatever the job count or cache
  state, except for the cache count line in text output.
- **Each phase lands as FDull requires (AGENTS.md):**
  - formatting, the Release build, tests, and the CLI's `verify`;
  - reviewed `fdull.json` permissions for the new code, with no generated permissions or
    suppressions;
  - README updates for the new flags.

## Phase 0: Measure

**Goal:** know where each project's time goes before building anything.

- Add `--timings` to `lint` and `review`. For each project it writes the time spent on the
  MSBuild export, on the worker, and on the parent's validation to stderr. It also writes the
  run's total.
- Run `review` on a large consumer (botf at `feat/definition-registry`) and record the split.

**Decides:** whether Phase 3 is worth doing. A cache hit still pays for the export, so if the
export dominates, Phase 2 alone saves less than expected.

**Tests:** `--timings` doesn't change stdout or exit codes.

## Phase 1: Analyze projects in parallel

**Where:** `Workspace.checkUsing` and `Workspace.review`, which today loop over
`WorkspaceAudit.order` sequentially.

**Change:**

- Analyze projects with a bounded number of workers at once, set by `--jobs N`.
- The default is `min(Environment.ProcessorCount, 4)`.
- Each worker's heap is already capped by `DOTNET_GCHeapHardLimit=20000000`. .NET reads that
  value as hex, so the cap is 512 MiB per worker and the default uses at most about 2 GiB.
- Collect results and errors back into `WorkspaceAudit.order` order before anything reads them.

`check` and `review` analyze against assemblies that are already built, so one project's
analysis doesn't depend on another's.

**Unchanged:**

- `Project.verify` keeps its sequential analyze-then-rebuild loop. Rebuilding a project
  invalidates the analysis of every project that uses it.
- `review`'s end-of-run check that no input changed (`BUILD004: Review inputs changed during
  analysis.`) still runs after all workers finish.

**Tests (`tests/FDull.Tests/WorkspaceTests.fs`):**

- `--jobs 1` and `--jobs 4` give identical `lint --format json` and `review --format json` output
  on every fixture workspace.
- A project that fails, by a diagnostic or an export error, is reported identically at any job
  count, in the same position.
- `--jobs 0` and negative values are refused with usage exit code 2.

## Phase 2: Cache each project's result

This is the core change.

### 2a. The worker sees only its project's part of the policy

Today the worker reads the whole `fdull.json` (`WorkspaceEngine`, `WorkspacePolicy.read` or
`readForReview`). The request carries the whole file's digest. Every policy edit changes the
input of every project, so no cache keyed on it would ever hit during a policy pass.

**Add `WorkspacePolicy.slice (project: string) (document: WorkspacePolicyDocument)`.** It keeps
the whole document except two lists, which it filters to the project's own files:

- `Capabilities` keeps entries whose `File` is one of the project's assigned sources. This list
  holds most of a consumer's policy (about 25,900 entries in botf) and is what a policy pass
  edits.
- `Sources` keeps the project's own assignments. Review refreshes their digests across many
  projects.
- `Projects`, `Inputs`, `Domains`, `Constructors`, `External`, `ExcludedProjects` and
  `DataContracts` stay whole. They are small and rarely change.

**Apply the slice inside the worker,** right after reading the policy and before the edge,
source and generated-file checks and `WorkspacePolicy.context`. The worker then can't depend on
anything outside the slice, so a key built from the slice is correct by construction. Don't
depend on the slice happening to look complete.

**The report's `Policy` digest becomes the slice's digest.** It is computed by a canonical
encoding of the sliced document, so the parent and the worker get the same value.

Everywhere that compares it must agree:

- `WorkspaceProtocol.validate`;
- `WorkspaceReview.validate`;
- `Project.matchesInvocation`;
- `Safety.verifyInputs`.

The request still pins the whole `fdull.json` by its file digest, so a policy edit during a run
is still caught.

### 2b. The cache key

The key is the SHA-256 of a canonical encoding of:

- the tool's own digest (`SafetyPolicy.fileDigest` of `FDull.dll`);
- the mode: `workspace` or `workspace-review`;
- the project's path, relative to the root;
- its project references;
- the exported compiler arguments;
- each source file, by path and digest;
- the generated sources;
- each reference, by path and digest;
- the build inputs, by path and digest;
- the slice's digest.

For project references, the compiler is passed reference assemblies (`obj/.../ref/*.dll`).
These are rewritten only when a public signature changes. A private change in one project
therefore doesn't change the key of the projects that use it.

### 2c. Storage

- **Location:** `<root>/.fdull/cache/<key>.json`. `.fdull/` is already ignored here, and a
  consumer should ignore it too (README).
- **An entry holds:** the worker's JSON output, its exit code, its stderr, and the key's
  encoded inputs, so a hit can be checked without the worker.
- **Writes:** to a temp file, then a rename, so parallel workers never leave a torn entry.
- **Eviction:** none in this phase. `fdull cache clear` deletes the directory. Keys change with
  every FDull build, so stale entries only cost disk.
- **`--no-cache`** turns the cache off for `lint` and `review`.

### 2d. How a run uses it

1. Export the project's invocation, as today. Phase 3 may cache this step.
2. Build the request, compute the slice and the key.
3. On a hit, decode the entry and run the same checks as a fresh result:
   - the `BUILD004` comparison against the request;
   - `WorkspaceProtocol.validate` or `WorkspaceReview.validate`, against the full policy.

   An entry that can't be read, or that fails any check, is a miss. It is re-analyzed and
   overwritten, never reported as an error.
4. On a miss, run the worker as today, then write the entry.
5. Text output ends with, for example, `44 projects (37 from cache); 0 reported diagnostics.` JSON
   output gains a `Cached` count per run and a `Cached` flag per project.

### 2e. Tests

**Slice equivalence (the soundness test):**

- On every fixture workspace, the worker's report and review findings with the full policy
  equal those with the slice.
- A test that adds a capability for another project's file shows the slice drops it and the
  result doesn't change.

**Hits:** a second `lint` or `review` with nothing changed is served entirely from cache, and
its JSON is byte-identical to the first.

**Misses, each against a two-project fixture where B references A:**

| Change | Projects re-analyzed |
|---|---|
| A capability on one of A's files | A only |
| A source digest of A's | A only |
| A private body in A | A only, since A's reference assembly is unchanged |
| A public signature in A | A and B |
| A compiler argument of A's | A only |
| `Inputs`, `Domains` or `Constructors` | Both |
| A new FDull build | Both |

**Robustness:**

- A truncated, corrupted or tampered entry, with a changed diagnostic or a changed use, is
  re-analyzed, never trusted.
- Two parallel runs on the same root don't corrupt the cache.

**Trust boundary:**

- `verify` leaves `.fdull/cache` untouched and ignores a planted entry.
- `BUILD005` still reports a permission that only a cached project used to use, once that use
  is gone.

## Phase 3 (optional): Cache the export

Do this only if Phase 0 shows the MSBuild export is a large part of each project's time.

- **Key:** the export's output is keyed by:
  - the digests of every file MSBuild imported (`MSBuildAllProjects`, requested in the same
    invocation);
  - `obj/project.assets.json`;
  - `global.json`;
  - the .NET SDK version;
  - FDull's own digest.
- **On a hit,** skip the export.
- **Keep checking:** the worker's own input checks still catch a changed source or reference.
- `verify` keeps re-exporting, as it does today, to compare invocations after each rebuild.

This is the most fragile part of the plan, because MSBuild evaluation depends on more than
files: environment variables, SDK resolution, and properties passed on the command line. It
needs its own equivalence test: a cached and a fresh export are identical on every fixture,
and on botf.

## Acceptance on a consumer

On botf at `feat/definition-registry`:

1. `review --format json --no-cache`, then `review --format json` twice. The three JSON
   outputs, with the cache count fields removed, are identical, and the third run is served
   entirely from cache.
2. Change one private function body in a leaf project and review again. Only that project
   is re-analyzed.
3. Record wall-clock times for a cold `--jobs 1`, a cold `--jobs 4`, and a warm run.

| Run | Before | Estimate after |
|---|---|---|
| Cold review | 6.6 min | about 2 min with 4 jobs |
| Review after a fix touching about 7 projects | 6.6 min | under 1 min |
| Verify | 10 min | about 10 min, unchanged by design |

The estimates are unconfirmed until Phase 0's timings exist.

## Consumer guidance (README)

- **Don't run `lint` right before `verify`.** `verify` runs the same per-project analysis and
  fails on the same diagnostics. A policy pass is review, fix, review, verify.
- **Use the cache for iteration** (`lint`, `review`). Never treat a cached `lint` as
  certification; that is `verify`'s job.
- **Ignore `.fdull/`** in version control.

## Order of work

Land one phase per commit, each with its tests and its `fdull.json` review:

1. Phase 0
2. Phase 1
3. Phase 2, the slice (2a) first with the equivalence test, then the cache (2b to 2e)
4. Phase 3, if Phase 0 justifies it

Phase 2 is where a mistake would let FDull pass code it should flag. Review it with the
equivalence and trust-boundary tests in front of you, not just the code.
