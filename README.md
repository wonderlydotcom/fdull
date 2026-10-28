# FDull

**F# with fewer sharp edges.** By [Wonderly](https://wonderly.com).

FDull checks a deliberately restricted subset of F#: immutable data, explicit
effects, controlled domain construction, explicit union cases and observed
results. It uses FSharp.Compiler.Service to inspect resolved symbols and types,
as well as source syntax. Aliases and nested payloads do not escape the checks.

This is a strict, experimental **0.2.0-preview.3** release for teams that want a
small approved vocabulary and reviewable exceptions. It is not a new language
or a proof that a program is safe.

## Packages

| Package | Purpose |
| --- | --- |
| `FDull.Tool` | The `fdull` command with an isolated, resource-limited checker worker. Recommended for repositories and CI. |
| `FDull` | The analysis engine and policy/report types for F# tooling authors, compiler defaults and rule specification. |
| `FDull.Analyzers` | Advisory source diagnostics in FsAutocomplete/Ionide editors, using FDull's canonical syntax rules. |

There are no application-framework or company-internal dependencies. The library
uses FSharp.Compiler.Service and FSharp.SystemTextJson. The tool contains its
worker and dependencies and does not require a source checkout.

## Compatibility

The preview supports **.NET SDK 10.0.200, F# 10.0, net10.0**, with
**FSharp.Core 10.0.x or 10.1.x**. FDull itself pins **FSharp.Core 10.1.201** and
**FSharp.Compiler.Service 43.12.201**. The worker
requires **Microsoft.NETCore.App 10.0.4** and disables runtime roll-forward.
Install the matching SDK/runtime first. New toolchains require compatibility
tests; the preview rejects untested compiler flags.

Projects affected by the pinned compiler's task-builder FS3180 warning may set
`WarningsNotAsErrors` to **3180**. FDull accepts only that exact downgrade and
performs a private compiler pass for every workspace project. It permits the
known optimizer warning for `awaiter` at the compiler's `unknown(1,1)` location
only when 3180 is opted in. It also excludes FS3186 warnings from that private
pass for the compiler defect explained below. Other compiler warnings and all
compiler errors still fail analysis. The normal build continues to display
compatible warnings.
For executables, the private diagnostic pass omits the Windows manifest, which
FCS otherwise tries to load from the runtime directory. The SDK build still
produces its normal manifest and remains part of verification.
The default compiler preset remains strict; `--nowarn` and other warning
downgrades remain unsupported.

The FS3180 warning shape is a compatibility allowance, not proof of task provenance:
an imported inline helper can produce the same diagnostic. Included projects
are checked individually, so their authored captures still fail at their source
locations. External inline APIs remain subject to their exact reviewed contracts.
Do not use the warning setting without FDull's additional compilation check as
evidence that authored capture warnings have passed.

FS3186 is not in the mandatory opt-in warning list. In the pinned FCS version,
the F# metadata reader can emit it for healthy anonymous-record information in
a referenced assembly even when the consumer does not need a missing type or
member. The [FCS linked-state check](https://github.com/dotnet/dotnet/blob/4d3023de605a78ba3e59e50c657eed70f125c68a/src/fsharp/src/Compiler/TypedTree/TypedTree.fs#L4553-L4570)
returns true for a missing link and false for a present one, while the
[metadata unpickler](https://github.com/dotnet/dotnet/blob/4d3023de605a78ba3e59e50c657eed70f125c68a/src/fsharp/src/Compiler/TypedTree/TypedTreePickle.fs#L1026-L1134)
uses that check when deciding whether to emit FS3186. The result can be a false
positive and can miss an unlinked node, so FS3186 is not a reliable safety
condition by itself. It can still appear in compiler output without an opt-in.
The FCS API used for the private pass can return that warning even when the SDK
compiler does not display it for the same healthy reference. Compiler errors,
missing output, unresolved symbols and types, and
incomplete typed analysis still block completion. Authored implementation
analysis and exact review of external APIs remain required.

Workspaces contain ordinary F# SDK projects with literal project references and
authored `.fs`/`.fsi` files. Release builds must use the standard
`obj/Release/net10.0` generated metadata layout. Exact profiles cover SDK-owned
MVC application-part assembly metadata, Microsoft.NET.Test.Sdk 17.14.1's F# test
entry point, xUnit v3 Core 3.1.0 with Microsoft Testing Platform MSBuild 1.8.4
and optional JunitXml.TestLogger 7.0.2 registration, and Aspire AppHost 13.4.6
project metadata emitted by FSharp.Aspire.Hosting.AppHost 13.0.0. Multi-targeting,
other source generators and arbitrary configurations are not supported in this preview.
Reviewed non-F# implementation, automation and tooling may coexist in a mixed
repository through the external workspace scope described below.

## Start with a small project

Install locally in a repository:

```sh
dotnet new tool-manifest
dotnet tool install FDull.Tool --version 0.2.0-preview.3
```

Pin `global.json`:

```json
{ "sdk": { "version": "10.0.200", "rollForward": "disable" } }
```

For a new repository without `Directory.Build.props`, create the compiler preset:

```sh
dotnet fdull defaults > Directory.Build.props
```

For an existing repository, inspect `dotnet fdull defaults` and merge its settings
into the existing props file. They enable checked arithmetic, nullability, strict
warnings, deterministic compilation, dependency lock files and NuGet auditing.

Restore so the evaluated compiler inputs exist, then initialize. Initialization
does not compile the source and can bootstrap a repository whose newly enabled
warnings do not yet pass:

```sh
dotnet restore --use-lock-file
dotnet fdull init .
dotnet build -c Release --no-restore -m:1 --disable-build-servers
dotnet fdull verify .
```

Pass a solution or project to restore/build when several projects exist without
a solution. `init` records the exact graph, source files and build fingerprints.
It starts every project in `PURE`, grants **zero exceptions**, and refuses to
replace an existing `fdull.json`. Review and commit policy and lock files, and add
`.fdull/` to `.gitignore` for the local result cache. An initial `verify` failure
means the code or its explicit policy needs work.

For a mixed-language repository, create a reviewed scope document before init:

```json
{
  "Version": 1,
  "External": [
    {
      "Path": "scripts",
      "Kind": "automation",
      "Reason": "Deployment checks run outside FDull's F# certification."
    },
    {
      "Path": "web",
      "Kind": "implementation",
      "Reason": "The TypeScript frontend has its own required checks."
    }
  ]
}
```

```sh
dotnet fdull init .
```

A reviewed `fdull.scope.json` at the workspace root is discovered automatically.
Use `--scope <file>` only for a differently named in-workspace document. The
selected scope file is fingerprinted into `fdull.json`. Classified paths cannot overlap
one another. A classified parent may contain F# source or protected build inputs,
but those inputs remain independently inventoried, fingerprinted and checked, so
the classification cannot hide them. Literal F# references to C# or Visual Basic
projects are excluded from F# cycle ordering only when the referenced project is
pinned and lies inside a reviewed `implementation` scope. Literal project paths
use platform-independent slash or backslash normalization before comparison with
the exact evaluated MSBuild closure. Classifications state
what FDull does not certify; they do not suppress an F# diagnostic or grant a
capability. Standard
`bin`, `obj`, `artifacts`, `.artifacts`, `.nuget-feed`, `.worktrees`,
`.claude/worktrees`, `.pi/worktrees`, `.pi/git` and `node_modules`
output/dependency trees are excluded from repository inventory;
exported compiler inputs remain exact. Inventory remains time/depth bounded and
supports up to 100,000 traversal steps. Symlinks are accepted only when their resolved target exists
inside the workspace; external and broken links fail inventory validation.

### Excluding fixture projects

A project can be deliberately excluded from F# analysis with an exact path and
a review reason in `fdull.scope.json`:

```json
{
  "Version": 1,
  "ExcludedProjects": [
    {
      "File": "tests/fixtures/Crm.Definitions/Crm.Definitions.fsproj",
      "Reason": "Conditional CRM fixtures are validated by the definitions integration tests."
    }
  ]
}
```

`External` can coexist with `ExcludedProjects`; both default to empty when absent
from a scope document. `init` copies the exclusions into `fdull.json`, fingerprints
the scope document, and exports only included projects. At least one F# project
must remain included. Existing policies without `ExcludedProjects` keep their
previous behavior. To change an existing policy, review its exclusions, source
assignments and protected input fingerprints together; `init` never overwrites it.

An exclusion accounts for the project's explicitly listed `.fs` and `.fsi` files,
including literal `Compile Include` entries in inactive conditional, project-level
`ItemGroup` elements. Nested groups (including `Choose` and `ProjectExtensions`)
are not supported for excluded source declarations.
It does not exclude a directory: new orphan sources and unrelated projects still
fail inventory validation. Wildcards, MSBuild expressions, item lists, missing
files and target-time `Compile` declarations are rejected. Imported or implicit
source lists are not inferred. Paths use forward slashes in the policy; literal
source includes can use either slash style and are normalized inside the workspace.
Excluded sources must be under their project's directory, except linked sources
that are also assigned to an included project and remain fully checked there.
An included project cannot reference an excluded F# project.

The excluded project's source bodies are not analyzed or fingerprinted. Its
project file and all ordinary protected build inputs (including props, targets
and existing lock files) remain fingerprinted. Adding source declarations therefore
requires review of the changed project input. FDull does not directly restore,
export, analyze or build excluded projects; normal build hooks of included
projects still execute. Exclusions do not sandbox those builds.

Text, JSON and SARIF reports disclose excluded projects and reasons. A passing
result covers only included projects, and never certifies excluded fixtures.
JSON workspace/review reports contain `ExcludedProjects`; SARIF records them in
`runs[].properties.excludedProjects`. Exclusions do not grant capabilities or
suppress findings in included projects.

For example, this passes:

```fsharp
module Example

let increment value = value + 1
let firstPositive values = values |> List.tryFind (fun value -> value > 0)
```

Authored mutation reports `MUT001`/`MUT002`. A partial operation such as `List.head`
reports `ERR006`. Unapproved external calls report `EFFECT001`.

## Policy and review

`fdull.json` assigns `PURE`, `PURE_HELPER`, `CONTRACTS`, `BOUNDARY`, `ADAPTER`,
`GUARD_TOOLING` or `TEST` profiles from outside source code. A profile alone does
not grant I/O, mutation or reflection. Necessary operations need exact entries
bound to a file, declaration, permission kind and resolved API identity. Every
entry needs a reason; unused, duplicate and overbroad entries fail verification.

Attribute permissions include the resolved attribute type, target, and exact
arguments. Literal named arguments include both the name and value. Supported
enum arguments include the resolved enum assembly, type, field, and 32-bit value;
changing a flag or named value requires a new review. Combined enum expressions
and other computed arguments remain unsupported. Simple `typeof<T>` and `nameof`
operands are bound to their resolved symbols; literal arrays retain element order
and are limited to 16 elements. Referenced string, Boolean and 32-bit integer
constants include their resolved symbol and value. This permits narrowly reviewed
test metadata such as `MethodImplOptions.NoInlining` without allowing attributes
by name alone.

Generated capture fields use `Kind: "capture"` for their rule permissions. FDull
maps each field's resolved declaration location to its authored constructor
parameter, named binding or auto-property, retaining the enclosing type, lexical
binding path and nested payload path. The key contains no source coordinates or generated field
name. For example, a stored callback binding can have this manually reviewed entry:

```json
{
  "File": "Service.fs",
  "Owner": "App.Service.``.ctor``",
  "Kind": "capture",
  "Identity": "EFFECT003 | [[\"App.Service\",\"binding\",\"callback\"],[]]",
  "Reason": "Reviewed callback storage; its authored body and exact effect calls remain checked."
}
```

Constructor parameters use `"parameter"` instead of `"binding"`; auto-properties
use `"property"` with the unique authored property name. These origins never share
a permission. The second array describes the payload location (`"field"`
with a field name or `"tuple"` with an index). Existing rule restrictions also
apply to capture permissions: pure/data profiles cannot grant callback exemptions.
Unknown or ambiguous source mappings fail analysis; use distinct named bindings.
Compiler names remain in diagnostic evidence. Actual external API identities stay
exact, and anonymous lambda bodies remain checked under their authored declaration.
Adding comments, lines or formatting does not change a capture key. Changed source
still requires fingerprint review, and moving captures between owners/files needs
new explicit review. Old permissions containing generated field names become
unused; review the new `capture` findings and replace those entries intentionally.
FDull never migrates or generates permissions automatically.

`DataContracts` provides an optional, reviewed data vocabulary for a consuming
project. An exact external F# record or union can be admitted once and then used
by any declaration in that project, including newly extracted helpers. Existing
policies without this field retain their behavior. `init` creates no data approvals.

For example, a manually reviewed entry has this shape (replace the illustrative
digest with the value from `dotnet fdull data-digest <compiler-reference.dll>`):

```json
{
  "DataContracts": [
    {
      "Project": "src/App/App.fsproj",
      "Identity": "Shared, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null | Shared.CustomerDto",
      "Assemblies": [
        {
          "Identity": "Shared, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null",
          "Digest": "sha256:<64 lowercase hexadecimal characters>"
        }
      ],
      "Reason": "Reviewed immutable customer transport representation."
    }
  ]
}
```

Pin every non-framework assembly reached through the record/union representation
and its concrete generic payloads. Use the actual compiler reference assembly
(often `obj/Release/net10.0/ref/Shared.dll`), which can differ from the runtime
assembly. Existing locked build inputs and before/after reference checks remain
required. Unused data entries and unused assembly pins fail verification.

`data-digest` prints a fingerprint, never a permission entry. F# reference
assemblies regenerate module IDs and PE timestamps during otherwise identical
builds. Their data fingerprints clear only the COFF timestamp, metadata module ID
and matching 16-byte `.mvid` section, after validating the reference-assembly marker
and layout. All other bytes, including metadata and embedded F# signature payloads,
remain hashed. Assemblies without that section use raw SHA-256. Source/build
fingerprints and per-analysis compiler-reference bindings always retain raw hashes.
Data fingerprinting supports assemblies up to 64 MiB; failures remain failed checks.

Every use recursively validates the actual representation, aliases, tuple/record/
union fields and concrete generic arguments. Mutable fields, arrays, callbacks,
resources, mutable/null-backed representations and unknown classes cannot pass
through an approved outer type. Custom equality/comparison and formatting hooks
are also excluded because ordinary data operations can invoke them implicitly.
Declaration type or rule grants cannot bypass
this validation. An approval is for an exact root type; a nested type used
independently in a helper's signature needs its own data entry, rather than new
entries for every helper. Current consuming-project source types remain checked
under their source fingerprints.

Data approvals do not approve methods, domain construction or materialization.
Those checks and exact effect grants remain independent. Changed representations
require intentional assembly-pin review and still have to pass structural
validation. `review` reports invalid data representations as source changes and
lists unused data entries/pins only after complete analysis.

`Domains` designates invariant-bearing types whose representations must be private.
`Constructors` holds exact approved construction-helper signatures. Serializers
must not construct private domain representations, including nested targets.

Source and build `Digest` fields are `sha256:` followed by a lowercase SHA-256
hash. A changed file invalidates its reviewed fingerprint. Review its existing
capabilities against the changed code before updating that hash. FDull provides
no suppression comment or automatic permission-baselining command.

This repository's own policy illustrates contracts around compiler metadata,
local buffers, process supervision and test assertions. The library, CLI, worker,
tests and build controls are themselves checked. Copying their tooling permissions
into application code changes the trust model.

## Commands and exits

```sh
dotnet fdull lint . --format json
dotnet fdull lint . --format sarif
dotnet fdull review .
dotnet fdull review . --format json
dotnet fdull verify . --jobs 4 --timings
dotnet fdull cache clear .
dotnet fdull coverage
```

`lint`, `review` and `verify` run independent projects concurrently. `--jobs <n>`
sets how many run at once, from 1 to 64. The default is the processor count, capped
at 4. Reports list projects in dependency order, and the output doesn't depend on
the job count. Each analysis worker's heap is capped at 512 MiB.

`verify` rebuilds each project after analyzing it, which changes the inputs of
every project that references it. So a project starts only after every project it
references has finished, and `--jobs 1` is the fully sequential schedule. If a
project throws, the report stops at the first failure in dependency order, as it
would with one job. Within a job, analysis (1 GiB working set) and the rebuild
(2 GiB) run one after the other, so allow about 2 GiB of memory per job. When two
projects share an intermediate or output directory, their builds would write the
same files: `verify` then runs one project at a time whatever `--jobs` says, and
the text report says which projects share which directory. The report's
`Sequential` field carries those lines; it is always empty in `lint` JSON. `verify`
takes no `--format` or `--no-cache`.

`--timings` writes per-project times, and the run's total, to stderr. For `lint`
and `review` they are MSBuild export, analysis worker and parent validation; for
`verify` they are output-directory evaluation, restore, build, analysis, rebuild
and re-export, plus the final validation pass. Stdout and the exit code are
unchanged.

`lint` and `review` keep a per-project result cache in `<root>/.fdull/cache`. A
project is served from it only when everything its analysis reads is unchanged:
the exact compiler invocation, source and reference fingerprints, policy slice,
FDull build and .NET runtime. A cached result still passes the same input
comparison and validation as a fresh one, and unused-permission checks still see
every project. A damaged, edited or mismatched entry is ignored and the project is
re-analyzed. Text output then reads `N projects (M from cache)`, and JSON has a
`Cached` count and a per-project `Cached` flag; nothing else in the output or exit
code depends on the cache. `--no-cache` turns it off, and `cache clear <root>`
deletes `<root>/.fdull/cache` (exit `3` if it is a symbolic link). Entries are never
evicted, and a new FDull build changes every key.

Rebuilding a project changes its F# reference assembly, so the project and every
project that references it are re-analyzed. A policy edit re-analyzes only the
projects whose slice it changes. Edits to `Inputs`, `Domains` or `Constructors`,
including compiler settings in a pinned project file, re-analyze every project.

The cache is for iteration. `lint` and `review` trust `.fdull/` as you trust your
own build output: an entry edited to stay consistent is believed. A cached `lint`
is therefore not certification, and `verify` never reads or writes the cache. For
CI, or anything checked out from someone else, run `verify`, or `lint --no-cache`. Don't run `lint` right before `verify`, which runs the
same per-project analysis and fails on the same diagnostics. A policy pass is
`review`, fix, `review`, `verify`.

| Exit | Meaning |
| --- | --- |
| `0` | Complete analysis with no violations. |
| `1` | Complete analysis found violations. |
| `2` | Invalid command usage. |
| `3` | Incomplete/failed analysis, invalid policy, changed inputs, or unavailable worker. |

`lint` checks current Release compiler inputs and fingerprints. `verify` performs
locked restores one project at a time, builds each project once its references are
built, then analyzes and recompiles each project after its references without
rebuilding them. A final pass re-checks every project once all builds have finished. Source, reference, policy
and evaluated compiler inputs are compared before accepting the result. JSON includes completeness and errors.
SARIF includes diagnostics and an invocation success flag; also check the exit code.

Each project is analyzed against its policy slice: the whole `fdull.json` except
other projects' `Sources` and the `Capabilities` keyed to their files. A project
report's `Policy` and its diagnostics' policy field are the slice's `sha256:`
digest, so a policy edit that another project's analysis can't observe leaves it
unchanged. The worker re-reads the whole file, which stays pinned for the run, and
rejects a slice that differs from the requested digest.

Text output starts with `PASS`, `FAIL` or `BLOCKED`. `BLOCKED` means analysis did
not complete and no clean result was established, even when zero diagnostics
were reported. Resolve the reported blockers and rerun the command. `lint` only
reports an analysis result; `verify` reports a verification pass after its build
and input checks finish. Failed commands also print a `BLOCKED` status. For JSON
and SARIF, incomplete-analysis status is written to stderr so stdout remains
machine-readable. Always check completeness and the exit code as well as findings.

`review` presents an **advisory** review inventory for the current source snapshot.
It groups actual findings by file, declaration, profile, permission kind and exact
resolved identity, with source locations, rules and evidence. Each item says whether
an exact contract can be reviewed or the source must change. Catalog-approved
operations and unsuccessful permission lookups that still pass ordinary analysis
do not create review items. A type contract does not approve the type's methods.

Each item includes rule- and profile-specific `Guidance` in JSON and a
`How to resolve` explanation in text. It names the policy section and permission
kind to review, explains the effective profile's restrictions, and uses the
item's exact file/declaration/identity as the scope. It never creates an approval.
For example, a raw `List.fold` in `PURE` cannot be exempted by a member or rule
permission. Use an approved combinator, or isolate it in a named helper and review
that declaration's `profile` permission for `PURE_HELPER` and its exact `member`
permission. `PURE_HELPER` alone does not approve the fold. In an `ADAPTER`, the
fold is eligible for exact `member` contract review in that declaration. Type
permissions, API permissions, rule/capture exceptions, and `Constructors` entries
remain separate; the guidance explains which one applies to the emitted finding.

Unlike `lint`, `review` can examine authored source whose reviewed fingerprint is
stale. It lists changed files, reviewed/current hashes and existing contracts that
need renewed review. Policy structure, source inventory, project edges, compiler
settings and protected build fingerprints still have to validate. Review uses the
same bounded worker and detects changes to the current snapshot during analysis.
It does not edit source, permissions or fingerprints, perform verification, or
generate permission entries from findings. MSBuild export can update generated
build metadata, as it does for `lint`.

For `review`, exit `0` means complete advisory analysis with no outstanding review
items, source drift or unused contracts; exit `1` means complete advisory analysis
with review work remaining; exit `3` means the inventory is incomplete or blocked.
Neither a complete review report nor exit `0` certifies a build. Incomplete reports
retain available evidence and blockers and make no unused-contract claims. Some
nested requirements depend on admitting an outer type. Review reports these in
the same run: it examines authored classes' stored fields and concrete payloads
inside rejected arrays, byrefs and ruled generic types, while retaining each
unsatisfied outer finding. This exposes nested type and stored-callback review
keys before the outer approval. Existing permissions are still checked at each
level; suppressing an outer rule does not imply admission of its type. These are
advisory requirements for the current source and the chosen type-approval path.
Changing the source or choosing a different policy path can change the inventory.
Compiler failures, unresolved metadata and exhausted traversal/worker limits still
make review incomplete; an incomplete run cannot promise the full inventory.

The workflow is: run `review`, inspect the reported declarations and regression
evidence, intentionally edit exact contracts and reviewed fingerprints, then run
`verify`. JSON review reports have a distinct advisory schema (`Advisory`,
`AnalysisComplete`, `Cached`, `Items`, `SourceChanges`, `UnusedCapabilities`,
`UnusedConstructors`, `Errors` and per-project analysis/evidence); they cannot be
decoded as authoritative workspace reports.

## Library integration and limits

For live editor feedback in FsAutocomplete/Ionide, reference the analyzer as a
development dependency in each checked project:

```xml
<PackageReference Include="FDull.Analyzers" Version="0.2.0-preview.3"
                  PrivateAssets="All" IncludeAssets="analyzers" />
```

The editor frontend reports source-syntax findings while a file is being edited.
It is deliberately advisory: it does not validate resolved APIs, project graphs,
fingerprints, builds or policy capabilities. `fdull verify` remains the complete
repository gate. Analyzer suppression comments are themselves FDull violations
and do not affect the authoritative CLI.

Reference `FDull` version `0.2.0-preview.3` to consume `SafetyDiagnostic`,
`SafetyReport`, workspace policy types and `SafetyEngine.check`. The in-process
engine accepts ordered, fingerprinted source/reference inputs. Hosts must enforce
process limits themselves; the library package does not install a worker beside
arbitrary executables. `FDull.Tool` supplies the supported bounded host for
`Workspace.check`, `Project.verify` and source-only `Safety.check`. The library's
`Workspace.check` and `Workspace.review` never use the result cache.

The worker has a 512 MiB managed heap ceiling, a 1 GiB working-set limit and a
60-second wall-time limit per project. Output and traversal are also bounded.
Exhausted limits cause an incomplete failed check. Tests run serially with a
separate 2 GiB heap ceiling.

The catalog contains 69 IDs. `fdull coverage` records implementation paths,
executable evidence and remaining work per rule. The broader safety standard is
**not completely implemented**. Required CI checks, protected policy ownership,
artifact signing and deployment admission need external infrastructure.

MSBuild evaluation executes repository build logic. Use repositories you trust
to build, or your CI sandbox. Fingerprints detect changed inputs; they are not an
OS sandbox or an external policy authority. A local policy can be edited by its
owner. Some result-flow, interprocedural validation and transitive dependency-asset
checks remain partial. See the coverage inventory and `docs/fsharp-safety-standard.md`.

## Develop and package

```sh
dotnet tool restore
dotnet tool run fantomas src tests tools --check
dotnet restore FDull.slnx --locked-mode
dotnet build FDull.slnx -c Release --no-restore -m:1 --disable-build-servers
dotnet test tests/FDull.Tests/FDull.Tests.fsproj -c Release --no-build --no-restore -m:1
dotnet src/FDull.Tool/bin/Release/net10.0/FDull.Cli.dll verify .
dotnet pack src/FDull/FDull.fsproj -c Release --no-build --no-restore -m:1 --disable-build-servers -o artifacts
dotnet pack src/FDull.Analyzers/FDull.Analyzers.fsproj -c Release --no-build --no-restore -m:1 --disable-build-servers -o artifacts
dotnet pack src/FDull.Tool/FDull.Tool.fsproj -c Release --no-build --no-restore -m:1 --disable-build-servers -o artifacts
dotnet tools/FDull.Ci/bin/Release/net10.0/FDull.Ci.dll smoke . artifacts
dotnet tools/FDull.Ci/bin/Release/net10.0/FDull.Ci.dll coverage .
```

GitHub Actions runs four Linux jobs on pull requests: **Format**,
**Build and test (Linux)**, **Self-verification**, and **Package and consumer smoke**.
Jobs install the exact
SDK from `global.json` (including its 10.0.4 runtime), restore locked dependencies,
keep strict compiler settings and preserve nonzero command exits. NuGet downloads
are cached by the lock files; results and logs are retained for seven days.

The compiled `tools/FDull.Ci` helper is part of the reviewed repository graph.
Its package smoke check uses fresh temporary projects and an isolated package
cache and CLI home outside the checkout. Source mapping requires all FDull
packages to come from the local feed. It installs the packed tool, checks the
bundled worker via successful verification, requires complete mutation failures
and blocked source drift, runs an independent library consumer and checks the
analyzer payload.
Self-verification also compares `site/public/coverage.json` with the built CLI's
coverage inventory. The workflow never publishes packages or deploys the site.

After merging the workflow, configure the `main` repository ruleset to require
all four named checks and code-owner review for the policy, build and workflow
paths in `.github/CODEOWNERS`. Review ownership currently names repository
administrator `@c10r`; select another trusted maintainer or team when independent
code-owner approval is needed for that administrator's own changes. These
repository settings are external controls, not guarantees made by `fdull verify`.

## Releases

Publishing is manual; CI builds and checks packages but does not upload them to
NuGet or create a GitHub release.

1. Prepare a release PR with a new, unused package version. Update `<Version>` in
   `Directory.Build.props`, the CLI's `--version` response in
   `src/FDull.Tool/Program.fs`, and the version references in this README and
   `site/public/index.html`. All three packages share that version. Review changed
   source/build contracts and update their SHA-256 fingerprints in `fdull.json`
   as described under **Policy and review**.
2. Wait for all four required PR checks to pass, then merge the release PR. From
   a clean checkout of the merged release commit, use the pinned SDK and run all
   commands under **Develop and package**, including verification, packing and
   the independent consumer smoke check. Keep the three `.nupkg` files for the
   new version from `artifacts/`; publish these packages from the same checked build.
3. Create and push a tag named `v<version>` at that exact tested commit. In
   [GitHub Releases](https://github.com/wonderlydotcom/fdull/releases), draft a
   release using that tag, add release notes and attach the three `.nupkg` files.
   Mark preview versions as a pre-release. See GitHub's
   [release instructions](https://docs.github.com/en/repositories/releasing-projects-on-github/managing-releases-in-a-repository)
   for the standard UI steps.
4. Wonderly employees should look up the NuGet credentials in 1Password in the
   login titled **Nuget Account**. Use that login to sign in to
   [NuGet.org](https://www.nuget.org/), then use **Upload** to publish a new version
   of **all three packages**: `FDull`, `FDull.Tool` and `FDull.Analyzers`. Upload
   each matching `.nupkg` from the checked build in `artifacts/`, review its ID
   and version, and submit it. See NuGet's
   [publishing instructions](https://learn.microsoft.com/en-us/nuget/nuget-org/publish-a-package#upload-to-the-nugetorg-web-portal).
5. Wait for NuGet validation and indexing, confirm the new version is available
   for all three packages, then publish the GitHub release. NuGet package versions
   cannot be overwritten; corrections require a new version and release build.

MIT licensed. Copyright 2026 Wonderly.
