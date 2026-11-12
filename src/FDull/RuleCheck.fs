namespace FDull

open System
open System.IO
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.Symbols
open FSharp.Compiler.Text

/// A rule `fdull check` applies to selected sources of an ordinary F# project.
type CheckRule =
    /// MODEL004: a wildcard or variable pattern stands in for cases of a union declared in the project.
    | CaseCoverage
    /// ERR007: a Result, or a Task of one, is discarded through ignore, a wildcard binding or a sequence.
    | DiscardedResult
    /// ERR006: a throwing collection/Option access or a throwing System parser.
    | PartialAccess

/// A selected rule violated in a selected source.
type CheckFinding =
    { Rule: CheckRule
      File: string
      StartLine: int
      StartColumn: int
      EndLine: int
      EndColumn: int
      Symbol: string
      Message: string }

/// Why a check could not complete: a compiler error, unsupported syntax or a suppressed warning.
type CheckBlocker =
    { Code: string
      File: string
      StartLine: int
      StartColumn: int
      EndLine: int
      EndColumn: int
      Message: string }

type CheckOutcome =
    /// The request names no source, an unknown or unsupported rule, or a path the project does not compile.
    | Invalid of reason: string
    /// The selected rules could not be applied completely; findings so far are not a result.
    | Incomplete of reason: string * blockers: CheckBlocker list
    | Clean
    | Violations of CheckFinding list

/// Files are relative to the project directory, in compile order.
type CheckReport =
    { Project: string
      Files: string list
      Rules: CheckRule list
      Outcome: CheckOutcome }

/// Selected FDull rules for selected sources of an existing project, without fdull.json.
/// The whole project is evaluated and type-checked; only the selection is inspected.
/// This is not FDull verification: calls into unselected code are trusted.
module RuleCheck =
    let ruleId rule =
        match rule with
        | CaseCoverage -> "MODEL004"
        | DiscardedResult -> "ERR007"
        | PartialAccess -> "ERR006"

    let supported = [ CaseCoverage; DiscardedResult; PartialAccess ]

    let private parseRule (id: string) =
        supported |> List.tryFind (fun rule -> ruleId rule = id.Trim())

    /// Comma-separated rule ids. Every id must be supported; an empty list is invalid.
    let parseRules (text: string) =
        let ids =
            text.Split(',', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
            |> Array.toList

        let unknown = ids |> List.filter (fun id -> (parseRule id).IsNone)

        if ids.IsEmpty then
            Error "Select at least one rule with --rules."
        elif not unknown.IsEmpty then
            Error(
                "Unsupported rule for fdull check: "
                + String.Join(", ", unknown)
                + ". Supported: "
                + String.Join(", ", supported |> List.map ruleId)
                + "."
            )
        else
            Ok(ids |> List.choose parseRule |> List.distinct)

    let private relative directory (path: string) =
        Path.GetRelativePath(directory, path).Replace('\\', '/')

    let private within (directory: string) (path: string) =
        let relativePath = Path.GetRelativePath(directory, path)

        relativePath <> ".."
        && not (relativePath.StartsWith(".." + string Path.DirectorySeparatorChar, StringComparison.Ordinal))
        && not (Path.IsPathRooted relativePath)

    /// Authored sources a selector can name: the project's compiled sources outside its bin/obj output.
    let private authored projectDirectory (sources: string list) =
        let outputs =
            [ "bin"; "obj" ] |> List.map (fun name -> Path.Combine(projectDirectory, name))

        sources
        |> List.filter (fun source -> not (outputs |> List.exists (fun output -> within output source)))

    /// Each selector is a compiled source file or a directory whose compiled sources it selects,
    /// including files added later. A selector that names no compiled source is invalid.
    let select projectDirectory (sources: string list) (selectors: string list) =
        let candidates = authored projectDirectory sources

        let matches (selector: string) =
            let path = Path.GetFullPath selector

            candidates
            |> List.filter (fun source -> source = path || (Directory.Exists path && within path source))

        let unmatched =
            selectors |> List.filter (fun selector -> (matches selector).IsEmpty)

        if selectors.IsEmpty then
            Error "Select at least one source file or directory."
        elif not unmatched.IsEmpty then
            Error(
                "No F# source compiled by the project matches: "
                + String.Join(", ", unmatched)
                + ". Name a compiled .fs/.fsi file or a directory that contains one."
            )
        else
            let chosen = selectors |> List.collect matches |> Set.ofList
            Ok(sources |> List.filter (fun source -> Set.contains source chosen))

    let private finding directory rule (r: range) symbol message =
        { Rule = rule
          File = relative directory r.FileName
          StartLine = max 1 r.StartLine
          StartColumn = r.StartColumn + 1
          EndLine = max 1 r.EndLine
          EndColumn = r.EndColumn + 1
          Symbol = symbol
          Message = message }

    let private blocker directory code (r: range) message =
        { Code = code
          File = relative directory r.FileName
          StartLine = max 1 r.StartLine
          StartColumn = r.StartColumn + 1
          EndLine = max 1 r.EndLine
          EndColumn = r.EndColumn + 1
          Message = message }

    /// What an id the analyzer emits means for this check.
    type private Emitted =
        /// BUILD003: the analyzer could not classify the code, so the check is incomplete.
        | Unclassified
        | Selected of CheckRule
        | Unselected

    let private classify rules (id: string) =
        if id = "BUILD003" then
            Unclassified
        else
            match parseRule id with
            | Some rule when List.contains rule rules -> Selected rule
            | _ -> Unselected

    /// FS0025, the compiler's incomplete-match warning, is how MODEL004 sees a missed case.
    let private incompleteMatch = 25

    let private suppressesIncompleteMatch (codes: string) =
        codes.Replace(';', ',').Replace(' ', ',').Replace('"', ',').Split(',', StringSplitOptions.RemoveEmptyEntries)
        |> Array.toList
        |> List.exists (fun code ->
            let digits =
                if code.StartsWith("FS", StringComparison.OrdinalIgnoreCase) then
                    code.Substring 2
                else
                    code

            match Int32.TryParse digits with
            | true, number -> number = incompleteMatch
            | _ -> false)

    /// The project's own flags, except that FS0025 stays enabled at a level that reports it.
    let private caseCoverageFlags (flags: string list) =
        flags
        |> List.choose (fun flag ->
            if flag.StartsWith("--nowarn:", StringComparison.Ordinal) then
                let kept =
                    flag.Substring(9).Split(',', StringSplitOptions.RemoveEmptyEntries)
                    |> Array.toList
                    |> List.filter (suppressesIncompleteMatch >> not)

                if kept.IsEmpty then
                    None
                else
                    Some("--nowarn:" + String.Join(",", kept))
            elif flag.StartsWith("--warn:", StringComparison.Ordinal) then
                match Int32.TryParse(flag.Substring 7) with
                | true, level when level < 3 -> Some "--warn:3"
                | _ -> Some flag
            else
                Some flag)

    /// Lines of a selected source whose #nowarn directive turns FS0025 off.
    let private suppressedIncompleteMatches (source: string) =
        source.Split('\n')
        |> Array.toList
        |> List.indexed
        |> List.filter (fun (_, line) ->
            let text = line.TrimStart()

            text.StartsWith("#nowarn", StringComparison.Ordinal)
            && suppressesIncompleteMatch (text.Substring 7))
        |> List.map (fun (index, line) -> index + 1, line.Trim())

    let private checker = lazy (FSharpChecker.Create(keepAssemblyContents = true))

    /// Type-check the evaluated project and apply `rules` to `selected`. Every expected failure is an
    /// Incomplete outcome; `run` reports an exception from the compiler or analyzer the same way.
    /// `referencedAssemblies` are the compiler inputs of the projects the project references directly.
    let private analyze
        project
        (evaluated: EvaluatedInvocation)
        (referencedAssemblies: string list)
        (selected: string list)
        (rules: CheckRule list)
        =
        let directory = evaluated.Directory
        let inputs = project :: evaluated.Sources @ evaluated.References
        let missing = inputs |> List.filter (File.Exists >> not)
        let coverage = List.contains CaseCoverage rules

        let incomplete reason = Incomplete(reason, [])

        if evaluated.Sources.IsEmpty || evaluated.References.IsEmpty then
            incomplete "MSBuild did not export a complete compiler invocation."
        elif not missing.IsEmpty then
            incomplete (
                "Compiler inputs are missing: "
                + String.Join(", ", missing |> List.truncate 5)
                + ". Restore and build the project's references in Release first."
            )
        else
            let before = inputs |> List.map SafetyPolicy.fileDigest

            // Work grows with the selection: the syntax and typed walks visit only selected sources.
            // Past 1000 selected files the budget stops growing, which keeps the product within int.
            let budget =
                SafetyBudget(
                    maximumSteps = 2000000 * (1 + min selected.Length 1000),
                    maximumDepth = 512,
                    milliseconds = 600000
                )

            let flags =
                if coverage then
                    caseCoverageFlags evaluated.Flags
                else
                    evaluated.Flags

            let options =
                checker.Value.GetProjectOptionsFromCommandLineArgs(project, List.toArray flags)

            let options =
                { options with
                    SourceFiles = List.toArray evaluated.Sources }

            let parsing, optionErrors =
                checker.Value.GetParsingOptionsFromProjectOptions options

            if options.IsIncompleteTypeCheckEnvironment || not optionErrors.IsEmpty then
                incomplete "FCS could not load the project's compiler options."
            else
                let blockers = ResizeArray<CheckBlocker>()

                let syntax =
                    selected
                    |> List.map (fun file ->
                        let source = File.ReadAllText file

                        let parsed =
                            checker.Value.ParseFile(file, SourceText.ofString source, parsing)
                            |> fun work -> Async.RunSynchronously(work, budget.RemainingMilliseconds)

                        let inspected =
                            SafetySyntax.inspect budget file source parsed.ParseTree (fun id r _ message _ ->
                                match classify rules id with
                                | Unclassified -> blockers.Add(blocker directory id r message)
                                | Selected _
                                | Unselected -> ())

                        file, inspected)
                    |> Map.ofList

                let suppressions =
                    if coverage then
                        selected
                        |> List.collect (fun file ->
                            suppressedIncompleteMatches (File.ReadAllText file)
                            |> List.map (fun (line, directive) ->
                                { Code = "FS0025"
                                  File = relative directory file
                                  StartLine = line
                                  StartColumn = 1
                                  EndLine = line
                                  EndColumn = 1
                                  Message = "MODEL004 needs the incomplete-match warning; remove " + directive + "." }))
                    else
                        []

                let result =
                    checker.Value.ParseAndCheckProject options
                    |> fun work -> Async.RunSynchronously(work, budget.RemainingMilliseconds)

                let selectedFiles = Set.ofList selected

                // A missed case in a selected file is a MODEL004 finding, whether the project reports
                // FS0025 as a warning or, with warnings as errors, as an error.
                let missedCases, otherDiagnostics =
                    result.Diagnostics
                    |> Array.toList
                    |> List.partition (fun diagnostic ->
                        let r = diagnostic.Range

                        coverage
                        && diagnostic.ErrorNumber = incompleteMatch
                        && Set.contains r.FileName selectedFiles)

                let errors =
                    otherDiagnostics
                    |> List.filter (fun diagnostic -> diagnostic.Severity = FSharpDiagnosticSeverity.Error)
                    |> List.map (fun diagnostic ->
                        blocker
                            directory
                            ("FS" + diagnostic.ErrorNumber.ToString("0000"))
                            diagnostic.Range
                            diagnostic.Message)

                let implementations =
                    result.AssemblyContents.ImplementationFiles |> List.map _.FileName |> Set.ofList

                let selectedImplementations =
                    selected |> List.filter (fun file -> Path.GetExtension file = ".fs")

                if result.HasCriticalErrors || not errors.IsEmpty then
                    Incomplete("Compiler errors prevented complete analysis.", List.truncate 50 errors)
                elif not suppressions.IsEmpty then
                    Incomplete("A selected source turns off the incomplete-match warning.", suppressions)
                elif blockers.Count > 0 then
                    Incomplete("Unsupported syntax in a selected source.", List.ofSeq blockers)
                elif
                    selectedImplementations
                    |> List.exists (fun file -> not (Set.contains file implementations))
                then
                    incomplete "The compiler did not return every selected implementation file."
                else
                    let projectSources = Set.ofList evaluated.Sources
                    let referenced = Set.ofList referencedAssemblies
                    let findings = ResizeArray<CheckFinding>()

                    // Unions declared anywhere in the project, or in a project it references
                    // directly, are closed domain decisions even outside the selection.
                    let declaredInProject (entity: FSharpEntity) =
                        let declaration = entity.DeclarationLocation

                        entity.IsFSharpUnion
                        && (Set.contains declaration.FileName projectSources
                            || entity.Assembly.FileName
                               |> Option.exists (fun file -> Set.contains (Path.GetFullPath file) referenced))

                    let context =
                        { SafetyContext.application with
                            Project = project
                            Domain = declaredInProject }

                    SafetyAnalysis.inspect
                        budget
                        context
                        false
                        selectedFiles
                        result
                        syntax
                        (fun id r symbol message _ _ ->
                            match classify rules id with
                            | Unclassified -> blockers.Add(blocker directory id r message)
                            | Selected rule when Set.contains r.FileName selectedFiles ->
                                findings.Add(finding directory rule r symbol message)
                            | Selected _
                            | Unselected -> ())

                    let missed =
                        missedCases
                        |> List.map (fun diagnostic ->
                            finding
                                directory
                                CaseCoverage
                                diagnostic.Range
                                "FS0025"
                                ("Incomplete match: " + diagnostic.Message))

                    let after = inputs |> List.map SafetyPolicy.fileDigest

                    let ordered =
                        Seq.append findings missed
                        |> Seq.distinct
                        |> Seq.sortBy (fun item -> item.File, item.StartLine, item.StartColumn, ruleId item.Rule)
                        |> Seq.toList

                    if after <> before then
                        incomplete "A source, reference or the project file changed during analysis."
                    elif blockers.Count > 0 then
                        Incomplete("The analyzer could not classify part of a selected source.", List.ofSeq blockers)
                    elif ordered.IsEmpty then
                        Clean
                    else
                        Violations ordered

    /// Check `rules` (comma-separated ids) in the sources `selectors` name within `projectFile`.
    let run (projectFile: string) (rules: string) (selectors: string list) =
        let project = Path.GetFullPath projectFile

        let report files rules outcome =
            { Project = project
              Files = files
              Rules = rules
              Outcome = outcome }

        match parseRules rules with
        | Error reason -> report [] [] (Invalid reason)
        | Ok rules when not (File.Exists project) || Path.GetExtension project <> ".fsproj" ->
            report [] rules (Invalid("Not an F# project file: " + projectFile))
        | Ok rules ->
            // Evaluation, selection and fingerprinting run processes and read files; any
            // unexpected failure there is an incomplete check, never a pass.
            try
                let directory =
                    Path.GetDirectoryName project
                    |> FDull.Transport.External.required "check.project-parent"

                let file =
                    Path.GetFileName project
                    |> FDull.Transport.External.required "check.project-file"

                match Workspace.evaluateWithDirectReferences directory file with
                | Error error ->
                    let detail =
                        if error.Length > 4000 then
                            error.Substring(0, 4000) + " ..."
                        else
                            error

                    report [] rules (Incomplete("MSBuild could not evaluate the project: " + detail, []))
                | Ok(evaluated, referencedAssemblies) ->
                    match select directory evaluated.Sources selectors with
                    | Error reason -> report [] rules (Invalid reason)
                    | Ok selected ->
                        analyze project evaluated referencedAssemblies selected rules
                        |> report (selected |> List.map (relative directory)) rules
            with error ->
                report [] rules (Incomplete("Check failed: " + error.Message, []))

    let exitCode outcome =
        match outcome with
        | Clean -> 0
        | Violations _ -> 1
        | Invalid _ -> 2
        | Incomplete _ -> 3

    /// What a passing report establishes, stated for CI logs and machine-readable output.
    let claim (report: CheckReport) =
        "Rules "
        + String.Join(", ", report.Rules |> List.map ruleId)
        + " in "
        + string report.Files.Length
        + " selected source files of "
        + report.Project
        + ". This is not FDull verification: unselected sources are type-checked but not inspected, and calls into them are trusted."
