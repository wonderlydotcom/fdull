# FDull

**F# with fewer sharp edges.** By [Wonderly](https://wonderly.com).

FDull checks a deliberately restricted subset of F#: immutable data, explicit
effects, controlled domain construction, explicit union cases and observed
results. It uses FSharp.Compiler.Service to inspect resolved symbols and types,
as well as source syntax. Aliases and nested payloads do not escape the checks.

This is a strict, experimental **0.1.0-preview.8** release for teams that want a
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
dotnet tool install FDull.Tool --version 0.1.0-preview.8
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
dotnet fdull lint .
dotnet fdull verify .
```

Pass a solution or project to restore/build when several projects exist without
a solution. `init` records the exact graph, source files and build fingerprints.
It starts every project in `PURE`, grants **zero exceptions**, and refuses to
replace an existing `fdull.json`. Review and commit policy and lock files. An
initial lint failure means the code or its explicit policy needs work.

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

Generated capture fields use `Kind: "capture"` for their rule permissions. FDull
maps each field's resolved declaration location to its authored constructor
parameter or named binding, retaining the enclosing type, lexical binding path
and nested payload path. The key contains no source coordinates or generated field
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

Constructor parameters use `"parameter"` instead of `"binding"`; the two never
share a permission. The second array describes the payload location (`"field"`
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
dotnet fdull coverage
```

| Exit | Meaning |
| --- | --- |
| `0` | Complete analysis with no violations. |
| `1` | Complete analysis found violations. |
| `2` | Invalid command usage. |
| `3` | Incomplete/failed analysis, invalid policy, changed inputs, or unavailable worker. |

`lint` checks current Release compiler inputs and fingerprints. `verify` performs
locked restores and serial builds, then analyzes and recompiles each project in
dependency order without rebuilding its dependencies. Source, reference, policy
and evaluated compiler inputs are compared before accepting the result. JSON includes completeness and errors.
SARIF includes diagnostics and an invocation success flag; also check the exit code.

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
nested requirements can still appear after earlier contracts are resolved; the
review inventory covers findings reachable in the current analysis.

The workflow is: run `review`, inspect the reported declarations and regression
evidence, intentionally edit exact contracts and reviewed fingerprints, then run
`verify`. JSON review reports have a distinct advisory schema (`Advisory`,
`AnalysisComplete`, `Items`, `SourceChanges`, `UnusedCapabilities`,
`UnusedConstructors`, `Errors` and per-project analysis/evidence); they cannot be
decoded as authoritative workspace reports.

## Library integration and limits

For live editor feedback in FsAutocomplete/Ionide, reference the analyzer as a
development dependency in each checked project:

```xml
<PackageReference Include="FDull.Analyzers" Version="0.1.0-preview.8"
                  PrivateAssets="All" IncludeAssets="analyzers" />
```

The editor frontend reports source-syntax findings while a file is being edited.
It is deliberately advisory: it does not validate resolved APIs, project graphs,
fingerprints, builds or policy capabilities. `fdull verify` remains the complete
repository gate. Analyzer suppression comments are themselves FDull violations
and do not affect the authoritative CLI.

Reference `FDull` version `0.1.0-preview.8` to consume `SafetyDiagnostic`,
`SafetyReport`, workspace policy types and `SafetyEngine.check`. The in-process
engine accepts ordered, fingerprinted source/reference inputs. Hosts must enforce
process limits themselves; the library package does not install a worker beside
arbitrary executables. `FDull.Tool` supplies the supported bounded host for
`Workspace.check`, `Project.verify` and source-only `Safety.check`.

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
dotnet tool run fantomas src tests --check
dotnet restore FDull.slnx --locked-mode
dotnet build FDull.slnx -c Release --no-restore -m:1 --disable-build-servers
dotnet test tests/FDull.Tests/FDull.Tests.fsproj -c Release --no-build --no-restore -m:1
dotnet src/FDull.Tool/bin/Release/net10.0/FDull.Cli.dll verify .
dotnet pack src/FDull/FDull.fsproj -c Release --no-build --no-restore -m:1 --disable-build-servers -o artifacts
dotnet pack src/FDull.Analyzers/FDull.Analyzers.fsproj -c Release --no-build --no-restore -m:1 --disable-build-servers -o artifacts
dotnet pack src/FDull.Tool/FDull.Tool.fsproj -c Release --no-build --no-restore -m:1 --disable-build-servers -o artifacts
```

MIT licensed. Copyright 2026 Wonderly.
