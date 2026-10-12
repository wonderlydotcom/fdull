namespace FDull

open System

type WorkspaceReviewItem =
    { File: string
      Owner: string
      Profile: string
      Kind: string
      Identity: string
      Resolution: string
      Evidence: SafetyDiagnostic list }

type WorkspaceSourceReview =
    { File: string
      ReviewedDigest: string
      CurrentDigest: string
      Contracts: WorkspaceCapability list
      Constructors: string list }

/// Advisory reports never represent a completed verification or permission approval.
type WorkspaceReviewReport =
    { Advisory: bool
      AnalysisComplete: bool
      ExitCode: int
      Projects: WorkspaceReviewProjectReport list
      Items: WorkspaceReviewItem list
      SourceChanges: WorkspaceSourceReview list
      UnusedCapabilities: WorkspaceCapability list
      UnusedConstructors: string list
      UnusedDataContracts: WorkspaceDataContract list
      Errors: string list }

module WorkspaceReview =
    let validate request policy processExit stderr (result: WorkspaceReviewProjectReport) =
        match WorkspaceProtocol.validate request policy processExit stderr result.Analysis with
        | Error error -> Error error
        | Ok() ->
            if
                not result.Advisory
                || result.Findings.Length > 4097
                || (result.Findings |> List.map _.Diagnostic |> Set.ofList)
                   <> Set.ofList result.Analysis.Report.Diagnostics
                || result.Findings
                   |> List.exists (fun finding ->
                       String.IsNullOrWhiteSpace finding.Owner
                       || String.IsNullOrWhiteSpace finding.Identity
                       || not (
                           List.contains
                               finding.Kind
                               [ "member"
                                 "type"
                                 "rule"
                                 "capture"
                                 "attribute"
                                 "constructor"
                                 "source"
                                 "data" ]
                       )
                       || finding.Resolution
                          <> WorkspacePolicy.reviewResolution
                              finding.Diagnostic.Profile
                              finding.Diagnostic.Rule
                              finding.Kind
                       || (finding.Kind = "rule"
                           && finding.Identity <> finding.Diagnostic.Rule + " | " + finding.Diagnostic.Symbol)
                       || (finding.Kind = "capture"
                           && (not (WorkspacePolicy.validCaptureIdentity finding.Identity)
                               || not (
                                   finding.Identity.StartsWith(
                                       finding.Diagnostic.Rule + " | ",
                                       StringComparison.Ordinal
                                   )
                               )))
                       || (finding.Kind = "constructor" && finding.Diagnostic.Rule <> "MODEL003")
                       || (finding.Kind = "data" && finding.Diagnostic.Rule <> "MODEL001")
                       || (finding.Kind = "attribute"
                           && not (
                               finding.Diagnostic.Rule.StartsWith("ATTR", StringComparison.Ordinal)
                               || finding.Diagnostic.Rule = "NATIVE001"
                           ))
                       || (finding.Kind = "type"
                           && not (
                               List.contains
                                   finding.Diagnostic.Rule
                                   [ "MODEL001"
                                     "CAST004"
                                     "ERR005"
                                     "MUT004"
                                     "MUT005"
                                     "MUT007"
                                     "MUT008"
                                     "NATIVE003"
                                     "REFL001"
                                     "NULL005"
                                     "EFFECT003" ]
                           )))
            then
                Error "BUILD003: Inconsistent advisory review evidence."
            else
                Ok()

    let create root (policy: WorkspacePolicyDocument) (snapshot: SafetyInput list) projects errors =
        let complete =
            errors |> List.isEmpty
            && projects |> List.forall (fun project -> project.Analysis.Report.Complete)

        let changes =
            policy.Sources
            |> List.choose (fun source ->
                snapshot
                |> List.tryFind (fun input -> input.Path = System.IO.Path.Combine(root, source.File))
                |> Option.bind (fun input ->
                    if input.Digest = source.Digest then
                        None
                    else
                        Some
                            { File = source.File
                              ReviewedDigest = source.Digest
                              CurrentDigest = input.Digest
                              Contracts = policy.Capabilities |> List.filter (fun entry -> entry.File = source.File)
                              Constructors =
                                policy.Constructors
                                |> List.filter (fun identity ->
                                    identity.StartsWith(source.File + " | ", StringComparison.Ordinal)) }))

        let observed = projects |> List.collect (fun project -> project.Analysis.Uses)

        let unused =
            if complete then
                WorkspaceAudit.unused policy observed |> Set.ofList
            else
                Set.empty

        let unusedCapabilities =
            policy.Capabilities
            |> List.filter (fun entry ->
                Set.contains (entry.File + " | " + entry.Owner + " | " + entry.Kind + " | " + entry.Identity) unused)

        let unusedConstructors =
            if complete then
                WorkspaceAudit.unusedConstructors policy observed
            else
                []

        let unusedDataContracts =
            if complete then
                WorkspaceAudit.unusedDataContracts policy observed
            else
                []

        let items =
            projects
            |> List.collect _.Findings
            |> List.groupBy (fun item ->
                let file =
                    System.IO.Path.GetRelativePath(root, item.Diagnostic.File).Replace('\\', '/')

                file, item.Owner, item.Diagnostic.Profile, item.Kind, item.Identity, item.Resolution)
            |> List.map (fun ((file, owner, profile, kind, identity, resolution), findings) ->
                { File = file
                  Owner = owner
                  Profile = profile
                  Kind = kind
                  Identity = identity
                  Resolution = resolution
                  Evidence =
                    findings
                    |> List.map _.Diagnostic
                    |> List.distinct
                    |> List.sortBy (fun evidence ->
                        evidence.StartLine, evidence.StartColumn, evidence.Rule, evidence.Symbol) })
            |> List.sortBy (fun item -> item.File, item.Owner, item.Kind, item.Identity, item.Resolution)

        { Advisory = true
          AnalysisComplete = complete
          ExitCode =
            if not complete then
                3
            elif
                not (
                    items.IsEmpty
                    && changes.IsEmpty
                    && unusedCapabilities.IsEmpty
                    && unusedConstructors.IsEmpty
                    && unusedDataContracts.IsEmpty
                )
            then
                1
            else
                0
          Projects = projects
          Items = items
          SourceChanges = changes
          UnusedCapabilities = unusedCapabilities
          UnusedConstructors = unusedConstructors
          UnusedDataContracts = unusedDataContracts
          Errors = errors }

    let format (report: WorkspaceReviewReport) =
        let decision =
            function
            | "contract-review" -> "Review the exact contract and its source evidence, or change the source."
            | "code-change" -> "Change the source; this finding cannot be resolved by an exemption in this profile."
            | _ -> "Resolve the analysis blocker; the review inventory is incomplete."

        [ yield "FDull review: ADVISORY. Verification has not run; no permissions or fingerprints were changed."
          yield
              sprintf
                  "Current-source analysis: %s; items=%d, changed files=%d, exit=%d"
                  (if report.AnalysisComplete then "complete" else "INCOMPLETE")
                  report.Items.Length
                  report.SourceChanges.Length
                  report.ExitCode
          if not report.AnalysisComplete then
              yield "Findings are partial. An empty list does not establish that no review is needed."
          for error in report.Errors do
              yield "Blocker: " + error
          for change in report.SourceChanges do
              yield ""
              yield "Source changed: " + change.File
              yield "  Reviewed: " + change.ReviewedDigest
              yield "  Current:  " + change.CurrentDigest
              yield "  Re-review this source and its existing contracts before manually updating its fingerprint."

              for entry in change.Contracts do
                  yield sprintf "  Existing %s: %s | %s" entry.Kind entry.Owner entry.Identity
                  yield "    Reason: " + entry.Reason

              for identity in change.Constructors do
                  yield "  Existing constructor: " + identity
          for item in report.Items do
              yield ""
              yield sprintf "%s | %s [%s]" item.File item.Owner item.Profile
              yield sprintf "  %s: %s" item.Kind item.Identity

              for evidence in item.Evidence do
                  yield
                      sprintf
                          "  %s(%d,%d) [%s]: %s"
                          item.File
                          evidence.StartLine
                          evidence.StartColumn
                          evidence.Rule
                          evidence.Message

                  yield "    Symbol/path: " + evidence.Symbol

              yield "  Decision: " + decision item.Resolution

              if item.Kind = "type" then
                  yield "  Type approval does not approve the type's methods."
          for entry in report.UnusedCapabilities do
              yield sprintf "Unused contract: %s | %s | %s | %s" entry.File entry.Owner entry.Kind entry.Identity
          for identity in report.UnusedConstructors do
              yield "Unused constructor: " + identity
          for contract in report.UnusedDataContracts do
              yield
                  "Unused data contract or assembly pin: "
                  + contract.Project
                  + " | "
                  + contract.Identity ]
        |> String.concat Environment.NewLine
