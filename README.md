# FDull

**F# with fewer sharp edges.** By [Wonderly](https://wonderly.com).

FDull checks a deliberately restricted subset of F#: immutable data, explicit
effects, controlled domain construction, explicit union cases and observed
results. It uses FSharp.Compiler.Service to inspect resolved symbols and types,
as well as source syntax. Aliases and nested payloads do not escape the checks.

This is a strict, experimental **0.1.0-preview.3** release for teams that want a
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
`obj/Release/net10.0` generated metadata layout, including SDK-owned MVC
application-part assembly metadata from F# Web SDK projects, and the generated F#
test entry point from Microsoft.NET.Test.Sdk 17.14.1. Multi-targeting, custom
source generators and arbitrary configurations are not supported in this preview.
Reviewed non-F# implementation, automation and tooling may coexist in a mixed
repository through the external workspace scope described below.

## Start with a small project

Install locally in a repository:

```sh
dotnet new tool-manifest
dotnet tool install FDull.Tool --version 0.1.0-preview.3
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
dotnet fdull init . --scope fdull.scope.json
```

The scope file is fingerprinted into `fdull.json`. Classifications must name
specific internal paths and cannot overlap or hide F# source, project files,
compiler props/targets, solutions, response files, lock files or SDK/NuGet
configuration. They state what FDull does not certify; they do not suppress an
F# diagnostic or grant a capability. `node_modules` is treated as a restored
dependency tree. Symlinks are accepted only when their resolved target exists
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

## Library integration and limits

For live editor feedback in FsAutocomplete/Ionide, reference the analyzer as a
development dependency in each checked project:

```xml
<PackageReference Include="FDull.Analyzers" Version="0.1.0-preview.3"
                  PrivateAssets="All" IncludeAssets="analyzers" />
```

The editor frontend reports source-syntax findings while a file is being edited.
It is deliberately advisory: it does not validate resolved APIs, project graphs,
fingerprints, builds or policy capabilities. `fdull verify` remains the complete
repository gate. Analyzer suppression comments are themselves FDull violations
and do not affect the authoritative CLI.

Reference `FDull` version `0.1.0-preview.3` to consume `SafetyDiagnostic`,
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
