namespace FDull

open System

type SafetyRuleCoverage =
    { Rule: string
      Description: string
      Implementation: string list
      CompilingNegativeFixture: bool
      OtherEvidence: string list
      Remaining: string }

type SafetyCoverageInventory =
    { Policy: string
      Specification: string
      FullStandardComplete: bool
      Scope: string
      Rules: SafetyRuleCoverage list }

/// Inventory of the canonical standard, including work that is not implemented.
/// Presence here is not permission, an exception, or a claim of full coverage.
module SafetyRules =
    let private syntax = "src/FDull/SafetySyntax.fs"
    let private analysis = "src/FDull/SafetyAnalysis.fs"
    let private build = "src/FDull/Project.fs"
    let private guard = "src/FDull/WorkspaceAudit.fs"
    let private engine = "src/FDull/SafetyEngine.fs"
    let private worker = "src/FDull/Safety.fs"
    let private workspace = "src/FDull/WorkspacePolicy.fs"
    let private audit = "src/FDull/WorkspaceAudit.fs"

    let private rule id description implementation negative evidence remaining =
        { Rule = id
          Description = description
          Implementation = implementation
          CompilingNegativeFixture = negative
          OtherEvidence = evidence
          Remaining = remaining }

    let private applicationMatrix =
        "Complete the operation, alias, positive and protected-profile acceptance matrix."

    let private profileMatrix =
        "Extend the tested exact profile contracts when admitting another API, declaration or dependency."

    let all =
        [ rule "MUT001" "Mutable bindings" [ syntax; analysis ] true [] applicationMatrix
          rule "MUT002" "Assignment and setters" [ syntax; analysis ] true [] applicationMatrix
          rule "MUT003" "Mutable fields and objects" [ syntax; analysis ] true [] applicationMatrix
          rule "MUT004" "Ref-cell capabilities" [ analysis ] true [] applicationMatrix
          rule "MUT005" "Mutable collections and builders" [ analysis ] true [] applicationMatrix
          rule "MUT006" "Mutating method references" [ analysis ] true [] applicationMatrix
          rule "MUT007" "Shared mutable state" [ analysis ] true [] applicationMatrix
          rule "MUT008" "Arrays and aliasable views" [ syntax; analysis ] true [] applicationMatrix
          rule "MUT009" "Statement loops" [ syntax ] true [] applicationMatrix
          rule "MUT010" "Nested mutable payloads" [ analysis ] true [] applicationMatrix
          rule "CAST001" "Downcasts" [ syntax ] true [] applicationMatrix
          rule "CAST002" "Unboxing" [ analysis ] true [] applicationMatrix
          rule "CAST003" "Indirect cast APIs" [ analysis ] true [] applicationMatrix
          rule "CAST004" "Object erasure" [ analysis ] true [] applicationMatrix
          rule "CAST005" "Runtime type dispatch" [ syntax; analysis ] true [] applicationMatrix
          rule "CAST006" "Custom conversions" [ analysis ] true [] applicationMatrix
          rule
              "CAST007"
              "Unapproved numeric conversions"
              [ analysis ]
              true
              []
              "Broaden numeric edge cases when admitting additional conversion helpers."
          rule "NULL001" "Authored null values" [ syntax ] true [] applicationMatrix
          rule "NULL002" "Unchecked/default construction" [ analysis ] true [] applicationMatrix
          rule "NULL003" "Unchecked nullability removal" [ analysis ] true [] applicationMatrix
          rule "NULL004" "Default/zero initialization" [ analysis ] true [] applicationMatrix
          rule "NULL005" "Unnormalized nullable data" [ analysis ] true [] profileMatrix
          rule "ATTR001" "Unapproved attributes" [ analysis ] true [] applicationMatrix
          rule "ATTR002" "Mutable/null/default representation attributes" [ analysis ] true [] profileMatrix
          rule
              "ATTR003"
              "Null-backed union representations"
              [ analysis ]
              true
              [ "SafetyTests.the allocation incident also stays bounded in the authoritative worker" ]
              applicationMatrix
          rule "ATTR004" "Attribute-based trust claims" [ analysis ] true [] profileMatrix
          rule "REFL001" "Runtime metadata capabilities" [ analysis ] true [] applicationMatrix
          rule "REFL002" "Reflective/dynamic construction" [ syntax; analysis ] true [] applicationMatrix
          rule "REFL003" "Uninitialized object construction" [ analysis ] true [] applicationMatrix
          rule "REFL004" "Runtime code loading and evaluation" [ syntax; analysis; guard ] true [] applicationMatrix
          rule "NATIVE001" "Native imports" [ analysis; guard ] true [] applicationMatrix
          rule "NATIVE002" "Unsafe memory APIs" [ analysis ] true [] applicationMatrix
          rule "NATIVE003" "Pointers and memory capabilities" [ syntax; analysis ] true [] applicationMatrix
          rule
              "DATA001"
              "Domain materialization targets"
              [ analysis; "src/FDull/Materializer.fs" ]
              true
              [ "TransportTests.generic materializers reject direct and nested domain targets" ]
              "Additional materializer libraries need their own recursive target contracts."
          rule "DATA002" "Raw materializers" [ analysis ] true [] profileMatrix
          rule "DATA003" "Arbitrary generic materializers" [ analysis ] true [] profileMatrix
          rule
              "DATA004"
              "Invalid transport payloads"
              [ analysis; "src/FDull/Materializer.fs" ]
              true
              [ "TransportTests.required DTO references are normalized before materialization" ]
              "Expand the DTO shape matrix when admitting additional transport types."
          rule
              "DATA005"
              "Unnormalized boundary data"
              [ analysis ]
              true
              []
              "Known serializers reject nested private domain values; this is not general interprocedural taint tracking."
          rule "MODEL001" "Unapproved data shapes" [ syntax; analysis ] true [] applicationMatrix
          rule
              "MODEL002"
              "Public invariant-bearing representations"
              [ analysis; workspace ]
              false
              [ "SafetyTests.protected invariant designations require private representations" ]
              "New invariant-bearing types require explicit designation and behavioral properties."
          rule
              "MODEL003"
              "Unchecked domain construction helpers"
              [ analysis; workspace; audit ]
              true
              []
              "Expand adversarial constructor and friend-assembly compatibility cases across consumer project graphs."
          rule "MODEL004" "Erased domain case coverage" [ syntax; analysis ] true [] applicationMatrix
          rule "EFFECT001" "Unapproved external APIs" [ analysis ] true [] profileMatrix
          rule "EFFECT002" "Ambient effects" [ analysis ] true [] applicationMatrix
          rule "EFFECT003" "Executable/resource data capabilities" [ syntax; analysis ] true [] applicationMatrix
          rule "EFFECT004" "Unapproved executable abstractions" [ syntax; analysis ] true [] profileMatrix
          rule "ERR001" "Application exception catches" [ syntax ] true [] profileMatrix
          rule "ERR002" "Throwing for application flow" [ syntax; analysis ] true [] applicationMatrix
          rule "ERR003" "Indirect catch/raise wrappers" [ analysis ] true [] applicationMatrix
          rule
              "ERR004"
              "Broad adapter exception translation"
              [ analysis; workspace ]
              true
              []
              "Broaden behavioral translation cases for newly admitted adapter exception types."
          rule "ERR005" "Exception-bearing business data" [ analysis ] true [] applicationMatrix
          rule "ERR006" "Partial/throwing operations" [ analysis ] true [] applicationMatrix
          rule
              "ERR007"
              "Obvious unobserved results"
              [ syntax; analysis ]
              true
              []
              "Expand documented local-flow cases; no claim of linear Result use."
          rule
              "ERR008"
              "Implicit error-to-success recovery"
              [ analysis ]
              true
              []
              "New recovery helpers need exact contracts and failure-path behavior tests."
          rule "ERR009" "Resources in pure code" [ syntax; analysis ] true [] profileMatrix
          rule
              "ERR010"
              "Blocking/background execution and cancellation"
              [ analysis ]
              true
              []
              "New providers need cancellation and lifecycle conformance tests."
          rule "STYLE001" "Backward pipes" [ syntax; analysis ] true [] applicationMatrix
          rule "STYLE002" "Tuple pipes and custom operators" [ syntax; analysis ] true [] applicationMatrix
          rule
              "STYLE003"
              "Raw accumulation outside approved helpers"
              [ analysis ]
              true
              []
              "No raw folds are granted in this workspace; additional helper grants need behavioral evidence."
          rule
              "ARCH001"
              "Forbidden dependency directions"
              [ build; guard; audit ]
              false
              [ "ProjectTests.every approved graph edge is exact and shipping cannot acquire a test dependency" ]
              "The preview supports an exact, acyclic Release/net10.0 F# project graph with literal project references."
          rule
              "ARCH002"
              "Unapproved dependency assets"
              [ build; guard; audit ]
              false
              []
              "Complete transitive package/runtime/source/native asset inventory and tampering cases."
          rule
              "ARCH003"
              "Unapproved languages/scripts/build extensions"
              [ guard; audit ]
              false
              [ "WorkspaceTests.policy and inventory drift cannot certify a workspace"
                "WorkspaceTests.mixed-language scope and internal links preserve inventory boundaries" ]
              profileMatrix
          rule
              "ARCH004"
              "Shipping dependencies on tests/tooling"
              [ build; guard; audit ]
              false
              [ "ProjectTests.every approved graph edge is exact and shipping cannot acquire a test dependency" ]
              "Additional configurations and generated-source producers require a separate exact inventory."
          rule
              "BUILD001"
              "Suppression and path spoofing"
              [ syntax ]
              true
              [ "SafetyTests.valid FSharp violations have canonical diagnostics and physical locations" ]
              applicationMatrix
          rule
              "BUILD002"
              "Compiler/toolchain policy drift"
              [ engine; build; worker; "Directory.Build.targets" ]
              false
              [ "SafetyWorkerTests.compiler policy target accepts the pinned platform defaults"
                "SafetyWorkerTests.compiler policy target rejects drift and downgrades" ]
              "Complete tool/policy drift fixtures and protected release provenance."
          rule
              "BUILD003"
              "Incomplete analysis and checker failure"
              [ engine; worker; "src/FDull/SafetyLimits.fs"; "src/FDull/WorkspaceEngine.fs" ]
              false
              [ "SafetyTests.unsupported type nesting cannot report complete analysis"
                "SafetyTests.unsupported expression depth fails before typed traversal"
                "SafetyTests.diagnostic overflow cannot be a complete truncated report"
                "SafetyTests.oversized sources are rejected before starting analysis"
                "SafetyWorkerTests.bounded process failures terminate the worker"
                "SafetyWorkerTests.crash or malformed output cannot certify inputs"
                "SafetyWorkerTests.worker runtime configuration enforces the managed heap limit"
                "SafetyWorkerTests.an allocation beyond the worker heap ceiling fails inside the child"
                "WorkspaceTests.workspace reports reject inconsistent process diagnostics and observations" ]
              "Complete supported-profile/unknown-classification compatibility coverage; resource limits do not prove termination."
          rule
              "BUILD004"
              "Analyzed/compiled input mismatch"
              [ engine; worker; build ]
              false
              [ "SafetyTests.changing analyzed inputs invalidates the report"
                "SafetyWorkerTests.changing any report authority binding cannot preserve success"
                "SafetyWorkerTests.changing source after worker analysis invalidates its response" ]
              "Install trusted artifact signing and deployment-side admission; complete compilation-race fixtures."
          rule
              "BUILD005"
              "Invalid protected exceptions"
              [ workspace; audit ]
              false
              [ "WorkspaceTests.policy and inventory drift cannot certify a workspace"
                "WorkspaceTests.permissions bind exact owners and cannot survive as unused entries"
                "WorkspaceTests.mixed-language scope and internal links preserve inventory boundaries" ]
              "Temporary exceptions are unsupported. Organizational review identities and expiry require an external policy authority."
          rule
              "BUILD006"
              "Untrusted required checks or bypasses"
              []
              false
              []
              "Requires organization-owned CI/check producers, repository rulesets and deployment admission; outside the scope of a locally installed NuGet package." ]

    let private byId = all |> List.map (fun item -> item.Rule, item) |> Map.ofList

    let knownDiagnostic (id: string) =
        Map.containsKey id byId
        || (not (String.IsNullOrEmpty id)
            && id.Length = 6
            && id.StartsWith("FS", StringComparison.Ordinal)
            && id.Substring(2) |> Seq.forall Char.IsAsciiDigit)

    let validate () =
        let expected =
            [ "MUT", 10
              "CAST", 7
              "NULL", 5
              "ATTR", 4
              "REFL", 4
              "NATIVE", 3
              "DATA", 5
              "MODEL", 4
              "EFFECT", 4
              "ERR", 10
              "STYLE", 3
              "ARCH", 4
              "BUILD", 6 ]
            |> List.collect (fun (family, count) -> [ for number in 1..count -> family + number.ToString("000") ])
            |> Set.ofList

        if byId.Count <> all.Length || (byId |> Map.keys |> Set.ofSeq) <> expected then
            invalidOp "BUILD003: rule coverage inventory does not match the canonical catalog."

        for item in all do
            if
                String.IsNullOrWhiteSpace item.Description
                || (item.Implementation.IsEmpty
                    && (item.CompilingNegativeFixture || not item.OtherEvidence.IsEmpty))
            then
                invalidOp ("BUILD003: inconsistent rule coverage: " + item.Rule)

    let inventory (policy: string) =
        validate ()

        { Policy = policy
          Specification = "1.0"
          FullStandardComplete = all |> List.forall (fun item -> item.Remaining = "")
          Scope =
            "Standalone F# Release/net10.0 workspaces, including FDull's own library, CLI, worker and tests. Coverage is intentionally incomplete; see each rule's remaining work."
          Rules = all }
