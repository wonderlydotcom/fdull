# Restricted F# Application Safety Standard
## Implementation plan for compiler, analyzer, build, and CI enforcement

**Specification version:** 1.0  
**Prepared:** 2026-09-04  
**Audience:** The agent implementing this standard, application maintainers, and platform owners  
**Proposed tool name:** `Company.FSharp.Guard`  
**Status:** Implementation specification, not an implemented or tested rule package

> Build a small, predictable F# application language: immutable data, explicit alternatives, controlled domain construction, and explicit expected failures. Put external effects and unavoidable framework behavior behind narrow, protected adapters. A violation must fail the approved build and required CI check; a warning in an editor is not sufficient.

This document consolidates the decisions from the discussion, including the final qualifications about folds, safe upcasts, exception translation, and resource cleanup. Earlier diagnostic numbers were provisional. **The catalog in this document is the canonical version-1 catalog.** Do not implement competing versions of the earlier tables.

The repository and its toolchain have not been inspected for this specification. Discover existing project names, frameworks, packages, build hooks, and SDK versions before changing them. Example company functions, CLI commands, policy schemas, and build-task names below are **interfaces to implement**, not claims that such tools already exist. The F# examples are policy examples; convert them into compiling acceptance fixtures under the selected toolchain. No F# compilation was performed when preparing this document.

## Contents

1. [Goals, decisions, and limits](#1-goals-decisions-and-limits)
2. [Profiles and architecture](#2-profiles-and-architecture)
3. [Canonical diagnostic catalog](#3-canonical-diagnostic-catalog)
4. [Mutation and immutable data](#4-mutation-and-immutable-data)
5. [Casts, type erasure, and conversions](#5-casts-type-erasure-and-conversions)
6. [Nulls, defaults, attributes, reflection, and native code](#6-nulls-defaults-attributes-reflection-and-native-code)
7. [DTOs, validation, and controlled construction](#7-dtos-validation-and-controlled-construction)
8. [Explicit domain matching](#8-explicit-domain-matching)
9. [Effects and executable capabilities](#9-effects-and-executable-capabilities)
10. [Result, exceptions, cancellation, and cleanup](#10-result-exceptions-cancellation-and-cleanup)
11. [Readability: operators and folds](#11-readability-operators-and-folds)
12. [Analyzer implementation](#12-analyzer-implementation)
13. [Policy, catalogs, and protected exceptions](#13-policy-catalogs-and-protected-exceptions)
14. [Compiler and dependency configuration](#14-compiler-and-dependency-configuration)
15. [Local build and editor integration](#15-local-build-and-editor-integration)
16. [Independent CI and merge enforcement](#16-independent-ci-and-merge-enforcement)
17. [Acceptance tests](#17-acceptance-tests)
18. [Implementation phases and completion criteria](#18-implementation-phases-and-completion-criteria)
19. [Instructions to the implementation agent](#19-instructions-to-the-implementation-agent)
20. [Primary references](#20-primary-references)

---

## 1. Goals, decisions, and limits

### 1.1 Intended applications

The target applications are standardized internal web applications, not numerical kernels, native-memory libraries, or performance-sensitive binary parsers. Application developers and coding agents should not introduce new runtimes, background processes, sidecars, native components, or framework patterns merely to implement ordinary business features.

The platform owns the supported capabilities. Applications compose those capabilities and express business rules.

### 1.2 Required invariants

The implementation MUST enforce these rules within the supported compilation and dependency model:

- Pure code uses approved immutable data and approved computations. No source-authored mutation, mutable capabilities, hidden effects, or unapproved external calls.
- Business data is not erased into `obj` and recovered through runtime casts or type tests.
- Invariant-bearing domain values are constructed through controlled, tested APIs, not unchecked defaults, reflection, serialization, or public unchecked constructors.
- External data is decoded into transport/storage data, normalized, and validated before it enters business operations.
- Expected absence and failure use `Option` and `Result`; business rejection is not exception-based control flow.
- Domain decisions name the relevant discriminated-union cases explicitly.
- Ordinary application code uses a small notation vocabulary. Backward pipes are prohibited. Raw folds are confined to specifically approved pure helpers.
- Application source cannot suppress the guard, change its own trust profile, or declare itself generated/trusted.
- Incomplete analysis, invalid policy, unsupported inputs, and checker failure are failures, not successful checks with zero findings.

### 1.3 Decisions that must not be lost during implementation

| Topic | Final decision |
|---|---|
| `<-` and mutation | Prohibited in ordinary application code, including local mutation. Framework mutation is confined to approved adapters. |
| `fold` | A readability restriction, not an unsafe-operation classification. Prohibit direct generic folds in ordinary workflows; permit exact approved pure helper implementations. |
| Safe upcasts | May be allowed to approved typed abstractions. An upcast to `obj`, a mutable interface, or an unapproved capability still fails. |
| Type tests | Prohibited for pure business modeling. Approved boundary exception matching and runtime normalization can receive narrow permission. |
| `try ... with` | Prohibited in normal application/business source. Approved adapters translate specified external exceptions to modeled errors. |
| `try ... finally` and `use` | Distinct from catching. Resource cleanup is allowed in approved effect/resource adapters, not in the pure core. |
| `Result` | Does not automatically catch exceptions. Do not wrap throwing code in `Ok` and call it safe. |
| DTO deserialization | Allowed only through approved materializers, and only into approved DTO shapes. Domain targets remain prohibited at every boundary. |
| Read-only interfaces | Not automatically accepted as deeply immutable snapshots. |
| Compiler/library internals | Do not ban standard F# abstractions because their implementation contains null, boxing, byrefs, loops, or mutation. Check authored operations and approved semantic contracts. |
| Ordinary .NET APIs | Not blanket-approved. A well-typed API can still mutate, perform I/O, or bypass construction. |
| Exceptions to rules | Protected, exact, reviewed, and generally expiring. No application-controlled suppression mechanism. |

### 1.4 What this standard does not prove

Passing the guard establishes conformance to the approved source, API, type-shape, and build policy. It does not prove total correctness, termination, absence of every exception, correct authorization, transaction isolation, race-free persistence, or mathematical validity of every business invariant.

F# does not acquire linear ownership or an effect type system merely because this checker exists. Immutable old values can still be reused. A stale-state/database-concurrency policy remains a separate responsibility.

The compiler, runtime, approved dependencies, platform adapters, generator configuration, guard implementation, and CI authority form the trusted computing base. A function signature alone is not evidence that arbitrary dependency code is pure. This is also not a sandbox for running hostile .NET/MSBuild code.

**Do not describe a regex linter, a small API denylist, or warnings-as-errors alone as proving all of these invariants.**

---

## 2. Profiles and architecture

### 2.1 Assign profiles from protected policy

Profiles attach to centrally approved project identities and, where necessary, exact declarations/modules. A folder name, namespace, project property, source attribute, or comment cannot grant a profile.

| Profile | Intended content | Permissions |
|---|---|---|
| `PURE` | Domain definitions, validation, business decisions, pure application transformations | All pure restrictions; approved immutable types and pure APIs only. |
| `PURE_HELPER` | Exact reviewed aggregation/algorithm implementations | Same safety restrictions as `PURE`; only specifically granted readability permissions, such as a fold call. |
| `CONTRACTS` | Transport/storage DTO definitions | Data only. Approved transport shapes; no domain references. Nullable fields, arrays, or `CLIMutable` require explicit DTO-shape approval. |
| `BOUNDARY` | HTTP/application orchestration, DTO-to-domain mapping, response mapping | Approved platform calls and task composition. Still no arbitrary mutation, casting, raw deserialization, catching, or reflection. Exact normalization functions can be granted limited null handling. |
| `ADAPTER` | Protected serializer, database, filesystem, framework, or resource adapter implementation | Only its approved capability catalog. Narrow permissions for nullable input, mutable APIs, exception translation, or required framework values. Not an unrestricted directory. |
| `TEST` | Non-shipping test harnesses | Approved assertion/test APIs and fixtures. Cannot be a dependency of shipping projects. No automatic production exemptions. |
| `GUARD_TOOLING` | The guard, compiler/project loading, and metadata inspection itself | Separately reviewed tooling policy. It necessarily interacts with compiler/build APIs; it is not reclassified as pure application code. |
| `INTEROP` | Future native integration | **No projects assigned initially.** Creating this category for an app requires separate platform approval. |

A `PURE_HELPER` does not become permitted to use mutation, defaults, reflection, or catches. A new helper declaration is ordinary `PURE` until the protected catalog grants its exact readability permission.

### 2.2 Suggested dependency structure

Adapt names to the repository. Prefer assembly boundaries where they make the dependency policy enforceable.

```text
App.Contracts                  transport data; no App.Domain reference

Company.PureSupport            protected pure helpers; no platform effects
          ^
          |
App.Domain                     approved domain data + validation + decisions
          ^
          |
App.Application                pure transformations, when a separate layer is useful
          ^
          |
App.Boundary  --------------------> App.Contracts
     |
     +----------------------------> Company.Platform approved public facade
     |
     +----------------------------> App.Serialization / App.Persistence adapters
                                              |
                                              +----> App.Contracts
                                              X----> App.Domain
```

Arrows denote permitted references from consumer to dependency. Domain and pure application projects MUST NOT reference boundary, serializer, persistence, host, or interop implementations, directly or transitively.

Serialization/persistence components that automatically instantiate records MUST NOT reference domain types. Boundary mapping code may reference both contracts and domain but MUST NOT have raw materializer capability.

This can be implemented with a small number of projects plus a protected module map in an existing small application. Do not force a large project split without first identifying the current boundaries. The invariant is the reference/capability graph, not this exact directory layout.

### 2.3 Data flow

```text
External request / database row / queue message / cache entry
                         |
                         v
                 Approved DTO decoding
                         |
                         v
            Normalize nulls and external shapes
                         |
                         v
         Validate through domain construction functions
                         |
                         v
             Result<DomainInput, ValidationError>
                         |
                         v
                    Pure decision
                         |
                         v
           Explicit command/event/result values
                         |
                         v
              Approved effectful orchestration
```

Outbound responses should also be explicit transport projections. Do not hand arbitrary private domain graphs to a serializer as a shortcut.

### 2.4 Strictness and trust

All mandatory rules are errors in their applicable profiles. An adapter receives permissions for particular operations; it does not turn off whole families by default.

Start with `exceptions: []` and `interopProjects: []`. Existing framework requirements must be inventoried and granted as specific protected adapter contracts. Never make the application compile by globally weakening a profile.

---

## 3. Canonical diagnostic catalog

**Detection abbreviations:** `S` = syntax/tokens; `T` = typed symbols/expressions; `H` = recursive type-shape/signature analysis; `G` = project/dependency/build graph; `P` = protected policy; `M` = metadata/artifact inspection; `X` = behavioral acceptance tests.

The scope column describes the default. Adapter exceptions must satisfy section 13. When one expression violates several rules, report the most actionable primary rule and related rule IDs rather than an unreadable flood of duplicate errors.

### 3.1 Mutation and data capabilities

| ID | Prohibition | Default scope | Detection |
|---|---|---|---|
| `MUT001` | Mutable local/module bindings, including `let mutable` | All application-authored code | S/T |
| `MUT002` | Assignment, field/property/index writes, setters invoked directly or through aliases | All application-authored code | S/T |
| `MUT003` | Mutable record/class/struct fields; writable/auto-settable properties; stateful application object declarations | Pure and ordinary boundary code | S/T/H |
| `MUT004` | Ref cells and their operations: `ref`, `FSharpRef<_>`, `:=`, dereference/update helpers, increment/decrement | All application-authored code | S/T/H |
| `MUT005` | Mutable collection/building capabilities, including `ResizeArray`, dictionaries, sets, queues, stacks, concurrent collections, mutable interfaces, and `StringBuilder` | Pure and ordinary boundary code | T/H |
| `MUT006` | Mutating method references/calls, even without `<-`; unapproved mutation hidden behind wrapper methods | Pure and ordinary boundary code | T/G |
| `MUT007` | Shared/static/global/thread-local mutable state, mutable singletons, unapproved caches, events, and synchronization primitives | All application-authored code | S/T/H |
| `MUT008` | Arrays of any rank, mutable collection views, or aliasable memory exposed/used as pure data | Pure | T/H |
| `MUT009` | `while`, including builder forms; statement-oriented `for` loops outside approved effect adapters | All application-authored code; pure list comprehensions are not statement loops | S/T |
| `MUT010` | Mutable or unapproved payloads hidden inside otherwise immutable wrappers | Pure | H |

### 3.2 Type recovery and conversions

| ID | Prohibition | Default scope | Detection |
|---|---|---|---|
| `CAST001` | Explicit/inferred downcast: `:?>`, `downcast` | Pure and ordinary boundary code | S/T |
| `CAST002` | `unbox` and unchecked unboxing, including function values and qualified/aliased references | Pure and ordinary boundary code | T |
| `CAST003` | Indirect cast APIs such as `Seq.cast` and LINQ `Cast<T>` | Pure and ordinary boundary code | T |
| `CAST004` | Explicit/implicit boxing or erasure to `obj`/`System.Object`/`objnull`; erased payloads nested in data or signatures | Pure; boundary only for exact required APIs | S/T/H |
| `CAST005` | Runtime-type modeling: `:?`, `tryUnbox`, `OfType`, custom runtime type-dispatch helpers | Pure; exact boundary normalization/exception patterns only | S/T |
| `CAST006` | Unapproved user-defined implicit/explicit conversion operators and their implicit use | All application-authored code | S/T |
| `CAST007` | Unapproved narrowing/truncating numeric conversions and unchecked numeric-to-enum/domain construction | Pure workflows; approved validation helpers may implement them | T/P/X |

Statically safe upcasts to approved typed abstractions are allowed. No general rule says that every `Coerce` expression is illegal.

### 3.3 Nulls and defaults

| ID | Prohibition | Default scope | Detection |
|---|---|---|---|
| `NULL001` | Source null literals and null-producing application expressions | Pure; exact boundary normalization/required arguments only | S/T |
| `NULL002` | `Unchecked.defaultof` and unapproved references to the `Operators.Unchecked` family | Pure and ordinary boundary code | T |
| `NULL003` | Unchecked removal of nullability, including `Unchecked.nonNull` and unchecked null-related active patterns present in the pinned Core version | All application-authored code | T |
| `NULL004` | Default/zero initialization of invariant-bearing structs or other unapproved element types, including `Array.zeroCreate` and equivalent materialization | Pure and ordinary boundary code | S/T/H |
| `NULL005` | Nullable/oblivious external values, `Nullable<T>`, or nullable-reference shapes admitted as pure data without normalization | Pure public boundary and pure data | T/H |

### 3.4 Attributes and metaprogramming

| ID | Prohibition | Default scope | Detection |
|---|---|---|---|
| `ATTR001` | Any application-authored attribute not in the profile allowlist, including its target and arguments | All profiles | S/T/P |
| `ATTR002` | `AllowNullLiteral`, F# `DefaultValue` including `false`, and `CLIMutable` | Pure; exact DTO/adapter exceptions only | T/H |
| `ATTR003` | Application-defined null representations via `CompilationRepresentationFlags.UseNullAsTrueValue` | All application types | T |
| `ATTR004` | Attribute-based trust escalation, unchecked nullability promises, friend access, suppression, native interop, or generated-code claims outside protected approval | All profiles | T/G/P |
| `REFL001` | Source-authored reflective metadata/runtime-type capabilities (`System.Type`, `typeof`, `typedefof`, `GetType`, reflection metadata objects) outside approved adapters | Pure and ordinary boundary code | S/T/H |
| `REFL002` | Reflective invocation, private member access, field/property writes, `Activator`, F# reflective construction, and dynamic invocation | Pure and ordinary boundary code | T |
| `REFL003` | Uninitialized-object creation, including runtime/formatter helpers | All application-authored code | T |
| `REFL004` | Unapproved dynamic assembly loading, emission, quotation/expression evaluation, embedded scripting/compilation, and type providers | All application-authored code and build inputs | S/T/G |
| `NATIVE001` | P/Invoke, `extern`, and native import declarations | All application projects; no interop profile assigned initially | S/T/M |
| `NATIVE002` | `NativePtr`, `Unsafe`, native/memory reinterpretation, unapproved `Marshal`/`MemoryMarshal` capabilities | All application projects | T/H/M |
| `NATIVE003` | Source-authored pointer/byref/outref/inref/native-int/span/memory-view capabilities and address-taking | Pure; approved boundary cases only | S/T/H |

### 3.5 Domain construction and data boundaries

| ID | Prohibition | Default scope | Detection |
|---|---|---|---|
| `DATA001` | Automatic deserialization/materialization into domain types, including any nested domain-containing target | Every materializer, including approved adapters | T/H/G |
| `DATA002` | Raw serializer, ORM materializer, request binder, cache/message materializer usage outside its approved adapter | Application code | T/G |
| `DATA003` | Public arbitrary-generic or runtime-type materialization wrappers that let callers select domain targets | Adapter APIs | T/H/G |
| `DATA004` | DTO shapes that contain domain types, executable capabilities, or other unapproved payloads | Contracts/materialization targets | H/G |
| `DATA005` | Unnormalized DTO/framework values passed directly into domain operations, or raw domain graphs used as transport contracts | Boundary contracts | T/H/P/X |
| `MODEL001` | Unapproved pure data shapes, including hidden mutable/executable payloads and unvetted opaque external types | Pure | H/P |
| `MODEL002` | Public representations/constructors for types designated as invariant-bearing | Designated domain types | S/T/H |
| `MODEL003` | New unchecked construction helpers, widened signatures, or friend-access changes bypassing approved domain construction | Domain APIs | T/H/G/P |
| `MODEL004` | Catch-all patterns replacing explicit case coverage in closed-domain-union decisions | Pure/domain decision code | S/T |

### 3.6 Effects and failures

| ID | Prohibition | Default scope | Detection |
|---|---|---|---|
| `EFFECT001` | Calls/references to external symbols outside the exact approved API catalog | All profiles, with profile-specific catalogs | T/G/P |
| `EFFECT002` | Ambient I/O/time/randomness/environment/configuration/culture/global-state reads in pure code | Pure | T |
| `EFFECT003` | Service objects, effectful callbacks, tasks, streams, lazy external enumerables, resources, or unapproved executable objects crossing into pure data contracts | Pure public ingress/data | H/T |
| `EFFECT004` | Unapproved computation-expression builders, query providers, expression callbacks, and operator/helper implementations that introduce effects | Pure and boundary code | S/T/G |
| `ERR001` | `try ... with` in ordinary application/business source | Pure and ordinary boundary code | S |
| `ERR002` | Deliberate throwing for application flow: `raise`, `reraise`, `failwith`, `failwithf`, `invalidArg`, `invalidOp`, exception declarations, and assert-as-validation | Pure and ordinary boundary code | S/T |
| `ERR003` | Indirect exception-catching/raising APIs and general-purpose “catch anything into Result” wrappers | Pure and ordinary boundary code | T/H |
| `ERR004` | Broad exception-to-business-error translation or swallowing unexpected failures | Adapters; exact top-level host handler treated separately | S/T/P/X |
| `ERR005` | `exn`/`System.Exception` or exception-containing error payloads used as pure business data | Pure | H |
| `ERR006` | Cataloged partial operations/throwing parsers where policy requires explicit absence or validated access | Pure and ordinary boundary code | T |
| `ERR007` | Obvious discarding/unobserved use of `Result`, `Task<Result<_,_>>`, or other cataloged must-handle operations | All application code | T/data flow |
| `ERR008` | Error-to-success/default conversions outside approved explicit recovery functions | Application workflows | T/P/X |
| `ERR009` | Resource acquisition/cleanup constructs in the pure core; confusing cleanup with exception recovery | Pure; approved adapters may use `use`/`use!`/`try ... finally` | S/T/H |
| `ERR010` | Cancellation swallowing, unapproved fire-and-forget, blocking waits, or ad hoc background execution | Boundary/adapters | T/P/X |

### 3.7 Readability and enforcement integrity

| ID | Prohibition | Default scope | Detection |
|---|---|---|---|
| `STYLE001` | `<\|`, `<\|\|`, `<\|\|\|`, including operator-as-value use and redefinitions | Application-authored code | S/T |
| `STYLE002` | `\|\|>`, `\|\|\|>` and application-defined symbolic operators outside the protected vocabulary | Application-authored code | S/T/P |
| `STYLE003` | Direct generic folding APIs outside approved pure helper declarations | Ordinary domain/application/boundary workflows | T/P |
| `ARCH001` | Forbidden direct/transitive project or package reference direction | All projects | G |
| `ARCH002` | Unapproved dependencies, versions, runtime assets, native libraries, source packages, or API capabilities | All projects | G/P |
| `ARCH003` | Unapproved projects/languages/scripts/processes/sidecars, source generators, type providers, or executable build extensions | Repository/build/deployment | G/P |
| `ARCH004` | Shipping code depending on tests/tooling or test/generated/profile declarations that self-authorize exemptions | All projects | G/P |
| `BUILD001` | Source suppression directives/comments/attributes and diagnostic-path spoofing | All checked source | S/T |
| `BUILD002` | Compiler/build policy drift, warning downgrades, skipped checks, changed tool/policy identity | Approved builds | G/P |
| `BUILD003` | Missing/partial analysis, unknown rule/profile/symbol classification, malformed inputs, checker crash/timeout | Guard execution | G/T/P |
| `BUILD004` | Mismatch between analyzed inputs and the actual compiled/deployed inputs | CI/build/deployment | G/M |
| `BUILD005` | Invalid, stale, widened, expired, self-approved, or unused exceptions | Policy | P |
| `BUILD006` | Untrusted required-status producer, altered check coverage, or unapproved merge/deployment bypass | CI configuration | P/G |

`ERR007` and similar data-flow checks must have a documented detection boundary. They do not prove that every result is used correctly. The closed API/catalog policy, explicit patterns, and tests complement them.

---
## 4. Mutation and immutable data

### 4.1 Bindings, assignments, fields, and setters

Reject each of the following in ordinary application code:

```fsharp
// MUT001 / MUT002
let mutable offset = 0
offset <- offset + 1

// MUT003
// A record is not immutable when its field is writable.
type Account =
    { mutable Balance: decimal }

// MUT003: generated accessor still exposes mutation.
type Counter() =
    member val Value = 0 with get, set

// MUT004: mutability is not limited to the '<-' spelling.
let count = ref 0
count.Value <- 1

// MUT002: array/index/property writes all count.
buffer[index] <- value
customer.Name <- replacement
```

Preferred pattern:

```fsharp
// Pure value transformation.
type Account =
    { Balance: decimal }

let deposit amount account =
    { account with Balance = account.Balance + amount }
```

This example illustrates immutability, not a complete financial model. An actual deposit operation must validate amount/range/business requirements and address overflow as appropriate.

Detect instance/static setters, index setters, init-style property assignment, object-initializer setter use, explicit `set_...` calls where exposed, mutable fields in classes/structs, and writes through an alias. A getter-only property whose implementation mutates state must fail on its implementation or its unapproved API identity.

Do not ban immutable record copy-and-update expressions. They construct new records; they are not field assignments to the existing record.

### 4.2 Mutation through method calls

Reject:

```fsharp
let errors = ResizeArray<string>()
errors.Add "Invalid quantity"

let values = System.Collections.Generic.Dictionary<string, int>()
values.Add("a", 1)

let builder = System.Text.StringBuilder()
builder.Append("hidden mutable state") |> ignore
```

Prefer:

```fsharp
type Item =
    { Name: string
      Quantity: int }

let validationErrors items =
    items
    |> List.choose (fun item ->
        if item.Quantity > 0 then
            None
        else
            Some (item.Name + ": quantity must be positive"))
```

The catalog must identify the declaring type/member, not reject every method named `Add`. `Set.add`, `Map.add`, and approved persistent collection operations return new values and remain eligible.

Initial mutable capability families to cover:

```text
System.Collections.Generic.List<T>              // F# ResizeArray<T>
System.Collections.Generic.Dictionary<K,V>
System.Collections.Generic.HashSet<T>
System.Collections.Generic.Queue<T>
System.Collections.Generic.Stack<T>
System.Collections.Generic.LinkedList<T>
System.Collections.Generic.SortedDictionary<K,V>
System.Collections.Generic.SortedList<K,V>
System.Collections.Generic.SortedSet<T>
System.Collections.Concurrent.*
System.Collections.ObjectModel.Collection<T>
System.Collections.ObjectModel.ObservableCollection<T>
System.Collections.ArrayList / Hashtable / Queue / Stack
System.Collections.IList / IDictionary
System.Collections.Generic.IList<T> / ICollection<T> / IDictionary<K,V> / ISet<T>
Microsoft.FSharp.Core.FSharpRef<T>
System.Text.StringBuilder
```

This list is a seed, not the proof of completeness. Unknown external types/APIs are denied pending classification. Resolve F# aliases: `ResizeArray<_>` is not F# `list<_>`, and a user alias does not change the underlying type.

```fsharp
// Still rejected after alias expansion.
type Bag<'a> = System.Collections.Generic.List<'a>
let bag = Bag<int>()
```

### 4.3 Arrays and apparently read-only wrappers

Reject pure signatures such as:

```fsharp
let total (values: int array) = ...

type Snapshot =
    { Values: System.Collections.Generic.IReadOnlyList<int> }

type HiddenMutation =
    { Values: ResizeArray<int> list }
```

These are illustrative signature/body fragments. A passing fixture must provide an actual implementation instead of `...`.

Prefer domain data such as:

```fsharp
type Snapshot =
    { Values: int list }
```

`IReadOnlyList<T>` describes a read-only list interface, not deep immutability of its elements. A sequence may defer work until enumeration. Neither is an automatic pure-boundary snapshot. [S20][S21]

Policy requirements:

1. Reject arrays of all ranks, `ArraySegment<_>`, memory views, and mutable collection wrappers as pure data unless a specific approved immutable abstraction hides and owns the implementation.
2. Do not blanket-approve `IReadOnlyCollection<_>`, `IReadOnlyDictionary<_,_>`, `IEnumerable<_>`, `seq<_>`, `IQueryable<_>`, `Lazy<_>`, or arbitrary “immutable”-named third-party classes.
3. Convert external collections to approved immutable data at the boundary, validating their elements. A shallow copy only freezes the outer container, not mutable elements.
4. Do not claim a coherent snapshot when another owner can concurrently mutate the source during copying. The adapter must establish ownership/stability or use the external system's snapshot mechanism.
5. Permit internal enumeration generated by an approved operation on approved data. Do not reject compiler/library enumerator machinery as authored mutable code.

For version 1, prohibit escaping `seq<_>` in pure public data contracts. Locally derived sequences are permitted only through an explicitly approved pure pipeline and are materialized before crossing a boundary. When provenance cannot be established, prefer `List` operations or reject the sequence.

Do not convert large upload/file byte buffers into giant lists merely to satisfy a rule. Keep binary transport in a protected adapter and expose validated metadata, a typed identifier, or another approved immutable abstraction to the domain.

### 4.4 Shared state and synchronization

Reject application-owned mutable globals/statics, mutable singletons, memoization caches, thread-local or async-local state, mutable event sources, and direct use of synchronization primitives in pure code.

Seed capability catalog:

```text
lock / Monitor
Interlocked / Volatile
ThreadLocal<T> / AsyncLocal<T>
ConcurrentDictionary<K,V>
Event<T> and arbitrary event subscriptions
unapproved shared Random instances
unapproved application caches
```

Immutable module constants are allowed. A `let` binding to a mutable object is not an immutable constant for policy purposes. `DateTimeOffset.UtcNow` stored in an immutable binding is still an effectful read; section 9 covers it.

### 4.5 Loops

Reject `while` and statement-oriented `for` in normal application code. Approved adapters may use a specific loop when their contract requires it. Ban `List.iter`/`Seq.iter` in pure workflows when they serve only to invoke effects; named pure transforms are preferred.

Do not reject a pure list comprehension merely because its syntax contains `for`:

```fsharp
// Eligible if the body uses approved pure operations.
let doubled =
    [ for value in values do
          yield value * 2 ]
```

`List.map` is usually the simpler approved form:

```fsharp
let doubled = values |> List.map (fun value -> value * 2)
```

Approved recursion is not banned, but it is not a loophole that makes stateful or unapproved operations legal. Folds and recursion are covered separately in section 11.

---

## 5. Casts, type erasure, and conversions

### 5.1 Downcasts and unboxing

Reject all source routes, including partially applied functions and aliases:

```fsharp
let recover1 (value: obj) = value :?> Customer
let recover2 (value: obj) : Customer = downcast value
let recover3 (value: obj) = unbox<Customer> value

module O = Microsoft.FSharp.Core.Operators
let recover4 = O.unbox<Customer>
let recoverMany values = values |> List.map (unbox<Customer>)

let recoverSequence values = values |> Seq.cast<Customer>
```

Reject obtaining a forbidden function value, not just a syntactically direct invocation. Analyze local wrappers' bodies. An external wrapper must be covered by the dependency/API catalog; do not assume its implementation is safe from its signature.

F# distinguishes statically checked upcasts from runtime-checked downcasts. Preserve that distinction in the diagnostics. [S02]

### 5.2 Runtime type tests

Reject pure business modeling such as:

```fsharp
let displayName (customer: obj) =
    match customer with
    | :? IndividualCustomer as individual -> individual.FullName
    | :? BusinessCustomer as business -> business.CompanyName
    | _ -> "Unknown"
```

Prefer:

```fsharp
type Customer =
    | Individual of IndividualCustomer
    | Business of BusinessCustomer

let displayName customer =
    match customer with
    | Individual individual -> individual.FullName
    | Business business -> business.CompanyName
```

`tryUnbox` and runtime-type filtering are not memory-unsafe in the same sense as `Unsafe.As`. They are prohibited in the pure profile to avoid runtime-type-driven modeling. Approved boundary handlers may use specific type tests for exception translation or external API normalization.

### 5.3 `obj`, implicit boxing, and nested erasure

Reject:

```fsharp
type Anything = obj

type EventEnvelope =
    { Payload: obj }

let metadata : Map<string, obj> =
    Map.ofList [ "count", box 3 ]

let erase customer : obj =
    customer
```

Check inferred types and implicit conversions as well as explicit `box` and `:> obj`. Resolve `objnull` and nullable-object forms supported by the selected Core version. Detect erased values inside records, tuples, unions, arrays/collections, generic arguments, function parameters, and returns.

The rule is **no object-typed application payloads or authored erasure**, not “reject every CLR type because it derives from `System.Object`.” Approved generic equality/formatting/library implementations may use boxing internally without allowing application code to manufacture arbitrary `obj` values.

Do not block an otherwise approved typed call solely because an optional metadata parameter has an object-typed default that the compiler supplies. Classify that exact interop call and its lowering in a compatibility fixture. Broad object overloads remain denied.

### 5.4 Safe upcasts

Eligible:

```fsharp
// Illustrative: both the value's type and interface must be approved.
let named : IApprovedImmutableName =
    approvedName :> IApprovedImmutableName
```

Still prohibited:

```fsharp
let erased = customer :> obj
let writable = mutableList :> System.Collections.Generic.IList<int>
```

A typed interface can also hide effects. Approval requires its implementation/capability policy, not only a statically legal conversion.

### 5.5 Numeric and custom conversions

Do not ban every expression named “conversion.” Use an exact catalog for approved total widening conversions and reviewed parsers/range-checking operations.

Reject unapproved application-defined `op_Implicit`/`op_Explicit`, unchecked narrowing, and raw numeric-to-domain-enum conversion. Prefer a named `tryCreate`/`create` returning `Result` for validated conversions. Returning a domain DU is preferable to using a CLR enum to model a closed set of business states.

A helper may validate a range before performing an approved conversion. The rule is not expected to prove arbitrary arithmetic implications. Put the helper implementation and its edge-case tests in the protected approval scope.

Compiler warnings for implicit conversions add a useful layer, but do not substitute for type-aware catalog enforcement. See section 14. [S02]

---

## 6. Nulls, defaults, attributes, reflection, and native code

### 6.1 Null and unchecked initialization

Reject:

```fsharp
let name : string = null
let customer = Unchecked.defaultof<Customer>
let customer = Unchecked.nonNull externalCustomer
let customers = Array.zeroCreate<Customer> 10
```

Some examples can already produce compiler diagnostics with null checking enabled. Keep dedicated guard tests for cases that remain valid F# but violate policy.

`Unchecked` includes operations that bypass ordinary checks, including default construction and unchecked removal of nullability. Maintain a resolved-symbol inventory for the **pinned** Core version, including any unchecked null-related active patterns. Deny new members until reviewed. [S08]

Do not regard any of these as proof of validation:

```fsharp
Some externalValue
Ok externalValue
externalValue |> Unchecked.nonNull
```

For an approved nullable-string normalization function at the boundary:

```fsharp
// BOUNDARY normalization permission; F# nullable-checking toolchain required.
let normalizeOptionalText (value: string | null) : string option =
    match value with
    | null -> None
    | text -> Some text
```

This normalizes null only. It does not establish non-emptiness, allowed length, or any business invariant. Apply a separate validator when those conditions are required. Nullability checking helps express nullable references, but external .NET values still require boundary treatment. [S03]

The normalizer's permission must not permit arbitrary unchecked null assertions or null-bearing return values.

### 6.2 Zero-initialized structs and buffers

`Array.zeroCreate<'T>` initializes elements using default values. Structs can also be zero-initialized; a struct's all-zero state can violate an invariant or contain null reference fields. Do not check only “is the element a reference type?” [S19][S14]

Policy:

- Invariant-bearing domain wrappers should default to reference DUs/private representations rather than introducing custom structs without need.
- Reject default construction of a designated invariant-bearing struct.
- Reject arbitrary generic zero-initialization where default-validity of `'T` is unknown.
- A centrally approved adapter may allocate a `byte` buffer or other exact default-valid element type. Its buffer must not escape into pure data.
- Legitimate primitive zeros, `false`, `None`, `ValueNone` where approved, and explicitly constructed empty immutable collections remain allowed.

Do not ban all generated `initobj`/default-value IL indiscriminately. Diagnose authored initialization capabilities and approved type contracts.

### 6.3 Attribute policy

Resolve attributes by assembly identity, namespace, type identity, target, constructor arguments, and named arguments. Evaluate flags semantically, including bitwise combinations.

Reject or require exact protected permission for:

```fsharp
[<AllowNullLiteral>]
type ExternalLikeObject() = class end

[<CLIMutable>]
type Customer =
    { Name: string }

// Field examples belong inside a valid containing declaration.
[<DefaultValue>]
val mutable Name: string

[<DefaultValue(false)>]
val mutable Customer: Customer
```

`CLIMutable` adds a CLI default constructor and setters. F# `DefaultValue` marks an uninitialized field, and its `false` form omits the usual null-support constraint check. These are construction/mutation permissions, not cosmetic annotations. [S09][S10]

Also reject application-defined null representations:

```fsharp
[<CompilationRepresentation(
    CompilationRepresentationFlags.UseNullAsTrueValue)>]
type CustomOptionalValue =
    | Missing
    | Present of string
```

The flag permits null as a union-case representation. This policy restricts new application declarations, not the internals of approved FSharp.Core types. [S11]

Attribute catalog requirements:

| Category | Requirement |
|---|---|
| `AllowNullLiteral` | Reject source use in pure types, including alternate argument forms; an explicit harmless `false` spelling still needs catalog approval. |
| F# `DefaultValue` | Deny by default, including `false`. |
| `CLIMutable` | Only approved DTO declarations, never domain types. |
| `CompilationRepresentation` | Permit only exact approved flags; disallow application null representations. |
| `DllImport`, interop layout/marshal/export attributes | No application permission initially. |
| `InternalsVisibleTo` and similar access changes | Protected approval; tests must not silently widen production construction access. |
| Nullability assertion/contract attributes | Deny unknown assertions; attributes do not prove bodies honor the promise. |
| Suppression attributes | Cannot suppress company policy. |
| `GeneratedCode`, `CompilerGenerated`, source-generation markers | Do not confer trust or exemption. |
| Custom attributes | Deny by default until exact type/usage is approved. |

Do not confuse `Microsoft.FSharp.Core.DefaultValueAttribute` with `System.ComponentModel.DefaultValueAttribute`. The latter describes metadata and does not initialize the property. An unknown attribute can still fail `ATTR001`, but the explanation must be accurate. [S12]

Seed harmless/necessary attribute permissions only after inspecting the repository, for example qualified-access, equality/comparison, sealed/interface-signature, literal, test-framework, and approved DTO attributes. Check exact scope; do not allow a whole namespace merely to make tests compile.

### 6.4 Reflection and dynamic execution

Reject application routes such as:

```fsharp
let targetType = typeof<Customer>
let actualType = value.GetType()
let customer = System.Activator.CreateInstance<Customer>()

// Other representative forbidden symbols:
// MethodInfo.Invoke / ConstructorInfo.Invoke
// FieldInfo.SetValue / PropertyInfo.SetValue
// Delegate.DynamicInvoke
// FSharpValue.MakeRecord / MakeUnion
// FSharpValue.PreComputeRecordConstructor / PreComputeUnionConstructor
// RuntimeHelpers.GetUninitializedObject
// FormatterServices.GetUninitializedObject
```

F# reflection provides record/union construction APIs, and the runtime exposes uninitialized-object creation. These require separate rules; banning cast syntax alone does not close them. [S22][S23]

Also deny unapproved assembly loading, `Reflection.Emit`, dynamic methods, expression-tree compilation/evaluation, quotation evaluation, scripting/FSI embedding, and runtime source compilation. Ordinary query expressions/builders are subject to the approved-provider catalog; their syntax does not bypass the rule.

A serializer may use reflection internally as part of its approved implementation. That does not grant raw reflection access to its callers. Direct DTO reflection, when unavoidable, gets a narrow adapter contract and cannot target domain types.

### 6.5 Native code and memory capabilities

Reject source P/Invoke and related declarations:

```fsharp
open System.Runtime.InteropServices

[<DllImport("native-library")>]
extern int nativeOperation()
```

F# native imports use `DllImport` and `extern`. `Unsafe.As` can bypass normal runtime type checks; do not treat it as equivalent to an ordinary checked downcast. [S24][S25]

Cover `NativePtr`, `Unsafe`, reinterpretation via `MemoryMarshal`, unmanaged allocation/copying/marshaling, function pointers, address-taking, and native import/export paths. Deny `nativeint`, `unativeint`, `byref`, `outref`, `inref`, `Span`, `ReadOnlySpan`, `Memory`, and `ReadOnlyMemory` as authored pure data capabilities.

Read-only spans/memory are not automatically immutable ownership. Approve exact boundary uses only when required.

**Compiler-generated interop lowering is a separate case.** A documented, approved F# tuple-returning call to a .NET `TryParse` method may lower to out-parameter/default-local machinery. Do not reject it solely because the compiler uses a temporary byref. Either approve the exact call/lowering or expose it through a reviewed parsing helper. Never broadly exempt every byref use. The .NET `TryParse` API itself uses an out result. [S31]

The guard's own PE/FCS tooling may use arrays and metadata APIs under `GUARD_TOOLING`. That exception does not justify adding low-level code to a shipping application.

---
## 7. DTOs, validation, and controlled construction

### 7.1 Deserialization into domain types is always prohibited

Reject every automatically materialized target containing a domain type:

```fsharp
JsonSerializer.Deserialize<Customer>(json)
JsonSerializer.Deserialize<Customer list>(json)
JsonSerializer.Deserialize<Envelope<Customer>>(json)
JsonSerializer.Deserialize<Customer option>(json)
```

This is still prohibited:

```fsharp
type CustomerDto =
    { Id: Domain.CustomerId
      Name: string }
```

A `Dto` suffix is not a safety classification. DTO ownership comes from protected assembly/type policy. DTO type graphs must not reference domain types, including through nested records, properties, generic payloads, optional values, collections, unions, or aliases.

Apply the rule to JSON libraries, HTTP body/model binding, database row mapping, ORM materialization, cache objects, message consumers, and other automatic constructors. The term “deserialization” includes these construction paths, not just methods spelled `Deserialize`.

### 7.2 Restrict materializer access

Only approved adapters may access raw materializers. Application-boundary code calls concrete DTO-returning operations:

```fsharp
// Proposed adapter interface, shown as an F# signature fragment.
val decodeCreateCustomer :
    string -> Result<CreateCustomerDto, DecodeError>
```

Do not expose:

```fsharp
// Prohibited application-facing API.
val deserializeAnything<'T> : string -> 'T
```

An `internal` generic helper inside the isolated serializer may be necessary, but all its instantiated targets must be permitted and callers cannot choose arbitrary domain types. Runtime `System.Type` overloads, converter factories, reflection-created delegates, and external generic wrappers require the same target guarantee or are denied.

Where the exact target cannot be determined conservatively, reject the operation instead of treating it as a DTO.

Framework registration needs checking too. A handler parameter typed as a domain object can enable direct framework materialization without an explicit deserializer call in the handler body. The catalog must include those binder/registration signatures or only permit a protected DTO-binding facade.

### 7.3 Separate normalization from business validation

An approved decoder/normalizer must account for missing fields, explicit null, null collection elements, incorrect types, unexpected enum values, ranges, and structural constraints relevant to the contract.

System.Text.Json nullability enforcement has documented limits around root values, collection elements, and generic members; missing-required-property rules are separate. Do not use nullable annotations as evidence that external data is already valid. [S18]

Prefer this shape:

```fsharp
// App.Contracts: normalized transport values, still not domain-valid.
type CreateCustomerDto =
    { Name: string option
      RequestedQuantity: int }

// App.Boundary or an approved mapper:
// DTO -> Result<DomainInput, ValidationError list>
```

When raw framework DTOs must contain nullable arrays/fields or mutable setters, put them in an exact approved contract/adapter scope. Normalize into data-only values before calling domain validators. Do not leak raw framework objects, `JsonElement`, database rows, or request/service objects into the core.

A copying or validation pass must address nested content. Merely constructing `Some dto`, `Ok dto`, or an outer immutable record is not the required transformation.

### 7.4 Private domain representation

Example invariant-bearing type:

```fsharp
module App.Domain.Quantity

type T = private Quantity of int

type Error =
    | MustBePositive

let create value =
    if value > 0 then
        Ok (Quantity value)
    else
        Error MustBePositive

let value (Quantity quantity) =
    quantity
```

Optional controlled public signature:

```fsharp
// Quantity.fsi; place before Quantity.fs in compilation order.
module App.Domain.Quantity

type T

type Error =
    | MustBePositive

val create : int -> Result<T, Error>
val value : T -> int
```

F# signature files control the exposed surface and must precede the associated implementation. Private representations and controlled signatures are compiler-level tools, not merely naming conventions. [S13][S39]

The implementation MUST:

1. Maintain a protected inventory of invariant-bearing types or designate an entire approved domain-type area for controlled construction.
2. Require private/hidden representations where needed; ordinary alternative-only DUs need not hide every case.
3. Inspect `.fsi` and `.fs` together; a signature must not hide an unchecked public construction capability from the guard's implementation analysis.
4. Restrict public API additions and access widening. A newly added `createUnchecked`, whatever its name, is not allowed merely because it returns `T`.
5. Test every approved validator and invariant-preserving operation at edge cases. The analyzer enforces construction routes; it does not prove a predicate is correct.
6. Prohibit bypass through reflection, default construction, friend access, generic materialization, and unapproved dependency helpers.

Do not implement “unchecked helper detection” as a search for the string `Unchecked`. Control all new public constructors/functions that can produce a designated domain type, regardless of spelling. Internal construction remains possible inside its defining implementation and requires tests/review.

### 7.5 Outbound data

Map approved domain values to explicit response/storage DTOs. This prevents transport schemas from silently becoming domain representations.

```fsharp
// Illustrative boundary projection.
let toQuantityDto (quantity: App.Domain.Quantity.T) =
    { RequestedQuantity = App.Domain.Quantity.value quantity
      Name = None }
```

A typed effect facade may accept validated domain commands when its implementation belongs to the boundary layer and its behavior is approved. Do not impose the serializer assembly's “no Domain reference” rule on every effectful component indiscriminately. The materializer component and mapper/orchestrator have different roles.

---

## 8. Explicit domain matching

### 8.1 Compiler exhaustiveness is necessary but not sufficient

Reject:

```fsharp
type Status =
    | Draft
    | Approved
    | Rejected

let canPublish status =
    match status with
    | Approved -> true
    | _ -> false
```

Prefer:

```fsharp
let canPublish status =
    match status with
    | Draft -> false
    | Approved -> true
    | Rejected -> false
```

F# reports incomplete matching with `FS0025`; a catch-all can make a match exhaustive without naming future cases. The company rule adds explicit domain-case coverage. [S15]

### 8.2 Exact rule

For a source-authored decision match over a protected closed domain DU:

- Require explicit constructor coverage.
- Reject wildcard or variable catch-all coverage (`_`, `other`, alias/as-pattern variants) that substitutes for unnamed cases.
- Apply the rule to `match`, `function`, nested matches, relevant lambda patterns, and active-pattern wrappers that hide the original domain alternatives.
- Guarded cases do not establish unconditional coverage; let the compiler check actual exhaustiveness as well.
- Require explicit case handling after adding a new domain union case.

Do not reject an underscore that ignores a payload inside an explicitly named case:

```fsharp
match decision with
| Accepted _ -> true
| Rejected _ -> false
```

An explicit OR pattern such as `Draft | Rejected` is eligible: the constructors are named. Catch-alls over open primitive domains, such as the failure branch of an integer range test, are not this rule's target. Neither is list tail-binding such as `head :: rest`.

Do not force every arbitrary `let wholeValue = value` to destructure a union. The rule applies to decision/destructuring coverage, not binding a value for forwarding. Tests must demonstrate this boundary to avoid false positives.

Prefer direct DU matching over unapproved active-pattern abstractions that reintroduce hidden defaults. Standard `Option`/`Result` handling must remain exhaustive; critical recovery rules in section 10 apply in addition.

---

## 9. Effects and executable capabilities

### 9.1 Explicit inputs instead of ambient reads

Reject in pure code:

```fsharp
let expired expiresAt =
    System.DateTimeOffset.UtcNow >= expiresAt

let id = System.Guid.NewGuid()
let configuration = System.Environment.GetEnvironmentVariable("CONFIG")
let content = System.IO.File.ReadAllText(path)
```

Prefer explicit data:

```fsharp
let isExpired
    (now: System.DateTimeOffset)
    (expiresAt: System.DateTimeOffset)
    =
    now >= expiresAt
```

The clock/identifier/environment/filesystem operations belong in an approved effect boundary. Maintain the ambient-effects catalog for time, randomness, environment variables, process state, culture-sensitive defaults, network, database, logging, filesystem, timers, and global configuration.

Even apparently harmless parsing/formatting can depend on ambient culture. Approve exact culture-stable APIs or helpers rather than blanket-allowing every overload. For example, `Int32.TryParse` has overloads with explicit style/provider control. [S31]

### 9.2 An interface does not make a computation pure

Reject service and callback capabilities in pure domain data contracts:

```fsharp
type RequestContext =
    { LoadCustomer: CustomerId -> Customer
      Clock: unit -> System.DateTimeOffset }
```

An effectful callback can be invoked without using any obviously effectful syntax in the receiving function. For version 1, pure ingress contracts should be data-only. Domain functions receive the loaded customer and current time as values.

Higher-order functions such as `List.map` remain allowed internally when their function values originate in checked pure code or approved pure libraries. Do not globally ban function types or parametric generic helpers. Distinguish **checked pure computation** from **behavior injected by unchecked callers**.

F# signatures do not encode purity. Enforce the data-only public boundary and checked call graph conservatively. When the guard cannot establish that a callback/service is approved, reject it or keep it in the boundary.

### 9.3 Builders and wrappers

Approve exact computation-expression builder identities and relevant member semantics. In pure code, allow only reviewed pure builders (or simply explicit `Result.bind`/matching). In boundary code, approve the standard task/async builders and selected company combinators.

Reject application-created builders/operators/helpers that hide effects, arbitrary catching, unchecked construction, or type erasure. Resolve custom operations and builder methods; do not assume `result { ... }` or a method named `Bind` has safe semantics.

Prohibit direct task/async resource values in pure data. Keep effectful application orchestration in `BOUNDARY`, even when the existing project happens to be named `Application`.

### 9.4 Approved API catalog

The catalog must classify **individual operations or deliberately reviewed groups**, not all of `System.*` or all of `FSharp.Core`.

Each entry records symbol identity, assembly/package identity, accepted overloads and type arguments, source profiles, effect category, data-shape conditions, null/default behavior where relevant, and tests.

An unknown operation fails until reviewed. Approved pure library internals may use mutation or boxing; application source gets the library's reviewed contract, not permission to use those mechanisms itself.

---

## 10. Result, exceptions, cancellation, and cleanup

### 10.1 Expected failures are data

Reject ordinary business control flow using `try ... with`, custom business exceptions, or throwing helpers:

```fsharp
exception CustomerMissing

let loadCustomer id =
    match findCustomer id with
    | Some customer -> customer
    | None -> raise CustomerMissing
```

Prefer:

```fsharp
type CustomerError =
    | CustomerNotFound

let requireCustomer customer =
    match customer with
    | Some value -> Ok value
    | None -> Error CustomerNotFound
```

Use error DUs for business alternatives, and `Option` for meaningful absence. Do not require `Result` for every total transformation merely to wrap its output in `Ok`.

Seed throwing-helper catalog:

```text
raise / reraise
failwith / failwithf
invalidArg / invalidOp / nullArg
ExceptionDispatchInfo.Throw and similar indirect throw paths
assert used as validation
exception declarations for expected business outcomes
```

Assertions from approved test frameworks belong to `TEST`, not the production error-handling model.

### 10.2 Result does not catch exceptions

Reject as a supposed safe parser:

```fsharp
let parseNumber (text: string) : Result<int, string> =
    Ok (System.Int32.Parse text)
```

An approved replacement might expose:

```fsharp
// Proposed pure parsing helper interface.
val parseInt32Invariant : string -> Result<int, NumberError>
```

`Result.bind` composes functions returning `Result`; it does not automatically turn thrown exceptions into `Error`. Use it for modeled failures, not as an exception boundary. [S16]

### 10.3 Approved exception translation

F# `try ... with` matches exceptions; exceptions not matched by a handler propagate. That mechanism is allowed only in specific adapter implementations. [S26]

Example protected adapter, not ordinary app source:

```fsharp
module Company.Platform.TextFiles

open System
open System.IO
open System.Threading
open System.Threading.Tasks

type ReadError =
    | FileMissing
    | AccessDenied

let readText
    (path: string)
    (cancellationToken: CancellationToken)
    : Task<Result<string, ReadError>> =
    task {
        try
            let! text = File.ReadAllTextAsync(path, cancellationToken)
            return Ok text
        with
        | :? FileNotFoundException ->
            return Error FileMissing
        | :? UnauthorizedAccessException ->
            return Error AccessDenied
    }
```

`File.ReadAllTextAsync` reports asynchronous failures when awaited. The adapter intentionally translates only the listed cases; it does not claim all possible failures are modeled. [S30]

Adapter contract requirements:

- The approved policy lists exact catch patterns and mappings.
- Do not catch `System.Exception`, `_`, or an unconstrained exception variable merely to return a generic business error.
- Do not swallow unexpected defects, authentication failures, programming errors, or unrelated infrastructure failures as “not found.”
- Do not pass `exn`/exception objects into pure business error types.
- A central host handler may log/report unexpected exceptions and produce an appropriate generic failure response. That is a separate protected host capability, not routine business recovery.
- Policy checks validate structure; tests validate the intended mapping and propagation behavior.

Reject these application shortcuts:

```fsharp
// ERR004: everything becomes an ordinary failure.
try
    performOperation ()
with
| _ -> Error CustomerNotFound

// ERR003: same capability hidden behind a library function.
let captured = Async.Catch operation
```

`Async.Catch` is an exception-capturing API even without authored catch syntax. Also catalog task continuation/fault-inspection helpers and company wrappers that provide equivalent catching. [S29]

### 10.4 Cancellation and task discipline

Preserve cancellation separately from ordinary domain failures. .NET task cancellation uses cancellation exceptions and task state; converting cancellation to an ordinary returned `Error` can instead complete a task normally. [S28]

Require adapter tests for already-cancelled tokens and cancellation during work. Do not broadly catch `OperationCanceledException` into a business `Error`. Some platform APIs need documented distinctions between caller cancellation and timeout; handle those only through explicit, tested adapter policy.

Ordinary apps must not introduce ad hoc background tasks, `Async.Start`, `Task.Run`, blocking `.Wait()`/`.Result`/`GetAwaiter().GetResult()`, or discarded tasks as a workaround. Use approved orchestration/background-job capabilities where the application genuinely needs them.

`Task<Result<_,_>>` remains an effectful boundary type. Its existence does not guarantee that the task cannot fault or be cancelled.

### 10.5 Cleanup is not catching

Keep source rules for `TryWith` and `TryFinally` distinct. `try ... finally` runs cleanup; `use` provides disposal. They are not alternative encodings of business success/failure. [S27][S32]

Approved adapter example:

```fsharp
let readTextFromStream (stream: System.IO.Stream) =
    use reader = new System.IO.StreamReader(stream)
    reader.ReadToEnd()
```

This is a resource-management illustration, not an approved application API or a promise of exception-free operation. The actual adapter must document stream ownership, whether disposal closes the supplied stream, failure mapping, and sync/async requirements.

Pure code does not acquire resources. Approved adapters may use `use`, `use!`, `using`, `try ... finally`, and the corresponding approved builder methods. Do not reject generated cleanup/state-machine IL as though it were authored business catching or mutation.

### 10.6 Partial operations and throwing accessors

Maintain an exact restricted-operation catalog, including applicable overloads:

```text
Option.get / Option.Value / equivalent ValueOption extraction
List.head / tail / last / item / find / findBack / pick / exactlyOne
Seq counterparts and array/index access outside approved handling
Map.find / map indexers where absence is not established
Enumerable.First / Single / Last and similar throwing lookups
throwing Parse overloads for expected untrusted input
```

This is a seed list; functions such as minimum/maximum/average on potentially empty input need classification too. A list operation can throw for unsupported input, while a corresponding `try` operation can return absence. `Option.get` also requires a present value. [S17][S33]

Prefer `tryHead`, `tryFind`, `tryItem`, explicit matching, or a reviewed nonempty/validated-index abstraction. Do not implement arbitrary theorem proving about every local guard; keep the application surface small and approve exact invariant-based helpers.

`List.tryFind` and `List.tryFindBack` are different operations. A migration must preserve whether the old implementation selected the first or last matching element.

Checked arithmetic does not prove arithmetic totality. Overflow/division/decimal range and other numerical failures require appropriate input constraints or approved result-returning arithmetic helpers when expected by the business model.

### 10.7 Must-handle results and intentional recovery

Reject obvious discarded results:

```fsharp
saveCustomer customer |> ignore
let _ = saveCustomer customer

// In a task workflow:
let! _ = saveCustomerAsync customer
```

Detection should include aliases of `ignore`, wildcard bindings, discarded sequence expressions, applicable `do`/`do!`, and known must-handle return types. Track simple local flows and produce tests for the supported detection boundary. Do not claim a complete linear-use proof.

Also restrict these outside approved recovery implementations:

```fsharp
result |> Result.defaultValue fallback
result |> Result.defaultWith (fun _ -> fallback)
result |> Result.toOption
```

These functions deliberately discard or replace error information; they are not always wrong, but a workflow must not silently convert failure into success. [S16]

Approved recovery should name its purpose and handle relevant cases explicitly:

```fsharp
type AvatarError =
    | AvatarMissing
    | AvatarStoreUnavailable

let recoverMissingAvatar fallback outcome =
    match outcome with
    | Ok avatar -> Ok avatar
    | Error AvatarMissing -> Ok fallback
    | Error AvatarStoreUnavailable -> Error AvatarStoreUnavailable
```

This helper illustrates an intentional policy choice. Its approval is scoped; it does not authorize generic “default on any error.” Ordinary optional defaults can remain allowed when absence is actually part of the domain model. Do not confuse harmless `Option.defaultValue` in such a model with erasing a critical `Result` failure.

The guard can reject cataloged shortcuts and syntactic erasure patterns. It cannot decide the business appropriateness of every `Error -> Ok` branch; reviewed recovery contracts and tests are required.

---

## 11. Readability: operators and folds

### 11.1 Operator vocabulary

Reject:

```fsharp
validate <| normalize input
createCustomer <|| (name, email)
createOrder <||| (customer, items, address)

(name, email) ||> createCustomer
(customer, items, address) |||> createOrder

let backwardApply = (<|)
```

Prefer:

```fsharp
input
|> normalize
|> validate

createCustomer name email
createOrder customer items address
```

Backward/tuple pipes are function-application notation, not type-safety primitives. This is a consistency/readability rule. The language also permits custom operators, so check declarations and operator-as-value references, not only infix use. [S34]

Permit ordinary application, `|>`, arithmetic/comparison/Boolean operators, list construction/append, and other individually approved built-ins. Version 1 does not automatically add a ban on every other built-in operator: for example, classify existing uses of `>>` and `<<` in the protected vocabulary rather than silently expanding the agreed ban. Prefer named functions or pipelines in ordinary workflows. Any additional hard style restriction requires an explicit policy decision.

Default-deny application-defined symbolic operators, including redefining permitted spellings. `let (|>) ...` must not replace the approved forward-pipe operation with arbitrary semantics.

For railway composition prefer:

```fsharp
request
|> validateRequest
|> Result.bind authorize
|> Result.bind decide
```

The functions must have compatible error types or map errors explicitly. Do not introduce custom symbolic bind/map operators merely to shorten this.

Autofixes must preserve evaluation order, overload resolution, and parenthesization. The examples use simple values/functions; an arbitrary syntactic rearrangement is not automatically equivalent.

### 11.2 Generic folds

Reject in ordinary workflows:

```fsharp
let customer =
    events |> List.fold applyEvent initialCustomer
```

Permit the same accumulation in a specifically approved pure helper:

```fsharp
module App.Domain.CustomerHistory

// Exact declaration approved for STYLE003 only.
let rebuild initialCustomer events =
    events |> List.fold applyEvent initialCustomer
```

Then ordinary workflow code reads:

```fsharp
let customer = CustomerHistory.rebuild initialCustomer events
```

A fold threads an accumulator; it can express immutable state transitions directly. Restricting its visibility does not make mutation a better substitute. [S17]

The protected helper permission MUST:

- Identify an exact declaration/module and permitted fold APIs, not every file ending in `Helpers.fs`.
- Leave all purity, casting, nullability, and effect restrictions enabled.
- Require tests for order, empty input, error behavior, and representative transitions.
- Reject trivial public renaming such as an unrestricted `Company.fold` to evade the readability policy.

Initial direct-fold catalog should include the relevant `fold`, `foldBack`, `fold2`, `foldBack2`, `mapFold`, `mapFoldBack`, and LINQ `Aggregate` overloads in the pinned approved libraries. Classify equivalent state-scanning/reduction APIs consistently; do not silently replace the ban with `scan` or a partial `reduce`. Unknown external symbols already fail the API catalog.

Do not ban arbitrary identifiers containing `fold`, or claim this rule prevents recursion from expressing accumulation. Do not automatically rewrite every fold into custom recursion. Prefer a named responsibility-specific helper and preserve behavior.

### 11.3 General lint and formatting

Keep formatting and ordinary style tooling separate from the safety authority. FSharpLint can provide naming, complexity, unnecessary-lambda, and idiom guidance; a formatter can supply deterministic layout.

Do not invent arbitrary size/complexity thresholds as new safety proofs. Select and pin ordinary lint/format settings after inspecting the codebase. Mandatory company bans come from the guard and are always build errors in their scope.

FSharpLint supports suppression comments. Do not allow application authors to use those comments to disable mandatory adopted lint policy or the company guard. [S35]

---
## 12. Analyzer implementation

### 12.1 Components

Implement one shared policy engine with separate frontends:

```text
Company.FSharp.Guard.Policy
    Schema, catalog loading, profile resolution, exception validation.

Company.FSharp.Guard.Analysis
    Source, symbol, type-shape, and supported local-flow rules.

Company.FSharp.Guard.ProjectModel
    Approved project loading, compiler-input normalization, graph checks.

Company.FSharp.Guard.Cli
    Authoritative analysis orchestration, diagnostics, reports, exit codes.

Company.FSharp.Guard.Analyzers
    Editor integration using the same rules, not a second implementation.

Company.FSharp.Guard.Build
    MSBuild integration and manifest export/verification.

Company.FSharp.Guard.Tests
    Positive, negative, bypass, build, and compatibility fixtures.
```

Use FSharp.Compiler.Service for syntax, type-checking, resolved symbols, and checked expressions. The analyzer SDK can provide editor integration; it documents separate CLI/editor contexts and compatibility dependencies. Pin and test the supported FCS/Core/editor combination. [S04][S05][S06]

Do not assume C# Roslyn analyzers inspect F# source. Do not assume installing an editor analyzer means `dotnet build` executes it.

### 12.2 Pass A: source syntax and tokens

Inspect source before optimization, including forbidden syntax in unused functions. Cover bindings, fields, members, patterns, attributes, directives, operators, computation expressions, object construction, quotation forms, and exception constructs.

Use real tokens/AST, not regex for language constructs. Operator strings in comments/documentation must not fail. Suppression directives are a separate token/trivia rule.

The FCS syntax model distinguishes constructs such as explicit/inferred downcasts, `TryWith`, and `TryFinally`. Implement against the actual pinned node shapes; do not copy unverified visitor code from a different version. [S44]

Source scanning must use the physical file identity, not only logical locations remapped by `#line`. Prohibit authored line/path remapping; permit only attested generated sources where the generator contract requires it.

### 12.3 Pass B: typed symbols and expressions

Inspect all resolved symbol uses and relevant checked expressions, not just direct `Call` nodes. At minimum cover:

- Function values, aliases, partial application, higher-order arguments, member groups, and extension methods.
- Constructors, property/index getters/setters, field reads/writes, attributes, generic arguments, and explicit/implicit conversions.
- Instantiated signatures after generic substitution, abbreviations, array ranks, nullability, and relevant base/interface types.
- Builder member calls, active patterns, operators, captured closures, and relevant inherited APIs.
- Local bodies, nested modules/types, and implementation declarations even when they are absent from public `.fsi` signatures.

Resolve a symbol to an approved identity using assembly identity plus fully qualified declaring type/member, generic arity, overload signature, and package/reference provenance. Account for documented type forwarding and pinned framework reference facades. Do not trust a type merely because it is named `System.SomeApprovedType` in a user assembly.

Use safe optional FCS identity APIs where a full name may not exist. Missing identity is not permission; determine the correct local symbol identity or report an analysis error. The SDK documentation specifically warns about accessing a missing entity full name. [S06]

`Coerce`, `DefaultValue`, boxing, and byref-like nodes need source/semantic classification. Some arise from approved compiler lowering. Classify exact approved patterns and add regression fixtures; do not blanket-skip “generated-looking” expressions.

Maintain both:

1. A deny catalog for precise diagnostics about known violations.
2. An allow catalog for the approved external surface, closing the gap from unknown wrappers/APIs.

### 12.4 Pass C: recursive shape and contract analysis

Implement a memoized recursive classifier with results such as:

```text
ApprovedPureData
ApprovedTransportData
ApprovedOpaqueImmutableValue
CheckedPureFunction
BoundaryCapability
Forbidden(reason, path)
Unresolved(reason)
```

This is a proposed internal model, not an FCS API.

The classifier must:

1. Expand type abbreviations and substitute generic arguments.
2. Track paths through fields, union payloads, tuples, records, collections, and signatures.
3. Handle recursive data types without infinite recursion; memoize by canonical type plus instantiated arguments/context.
4. Reject unknown external data types unless an explicit opaque immutable contract approves them.
5. Distinguish generic parametric code from an unknown concrete payload. Do not reject `List.map` merely because it is generic; check permitted instantiations and the checked implementation contract.
6. Reject generic materializers that can manufacture arbitrary output types rather than preserve/transform approved inputs.
7. Inspect source members/bodies of local types. An immutable-looking field list does not establish that arbitrary methods/getters are pure.
8. Do not recurse through every implementation field of approved framework primitives such as strings or F# lists. Those use explicit library contracts.
9. Separate a type's eligibility as data from the eligibility of every operation on it. An approved immutable timestamp value does not approve `UtcNow`.

A useful diagnostic includes the path:

```text
CustomerEnvelopeDto.Customer.Id
  -> Domain.CustomerId
DATA001: materialization target contains a domain type.
```

### 12.5 Pass D: project/build graph and approved inputs

Load real project compilation contexts, including source order, references, framework, configuration, defines, language version, and generated inputs. Do not use script project options as a substitute for loading `.fsproj` files.

Use a tested project-loader integration or a trusted compiler-input export. The loader must not recursively build the current project from inside its own pre-compile guard hook.

Check transitive references, package assets, build imports/tasks, source inputs, and all shipping configurations. A graph loaded with missing references is not complete enough to certify.

Fail when an unsupported FCS/SDK feature, AST construct, type shape, project language, or generator prevents full supported analysis. For ordinary new syntax, distinguish “known permitted node with recursively checked children” from “unknown node silently ignored.”

### 12.6 Required artifact checks; optional deeper IL analysis

Implement a defense-in-depth metadata pass over application-owned compiled assemblies. The required baseline verifies expected assembly identities/dependencies, unexpected P/Invoke/native declarations, and the input/output artifact linkage. Extend it with precisely defined prohibited-reference and materializer/attribute checks where the compiled metadata supports them; deeper instruction analysis is optional unless explicitly included in protected policy.

Use maintained metadata/IL reading libraries where feasible. Do not implement an ad hoc bytecode decoder merely to claim completeness. A metadata pass must report parse/inspection failure as failure.

Do not globally ban `castclass`, `unbox.any`, `box`, `ldnull`, `initobj`, field stores, or exception-handling clauses in compiler-generated IL. Such a rule would reject ordinary F# compilation rather than enforce the intended authored-language policy.

If selected truly low-level instructions are disallowed in application assemblies, define exact supported lowering exceptions and regression tests. Metadata scanning supplements source/type policy; it does not prove arbitrary dependencies safe.

### 12.7 Diagnostics and CLI contract

Diagnostics must include rule ID, error severity, physical file/range, project/profile, resolved offending symbol/type when relevant, short reason, approved alternative, and policy/tool version. Emit stable MSBuild-friendly text, JSON, and SARIF.

Example:

```text
src/App.Domain/Decode.fs(21,9): error CAST002:
Unboxing is prohibited in PURE code.
Resolved symbol: Microsoft.FSharp.Core.Operators.unbox
Use an explicit domain union or an approved boundary normalizer.
```

Proposed commands to implement:

```text
company-fsharp-guard doctor --repo <path> --policy <protected-policy>
company-fsharp-guard inventory --repo <path> --output <inventory.json>
company-fsharp-guard check-project --manifest <compiler-inputs.json> --policy <policy>
company-fsharp-guard check-repository --repo <path> --policy <policy> --matrix <matrix.json>
company-fsharp-guard verify-artifacts --manifest <build-attestation.json> --policy <policy>
company-fsharp-guard validate-policy --policy <policy>
```

Exit-code contract:

| Code | Meaning |
|---|---|
| `0` | Complete supported analysis; no unexcepted violations; all mandatory validations passed. |
| `1` | Policy violations. |
| `2` | Invalid policy/configuration/unsupported toolchain. |
| `3` | Analysis failed or was incomplete, including crashes/timeouts/missing references. |
| `4` | Input/artifact/attestation mismatch or integrity failure. |

Every nonzero code fails build/CI. `inventory` is an informational command and must never be accepted as the required safety check. Do not add `--ignore-rule`, `--continue-on-error`, or application-controlled suppression flags to the authoritative commands.

Editor analysis may be temporarily incomplete while someone types. It should report the available diagnostics without pretending to certify the whole project. Only the complete CLI/build/CI path can pass the gate.

---

## 13. Policy, catalogs, and protected exceptions

### 13.1 Ownership

Store the authoritative policy and approved tool artifacts outside ordinary application write permissions, preferably in a centrally maintained platform repository/artifact system. A repository-local copy is for developer feedback; CI compares it with the approved identity or ignores it in favor of the protected copy.

Protect at least:

```text
Guard/analyzer/tool binaries and source of the authoritative tool
Policy schema, rule catalog, approved symbols/types/attributes
Project/profile and invariant-type classification
Exceptions and approved helper declarations
Directory.Build.props / Directory.Build.targets and imported build files
*.fsproj / global.json / dependency and lock files
Generator/build-task configuration
CI workflows, check identities, deployment configuration
Public invariant-construction signatures and approved adapter contracts
```

Code ownership is useful, but the authoritative check must also reject unapproved changes. A comment claiming approval is not authorization.

### 13.2 Proposed policy shape

This JSON is a schema illustration. Replace placeholders with verified values during implementation; placeholder values must fail validation.

```json
{
  "schemaVersion": 1,
  "policyVersion": "1.0.0",
  "defaultSeverity": "error",
  "unknownProject": "error",
  "unknownExternalSymbol": "error",
  "unknownAttribute": "error",
  "incompleteAnalysis": "error",
  "toolchainManifest": "toolchain.lock.json",
  "projects": [
    {
      "path": "src/App.Contracts/App.Contracts.fsproj",
      "profile": "CONTRACTS"
    },
    {
      "path": "src/App.Domain/App.Domain.fsproj",
      "profile": "PURE"
    },
    {
      "path": "src/App.Boundary/App.Boundary.fsproj",
      "profile": "BOUNDARY"
    }
  ],
  "approvedApiCatalog": "approved-apis.json",
  "approvedTypeCatalog": "approved-types.json",
  "approvedAttributeCatalog": "approved-attributes.json",
  "approvedHelperCatalog": "approved-pure-helpers.json",
  "approvedMaterializerCatalog": "approved-materializers.json",
  "invariantTypeCatalog": "domain-invariants.json",
  "compilerPolicy": "compiler-policy.json",
  "interopProjects": [],
  "exceptions": []
}
```

Validate schemas strictly: unknown keys, invalid IDs, duplicate/conflicting profiles, overlaps that widen trust, unresolved selectors, or impossible catalog entries fail. Do not ignore misspelled settings.

Normalize paths and reject path traversal, symlink escapes, unexpected casing collisions, and source remapping that defeats policy assignment. A rename must not silently move code to a weaker profile.

### 13.3 Permanent approved capabilities versus temporary exceptions

Use reviewed catalogs for intentional supported abstractions: a DTO serializer, resource adapter, or named pure fold helper. Use an exception for a temporary, specific deviation that should disappear.

An exception is not “this folder is exempt.” Its selector must identify project, rule, declaration or exact source occurrence, allowed operation/type where applicable, and source/contract fingerprint. It cannot grant native/reflection permissions by implication.

Illustrative temporary exception, **not active by default**:

```yaml
id: EX-0042
rule: STYLE003
project: src/App.Domain/App.Domain.fsproj
declaration: App.Domain.CustomerHistory.rebuild
allowedOperation: Microsoft.FSharp.Collections.ListModule.Fold
sourceFingerprint: "<approved-normalized-declaration-hash>"
maximumMatches: 1
reason: "Temporary implementation pending a shared domain-specific history helper."
approvalRecord: "<protected-review-identifier>"
owner: "<platform-owner>"
expiresAtUtc: "<approved-ISO-8601-expiry>"
```

This schema is proposed; resolve source names to the actual pinned compiled symbol rather than assuming a display name is sufficient identity.

The exception engine MUST reject expired entries, content drift, excessive matches, unknown rules, wildcard-wide scopes, stale/unused entries, and missing approval records. Verify approval from a trusted policy source; an `approvedBy` string supplied in a pull request is not proof.

A narrowly granted style exception leaves every safety rule active. If a legitimate adapter operation needs several permissions, enumerate them rather than treating one as blanket trust.

Migration baselines, if authorized, are centrally fingerprinted, non-growing, expiring exceptions. They are not new default policy. Do not declare full compliance until the unapproved baseline is zero and the intended gates are active.

### 13.4 No source suppression

Reject active suppression mechanisms such as:

```fsharp
#nowarn "0025"

// fsharplint:disable
// fsharplint:disable-next-line

[<System.Diagnostics.CodeAnalysis.SuppressMessage("Company", "CAST002")>]
let bypass () = ...
```

The example is deliberately prohibited and schematic. Do not create a company suppression attribute.

F# supports warning directives, and newer compiler versions support scoped warning control. FSharpLint also supports suppression comments. Parse these as mechanisms rather than banning every explanatory string that mentions them. [S42][S35]

Disallow disabling warnings via `.fsproj`, command-line response files, `OtherFlags`, environment/configuration changes, analyzer options, or generated imports. `#warnon` does not make a neighboring forbidden `#nowarn` acceptable.

For version 1, prohibit application-authored conditional compilation and line directives unless the protected policy explicitly requires them. Any approved conditional source must be checked in every supported configuration/symbol set. A default Debug analysis cannot certify a different Release branch.

---

## 14. Compiler and dependency configuration

### 14.1 Discover and pin the actual toolchain

Before implementation, record SDK, language version, FSharp.Core, FCS, analyzer SDK, editor integration, target frameworks, runtime identifiers, and all build configurations. Do not upgrade to an arbitrary “latest” version to silence compatibility errors.

Require a toolchain supporting the agreed nullable checks. When the existing repository cannot support the standard, report the compatibility gap and an explicit upgrade plan. Do not silently disable the rule.

Pin an exact approved SDK in `global.json`; use controlled updates, not a permanently frozen vulnerable toolchain. `rollForward: "disable"` requests the exact SDK, and `allowPrerelease: false` avoids unapproved prerelease selection. [S37]

```json
{
  "sdk": {
    "version": "<EXACT-APPROVED-INSTALLED-SDK-VERSION>",
    "rollForward": "disable",
    "allowPrerelease": false
  }
}
```

The placeholder is intentionally not usable. The implementation must supply a tested real value and pin compatible FCS/Core/analyzer dependencies. Do not confuse analyzer-host Core requirements with the application's package identity; record both and verify compatibility rather than blindly forcing every component to one number.

### 14.2 Compiler baseline

The following is a **baseline template for a compatible pinned compiler**. Verify support for each warning and the emitted compiler arguments before enabling it. Failure to support a required policy is a compatibility failure, not permission to silently remove it.

```xml
<Project>
  <PropertyGroup Condition="'$(MSBuildProjectExtension)' == '.fsproj'">
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <WarningLevel>5</WarningLevel>
    <Nullable>enable</Nullable>
    <Deterministic>true</Deterministic>

    <!-- Set centrally to the explicit approved language version. -->
    <LangVersion>$(CompanyApprovedLangVersion)</LangVersion>

    <!-- One canonical list; do not let a later declaration replace it. -->
    <WarnOn>21,22,52,1178,1182,3180,3186,3218,3391,3395,3559,3570,3579,3582,3878</WarnOn>

    <OtherFlags>$(OtherFlags) --checked+</OtherFlags>
  </PropertyGroup>
</Project>
```

F# documents warning level 5, opt-in warnings, warnings-as-errors, null checking, and checked arithmetic. The `WarnOn` MSBuild property is documented as a comma-separated list, with later assignments replacing earlier ones. [S01]

Important warning intents:

| Warning(s) | Intent |
|---|---|
| `21`, `22` | Recursive initialization/order hazards. |
| `52` | Implicit struct copies. |
| `1178` | Implicit equality/comparison capability inference. |
| `1182` | Unused bindings. |
| `3180` | Captured mutable local becoming a reference cell. |
| `3186` | F# metadata/declaration inconsistency. |
| `3218` | Parameter-name differences between signature and implementation. |
| `3391`, `3395` | .NET-style implicit conversions. |
| `3559` | Type inferred as `obj`. |
| `3570`, `3579`, `3582`, `3878` | Ambiguous underscore use, untyped interpolation, union-case shadowing, and invalid attribute usage. |

The descriptions and availability must be validated against the chosen compiler. `3218` is documented with signature files; conversion warnings are documented separately. [S13][S02]

`FS0025` incomplete-match diagnostics must remain errors; do not suppress default compiler warnings simply because they are absent from the opt-in list. `<Nullable>enable</Nullable>` supplies F# null-checking configuration, but it does not prohibit unchecked null/default APIs by itself. [S15][S03]

`3388` and `3389` are optional additional explicitness warnings for implicit upcasts/widening, not required unsafe-cast detectors. Keep their final enablement in the protected compiler policy. Warnings such as index-notation, XML-documentation, optimizer, and record-update style warnings can be adopted separately; do not present all of them as mandatory safety mechanisms. [S01][S02]

`--checked+` asks for overflow checking; it is not a universal proof that all numeric operations/conversions are checked or exception-free. Audit approved arithmetic/conversion helpers separately.

### 14.3 Validate the effective command, not only XML

Use `Directory.Build.props` for shared early defaults and a protected `Directory.Build.targets`/build-task integration for later enforcement. MSBuild imports are customizable and can be redirected/overridden; late imports are useful, not a tamper-proof boundary. [S36]

The authoritative guard MUST inspect the final approved compiler invocation and reject:

```text
TreatWarningsAsErrors=false
WarningsNotAsErrors / unapproved NoWarn
--nowarn / --warnaserror- / a later disabling flag
--checknulls- / nullable disabled
--checked-
LangVersion=preview / latest / unapproved numeric version
unapproved DefineConstants / conditional branches
unapproved response files / compiler replacement / reference overrides
missing required opt-in warnings
unapproved design-time/skip/analyzer-disable switches in a shipping build
```

Account for legitimate compiler-generated framework defaults through a pinned, reviewed command profile, not an unrestricted suppression list. Verify command ordering, because later flags can alter effective behavior.

Unknown build properties that materially change compilation or guard execution require classification. Do not rely on clearing one XML property to erase every possible override path.

### 14.4 Dependencies and restoration

Use centrally approved package versions and lock files. For PackageReference-based projects, enable lock-file generation and use locked restore in CI. NuGet documents `RestorePackagesWithLockFile` and `RestoreLockedMode`. [S38]

```xml
<Project>
  <PropertyGroup>
    <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
  </PropertyGroup>
</Project>
```

CI invocation, after trusted policy/project preflight:

```sh
dotnet restore App.sln --locked-mode
dotnet build App.sln --configuration Release --no-restore
```

These commands alone do not establish compliance. The CI driver must also run the independent guard and verify the actual compilation manifest. Adapt the solution name and complete configuration/framework matrix to the repository.

Reject floating/unapproved versions, local binary references, executable package assets, native runtime assets, type providers, generators, and build-transitive imports not present in the approved inventory. If the repository uses another package-management approach, provide equivalent locked graph and identity validation instead of adding a second resolver casually.

---

## 15. Local build and editor integration

### 15.1 Build-task contract

A normal supported `dotnet build` must fail on an unexcepted guard violation. Implement a pre-compile integration after the approved generation/reference-resolution steps, plus input/artifact verification as required.

Use a dedicated MSBuild task that launches the guard without constructing an untrusted shell command. Pass argument values separately using an appropriate process API. A raw `Exec` string built from attacker-controlled paths/properties is not the strongest implementation.

Illustrative integration **for the task to be implemented**:

```xml
<Project>
  <UsingTask
      TaskName="Company.FSharp.Guard.Build.RunGuard"
      AssemblyFile="$(CompanyGuardBuildTaskAssembly)" />

  <Target Name="CompanyFSharpSafety"
          BeforeTargets="CoreCompile"
          Condition="'$(MSBuildProjectExtension)' == '.fsproj'
                     and '$(DesignTimeBuild)' != 'true'">

    <Error Condition="'$(CompanyApprovedLangVersion)' == ''"
           Text="Company safety policy is missing the approved language version." />

    <Error Condition="!Exists('$(CompanyGuardBuildTaskAssembly)')"
           Text="The approved Company.FSharp.Guard build integration is missing." />

    <Error Condition="!Exists('$(CompanyGuardPolicyPath)')"
           Text="The approved F# safety policy is missing." />

    <Company.FSharp.Guard.Build.RunGuard
        ProjectPath="$(MSBuildProjectFullPath)"
        Configuration="$(Configuration)"
        TargetFramework="$(TargetFramework)"
        GuardPath="$(CompanyGuardPath)"
        PolicyPath="$(CompanyGuardPolicyPath)"
        InputManifestPath="$(CompanyGuardInputManifestPath)" />
  </Target>
</Project>
```

The build task, manifest-export target, and these properties do not already exist. Implement and test them. The task must return failure for every nonzero guard outcome, missing manifest, unsupported profile, or invalid tool identity. No `ContinueOnError` behavior is permitted.

MSBuild supports custom build hooks, but the exact placement must be tested with this repository's source generators and SDK. `BeforeTargets="CoreCompile"` by itself does not prove that the analyzer saw every subsequently compiled input. [S43]

Required behavior:

1. Resolve the approved tool and policy before analysis; do not build the guard recursively inside the app build.
2. Export normalized compiler inputs after every approved source-producing step.
3. Preserve F# source order, references, flags, defines, target framework, and generated source identity.
4. Run FCS analysis against those inputs without re-entering the current build target.
5. Verify they match the inputs actually used to compile. Fail if late tasks change them.
6. Re-run or correctly invalidate analysis when source, policy, references, compiler, or any relevant configuration changes.
7. Make incremental no-change builds succeed without stale certification; cache keys include all security-relevant inputs and the checker version.

Skipping this task for genuine design-time editor evaluation is an ergonomics choice only. Setting `DesignTimeBuild=true` cannot produce an authoritative shipping pass. Independent CI must not honor it as a guard bypass.

### 15.2 Existing analyzer integration

The FSharp.Analyzers SDK also documents MSBuild integration through `FSharp.Analyzers.Build` and its analysis target. It can be used as an implementation component if its complete failure behavior, input coverage, and version compatibility satisfy this specification. Do not copy documentation sample package versions as “latest.” [S07]

Choose one authoritative invocation path and share the rule engine. Avoid duplicate rule implementations or two tools disagreeing about what passes.

### 15.3 Editor behavior

Package editor diagnostics from the same analysis engine. Source-only findings should be fast; project/shape findings can run when complete context exists. Show unresolved/incomplete analysis honestly.

Code fixes may suggest immutable transforms, DTO mapping, explicit cases, and named helper calls. Do not autofix by moving code to an adapter, adding policy exceptions, changing business semantics, or suppressing warnings.

### 15.4 Formatting and ordinary lint

Run the pinned formatter and adopted FSharpLint configuration as separate checks. Their failure policy can be strict, but they are not the authority for the custom safety invariant.

An adopted mandatory linter failure must fail the build/CI, not be swallowed. Prohibit application-source suppression directives for those mandatory rules. Optional advisory rules should be clearly distinguished rather than silently mixed with hard company bans.

---
## 16. Independent CI and merge enforcement

### 16.1 Authoritative pipeline

The required check must not trust the application to decide whether its guard ran.

```text
Immutable source snapshot + trusted policy/tool identities
                         |
                         v
        Non-executing repository/configuration preflight
                         |
                         v
      Isolated approved restore/generation/project evaluation
                         |
                         v
          Complete compiler-input matrix and graph
                         |
                         v
           Independent source/type/shape guard run
                         |
                         v
             Approved compilation and tests
                         |
                         v
       Input/output identity + metadata verification
                         |
                         v
       Trusted reporter publishes the required check
```

For these standardized apps, prefer a trusted build driver that constructs compilation from approved templates. Do not execute arbitrary repository-defined MSBuild logic and then accept whatever manifest it claims to have compiled.

When MSBuild evaluation/build execution is necessary, preflight imports/tasks/package assets against protected policy, isolate it from the reporter/tool files/secrets, and verify compiler invocations independently. A manifest written by untrusted build code is evidence to validate, not authority.

### 16.2 Required matrix

Check every shipping project, target framework, configuration, runtime-specific compilation variant, and approved define set. At minimum cover the actual Debug/developer and Release/deployment policies; do not invent unsupported configurations.

Where conditional source is approved, enumerate supported variants or disallow the unsupported combination. Raw lexical checks can see forbidden tokens, but they cannot replace typed checking of all shipped branches.

Include `.fs`, `.fsi`, linked sources, approved generated source, embedded/compiled source, and any additional compiler input. Do not trust the solution file as the sole project inventory; an omitted project must not bypass the gate and later ship.

The source inventory should distinguish shipping code, tests, fixtures, tools, static assets, and unsupported/unassigned files. Unexpected compilable source or a new project fails until assigned. Negative fixtures are deliberate test inputs, never a generic `tests/**` exemption that shipping code may reference.

### 16.3 Input and artifact identity

Record, at minimum:

```text
Repository and exact commit/tree identity
Policy/catalog/exception identities
Guard/toolchain identities
Projects/profiles and complete compilation matrix
Physical source paths, ordered source lists, content hashes
Reference/package identities and approved assets
Compiler arguments and response-file expansion
Approved generator identities and output hashes
Diagnostic counts and completion status per project/configuration
Exception matches and expiry checks
Compiled artifact hashes and dependency/native metadata results
```

Analyze and build the same sealed input snapshot. Reject source changes between analysis and compilation. A post-analysis regeneration step must invalidate the analysis. Deployment accepts only artifacts linked to a complete successful attestation from the trusted pipeline.

Hashing alone does not establish trust if the untrusted process can replace both the file and its claimed hash. The verifier must obtain/read the actual inputs from its controlled snapshot and protect its reports.

### 16.4 CI hardening

Keep the following outside application-author-controlled execution:

- Signing/reporting credentials and required-status identity.
- Tool/policy selection and approved artifact verification.
- The definition of the mandatory project/configuration matrix.
- Completion/failure aggregation and the decision to publish success.

Run code builds/tests with least privilege and no status-reporting secrets. Pin external actions/workflows to reviewed immutable identities. Do not execute untrusted pull-request code with privileged event credentials. GitHub's security guidance discusses action pinning and the risks of combining untrusted code with privileged execution. [S41]

If repository-defined build tasks can run arbitrary code, treat them as untrusted execution even before tests start. Avoid shared writable state that would let those tasks rewrite the guard binary, policy, source snapshot, or final status report.

A crash, missing report, skipped project, zero analyzed files, stale cache entry, timeout, malformed report, or cancelled evaluation must not be aggregated into a successful safety result.

### 16.5 GitHub rulesets or equivalent

Use organization-controlled required checks/workflows supported by the actual GitHub plan and repository configuration. Do not assume a feature is enabled without verifying it during implementation.

Require an appropriate trusted status/check producer. GitHub supports selecting an expected GitHub App as the source of required status updates. Restrict bypass permissions and require review for changes to protected policy/build assets. [S40]

The expected App identity is not sufficient when arbitrary untrusted jobs using that same identity can publish a look-alike result. Also protect the workflow/check provenance or use a dedicated reporter whose credentials the app build cannot access.

Required implementation tests:

1. Deleting the local guard target does not yield an authoritative pass.
2. Changing the local policy or tool path does not change authoritative rules.
3. A look-alike job/status cannot satisfy the trusted check.
4. Renaming/skipping a required workflow or using path filters cannot silently omit analysis.
5. The merge candidate is checked against the relevant base/merge-queue state, not only a stale head commit.
6. Direct-push, administrator, and emergency bypass paths are explicitly controlled and auditable.

The guard cannot stop a repository owner from deliberately changing every administrative control. State that trust boundary; do not promise an agent is physically incapable of writing forbidden code. The intended guarantee is that unauthorized changes cannot pass the approved merge/deployment process.

---

## 17. Acceptance tests

### 17.1 Test the guard as a product

Every catalog ID MUST have a coverage entry with:

```text
Rule ID and policy version
A valid-F# negative fixture that requires the guard, where such a case exists
A compiler-negative fixture when native compiler enforcement is relevant
An approved positive alternative
Aliased/qualified/higher-order variants where relevant
Applicable and non-applicable profile cases
Expected diagnostic identity, physical location, and exit code
An approved-exception case and a stale/widened-exception rejection case
Build/CI integration coverage where relevant
```

A fixture that fails because `Customer` is undefined does not demonstrate that the cast rule works. Supply supporting definitions/references. Assert the intended diagnostic stage and reason, not merely “some command failed.”

Include a test that the rule registry and catalog coverage manifest have exactly the same mandatory IDs. A rule without required tests is unfinished; a schema entry for an unimplemented rule must fail validation.

### 17.2 Language and data fixtures

| Fixture | Expected outcome |
|---|---|
| `let mutable` with local-only use | `MUT001` failure. |
| Setter/index assignment without a mutable binding | `MUT002` failure. |
| Mutable field, auto-property, object initializer, or explicit setter reference | Mutation failure at correct source location. |
| `ref`, `.Value`, `:=`, dereference/update helper aliases | `MUT004` failure. |
| `ResizeArray` hidden behind type/module aliases | `MUT005` failure. |
| Mutating `.Add`/`.Append` passed as a function | Mutation/capability failure. |
| Immutable `Map.add`/`Set.add` | Pass with approved types/APIs. |
| `ConcurrentDictionary`, `AsyncLocal`, `Interlocked` | Shared-state/capability failure. |
| Array or read-only view in a nested domain record | Shape failure with nested path. |
| Immutable record containing mutable elements | `MUT010`/`MODEL001` failure. |
| Pure list comprehension | Pass; do not mistake it for a statement effect loop. |
| `while` hidden in a builder or unused function | Failure. |
| `:?>` and inferred `downcast` | `CAST001` failure. |
| Qualified/aliased/partially applied `unbox` | `CAST002` failure. |
| `Seq.cast`/LINQ `Cast`/runtime type filter | Correct casting/modeling failure. |
| Implicit `obj` conversion without source `box` | `CAST004` failure. |
| `obj` hidden in aliases, generic arguments, or inferred branches | Shape/erasure failure. |
| Approved static upcast to a permitted typed abstraction | Pass. |
| Upcast to `obj` or mutable/effectful interface | Fail. |
| Custom implicit conversion used without explicit operator call | `CAST006` failure. |
| Approved validated numeric conversion helper | Pass; edge cases tested. |
| `null`, `Unchecked.defaultof`, `Unchecked.nonNull` | Correct null/default failure. |
| Unchecked null active pattern from pinned Core | Failure. |
| `Array.zeroCreate` of a struct containing references | Failure; not misclassified as safe just because it is a struct. |
| Default construction of invariant-bearing struct | Failure. |
| Approved adapter byte buffer | Pass only within its capability contract. |
| Ordinary `None` and immutable primitive constants | Pass. |
| `CLIMutable` on a domain record | `ATTR002` failure. |
| Approved DTO `CLIMutable` use | Pass only for its exact approved declaration. |
| F# `DefaultValue(false)` and qualified attribute names | Failure. |
| Unrelated `System.ComponentModel.DefaultValue` | Classified accurately; no false explanation about uninitialized fields. |
| `UseNullAsTrueValue` hidden in a combined flag value | Failure. |
| Unknown attribute or custom trust marker | Failure. |
| `typeof`, F# reflection constructor, uninitialized-object API | Reflection/construction failure. |
| Dynamic expression/quotation evaluation or assembly loading | Failure. |
| P/Invoke, `Unsafe`, `NativePtr`, span/byref escape | Native/capability failure. |
| Approved `TryParse` lowering | Pass without a false generated-byref/default diagnostic. |

### 17.3 Architecture and result fixtures

| Fixture | Expected outcome |
|---|---|
| Deserialize a domain value directly | `DATA001` failure. |
| Deserialize list/option/generic envelope of domain values | Recursive `DATA001` failure. |
| DTO field indirectly contains a domain type | `DATA004`/`DATA001` failure. |
| Public generic serializer helper instantiated with a domain type | Failure. |
| Runtime-Type deserializer with unprovable target | Failure. |
| Framework binder automatically constructs domain parameter | Failure. |
| Approved concrete DTO decoder + domain validator | Pass. |
| New unchecked construction function with innocuous name | Public-surface/constructor-policy failure. |
| `.fsi` signature widened to expose a private representation | Failure. |
| Unauthorized `InternalsVisibleTo` | Failure. |
| Catch-all `_`, variable, alias pattern on protected domain DU | `MODEL004` failure. |
| Explicit named DU cases with ignored payloads | Pass. |
| Add new DU case without updating relevant decisions | Compiler/guard failure. |
| Direct clock/random/environment/file read in pure code | Effect failure. |
| Effect callback hidden in otherwise immutable record | Contract/shape failure. |
| Approved pure higher-order list operation | Pass. |
| Unapproved builder hides mutation or catching | Failure. |
| Business `try ... with` or throwing helper alias | Exception-policy failure. |
| Broad catch returns “not found” for all failures | `ERR004` failure. |
| Approved adapter maps specified exception | Pass with behavioral test. |
| Unexpected adapter exception | Propagates to approved host handling; not ordinary domain recovery. |
| Cancel before/during approved adapter work | Preserves documented cancellation, not successful completion containing a business error. |
| Approved adapter `use`/`try ... finally` | Pass; cleanup happens on success/failure/cancellation where applicable. |
| `Async.Catch` or general `Result.catch` wrapper in business code | `ERR003` failure. |
| Exception object inside domain `Error` payload | `ERR005` failure. |
| Partial lookup or `Ok (Parse input)` | Partial/throwing API failure. |
| Discarded result via `ignore`, wildcard, or `let! _` | `ERR007` failure within supported flow analysis. |
| Unreviewed `Result.defaultValue`/`toOption` recovery | `ERR008` failure. |
| Explicit approved case-specific recovery | Pass. |
| Raw backward/tuple pipes or operator-as-value aliases | Style failure. |
| Redefinition of approved `\|>` | Operator-policy failure. |
| Direct fold in workflow | `STYLE003` failure. |
| Exact approved fold helper, still pure | Pass. |
| Mutation inside an approved fold helper | Still fails mutation policy. |
| Trivial unrestricted public fold renaming | Rejected by helper contract review/policy. |

### 17.4 Enforcement and compatibility fixtures

| Fixture | Expected outcome |
|---|---|
| `#nowarn`, scoped suppression, lint-disable comments, suppression attributes | `BUILD001` failure. |
| Strings/comments explaining forbidden operators | Pass. |
| `#line` remaps a violation into a trusted-looking path | Rejected or attributed to the physical source, never trusted by logical path. |
| Application adds an `Interop`, `Generated`, or `Trusted` folder/profile property | No permission gained; unapproved project/profile failure where applicable. |
| Fake `[<GeneratedCode>]` or checked-in `.g.fs` | Still analyzed/rejected. |
| Domain references boundary via an intermediate project/package | Transitive graph failure. |
| F# app hides bypass in a new C#/VB/native/script helper | Unapproved language/project/dependency failure. |
| New package/build-transitive target/source generator/type provider | Fails preflight unless explicitly approved. |
| New sidecar/process/deployment command | Architecture/deployment policy failure. |
| Compiler warning downgraded in late `OtherFlags`/response file | Effective-command policy failure. |
| Delete/disable local build hook | Independent check still fails violations and identifies drift. |
| Analyzer missing/crashing/timing out | Nonzero incomplete-analysis failure. |
| Unknown AST/API/type shape/version | Explicit unsupported/unknown failure, never success by omission. |
| Invalid/expired/unused/widened exception | `BUILD005` failure. |
| Release-only forbidden source | Fails relevant matrix entry. |
| Source added after analysis by a build target | `BUILD004` failure. |
| Source changes between analysis and compilation | Identity failure. |
| Cached report from another policy or toolchain | Rejected. |
| Negative fixture files accidentally enter shipping compilation | Inventory/graph failure. |
| Zero analyzed shipping files | Failure. |
| Status spoofing or skipped mandatory job | Cannot satisfy the trusted authoritative check. |
| Compiler-generated task state machine/disposal/equality/boxing | No blanket false positive; source semantics still checked. |
| Toolchain upgrade | Entire compatibility and rejection suite must pass before approval. |

### 17.5 Metamorphic and semantic tests

Generate source variants that qualify names, introduce aliases, change whitespace, use operators as values, reorder harmless declarations, insert nested types/functions, and wrap forbidden types in containers. They must preserve the intended diagnostic.

For migrations, test semantics rather than just guard compliance: first versus last match, ordering, duplicate-key policy, empty input, laziness/evaluation timing, exception/cancellation propagation, resource ownership, error accumulation, and transaction boundaries. A refactor that passes the guard but changes the application's intended behavior is not complete.

---

## 18. Implementation phases and completion criteria

### Phase 0 — Inventory and compatibility baseline

Produce the repository/project/toolchain inventory, supported configuration matrix, current violation report, proposed exact profile map, and approved-dependency surface proposal. Identify existing protected CI ownership and available merge controls.

Do not rewrite business code yet. Do not treat the inventory command as a safety pass. Establish compiling positive/negative fixtures and choose pinned tooling versions.

**Exit condition:** Maintainers can see what is shipping, what is trusted, what needs migration, and which proposed controls are not yet available.

### Phase 1 — Enforcement skeleton and fail-closed behavior

Implement schemas, policy ownership/identity checks, rule registry, CLI exit codes, project manifests, physical path normalization, complete-input validation, and minimal editor/build integration. Add tests for missing tool/policy, crash, skip, invalid config, and late compiler-setting drift.

**Exit condition:** A deliberately triggered diagnostic fails local build and the independent test pipeline; failure cannot be hidden by local suppression or deleting the build hook.

### Phase 2 — Syntax and mutation rules

Implement mutation, loops, source suppression, prohibited operators, authored exception constructs, direct casts, nullable/default syntax, and dangerous attributes. Make true symbol rules typed; do not leave their final implementations as regex placeholders.

**Exit condition:** Direct and common aliased forms fail with accurate locations, and approved record updates/list transformations pass.

### Phase 3 — Semantic catalog, shape, and construction rules

Implement resolved API/type catalogs, implicit erasure, unboxing/casting helpers, reflection/native capabilities, recursive shapes, controlled constructors, DTO graph checks, builder/callback policy, and project-reference closure.

**Exit condition:** Nested DTO/domain and mutable-payload bypass tests fail; unknown symbols/types fail closed; approved standard library abstractions remain usable.

### Phase 4 — Failure handling and readability helpers

Implement partial-operation restrictions, must-handle result checks, recovery contracts, exception/cancellation adapter tests, explicit DU matching, and fold-helper permissions. Provide the approved APIs needed to migrate common patterns without reintroducing mutation or arbitrary catch wrappers.

**Exit condition:** Representative workflows use explicit data/Result handling; adapters translate only their approved failures; pure fold helpers remain pure.

### Phase 5 — Migrate one representative app

Refactor one app incrementally, retaining behavioral tests. Establish DTO/validation boundaries, hide invariant representations, replace prohibited collection/state patterns, and move true framework behavior into protected adapters.

Do not change matching semantics, error meanings, data schemas, or cancellation just to make the linter green. Record any necessary domain behavior change separately.

**Exit condition:** The app builds and passes tests with zero unapproved violations in all shipping configurations. Any active exceptions are exact, approved, visible, and expiring.

### Phase 6 — Authoritative CI and artifact linkage

Deploy the centrally controlled checker/policy, isolate evaluation/build execution, implement trusted reporting, protect merge/bypass configuration, and link deployed artifacts to analyzed inputs. Add metadata inspection where specified and full anti-bypass tests.

**Exit condition:** Tampering with local targets, policy, profiles, source lists, or check names cannot yield a trusted green result for a violating artifact.

### Phase 7 — Standard template rollout and ongoing upgrades

Make the reference app, rule catalogs, adapters, and rejection tests part of the shared application template. Roll out app by app; do not copy divergent policy files into each repo as the long-term authority.

Toolchain/library upgrades are reviewed changes that run the entire compatibility suite and update approved symbol/lowering catalogs. Do not auto-accept new APIs or compiler syntax because a package version changed.

### Required deliverables

| Deliverable | Required content |
|---|---|
| Inventory/compatibility report | Actual SDKs, source projects, references, configurations, profiles, gaps. |
| Guard library + CLI | All mandatory catalog rules; deterministic diagnostics; complete failure semantics. |
| Editor integration | Same rules, useful diagnostics, no separate policy logic. |
| Build integration | Proven coverage of real compilation inputs and nonzero failure propagation. |
| Policy schema and initial policy | Strict validation; exact profiles; approved catalogs; initially empty unauthorized exceptions. |
| Tests | Rule coverage manifest plus positive/negative/bypass/build/compatibility tests. |
| Approved helper/adapter APIs | Concrete DTO decoders, validators, pure aggregation helpers, explicit recovery/interop boundaries actually needed by the app. |
| Migration changes | Behavior-preserving refactors with tests and documented required changes. |
| CI/ruleset configuration | Trusted check provenance, complete matrix, protected configuration, artifact linkage. |
| Maintainer documentation | Adding an approved API, requesting an exception, upgrading the toolchain, diagnosing a failure. |

### Definition of done

The implementation is complete only when all mandatory IDs are implemented and tested; local builds and independent CI fail on representative violations; aliases and nested types cannot bypass the supported rules; positive fixtures compile; shipping configurations are fully covered; the app's behavioral tests pass; and the trusted merge/deployment gates are actually enabled.

Document unsupported analysis cases and remaining trusted assumptions. An unsupported case must fail closed or have an explicitly approved bounded exception. Do not label a checker “complete” when mandatory rules are stubs, advisory warnings, or unchecked branches.

---

## 19. Instructions to the implementation agent

Use this document as the desired policy, not permission to weaken enforcement until existing code compiles.

**Work order:** inspect the actual repository, establish tests and compatibility, build the guard, add approved platform alternatives, migrate behavior-preservingly, then prove the local and independent CI gates. Keep changes reviewable; avoid a single large untested rewrite.

**Do not:** add source suppressions; change a project's profile to escape a rule; invent an `Interop` directory; widen exception scopes; add unapproved packages/runtimes/sidecars; hide a cast or mutation in a wrapper; replace every failure with a default value; replace every fold with bespoke recursion; remove failing tests; or accept a partial analysis as successful.

**When a rule conflicts with a real framework requirement:** identify the exact symbol, data shape, source location, and required behavior. Prefer an existing approved adapter. Otherwise propose the smallest protected capability contract with tests. Do not silently grant it in the application repository.

**When reporting completion:** include implemented rule IDs, commands run, actual test results, effective compiler settings, checked configurations, active exceptions, CI enforcement evidence, and remaining gaps. Do not claim an F# example compiles, a ruleset is enabled, or a build is protected unless it was actually verified.

The intended daily coding model is:

```text
Approved immutable values
    + records / discriminated unions / private invariant representations
    + explicit pattern matching
    + Option / Result
    + named pure transformations
    + ordinary application / forward pipelines
    + approved responsibility-specific helpers

Effects, framework mutation, serialization, exception translation,
and any future native integration remain behind protected adapters.
```

---

## 20. Primary references

These references establish language/tool behavior. The company profiles, rule IDs, approval model, schemas, CLI commands, and implementation requirements are the proposed design in this document. Documentation and packages can change; verify against the pinned toolchain before implementation. Reference pages were consulted while preparing this specification.

[S01]: https://learn.microsoft.com/en-us/dotnet/fsharp/language-reference/compiler-options "F# compiler options and opt-in warnings"
[S02]: https://learn.microsoft.com/en-us/dotnet/fsharp/language-reference/casting-and-conversions "F# casts, implicit conversions, and related warnings"
[S03]: https://learn.microsoft.com/en-us/dotnet/fsharp/language-reference/values/null-values "F# null values and nullable checking configuration"
[S04]: https://fsharp.github.io/fsharp-compiler-docs/fcs/ "FSharp.Compiler.Service overview"
[S05]: https://fsharp.github.io/fsharp-compiler-docs/fcs/typedtree.html "FCS typed expressions and resolved symbols"
[S06]: https://ionide.io/FSharp.Analyzers.SDK/content/Getting%20Started%20Writing.html "F# analyzer SDK contexts, compatibility, and API notes"
[S07]: https://ionide.io/FSharp.Analyzers.SDK/content/getting-started/MSBuild.html "F# analyzer MSBuild integration"
[S08]: https://fsharp.github.io/fsharp-core-docs/reference/fsharp-core-operators-unchecked.html "FSharp.Core Unchecked operations"
[S09]: https://fsharp.github.io/fsharp-core-docs/reference/fsharp-core-climutableattribute.html "CLIMutableAttribute"
[S10]: https://fsharp.github.io/fsharp-core-docs/reference/fsharp-core-defaultvalueattribute.html "F# DefaultValueAttribute"
[S11]: https://fsharp.github.io/fsharp-core-docs/reference/fsharp-core-compilationrepresentationflags.html "CompilationRepresentationFlags"
[S12]: https://learn.microsoft.com/en-us/dotnet/api/system.componentmodel.defaultvalueattribute?view=net-10.0 "System.ComponentModel.DefaultValueAttribute"
[S13]: https://learn.microsoft.com/en-us/dotnet/fsharp/language-reference/signature-files "F# signature files"
[S14]: https://learn.microsoft.com/en-us/dotnet/fsharp/language-reference/structs "F# structs and zero initialization"
[S15]: https://learn.microsoft.com/en-us/dotnet/fsharp/language-reference/compiler-messages/fs0025 "FS0025 incomplete pattern matching"
[S16]: https://fsharp.github.io/fsharp-core-docs/reference/fsharp-core-resultmodule.html "Result module"
[S17]: https://fsharp.github.io/fsharp-core-docs/reference/fsharp-collections-listmodule.html "List module"
[S18]: https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/nullable-annotations "System.Text.Json nullability enforcement and limitations"
[S19]: https://fsharp.github.io/fsharp-core-docs/reference/fsharp-collections-arraymodule.html "Array module and zeroCreate"
[S20]: https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.ireadonlylist-1?view=net-10.0 "IReadOnlyList<T> contract"
[S21]: https://learn.microsoft.com/en-us/dotnet/fsharp/language-reference/sequences "F# sequences"
[S22]: https://fsharp.github.io/fsharp-core-docs/reference/fsharp-reflection-fsharpvalue.html "F# reflective value construction"
[S23]: https://learn.microsoft.com/en-us/dotnet/api/system.runtime.compilerservices.runtimehelpers.getuninitializedobject?view=net-10.0 "RuntimeHelpers.GetUninitializedObject"
[S24]: https://learn.microsoft.com/en-us/dotnet/fsharp/language-reference/functions/external-functions "F# native imports"
[S25]: https://learn.microsoft.com/en-us/dotnet/api/system.runtime.compilerservices.unsafe.as?view=net-10.0 "Unsafe.As and bypassed runtime type checks"
[S26]: https://learn.microsoft.com/en-us/dotnet/fsharp/language-reference/exception-handling/the-try-with-expression "F# try...with"
[S27]: https://learn.microsoft.com/en-us/dotnet/fsharp/language-reference/exception-handling/the-try-finally-expression "F# try...finally"
[S28]: https://learn.microsoft.com/en-us/dotnet/standard/parallel-programming/task-cancellation "Task cancellation semantics"
[S29]: https://fsharp.github.io/fsharp-core-docs/reference/fsharp-control-fsharpasync.html "F# Async APIs including Catch"
[S30]: https://learn.microsoft.com/en-us/dotnet/api/system.io.file.readalltextasync?view=net-10.0 "File.ReadAllTextAsync behavior"
[S31]: https://learn.microsoft.com/en-us/dotnet/api/system.int32.tryparse?view=net-10.0 "Int32.TryParse overloads and out-result behavior"
[S32]: https://learn.microsoft.com/en-us/dotnet/fsharp/language-reference/resource-management-the-use-keyword "F# use and resource management"
[S33]: https://fsharp.github.io/fsharp-core-docs/reference/fsharp-core-optionmodule.html "Option module"
[S34]: https://learn.microsoft.com/en-us/dotnet/fsharp/language-reference/symbol-and-operator-reference/ "F# operator reference"
[S35]: https://fsprojects.github.io/FSharpLint/how-tos/rule-suppression.html "FSharpLint suppression mechanisms"
[S36]: https://learn.microsoft.com/en-us/visualstudio/msbuild/customize-by-directory?view=visualstudio "Directory.Build.props and Directory.Build.targets"
[S37]: https://learn.microsoft.com/en-us/dotnet/core/tools/global-json "global.json SDK selection"
[S38]: https://learn.microsoft.com/en-us/nuget/consume-packages/package-references-in-project-files "NuGet lock files and locked restore"
[S39]: https://learn.microsoft.com/en-us/dotnet/fsharp/style-guide/component-design-guidelines "F# component design and private representations"
[S40]: https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-rulesets/available-rules-for-rulesets "GitHub rulesets and required check provenance"
[S41]: https://docs.github.com/en/actions/reference/security/secure-use "GitHub Actions secure-use guidance"
[S42]: https://learn.microsoft.com/en-us/dotnet/fsharp/language-reference/compiler-directives "F# compiler directives"
[S43]: https://learn.microsoft.com/en-us/visualstudio/msbuild/how-to-extend-the-visual-studio-build-process?view=visualstudio "MSBuild extension hooks"
[S44]: https://fsharp.github.io/fsharp-compiler-docs/reference/fsharp-compiler-syntax-synexpr.html "FCS source expression syntax"

### Reference index

| References | Subject |
|---|---|
| [S01], [S02], [S03], [S13], [S14], [S15], [S34], [S39], [S42] | Language rules, nullability, conversions, signatures, matching, operators. |
| [S04], [S05], [S06], [S07], [S44] | Compiler-service/analyzer implementation and integration. |
| [S08], [S09], [S10], [S11], [S12], [S22], [S23], [S24], [S25] | Defaults, attributes, reflection, native/unsafe operations. |
| [S16], [S17], [S19], [S20], [S21], [S33] | Result, collections, immutable-data boundaries. |
| [S18], [S26], [S27], [S28], [S29], [S30], [S31], [S32] | Serialization, exception handling, parsing, cancellation, cleanup. |
| [S35], [S36], [S37], [S38], [S40], [S41], [S43] | Lint suppression, build/dependency controls, CI trust. |
