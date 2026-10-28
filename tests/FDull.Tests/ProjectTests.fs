namespace FDull.Tests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open Xunit
open FDull
open FDull.Transport

module ProjectTests =
    let private graph =
        [ { File = "Contracts.fsproj"
            Profile = "CONTRACTS"
            References = [] }
          { File = "Pure.fsproj"
            Profile = "PURE"
            References = [ "Contracts.fsproj" ] }
          { File = "Adapter.fsproj"
            Profile = "ADAPTER"
            References = [ "Pure.fsproj" ] }
          { File = "Tool.fsproj"
            Profile = "GUARD_TOOLING"
            References = [ "Adapter.fsproj" ] }
          { File = "Tests.fsproj"
            Profile = "TEST"
            References = [ "Tool.fsproj" ] } ]

    [<Fact>]
    let ``every approved graph edge is exact and shipping cannot acquire a test dependency`` () =
        for project in graph do
            Assert.Equal(Ok(), WorkspaceAudit.validateEdges graph project.File project.References)

            if project.Profile <> "TEST" then
                match WorkspaceAudit.validateEdges graph project.File ("Tests.fsproj" :: project.References) with
                | Error error -> Assert.StartsWith("ARCH004:", error)
                | Ok() -> failwith "Shipping acquired a test reference."

        Assert.True(WorkspaceAudit.validateEdges graph "Pure.fsproj" [] |> Result.isError)

        Assert.True(WorkspaceAudit.validateEdges graph "Unknown.fsproj" [] |> Result.isError)

        for target in [ "Adapter.fsproj"; "Tool.fsproj"; "Unknown.fsproj"; "Pure.fsproj" ] do
            let changed =
                graph
                |> List.map (fun project ->
                    if project.File = "Pure.fsproj" then
                        { project with References = [ target ] }
                    else
                        project)

            Assert.True(WorkspaceAudit.validateEdges changed "Pure.fsproj" [ target ] |> Result.isError)

        Assert.True(
            WorkspaceAudit.validateEdges graph "Pure.fsproj" [ "Contracts.fsproj"; "Contracts.fsproj" ]
            |> Result.isError
        )

    [<Fact>]
    let ``verification binds the ordered invocation and project edges`` () =
        let invocation =
            { Sources = [ "A.fs"; "B.fs" ]
              References = [ "Library.dll" ]
              Arguments = [ "--checked+" ]
              Generated = []
              ProjectReferences = [ "Library.fsproj" ] }

        let result =
            { Project = "App.fsproj"
              ProjectReferences = invocation.ProjectReferences
              Uses = []
              Cached = false
              Report =
                { Complete = true
                  ExitCode = 0
                  Policy = "policy"
                  Tool = "tool"
                  Arguments = invocation.Arguments
                  Diagnostics = []
                  Sources = invocation.Sources |> List.map (fun file -> { Path = file; Digest = "source" })
                  References =
                    invocation.References
                    |> List.map (fun file -> { Path = file; Digest = "reference" }) } }

        Assert.True(Project.matchesInvocation result invocation)

        for changed in
            [ { invocation with
                  Sources = List.rev invocation.Sources }
              { invocation with
                  References = [ "Different.dll" ] }
              { invocation with
                  Arguments = invocation.Arguments @ [ "--checked-" ] }
              { invocation with
                  ProjectReferences = [] } ] do
            Assert.False(Project.matchesInvocation result changed)

        Assert.False(
            Project.matchesInvocation
                { result with
                    Report = { result.Report with Complete = false } }
                invocation
        )

    let private withDirectory action =
        let root =
            Path.Combine(Path.GetTempPath(), "fdull-consumer-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory root |> ignore

        try
            action root
        finally
            Directory.Delete(root, true)

    let private run root (arguments: string list) =
        let start = ProcessStartInfo("dotnet", WorkingDirectory = root)

        for argument in arguments do
            start.ArgumentList.Add argument

        start.Environment["DOTNET_GCHeapHardLimit"] <- "80000000"
        start.Environment["MSBUILDDISABLENODEREUSE"] <- "1"

        match
            SafetyProcess.run
                { Milliseconds = 90000
                  OutputCharacters = 1048576
                  WorkingSetBytes = 2147483648L }
                start
                ""
        with
        | Ok result -> Assert.True(result.ExitCode = 0, result.Error + result.Output)
        | Error error -> failwith error

    let private writeConsumerSettings (root: string) =
        File.WriteAllText(
            Path.Combine(root, "global.json"),
            "{\"sdk\":{\"version\":\"10.0.200\",\"rollForward\":\"disable\"}}"
        )

        File.WriteAllText(Path.Combine(root, "Directory.Build.props"), Project.defaults ())

        File.WriteAllText(
            Path.Combine(root, "NuGet.config"),
            "<configuration><packageSources><clear/><add key=\"nuget.org\" value=\"https://api.nuget.org/v3/index.json\"/></packageSources></configuration>"
        )

    let private restoreConsumer (root: string) (coreVersion: string option) (source: string) =
        writeConsumerSettings root

        let coreProperty =
            coreVersion
            |> Option.map (fun version ->
                "<PropertyGroup><FSharpCoreImplicitPackageVersion>"
                + version
                + "</FSharpCoreImplicitPackageVersion></PropertyGroup>")
            |> Option.defaultValue ""

        File.WriteAllText(
            Path.Combine(root, "App.fsproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\">"
            + coreProperty
            + "<ItemGroup><Compile Include=\"App.fs\"/></ItemGroup></Project>"
        )

        File.WriteAllText(Path.Combine(root, "App.fs"), source)
        run root [ "restore"; "App.fsproj"; "-m:1"; "-nr:false" ]

    let private prepare root =
        restoreConsumer root None "module App\nlet increment value = value + 1\n"

        run
            root
            [ "build"
              "App.fsproj"
              "-c"
              "Release"
              "--no-restore"
              "-m:1"
              "--disable-build-servers" ]

        Project.initialize root |> ignore

    let private read root =
        File.ReadAllText(Path.Combine(root, "fdull.json"))
        |> Codec.decode<WorkspacePolicyDocument>

    let private cliDirectory =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/FDull.Tool/bin/Release/net10.0"))

    let private runCliAt (cli: string) root (arguments: string list) =
        let start = ProcessStartInfo("dotnet", WorkingDirectory = root)

        for argument in cli :: arguments do
            start.ArgumentList.Add argument

        start.Environment["DOTNET_GCHeapHardLimit"] <- "80000000"
        start.Environment["MSBUILDDISABLENODEREUSE"] <- "1"

        match
            SafetyProcess.run
                { Milliseconds = 90000
                  OutputCharacters = 1048576
                  WorkingSetBytes = 2147483648L }
                start
                ""
        with
        | Ok result -> result
        | Error error -> failwith error

    let private runCli root arguments =
        runCliAt (Path.Combine(cliDirectory, "FDull.Cli.dll")) root arguments

    let private excludedFixture root (projectText: string) =
        let fixture = Path.Combine(root, "Fixture")
        Directory.CreateDirectory fixture |> ignore
        File.WriteAllText(Path.Combine(fixture, "Fixture.fsproj"), projectText)
        File.WriteAllText(Path.Combine(fixture, "Bad.fs"), "module Bad\nlet value: int = \"wrong\"\n")
        File.WriteAllText(Path.Combine(fixture, "Inactive.fsi"), "module Inactive\nval value: int\n")

        File.WriteAllText(
            Path.Combine(root, "fdull.scope.json"),
            "{\"Version\":1,\"ExcludedProjects\":[{\"File\":\"Fixture/Fixture.fsproj\",\"Reason\":\"Conditional test fixture is checked separately.\"}]}"
        )

    let private conditionalFixtureProject =
        "<Project Sdk=\"Microsoft.NET.Sdk\">"
        + "<ItemGroup Condition=\"'$(Configuration)' == 'Release'\"><Compile Include=\"Bad.fs\"/></ItemGroup>"
        + "<ItemGroup Condition=\"'$(Configuration)' == 'Never'\"><Compile Include=\"Inactive.fsi\"/></ItemGroup>"
        + "</Project>"

    [<Fact>]
    let ``excluded conditional project is neither exported nor built and remains visible in reports`` () =
        withDirectory (fun root ->
            restoreConsumer root None "module App\nlet increment value = value + 1\n"
            excludedFixture root conditionalFixtureProject
            let policyFile = Project.initialize root
            let policy = read root

            Assert.Equal(
                "Fixture/Fixture.fsproj",
                (Assert.Single(policy.ExcludedProjects |> Option.defaultValue [])).File
            )

            Assert.DoesNotContain(policy.Projects, fun project -> project.File = "Fixture/Fixture.fsproj")

            Assert.DoesNotContain(
                policy.Sources,
                fun source -> source.File.StartsWith("Fixture/", StringComparison.Ordinal)
            )

            Assert.Contains(policy.Inputs, fun input -> input.File = "Fixture/Fixture.fsproj")
            Assert.Contains(policy.Inputs, fun input -> input.File = "fdull.scope.json")
            Assert.Equal(Ok(), Workspace.validatePolicy root)

            let verified = Project.verify root
            Assert.Equal(0, verified.ExitCode)
            Assert.False(Directory.Exists(Path.Combine(root, "Fixture/obj")))

            let lint = Workspace.check root
            Assert.True(lint.Complete, Codec.encode lint)
            Assert.Equal(0, lint.ExitCode)
            Assert.Equal("Fixture/Fixture.fsproj", (Assert.Single lint.ExcludedProjects).File)
            Assert.Equal(Path.Combine(root, "App.fsproj"), (Assert.Single lint.Projects).Project)

            let review = Workspace.review root
            Assert.True(review.AnalysisComplete, WorkspaceReview.format review)
            Assert.Equal(0, review.ExitCode)
            Assert.Equal("Fixture/Fixture.fsproj", (Assert.Single review.ExcludedProjects).File)
            Assert.Contains("Fixture/Fixture.fsproj", WorkspaceReview.format review)

            for format in [ "json"; "text"; "sarif" ] do
                let result = runCli root [ "lint"; "."; "--format"; format ]
                Assert.Equal(0, result.ExitCode)
                Assert.Contains("Fixture/Fixture.fsproj", result.Output)

                if format = "json" then
                    let report = Codec.decode<WorkspaceReport> result.Output
                    Assert.Equal("Fixture/Fixture.fsproj", (Assert.Single report.ExcludedProjects).File)
                elif format = "sarif" then
                    use document = JsonDocument.Parse result.Output

                    let excluded =
                        document.RootElement
                            .GetProperty("runs")
                            .[0].GetProperty("properties")
                            .GetProperty("excludedProjects")
                            .[0]

                    Assert.Equal("Fixture/Fixture.fsproj", excluded.GetProperty("file").GetString())
                    Assert.False(String.IsNullOrWhiteSpace(excluded.GetProperty("reason").GetString()))

            Assert.True(File.Exists policyFile))

    [<Fact>]
    let ``excluded project declarations and policy entries remain exact`` () =
        withDirectory (fun root ->
            restoreConsumer root None "module App\nlet increment value = value + 1\n"
            excludedFixture root conditionalFixtureProject
            Project.initialize root |> ignore
            let baseline = read root
            let policyPath = Path.Combine(root, "fdull.json")
            let projectPath = Path.Combine(root, "Fixture/Fixture.fsproj")
            let originalProject = File.ReadAllText projectPath
            let originalBad = File.ReadAllText(Path.Combine(root, "Fixture/Bad.fs"))
            let exclusion = Assert.Single(baseline.ExcludedProjects |> Option.defaultValue [])

            let checkInvalid label expected repinProject mutate =
                File.WriteAllText(policyPath, Codec.encode baseline)
                File.WriteAllText(projectPath, originalProject)
                File.WriteAllText(Path.Combine(root, "Fixture/Bad.fs"), originalBad)

                if File.Exists(Path.Combine(root, "Unrelated.fs")) then
                    File.Delete(Path.Combine(root, "Unrelated.fs"))

                mutate ()

                if repinProject then
                    let current = read root

                    File.WriteAllText(
                        policyPath,
                        Codec.encode
                            { current with
                                Inputs =
                                    current.Inputs
                                    |> List.map (fun input ->
                                        if input.File = exclusion.File then
                                            { input with
                                                Digest = SafetyPolicy.fileDigest projectPath }
                                        else
                                            input) }
                    )

                match Workspace.validatePolicy root with
                | Error message -> Assert.StartsWith(expected + ":", message)
                | Ok() -> failwith ("Accepted invalid excluded project case: " + label)

            for label, includePath in
                [ "wildcard", "*.fs"
                  "property", "$(FixtureSource)"
                  "item", "@(FixtureSources)"
                  "semicolon", "Bad.fs;Inactive.fsi"
                  "missing", "Missing.fs"
                  "outside", "../../Escaping.fs" ] do
                checkInvalid label "BUILD005" true (fun () ->
                    File.WriteAllText(
                        projectPath,
                        "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><Compile Include=\""
                        + includePath
                        + "\"/></ItemGroup></Project>"
                    ))

            checkInvalid "unowned linked source" "BUILD005" true (fun () ->
                File.WriteAllText(Path.Combine(root, "Unrelated.fs"), "module Unrelated\nlet value = 1\n")

                File.WriteAllText(
                    projectPath,
                    "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup Condition=\"'$(Configuration)' == 'Never'\"><Compile Include=\"../Unrelated.fs\"/><Compile Include=\"Bad.fs\"/><Compile Include=\"Inactive.fsi\"/></ItemGroup></Project>"
                ))

            checkInvalid "target-time Compile" "BUILD005" true (fun () ->
                File.WriteAllText(
                    projectPath,
                    "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><Compile Include=\"Inactive.fsi\"/></ItemGroup><Target Name=\"LateCompile\"><ItemGroup><Compile Include=\"Bad.fs\"/></ItemGroup></Target></Project>"
                ))

            checkInvalid "ignored ProjectExtensions Compile" "BUILD005" true (fun () ->
                File.WriteAllText(
                    projectPath,
                    "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><Compile Include=\"Inactive.fsi\"/></ItemGroup><ProjectExtensions><ItemGroup><Compile Include=\"Bad.fs\"/></ItemGroup></ProjectExtensions></Project>"
                ))

            for label, expected, entries in
                [ "missing project",
                  "BUILD005",
                  [ { exclusion with
                        File = "Fixture/Missing.fsproj" } ]
                  "stale entry", "ARCH002", []
                  "duplicate entry", "BUILD005", [ exclusion; exclusion ]
                  "blank reason", "BUILD005", [ { exclusion with Reason = "  " } ]
                  "checked project", "ARCH002", [ { exclusion with File = "App.fsproj" } ] ] do
                checkInvalid label expected false (fun () ->
                    File.WriteAllText(
                        policyPath,
                        Codec.encode
                            { baseline with
                                ExcludedProjects = Some entries }
                    ))

            checkInvalid "changed project pin" "BUILD005" false (fun () ->
                File.AppendAllText(projectPath, "\n<!-- changed -->\n"))

            checkInvalid "missing project pin" "BUILD005" false (fun () ->
                File.WriteAllText(
                    policyPath,
                    Codec.encode
                        { baseline with
                            Inputs = baseline.Inputs |> List.filter (fun input -> input.File <> exclusion.File) }
                ))

            checkInvalid "orphan source beside excluded project" "ARCH002" false (fun () ->
                File.WriteAllText(Path.Combine(root, "Fixture/Orphan.fs"), "module Orphan\nlet value = 1\n"))

            File.Delete(Path.Combine(root, "Fixture/Orphan.fs"))

            checkInvalid "unrelated project beside excluded project" "ARCH002" false (fun () ->
                File.WriteAllText(
                    Path.Combine(root, "Fixture/Other.fsproj"),
                    "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><Compile Include=\"Bad.fs\"/></ItemGroup></Project>"
                ))

            File.Delete(Path.Combine(root, "Fixture/Other.fsproj")))

    [<Fact>]
    let ``checked ownership of a shared excluded source still reports violations`` () =
        withDirectory (fun root ->
            restoreConsumer root None "module App\nlet increment value = value + 1\n"
            excludedFixture root conditionalFixtureProject
            File.WriteAllText(Path.Combine(root, "Shared.fs"), "module Shared\nlet first values = List.head values\n")

            File.WriteAllText(
                Path.Combine(root, "App.fsproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><Compile Include=\"Shared.fs\"/><Compile Include=\"App.fs\"/></ItemGroup></Project>"
            )

            File.WriteAllText(
                Path.Combine(root, "Fixture/Fixture.fsproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup Condition=\"'$(Configuration)' == 'Release'\"><Compile Include=\"..\\Shared.fs\"/><Compile Include=\"Bad.fs\"/></ItemGroup><ItemGroup Condition=\"'$(Configuration)' == 'Never'\"><Compile Include=\"Inactive.fsi\"/></ItemGroup></Project>"
            )

            run
                root
                [ "build"
                  "App.fsproj"
                  "-c"
                  "Release"
                  "--no-restore"
                  "-m:1"
                  "--disable-build-servers" ]

            Project.initialize root |> ignore
            let policy = read root
            Assert.Contains(policy.Sources, fun source -> source.File = "Shared.fs" && source.Project = "App.fsproj")
            Assert.Equal(Ok(), Workspace.validatePolicy root)
            let report = Workspace.check root
            Assert.Equal(1, report.ExitCode)

            Assert.Contains(
                report.Projects |> List.collect (fun project -> project.Report.Diagnostics),
                fun diagnostic ->
                    diagnostic.File.EndsWith("Shared.fs", StringComparison.Ordinal)
                    && diagnostic.Rule = "ERR006"
            )

            let projectPath = Path.Combine(root, "App.fsproj")

            File.WriteAllText(
                projectPath,
                "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"Fixture/Fixture.fsproj\"/><Compile Include=\"Shared.fs\"/><Compile Include=\"App.fs\"/></ItemGroup></Project>"
            )

            let repinned =
                { policy with
                    Inputs =
                        policy.Inputs
                        |> List.map (fun input ->
                            if input.File = "App.fsproj" then
                                { input with
                                    Digest = SafetyPolicy.fileDigest projectPath }
                            else
                                input) }

            File.WriteAllText(Path.Combine(root, "fdull.json"), Codec.encode repinned)

            match Workspace.validatePolicy root with
            | Error message -> Assert.StartsWith("ARCH001:", message)
            | Ok() -> failwith "Checked project acquired an excluded FSharp project dependency.")

    [<Fact>]
    let ``initialization rejects a workspace with every FSharp project excluded`` () =
        withDirectory (fun root ->
            restoreConsumer root None "module App\nlet increment value = value + 1\n"

            File.WriteAllText(
                Path.Combine(root, "fdull.scope.json"),
                "{\"Version\":1,\"External\":[],\"ExcludedProjects\":[{\"File\":\"App.fsproj\",\"Reason\":\"This fixture would leave no checked project.\"}]}"
            )

            let error =
                Assert.Throws<InvalidOperationException>(fun () -> Project.initialize root |> ignore)

            Assert.StartsWith("ARCH002:", error.Message)
            Assert.False(File.Exists(Path.Combine(root, "fdull.json"))))

    [<Fact>]
    let ``CLI blocks unclassified trial automation even with zero diagnostics`` () =
        withDirectory (fun root ->
            prepare root
            let clean = runCli root [ "lint"; "." ]
            Assert.Equal(0, clean.ExitCode)
            Assert.StartsWith("FDull lint: PASS", clean.Output)
            Assert.DoesNotContain("verification completed", clean.Output)
            let verified = runCli root [ "verify"; "." ]
            Assert.Equal(0, verified.ExitCode)

            Assert.StartsWith(
                "FDull verify: PASS — verification completed for the included projects.",
                verified.Output
            )

            Directory.CreateDirectory(Path.Combine(root, ".trial")) |> ignore
            File.WriteAllText(Path.Combine(root, ".trial/check.py"), "print('checked')\n")

            let blocked = runCli root [ "lint"; "." ]
            Assert.Equal(3, blocked.ExitCode)
            Assert.StartsWith("FDull lint: BLOCKED", blocked.Output)
            Assert.Contains("0 reported diagnostics; findings are partial", blocked.Output)
            Assert.Contains("no clean result", blocked.Output)
            Assert.Contains("rerun fdull lint", blocked.Output)
            Assert.Contains("ARCH003:", blocked.Error)
            Assert.Contains(".trial/check.py", blocked.Error)
            Assert.DoesNotContain("PASS", blocked.Output + blocked.Error)

            let json = runCli root [ "lint"; "."; "--format"; "json" ]
            Assert.Equal(3, json.ExitCode)
            Assert.StartsWith("FDull lint: BLOCKED", json.Error)
            let report = Codec.decode<WorkspaceReport> json.Output
            Assert.False(report.Complete)
            Assert.Equal(3, report.ExitCode)
            Assert.Empty(report.Projects)
            Assert.Contains(report.Errors, fun error -> error.Contains("ARCH003:"))

            let sarif = runCli root [ "lint"; "."; "--format"; "sarif" ]
            Assert.Equal(3, sarif.ExitCode)
            Assert.StartsWith("FDull lint: BLOCKED", sarif.Error)
            Assert.Contains("ARCH003:", sarif.Error)
            use document = JsonDocument.Parse sarif.Output
            let run = document.RootElement.GetProperty("runs").[0]
            let invocation = run.GetProperty("invocations").[0]
            Assert.False(invocation.GetProperty("executionSuccessful").GetBoolean())
            Assert.Equal(3, invocation.GetProperty("exitCode").GetInt32())
            Assert.Equal(0, run.GetProperty("results").GetArrayLength())

            let failed = runCli root [ "verify"; "." ]
            Assert.Equal(3, failed.ExitCode)
            Assert.StartsWith("FDull: BLOCKED", failed.Error)
            Assert.Contains("ARCH003:", failed.Error)
            Assert.Contains(".trial/check.py", failed.Error)
            Assert.DoesNotContain("PASS", failed.Output + failed.Error))

    [<Fact>]
    let ``CLI distinguishes complete violations from partial compiler findings`` () =
        withDirectory (fun root ->
            prepare root
            let policy = read root
            let source = Path.Combine(root, "App.fs")
            File.WriteAllText(source, "module App\nlet first values = List.head values\n")

            let reviewed =
                { policy with
                    Sources =
                        policy.Sources
                        |> List.map (fun entry ->
                            { entry with
                                Digest = SafetyPolicy.fileDigest source }) }

            File.WriteAllText(Path.Combine(root, "fdull.json"), Codec.encode reviewed)
            let violations = runCli root [ "lint"; "." ]
            Assert.Equal(1, violations.ExitCode)
            Assert.StartsWith("FDull lint: FAIL — complete analysis found violations", violations.Output)
            Assert.Contains("ERR006:", violations.Output)
            Assert.DoesNotContain("BLOCKED", violations.Output)

            File.WriteAllText(source, "module App\nlet value: int = \"wrong\"\n")

            let compilerFailure =
                { reviewed with
                    Sources =
                        reviewed.Sources
                        |> List.map (fun entry ->
                            { entry with
                                Digest = SafetyPolicy.fileDigest source }) }

            File.WriteAllText(Path.Combine(root, "fdull.json"), Codec.encode compilerFailure)
            let blocked = runCli root [ "lint"; "." ]
            Assert.Equal(3, blocked.ExitCode)
            Assert.StartsWith("FDull lint: BLOCKED", blocked.Output)
            Assert.Contains("findings are partial", blocked.Output)
            Assert.Contains("FS0001:", blocked.Output)
            Assert.DoesNotContain("PASS", blocked.Output + blocked.Error))

    [<Fact>]
    let ``timings write only to stderr and leave reports and exit codes unchanged`` () =
        withDirectory (fun root ->
            prepare root

            for command, format in [ "lint", "json"; "lint", "text"; "review", "json" ] do
                let plain = runCli root [ command; "."; "--format"; format; "--no-cache" ]

                let timed =
                    runCli root [ command; "."; "--timings"; "--no-cache"; "--format"; format ]

                Assert.Equal(0, plain.ExitCode)
                Assert.Equal(plain.ExitCode, timed.ExitCode)
                Assert.Equal(plain.Output, timed.Output)
                Assert.DoesNotContain("FDull timing:", plain.Output + plain.Error)
                Assert.Matches("FDull timing: App.fsproj export=\\d+ms worker=\\d+ms validation=\\d+ms", timed.Error)
                Assert.Matches("FDull timing: total=\\d+ms projects=1", timed.Error)

            for invalid in
                [ [ "lint"; "."; "--timings"; "--timings" ]
                  [ "lint"; "."; "--no-cache"; "--no-cache" ]
                  [ "lint"; "."; "--format"; "json"; "--format"; "text" ]
                  [ "review"; "."; "--format"; "sarif" ]
                  [ "lint"; "."; "--unknown" ]
                  [ "lint"; "--timings" ] ] do
                Assert.Equal(2, (runCli root invalid).ExitCode))

    /// A counter with one local mutation, which `PURE` reports and `ADAPTER` can approve as a rule.
    let private counter name =
        "module "
        + name
        + "\nlet count () =\n    let mutable total = 0\n    total <- total + 1\n    total\n"

    let private increment name =
        "module " + name + "\nlet increment value = value + 1\n"

    /// Initialize a Release-built workspace where App references Shared and Other stands alone.
    let private restoreProjects root app other =
        restoreConsumer root None app

        for name, source in [ "Shared", increment "Shared"; "Other", other ] do
            Directory.CreateDirectory(Path.Combine(root, name)) |> ignore

            File.WriteAllText(
                Path.Combine(root, name, name + ".fsproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><Compile Include=\""
                + name
                + ".fs\"/></ItemGroup></Project>"
            )

            File.WriteAllText(Path.Combine(root, name, name + ".fs"), source)

        File.WriteAllText(
            Path.Combine(root, "App.fsproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"Shared/Shared.fsproj\"/><Compile Include=\"App.fs\"/></ItemGroup></Project>"
        )

        for project in [ "App.fsproj"; "Other/Other.fsproj" ] do
            run root [ "restore"; project; "-m:1"; "-nr:false" ]

            run
                root
                [ "build"
                  project
                  "-c"
                  "Release"
                  "--no-restore"
                  "-m:1"
                  "--disable-build-servers" ]

        Project.initialize root |> ignore

    let private approval file owner kind identity =
        { File = file
          Owner = owner
          Kind = kind
          Identity = identity
          Reason = "Fixture approval keyed to one project's source." }

    /// The initial policy with `adapters` in ADAPTER and the counters' rule uses approved, so lint is clean.
    let private adapterPolicy (initial: WorkspacePolicyDocument) adapters =
        { initial with
            Projects =
                initial.Projects
                |> List.map (fun project ->
                    if Set.contains project.File adapters then
                        { project with Profile = "ADAPTER" }
                    else
                        project)
            Sources =
                initial.Sources
                |> List.map (fun source ->
                    if Set.contains source.Project adapters then
                        { source with Profile = "ADAPTER" }
                    else
                        source)
            Capabilities =
                [ for file, owner in [ "App.fs", "App.count"; "Other/Other.fs", "Other.count" ] do
                      for identity in [ "MUT001 | "; "MUT001 | total"; "MUT002 | " ] do
                          approval file owner "rule" identity ] }

    [<Fact>]
    let ``job count leaves reports and failure positions unchanged`` () =
        withDirectory (fun root ->
            restoreProjects root (counter "App") (increment "Other")

            let compare expected marker =
                for command in [ "lint"; "review" ] do
                    let one =
                        runCli root [ command; "."; "--format"; "json"; "--jobs"; "1"; "--no-cache" ]

                    let four =
                        runCli root [ command; "."; "--jobs"; "4"; "--no-cache"; "--format"; "json" ]

                    Assert.True(one.ExitCode = expected, one.Error + one.Output)
                    Assert.Equal(one.ExitCode, four.ExitCode)
                    Assert.Equal(one.Output, four.Output)
                    Assert.Contains(marker, one.Output)

            compare 1 "MUT001"
            let other = Path.Combine(root, "Other/Other.fsproj")

            File.WriteAllText(
                other,
                "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><Compile Include=\"Other.fs\"/></ItemGroup>"
                + "<Target Name=\"FixtureExportFailure\" BeforeTargets=\"CoreCompile\" Condition=\"'$(FDullExport)' == 'true'\">"
                + "<Error Text=\"Fixture export failure.\"/></Target></Project>"
            )

            let current = read root

            File.WriteAllText(
                Path.Combine(root, "fdull.json"),
                Codec.encode
                    { current with
                        Inputs =
                            current.Inputs
                            |> List.map (fun input ->
                                if input.File = "Other/Other.fsproj" then
                                    { input with
                                        Digest = SafetyPolicy.fileDigest other }
                                else
                                    input) }
            )

            compare 3 "Other/Other.fsproj: "

            for invalid in
                [ [ "lint"; "."; "--jobs"; "0" ]
                  [ "lint"; "."; "--jobs"; "-1" ]
                  [ "lint"; "."; "--jobs"; "+2" ]
                  [ "lint"; "."; "--jobs"; "65" ]
                  [ "review"; "."; "--jobs"; "two" ]
                  [ "review"; "."; "--jobs" ]
                  [ "review"; "."; "--jobs"; "1"; "--jobs"; "2" ] ] do
                Assert.Equal(2, (runCli root invalid).ExitCode))

    /// Probes for graph fixtures. While `bin/probe` exists, a project touches `bin/probe/<name>.start`
    /// when its first export starts and `bin/probe/<name>.end` when its rebuild ends. A `bin/mutate-<name>`
    /// file names a source that the project's rebuild appends to. `bin` is outside the policy inventory.
    let private graphTargets =
        "<Project>"
        + "<Target Name=\"FixtureStart\" BeforeTargets=\"CoreCompile\" Condition=\"'$(FDullExport)' == 'true' And Exists('$(MSBuildThisFileDirectory)bin/probe') And !Exists('$(MSBuildThisFileDirectory)bin/probe/$(MSBuildProjectName).start')\">"
        + "<Touch Files=\"$(MSBuildThisFileDirectory)bin/probe/$(MSBuildProjectName).start\" AlwaysCreate=\"true\"/></Target>"
        + "<Target Name=\"FixtureEnd\" AfterTargets=\"Rebuild\" Condition=\"Exists('$(MSBuildThisFileDirectory)bin/probe')\">"
        + "<Touch Files=\"$(MSBuildThisFileDirectory)bin/probe/$(MSBuildProjectName).end\" AlwaysCreate=\"true\"/></Target>"
        + "<Target Name=\"FixtureMutate\" AfterTargets=\"Rebuild\" Condition=\"Exists('$(MSBuildThisFileDirectory)bin/mutate-$(MSBuildProjectName)')\">"
        + "<ReadLinesFromFile File=\"$(MSBuildThisFileDirectory)bin/mutate-$(MSBuildProjectName)\"><Output TaskParameter=\"Lines\" PropertyName=\"FixtureSource\"/></ReadLinesFromFile>"
        + "<WriteLinesToFile File=\"$(MSBuildThisFileDirectory)$(FixtureSource)\" Lines=\"// Changed by $(MSBuildProjectName).\"/></Target>"
        + "</Project>"

    /// Initialize a Release-built workspace of `projects`: a name, the names it references and extra project
    /// XML, references first. Each project lives in `<name>/<name>.fsproj` and shares `graphTargets`.
    let private restoreGraph root (projects: (string * string list * string) list) =
        writeConsumerSettings root
        File.WriteAllText(Path.Combine(root, "Directory.Build.targets"), graphTargets)

        for name, references, extra in projects do
            Directory.CreateDirectory(Path.Combine(root, name)) |> ignore

            File.WriteAllText(
                Path.Combine(root, name, name + ".fsproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\">"
                + extra
                + "<ItemGroup>"
                + (references
                   |> List.map (fun reference ->
                       "<ProjectReference Include=\"../" + reference + "/" + reference + ".fsproj\"/>")
                   |> String.concat "")
                + "<Compile Include=\""
                + name
                + ".fs\"/></ItemGroup></Project>"
            )

            // Edges come from the project references alone; a use of a referenced API needs its own approval.
            File.WriteAllText(Path.Combine(root, name, name + ".fs"), "module " + name + "\nlet value = 1\n")

        for name, _, _ in projects do
            let project = name + "/" + name + ".fsproj"
            run root [ "restore"; project; "-m:1"; "-nr:false" ]

            run
                root
                [ "build"
                  project
                  "-c"
                  "Release"
                  "--no-restore"
                  "-m:1"
                  "--disable-build-servers" ]

        Project.initialize root |> ignore

    /// B and C reference A; D references B and C.
    let private diamond =
        [ "A", [], ""; "B", [ "A" ], ""; "C", [ "A" ], ""; "D", [ "B"; "C" ], "" ]

    /// A fresh `bin/probe` directory, and a reader of each project's recorded start and rebuild end.
    let private probe root =
        let directory = Path.Combine(root, "bin", "probe")

        if Directory.Exists directory then
            Directory.Delete(directory, true)

        Directory.CreateDirectory directory |> ignore

        fun name ->
            File.GetLastWriteTimeUtc(Path.Combine(directory, name + ".start")),
            File.GetLastWriteTimeUtc(Path.Combine(directory, name + ".end"))

    /// A verify report without reference digests. F# reference assemblies differ on every rebuild of unchanged
    /// source, so consecutive runs record new digests for each consumer's references whatever the job count.
    let private comparable (report: WorkspaceReport) =
        Codec.encode
            { report with
                Projects =
                    report.Projects
                    |> List.map (fun project ->
                        { project with
                            Report =
                                { project.Report with
                                    References =
                                        project.Report.References
                                        |> List.map (fun reference -> { reference with Digest = "" }) } }) }

    [<Fact>]
    let ``verify reports a diamond identically for every job count and waits for references`` () =
        withDirectory (fun root ->
            restoreGraph root diamond

            let verify jobs =
                let span = probe root
                let report, (timings: VerifyTiming list, _) = Project.verifyTimed jobs root
                let spans = diamond |> List.map (fun (name, _, _) -> name, span name) |> Map.ofList
                report, timings, spans

            let one, timings, sequential = verify 1
            let four, _, concurrent = verify 4
            Assert.True(one.Complete && one.ExitCode = 0, Codec.encode one)
            Assert.Equal(comparable one, comparable four)
            Assert.Empty one.Sequential

            Assert.Equal<string list>(
                [ for name in [ "A"; "B"; "C"; "D" ] -> Path.Combine(root, name, name + ".fsproj") ],
                one.Projects |> List.map _.Project
            )

            Assert.Equal<string list>(
                [ "A/A.fsproj"; "B/B.fsproj"; "C/C.fsproj"; "D/D.fsproj" ],
                timings |> List.map _.Project
            )

            Assert.True(
                timings
                |> List.forall (fun timing -> timing.AnalyzeMilliseconds > 0L && timing.RebuildMilliseconds > 0L)
            )

            for spans in [ sequential; concurrent ] do
                for name, references, _ in diamond do
                    for reference in references do
                        Assert.True(snd spans[reference] < fst spans[name], name + " started before " + reference)

            let overlap (spans: Map<string, DateTime * DateTime>) =
                fst spans["B"] < snd spans["C"] && fst spans["C"] < snd spans["B"]

            Assert.False(overlap sequential)
            Assert.True(overlap concurrent))

    [<Fact>]
    let ``verify fails identically for every job count and a concurrent source change never passes`` () =
        withDirectory (fun root ->
            restoreGraph root diamond
            let source name = Path.Combine(root, name, name + ".fs")

            let originals =
                diamond |> List.map (fun (name, _, _) -> name, File.ReadAllText(source name))

            let verify jobs =
                for name, text in originals do
                    File.WriteAllText(source name, text)

                Project.verifyTimed jobs root |> fst

            let mutate project target =
                let flag = Path.Combine(root, "bin", "mutate-" + project)
                Directory.CreateDirectory(Path.Combine(root, "bin")) |> ignore
                File.WriteAllText(flag, target + "/" + target + ".fs\n")
                flag

            // C's rebuild changes C's own source, so C fails and the report stops at C.
            let own = mutate "C" "C"
            let failures = [ for jobs in [ 1; 2; 4 ] -> comparable (verify jobs) ]
            Assert.Single(List.distinct failures) |> ignore
            let failure = Codec.decode<WorkspaceReport> failures.Head
            Assert.Equal(3, failure.ExitCode)

            Assert.Equal<string list>(
                [ Path.Combine(root, "A", "A.fsproj"); Path.Combine(root, "B", "B.fsproj") ],
                failure.Projects |> List.map _.Project
            )

            Assert.Equal<string list>(
                [ "BUILD004: Source/reference inputs changed while compiling C/C.fsproj" ],
                failure.Errors
            )

            File.Delete own

            // D's rebuild changes A's source after every project's own check passed.
            let sink = mutate "D" "A"

            for jobs in [ 1; 4 ] do
                let error =
                    Assert.Throws<InvalidOperationException>(fun () -> verify jobs |> ignore)

                Assert.Equal("BUILD004: Source/reference inputs changed during compilation.", error.Message)

            File.Delete sink

            // B's rebuild changes C's source, before or while C is analyzed.
            mutate "B" "C" |> ignore

            for jobs in [ 1; 4 ] do
                let report = verify jobs
                Assert.True(report.ExitCode = 3, Codec.encode report)
                Assert.NotEmpty report.Errors)

    [<Fact>]
    let ``verify runs projects sharing an output directory one at a time and says why`` () =
        withDirectory (fun root ->
            let shared =
                "<PropertyGroup><OutDir>$(MSBuildThisFileDirectory)../bin/shared/</OutDir></PropertyGroup>"

            restoreGraph root [ "A", [], shared; "B", [], shared ]
            let span = probe root
            let timed = runCli root [ "verify"; "."; "--timings"; "--jobs"; "4" ]
            Assert.True(timed.ExitCode = 0, timed.Error + timed.Output)
            Assert.True(snd (span "A") < fst (span "B"), "B started before A finished")

            Assert.Contains(
                "Verify runs one project at a time: A/A.fsproj and B/B.fsproj share bin/shared.",
                timed.Output
            )

            for name in [ "A"; "B" ] do
                Assert.Matches(
                    "FDull timing: "
                    + name
                    + "/"
                    + name
                    + ".fsproj evaluate=\\d+ms restore=\\d+ms build=\\d+ms analyze=\\d+ms rebuild=\\d+ms reexport=\\d+ms",
                    timed.Error
                )

            Assert.Matches("FDull timing: final-validation=\\d+ms total=\\d+ms projects=2", timed.Error)
            let plain = runCli root [ "verify"; "." ]
            Assert.Equal(0, plain.ExitCode)
            Assert.Equal(timed.Output, plain.Output)
            Assert.DoesNotContain("FDull timing:", plain.Output + plain.Error)

            for invalid in
                [ [ "verify"; "."; "--jobs"; "0" ]
                  [ "verify"; "."; "--jobs"; "65" ]
                  [ "verify"; "."; "--jobs"; "1"; "--jobs"; "2" ]
                  [ "verify"; "."; "--timings"; "--timings" ]
                  [ "verify"; "."; "--no-cache" ]
                  [ "verify"; "."; "--format"; "text" ]
                  [ "verify"; "--jobs"; "2" ] ] do
                Assert.Equal(2, (runCli root invalid).ExitCode))

    [<Fact>]
    let ``policy slices analyze each project as the whole policy does`` () =
        withDirectory (fun root ->
            restoreProjects root (counter "App") (counter "Other")

            let reviewed =
                adapterPolicy (read root) (set [ "App.fsproj"; "Other/Other.fsproj" ])

            let projects = [ "Shared/Shared.fsproj"; "App.fsproj"; "Other/Other.fsproj" ]

            let analyze document =
                File.WriteAllText(Path.Combine(root, "fdull.json"), Codec.encode document)

                projects
                |> List.map (fun project ->
                    match Workspace.request root project with
                    | Error error -> failwith error
                    | Ok request ->
                        let expected =
                            { document with
                                Sources = document.Sources |> List.filter (fun source -> source.Project = project)
                                Capabilities =
                                    document.Capabilities
                                    |> List.filter (fun entry ->
                                        document.Sources
                                        |> List.exists (fun source ->
                                            source.File = entry.File && source.Project = project)) }

                        Assert.Equal(Codec.encode expected, Codec.encode (WorkspaceSlice.create project document))
                        Assert.Equal(WorkspaceSlice.digest project document, request.PolicySlice)
                        let sliced = WorkspaceEngine.check request
                        Assert.True(sliced.Report.Complete, Codec.encode sliced)
                        Assert.True(sliced.Report.ExitCode = 0, Codec.encode sliced.Report.Diagnostics)
                        Assert.Equal(request.PolicySlice, sliced.Report.Policy)
                        Assert.Equal(Codec.encode sliced, Codec.encode (WorkspaceEngine.checkWholePolicy request))

                        Assert.Equal(
                            Codec.encode (WorkspaceEngine.review request),
                            Codec.encode (WorkspaceEngine.reviewWholePolicy request)
                        )

                        request, sliced)

            let before = analyze reviewed

            let after =
                analyze
                    { reviewed with
                        Capabilities =
                            reviewed.Capabilities
                            @ [ approval
                                    "Other/Other.fs"
                                    "Other.count"
                                    "type"
                                    "System.Runtime, Version=10.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a | System.Uri" ] }

            match before, after with
            | [ shared; app; other ], [ sharedAfter; appAfter; otherAfter ] ->
                Assert.Equal<string list>(
                    [ "App.fs | App.count | rule | MUT001 | "
                      "App.fs | App.count | rule | MUT001 | total"
                      "App.fs | App.count | rule | MUT002 | " ],
                    (snd app).Uses
                    |> List.filter (fun item -> item.File = "App.fs")
                    |> List.map (fun item ->
                        item.File + " | " + item.Owner + " | " + item.Kind + " | " + item.Identity)
                )

                // Another project's capability changes neither the slice nor the report.
                for (request, report), (changedRequest, changedReport) in [ shared, sharedAfter; app, appAfter ] do
                    Assert.Equal(request.PolicySlice, changedRequest.PolicySlice)
                    Assert.Equal(Codec.encode report, Codec.encode changedReport)

                Assert.NotEqual<string>((fst other).PolicySlice, (fst otherAfter).PolicySlice)

                let mismatch =
                    WorkspaceEngine.check
                        { fst appAfter with
                            PolicySlice = (fst otherAfter).PolicySlice }

                Assert.False(mismatch.Report.Complete)
                Assert.Contains(mismatch.Report.Diagnostics, fun diagnostic -> diagnostic.Rule = "BUILD004")
            | _ -> failwith "Each policy analyzes the three fixture projects.")

    let private projectNames = [ "Other.fsproj"; "Shared.fsproj"; "App.fsproj" ]

    /// Lint JSON with its cache count and flags cleared, to compare runs across cache states.
    let private uncached (output: string) =
        let report = Codec.decode<WorkspaceReport> output

        Codec.encode
            { report with
                Cached = 0
                Projects = report.Projects |> List.map (fun project -> { project with Cached = false }) }

    let private reviewUncached (output: string) =
        let report = Codec.decode<WorkspaceReviewReport> output

        Codec.encode
            { report with
                Cached = 0
                Projects =
                    report.Projects
                    |> List.map (fun project ->
                        { project with
                            Analysis = { project.Analysis with Cached = false } }) }

    /// Project file names a review JSON report analyzed afresh instead of serving from cache.
    let private reviewAnalyzed (output: string) =
        (Codec.decode<WorkspaceReviewReport> output).Projects
        |> List.filter (fun project -> not project.Analysis.Cached)
        |> List.map (fun project ->
            Path.GetFileName project.Analysis.Project
            |> External.required "test.project-name")

    /// Project file names a lint JSON report analyzed afresh instead of serving from cache.
    let private analyzed (output: string) =
        (Codec.decode<WorkspaceReport> output).Projects
        |> List.filter (fun project -> not project.Cached)
        |> List.map (fun project -> Path.GetFileName project.Project |> External.required "test.project-name")

    let private cacheDirectory root = Path.Combine(root, ".fdull", "cache")

    /// Each cache entry's path by the file name of the project it stores.
    let private cacheEntries root =
        Directory.GetFiles(cacheDirectory root, "*.json")
        |> Array.map (fun path ->
            let entry = File.ReadAllText path |> Codec.decode<WorkspaceCacheEntry>

            (Path.GetFileName entry.Key.Request.Project
             |> External.required "test.project-name"),
            path)
        |> Map.ofArray

    let private cacheSnapshot root =
        Directory.GetFiles(cacheDirectory root, "*", SearchOption.AllDirectories)
        |> Array.sort
        |> Array.map (fun path -> path, File.ReadAllText path)
        |> List.ofArray

    /// Rewrite one stored worker report in place, keeping the entry's key.
    let private tamper path (change: WorkspaceProjectReport -> WorkspaceProjectReport) =
        let entry = File.ReadAllText path |> Codec.decode<WorkspaceCacheEntry>
        let report = change (Codec.decode<WorkspaceProjectReport> entry.Output)

        File.WriteAllText(
            path,
            Codec.encode
                { entry with
                    ExitCode = report.Report.ExitCode
                    Output = Codec.encode report }
        )

    /// A validly shaped clean report: the edit an entry would need to hide a violation.
    let private hideDiagnostics (report: WorkspaceProjectReport) =
        { report with
            Report =
                { report.Report with
                    ExitCode = 0
                    Diagnostics = [] } }

    [<Fact>]
    let ``result cache serves unchanged projects and re-analyzes exactly the changed ones`` () =
        withDirectory (fun root ->
            restoreProjects root (counter "App") (counter "Other")
            let policyPath = Path.Combine(root, "fdull.json")

            let update (change: WorkspacePolicyDocument -> WorkspacePolicyDocument) () =
                File.WriteAllText(policyPath, Codec.encode (change (read root)))

            update
                (fun policy ->
                    adapterPolicy policy (set [ "Shared/Shared.fsproj"; "App.fsproj"; "Other/Other.fsproj" ]))
                ()

            let lintAt cli =
                let result = runCliAt cli root [ "lint"; "."; "--format"; "json" ]
                Assert.True(List.contains result.ExitCode [ 0; 1; 3 ], result.Error + result.Output)
                result

            let lint () =
                lintAt (Path.Combine(cliDirectory, "FDull.Cli.dll"))

            let cold = lint ()
            Assert.True(cold.ExitCode = 0, cold.Error + cold.Output)
            Assert.Equal<string list>(projectNames, analyzed cold.Output)
            Assert.Equal(0, (Codec.decode<WorkspaceReport> cold.Output).Cached)
            let warm = lint ()
            Assert.Equal(0, warm.ExitCode)
            Assert.Empty(analyzed warm.Output)
            Assert.Equal(3, (Codec.decode<WorkspaceReport> warm.Output).Cached)
            Assert.Equal(uncached cold.Output, uncached warm.Output)
            Assert.Contains("3 projects (3 from cache); 0 reported diagnostics.", (runCli root [ "lint"; "." ]).Output)

            let reviewCold = runCli root [ "review"; "."; "--format"; "json" ]
            let reviewWarm = runCli root [ "review"; "."; "--format"; "json" ]
            Assert.True(reviewCold.ExitCode = 0, reviewCold.Error + reviewCold.Output)
            Assert.Equal(0, reviewWarm.ExitCode)
            Assert.Equal(0, (Codec.decode<WorkspaceReviewReport> reviewCold.Output).Cached)
            Assert.Equal(3, (Codec.decode<WorkspaceReviewReport> reviewWarm.Output).Cached)
            Assert.Equal(reviewUncached reviewCold.Output, reviewUncached reviewWarm.Output)
            Assert.Contains("3 projects (3 from cache).", (runCli root [ "review"; "." ]).Output)

            let review () =
                let result = runCli root [ "review"; "."; "--format"; "json" ]
                Assert.True(List.contains result.ExitCode [ 0; 1 ], result.Error + result.Output)
                reviewAnalyzed result.Output

            let expectReview projects change =
                change ()
                Assert.Equal<string list>(projects, review ())

            let expect projects change =
                expectReview projects change
                Assert.Equal<string list>(projects, analyzed (lint ()).Output)

            let shared = Path.Combine(root, "Shared", "Shared.fs")

            let setDigest file digest =
                update (fun policy ->
                    { policy with
                        Sources =
                            policy.Sources
                            |> List.map (fun source ->
                                if source.File = file then
                                    { source with Digest = digest }
                                else
                                    source) })

            let rebuild (source: string) () =
                File.WriteAllText(shared, source)

                run
                    root
                    [ "build"
                      "App.fsproj"
                      "-c"
                      "Release"
                      "--no-restore"
                      "-m:1"
                      "--disable-build-servers" ]

                setDigest "Shared/Shared.fs" (SafetyPolicy.fileDigest shared) ()

            // A's policy slice: one of its capabilities, then one of its source fingerprints.
            expect
                [ "Shared.fsproj" ]
                (update (fun policy ->
                    { policy with
                        Capabilities =
                            policy.Capabilities
                            @ [ approval
                                    "Shared/Shared.fs"
                                    "Shared.increment"
                                    "type"
                                    "System.Runtime, Version=10.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a | System.Uri" ] }))

            // Lint blocks the whole workspace on a stale fingerprint; review reports it per source.
            expectReview [ "Shared.fsproj" ] (setDigest "Shared/Shared.fs" ("sha256:" + String('0', 64)))
            // F# reference assemblies carry optimization data and a fresh MVID per compile, so even a
            // private body change reaches B's key. Over-keying costs time, never correctness.
            expect [ "Shared.fsproj"; "App.fsproj" ] (rebuild "module Shared\nlet increment value = value + 2\n")
            // A public signature changes the reference assembly B compiles against.
            expect
                [ "Shared.fsproj"; "App.fsproj" ]
                (rebuild "module Shared\nlet increment value = value + 2\nlet twice value = value * 2\n")

            // A compiler argument lives in a pinned build file, and `Inputs` are part of every slice.
            let sharedProject = Path.Combine(root, "Shared", "Shared.fsproj")

            expect projectNames (fun () ->
                File.WriteAllText(
                    sharedProject,
                    "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><DefineConstants>$(DefineConstants);NULLABLE</DefineConstants></PropertyGroup>"
                    + "<ItemGroup><Compile Include=\"Shared.fs\"/></ItemGroup></Project>"
                )

                update
                    (fun policy ->
                        { policy with
                            Inputs =
                                policy.Inputs
                                |> List.map (fun input ->
                                    if input.File = "Shared/Shared.fsproj" then
                                        { input with
                                            Digest = SafetyPolicy.fileDigest sharedProject }
                                    else
                                        input) })
                    ())

            expect
                projectNames
                (update (fun policy ->
                    { policy with
                        Domains = [ "Fixture.Domain" ] }))

            expect
                projectNames
                (update (fun policy ->
                    { policy with
                        Constructors = [ "Fixture.Constructor" ] }))

            // A new FDull build: the same CLI with one more file in its worker closure.
            let copy = root + "-cli"

            try
                for file in Directory.GetFiles(cliDirectory, "*", SearchOption.AllDirectories) do
                    let target = Path.Combine(copy, Path.GetRelativePath(cliDirectory, file))

                    Directory.CreateDirectory(
                        Path.GetDirectoryName target
                        |> FDull.Transport.External.required "test.cli-copy"
                    )
                    |> ignore

                    File.Copy(file, target)

                File.WriteAllText(Path.Combine(copy, "safety-worker", "fixture-build.txt"), "another build")
                let rebuilt = lintAt (Path.Combine(copy, "FDull.Cli.dll"))
                Assert.Equal<string list>(projectNames, analyzed rebuilt.Output)
                Assert.Empty(analyzed (lint ()).Output)
            finally
                if Directory.Exists copy then
                    Directory.Delete(copy, true))

    [<Fact>]
    let ``damaged, misplaced or inconsistent cache entries are re-analyzed`` () =
        withDirectory (fun root ->
            restoreProjects root (counter "App") (counter "Other")
            let cold = runCli root [ "lint"; "."; "--format"; "json" ]
            Assert.True(cold.ExitCode = 1, cold.Error + cold.Output)
            Assert.Equal(3, (cacheEntries root).Count)

            let damage projects (change: Map<string, string> -> unit) =
                change (cacheEntries root)
                let result = runCli root [ "lint"; "."; "--format"; "json" ]
                Assert.Equal(1, result.ExitCode)
                Assert.Equal(uncached cold.Output, uncached result.Output)
                Assert.Equal<string list>(projects, analyzed result.Output)

            damage projectNames (fun entries ->
                let text = File.ReadAllText entries["Shared.fsproj"]
                File.WriteAllText(entries["Shared.fsproj"], text.Substring(0, text.Length / 2))
                File.WriteAllBytes(entries["Other.fsproj"], [| 0uy; 255uy; 123uy |])

                // A hit passes the same BUILD004 comparison as a fresh result.
                tamper entries["App.fsproj"] (fun report ->
                    { report with
                        Report =
                            { report.Report with
                                Policy = "sha256:" + String('0', 64) } }))

            // An intact entry stored under another project's key.
            damage [ "Other.fsproj" ] (fun entries -> File.Copy(entries["App.fsproj"], entries["Other.fsproj"], true))

            let warm = runCli root [ "lint"; "."; "--format"; "json" ]
            Assert.Empty(analyzed warm.Output)
            Assert.Equal(uncached cold.Output, uncached warm.Output))

    [<Fact>]
    let ``parallel runs share the cache safely and cache clear removes only the cache`` () =
        withDirectory (fun root ->
            restoreProjects root (increment "App") (increment "Other")

            let runs =
                [ for _ in 1..2 -> async { return runCli root [ "lint"; "."; "--format"; "json"; "--jobs"; "2" ] } ]
                |> Async.Parallel
                |> Async.RunSynchronously

            for result in runs do
                Assert.True(result.ExitCode = 0, result.Error + result.Output)
                Assert.Equal(uncached runs[0].Output, uncached result.Output)

            Assert.Equal(3, (cacheEntries root).Count)
            Assert.Empty(Directory.GetFiles(cacheDirectory root, "*.tmp"))
            let entries = cacheEntries root
            let warm = runCli root [ "lint"; "."; "--format"; "json" ]

            Assert.True(
                (analyzed warm.Output).IsEmpty,
                String.Join(", ", analyzed warm.Output)
                + " missed; entries before: "
                + Codec.encode entries
                + "; after: "
                + Codec.encode (cacheEntries root)
            )

            Assert.Equal(uncached runs[0].Output, uncached warm.Output)

            let cleared = runCli root [ "cache"; "clear"; "." ]
            Assert.Equal(0, cleared.ExitCode)
            Assert.False(Directory.Exists(cacheDirectory root))
            Assert.True(File.Exists(Path.Combine(root, "fdull.json")))
            Assert.Equal<string list>(projectNames, analyzed (runCli root [ "lint"; "."; "--format"; "json" ]).Output)

            // A linked `.fdull` is neither cleared nor written through.
            let outside = root + "-outside"

            try
                Directory.CreateDirectory(Path.Combine(outside, "cache")) |> ignore
                File.WriteAllText(Path.Combine(outside, "cache", "keep.json"), "{}")
                Directory.Delete(Path.Combine(root, ".fdull"), true)
                Directory.CreateSymbolicLink(Path.Combine(root, ".fdull"), outside) |> ignore
                let refused = runCli root [ "cache"; "clear"; "." ]
                Assert.Equal(3, refused.ExitCode)
                Assert.Contains("symbolic link", refused.Error)
                let linked = runCli root [ "lint"; "."; "--format"; "json" ]
                Assert.Equal(0, linked.ExitCode)
                Assert.Equal<string list>(projectNames, analyzed linked.Output)

                Assert.Equal<string list>(
                    [ "keep.json" ],
                    Directory.GetFiles(outside, "*", SearchOption.AllDirectories)
                    |> Array.map (fun path -> Path.GetFileName path |> FDull.Transport.External.required "test.file")
                    |> List.ofArray
                )

                for invalid in [ [ "cache"; "clear" ]; [ "cache"; "list"; "." ] ] do
                    Assert.Equal(2, (runCli root invalid).ExitCode)
            finally
                Directory.Delete(outside, true))

    [<Fact>]
    let ``verify neither reads nor writes the result cache`` () =
        withDirectory (fun root ->
            restoreProjects root (counter "App") (increment "Other")
            let lint = runCli root [ "lint"; "."; "--format"; "json" ]
            Assert.True(lint.ExitCode = 1, lint.Error + lint.Output)

            // The cache is trusted local state: lint believes an edited entry that stays consistent.
            // Verify analyzes from scratch regardless.
            let entries = cacheEntries root
            tamper entries["App.fsproj"] hideDiagnostics
            let trusted = runCli root [ "lint"; "." ]
            Assert.Equal(0, trusted.ExitCode)
            Assert.Contains("3 projects (3 from cache); 0 reported diagnostics.", trusted.Output)
            let before = cacheSnapshot root

            let verify = runCli root [ "verify"; "." ]
            Assert.Equal(1, verify.ExitCode)
            Assert.Contains("MUT001", verify.Output)
            Assert.Contains("3 projects; ", verify.Output)
            Assert.Equal<(string * string) list>(before, cacheSnapshot root)

            let fresh = runCli root [ "lint"; "."; "--no-cache" ]
            Assert.Equal(1, fresh.ExitCode)
            Assert.Contains("MUT001", fresh.Output)
            Assert.Equal<(string * string) list>(before, cacheSnapshot root))

    [<Fact>]
    let ``unused permissions are reported while other projects come from cache`` () =
        withDirectory (fun root ->
            restoreProjects root (counter "App") (counter "Other")
            let policyPath = Path.Combine(root, "fdull.json")

            File.WriteAllText(
                policyPath,
                Codec.encode (adapterPolicy (read root) (set [ "App.fsproj"; "Other/Other.fsproj" ]))
            )

            let cold = runCli root [ "lint"; "."; "--format"; "json" ]
            Assert.True(cold.ExitCode = 0, cold.Error + cold.Output)
            let app = Path.Combine(root, "App.fs")
            File.WriteAllText(app, increment "App")
            let current = read root

            File.WriteAllText(
                policyPath,
                Codec.encode
                    { current with
                        Sources =
                            current.Sources
                            |> List.map (fun source ->
                                if source.File = "App.fs" then
                                    { source with
                                        Digest = SafetyPolicy.fileDigest app }
                                else
                                    source) }
            )

            let result = runCli root [ "lint"; "."; "--format"; "json" ]
            Assert.Equal(3, result.ExitCode)
            Assert.Equal<string list>([ "App.fsproj" ], analyzed result.Output)

            Assert.Equal<string list>(
                [ for identity in [ "MUT001 | "; "MUT001 | total"; "MUT002 | " ] ->
                      "BUILD005: Unused protected capability: App.fs | App.count | rule | " + identity ],
                (Codec.decode<WorkspaceReport> result.Output).Errors
            ))

    [<Fact>]
    let ``attribute contracts bind resolved enum fields and named literal values`` () =
        withDirectory (fun root ->
            prepare root
            let policy = read root
            let sourcePath = Path.Combine(root, "App.fs")
            let policyPath = Path.Combine(root, "fdull.json")

            let source =
                "module App\n"
                + "[<System.AttributeUsage(System.AttributeTargets.All, AllowMultiple=true)>]\n"
                + "type MarkerAttribute() = inherit System.Attribute()\n"
                + "[<System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)>]\n"
                + "let increment value = value + 1\n"

            File.WriteAllText(sourcePath, source)
            let before = Workspace.review root
            Assert.True(before.AnalysisComplete, WorkspaceReview.format before)
            let attributes = before.Items |> List.filter (fun item -> item.Kind = "attribute")
            Assert.Equal(2, attributes.Length)
            Assert.All(attributes, fun item -> Assert.Contains("enum:", item.Identity))
            Assert.Contains(attributes, fun item -> item.Identity.Contains("named:"))

            let reviewed =
                { policy with
                    Capabilities =
                        attributes
                        |> List.map (fun item ->
                            { File = item.File
                              Owner = item.Owner
                              Kind = "attribute"
                              Identity = item.Identity
                              Reason = "This regression approves only the displayed enum and named literal values." }) }

            File.WriteAllText(policyPath, Codec.encode reviewed)
            let after = Workspace.review root
            Assert.True(after.AnalysisComplete, WorkspaceReview.format after)

            Assert.DoesNotContain(
                after.Items,
                fun item -> item.Evidence |> List.exists (fun diagnostic -> diagnostic.Rule = "ATTR001")
            )

            File.WriteAllText(
                sourcePath,
                source
                    .Replace("AllowMultiple=true", "AllowMultiple=false")
                    .Replace("MethodImplOptions.NoInlining", "MethodImplOptions.NoOptimization")
            )

            let changed = Workspace.review root
            Assert.True(changed.AnalysisComplete, WorkspaceReview.format changed)

            let changedAttributes =
                changed.Items |> List.filter (fun item -> item.Kind = "attribute")

            Assert.Equal(2, changedAttributes.Length)

            Assert.All(
                changedAttributes,
                fun item -> Assert.DoesNotContain(attributes, fun original -> original.Identity = item.Identity)
            )

            File.WriteAllText(
                sourcePath,
                source.Replace(
                    "MethodImplOptions.NoInlining",
                    "MethodImplOptions.NoInlining ||| System.Runtime.CompilerServices.MethodImplOptions.NoOptimization"
                )
            )

            let expression = Workspace.review root
            Assert.True(expression.AnalysisComplete, WorkspaceReview.format expression)

            Assert.Contains(
                expression.Items,
                fun item ->
                    item.Kind = "source"
                    && item.Evidence |> List.exists (fun diagnostic -> diagnostic.Rule = "ATTR001")
            ))

    [<Theory>]
    [<InlineData("10.0.100")>]
    [<InlineData("10.1.201")>]
    let ``attribute contracts bind typeof nameof and ordered literal arrays`` coreVersion =
        withDirectory (fun root ->
            restoreConsumer root (Some coreVersion) "module App\nlet increment value = value + 1\n"

            run
                root
                [ "build"
                  "App.fsproj"
                  "-c"
                  "Release"
                  "--no-restore"
                  "-m:1"
                  "--disable-build-servers" ]

            Project.initialize root |> ignore
            let policy = read root
            let sourcePath = Path.Combine(root, "App.fs")
            let policyPath = Path.Combine(root, "fdull.json")

            let source =
                "module App\n"
                + "open System\n"
                + "[<Literal>]\nlet maxTest = 100\n"
                + "type First = class end\n"
                + "type Second = class end\n"
                + "type Cases = static member One = 1; static member Two = 2\n"
                + "type TypeMarkerAttribute(_value: Type) = inherit Attribute()\n"
                + "type NameMarkerAttribute(_value: string) = inherit Attribute()\n"
                + "type ArrayMarkerAttribute() =\n"
                + "    inherit Attribute()\n"
                + "    member val Arbitrary: Type array = [||] with get, set\n"
                + "    member val MaxTest = 0 with get, set\n"
                + "[<TypeMarker(typeof<First>)>] type TypeTarget = class end\n"
                + "[<NameMarker(nameof Cases.One)>] type NameTarget = class end\n"
                + "[<ArrayMarker(Arbitrary = [| typeof<First>; typeof<Second> |], MaxTest = maxTest)>]\n"
                + "type ArrayTarget = class end\n"

            File.WriteAllText(sourcePath, source)

            let reviewedAttributes () =
                let report = Workspace.review root
                Assert.True(report.AnalysisComplete, WorkspaceReview.format report)

                report.Items
                |> List.filter (fun item ->
                    item.Kind = "attribute"
                    && (item.Owner = "App.TypeTarget"
                        || item.Owner = "App.NameTarget"
                        || item.Owner = "App.ArrayTarget"))

            let initial = reviewedAttributes ()
            Assert.Equal(3, initial.Length)
            Assert.Contains(initial, fun item -> item.Identity.Contains("typeof:"))
            Assert.Contains(initial, fun item -> item.Identity.Contains("nameof:"))

            Assert.Contains(
                initial,
                fun item -> item.Identity.Contains("array:") && item.Identity.Contains("literal:")
            )

            let approved =
                { policy with
                    Capabilities =
                        initial
                        |> List.map (fun item ->
                            { File = item.File
                              Owner = item.Owner
                              Kind = "attribute"
                              Identity = item.Identity
                              Reason =
                                "The resolved local type, named member and literal array are fixed by this regression." }) }

            File.WriteAllText(policyPath, Codec.encode approved)
            Assert.Empty(reviewedAttributes ())

            File.WriteAllText(
                sourcePath,
                source
                    .Replace("TypeMarker(typeof<First>)", "TypeMarker(typeof<Second>)")
                    .Replace("nameof Cases.One", "nameof Cases.Two")
                    .Replace("let maxTest = 100", "let maxTest = 101")
                    .Replace("[| typeof<First>; typeof<Second> |]", "[| typeof<Second>; typeof<First> |]")
            )

            let changed = reviewedAttributes ()
            Assert.Equal(3, changed.Length)

            Assert.All(
                changed,
                fun item -> Assert.DoesNotContain(initial, fun original -> original.Identity = item.Identity)
            )

            File.WriteAllText(sourcePath, source.Replace("MaxTest = maxTest", "MaxTest = 50 + 50"))
            let computed = Workspace.review root

            Assert.True(
                computed.Items
                |> List.exists (fun item ->
                    item.Kind = "source"
                    && item.Evidence |> List.exists (fun diagnostic -> diagnostic.Rule = "ATTR001"))
                || not computed.AnalysisComplete,
                WorkspaceReview.format computed
            ))

    [<Fact>]
    let ``advisory review groups actual findings and preserves stale source rejection`` () =
        withDirectory (fun root ->
            prepare root
            let policy = read root

            let reviewed =
                { policy with
                    Projects =
                        policy.Projects
                        |> List.map (fun project -> { project with Profile = "ADAPTER" })
                    Sources = policy.Sources |> List.map (fun source -> { source with Profile = "ADAPTER" })
                    Capabilities =
                        [ { File = "App.fs"
                            Owner = "App.forbidden"
                            Kind = "profile"
                            Identity = "PURE"
                            Reason = "This workflow remains pure and cannot admit a fold exemption." }
                          { File = "App.fs"
                            Owner = "App.helper"
                            Kind = "profile"
                            Identity = "PURE_HELPER"
                            Reason = "An exact pure helper can submit its fold implementation for contract review." } ] }

            let policyPath = Path.Combine(root, "fdull.json")
            File.WriteAllText(policyPath, Codec.encode reviewed)
            let policyBytes = File.ReadAllText policyPath

            let source =
                "module App\n"
                + "let describe (uri: System.Uri) = uri.AbsoluteUri + uri.AbsoluteUri\n"
                + "let other (uri: System.Uri) = uri.AbsoluteUri\n"
                + "let clean value = value + 1\n"
                + "type Local = { Name: string }\n"
                + "let local (value: Local) = value.Name\n"
                + "let forbidden values = List.fold (+) 0 values\n"
                + "let helper values = List.fold (+) 0 values\n"
                + "let adapterFold values = List.fold (+) 0 values\n"
                + "[<System.Obsolete(\"review marker\")>]\ntype Annotated = { Value: int }\n"
                + "let mutate () =\n    let mutable value = 0\n    value <- 1\n    value\n"
                + "type Quantity = private Quantity of int\nlet create value = Ok (Quantity value)\n"

            File.WriteAllText(Path.Combine(root, "App.fs"), source)

            let report = Workspace.review root
            Assert.True(report.Advisory)
            Assert.True(report.AnalysisComplete, WorkspaceReview.format report)
            Assert.Equal(1, report.ExitCode)
            let change = Assert.Single report.SourceChanges
            Assert.Equal("App.fs", change.File)
            let originalSource = Assert.Single policy.Sources
            Assert.Equal(originalSource.Digest, change.ReviewedDigest)
            Assert.Equal(SafetyPolicy.fileDigest (Path.Combine(root, "App.fs")), change.CurrentDigest)
            Assert.Equal(2, change.Contracts.Length)

            let typeItem =
                report.Items
                |> List.filter (fun item ->
                    item.Owner = "App.describe"
                    && item.Kind = "type"
                    && item.Identity.EndsWith(" | System.Uri", StringComparison.Ordinal))
                |> Assert.Single

            Assert.Equal("App.describe", typeItem.Owner)
            Assert.Equal("contract-review", typeItem.Resolution)
            Assert.Contains("Kind=type", Assert.Single typeItem.Guidance)

            let memberItem =
                report.Items
                |> List.filter (fun item ->
                    item.Owner = "App.describe"
                    && item.Kind = "member"
                    && item.Identity.EndsWith(" | P:System.Uri.AbsoluteUri", StringComparison.Ordinal))
                |> Assert.Single

            Assert.Equal("App.describe", memberItem.Owner)
            Assert.Equal("contract-review", memberItem.Resolution)
            Assert.Equal(2, memberItem.Evidence.Length)
            Assert.All(memberItem.Evidence, fun evidence -> Assert.Equal("EFFECT001", evidence.Rule))
            let memberGuidance = Assert.Single memberItem.Guidance
            Assert.Contains("EFFECT001:", memberGuidance)
            Assert.Contains("Kind=member", memberGuidance)
            Assert.Contains("exact File, Owner and Identity", memberGuidance)

            let other =
                report.Items
                |> List.filter (fun item -> item.Owner = "App.other" && item.Kind = "member")
                |> Assert.Single

            Assert.Equal(memberItem.Identity, other.Identity)
            Assert.Single(other.Evidence) |> ignore

            Assert.DoesNotContain(
                report.Items,
                fun item ->
                    item.Owner = "App.clean"
                    || item.Owner = "App.local"
                    || item.Identity.EndsWith(" | App.Local", StringComparison.Ordinal)
            )

            let fold =
                report.Items
                |> List.filter (fun item -> item.Owner = "App.forbidden")
                |> Assert.Single

            Assert.Equal("member", fold.Kind)
            Assert.Equal("PURE", fold.Profile)
            Assert.Equal("code-change", fold.Resolution)
            let foldEvidence = Assert.Single fold.Evidence
            Assert.Equal("STYLE003", foldEvidence.Rule)
            let pureGuidance = Assert.Single fold.Guidance
            Assert.Contains("a member or rule permission in PURE cannot override", pureGuidance)
            Assert.Contains("Kind=profile, Identity=PURE_HELPER", pureGuidance)

            let helper =
                report.Items
                |> List.filter (fun item -> item.Owner = "App.helper")
                |> Assert.Single

            Assert.Equal("PURE_HELPER", helper.Profile)
            Assert.Equal("member", helper.Kind)
            Assert.Equal("contract-review", helper.Resolution)
            let helperGuidance = Assert.Single helper.Guidance
            Assert.Contains("the profile alone does not approve the API", helperGuidance)
            Assert.Contains("Kind=member", helperGuidance)

            let adapter =
                report.Items
                |> List.filter (fun item -> item.Owner = "App.adapterFold")
                |> Assert.Single

            Assert.Equal("contract-review", adapter.Resolution)
            Assert.Contains("ADAPTER allows exact review", Assert.Single adapter.Guidance)
            Assert.Contains("Kind=member", Assert.Single adapter.Guidance)

            let attribute =
                report.Items
                |> List.filter (fun item -> item.Kind = "attribute")
                |> Assert.Single

            Assert.Contains("Kind=attribute", Assert.Single attribute.Guidance)

            let mutation =
                report.Items
                |> List.filter (fun item -> item.Kind = "rule" && item.Identity = "MUT001 | value")
                |> Assert.Single

            Assert.Contains("Kind=rule", Assert.Single mutation.Guidance)

            let constructor =
                report.Items
                |> List.filter (fun item -> item.Kind = "constructor")
                |> Assert.Single

            Assert.Contains("exact identity in Constructors", Assert.Single constructor.Guidance)
            let text = WorkspaceReview.format report
            Assert.Contains("ADVISORY", text)
            Assert.Contains("Type approval does not approve the type's methods.", text)
            Assert.Contains("this finding cannot be resolved by an exemption in this profile", text)
            Assert.Contains("How to resolve: STYLE003:", text)
            Assert.Equal(report, Codec.encode report |> Codec.decode<WorkspaceReviewReport>)
            Assert.Equal(policyBytes, File.ReadAllText policyPath)
            Assert.Equal(source, File.ReadAllText(Path.Combine(root, "App.fs")))
            let lint = Workspace.check root
            Assert.False(lint.Complete)
            Assert.Equal(3, lint.ExitCode)
            Assert.Contains(lint.Errors, fun error -> error.StartsWith("BUILD005:", StringComparison.Ordinal)))

    [<Fact>]
    let ``advisory review reports blockers and never infers unused contracts from partial analysis`` () =
        withDirectory (fun root ->
            prepare root
            let clean = Workspace.review root
            Assert.True(clean.AnalysisComplete, WorkspaceReview.format clean)
            Assert.Equal(0, clean.ExitCode)
            Assert.Empty(clean.Items)
            Assert.Empty(clean.SourceChanges)

            let policy = read root
            let policyPath = Path.Combine(root, "fdull.json")

            let policyWithUnused =
                { policy with
                    Capabilities =
                        [ { File = "App.fs"
                            Owner = "App.increment"
                            Kind = "type"
                            Identity =
                              "System.Runtime, Version=10.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a | System.Uri"
                            Reason = "Intentionally unused contract verifies advisory removal evidence." } ] }

            File.WriteAllText(policyPath, Codec.encode policyWithUnused)
            let unused = Workspace.review root
            Assert.True(unused.AnalysisComplete, WorkspaceReview.format unused)
            Assert.Equal(1, unused.ExitCode)
            Assert.Single(unused.UnusedCapabilities) |> ignore

            File.WriteAllText(Path.Combine(root, "App.fs"), "module App\nlet value: int = \"wrong\"\n")
            let compilerError = Workspace.review root
            Assert.False(compilerError.AnalysisComplete)
            Assert.Equal(3, compilerError.ExitCode)
            Assert.Empty(compilerError.UnusedCapabilities)

            Assert.Contains(
                compilerError.Items,
                fun item ->
                    item.Evidence
                    |> List.exists (fun evidence -> evidence.Rule.StartsWith("FS", StringComparison.Ordinal))
            )

            Assert.Contains("Findings are partial", WorkspaceReview.format compilerError)

            Assert.Contains(
                compilerError.Items,
                fun item ->
                    item.Guidance
                    |> List.exists (fun guidance -> guidance.Contains("Compiler diagnostics have no permission entry"))
            )

            File.AppendAllText(Path.Combine(root, "Directory.Build.props"), "\n")
            let buildDrift = Workspace.review root
            Assert.False(buildDrift.AnalysisComplete)
            Assert.Equal(3, buildDrift.ExitCode)
            Assert.Empty(buildDrift.Projects)
            Assert.Contains(buildDrift.Errors, fun error -> error.StartsWith("BUILD005:", StringComparison.Ordinal)))

    [<Theory>]
    [<InlineData("class")>]
    [<InlineData("array")>]
    [<InlineData("collection")>]
    [<InlineData("nested")>]
    let ``review exposes nested requirements before outer type approval`` variant =
        withDirectory (fun root ->
            prepare root
            let policy = read root

            let source, parents, nestedKind =
                match variant with
                | "class" ->
                    "module App\ntype Service(callback: int -> int) =\n    let first value = callback value\n    let callback = first\n    member _.Run(value) = callback value\n",
                    [ "App.Service.``.ctor``", "source:App.fs | App.Service"
                      "App.Service.Run", "source:App.fs | App.Service" ],
                    "capture"
                | "array" ->
                    "module App\nlet inspect (_: System.Uri array) = ()\n",
                    [ "App.inspect",
                      "FSharp.Core, Version=10.1.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a | []" ],
                    "type"
                | "collection" ->
                    "module App\nlet inspect (_: System.Collections.Generic.List<System.Uri>) = ()\n",
                    [ "App.inspect",
                      "System.Collections, Version=10.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a | System.Collections.Generic.List`1" ],
                    "type"
                | "nested" ->
                    "module App\nlet inspect (_: System.Collections.Generic.List<System.Uri array option>) = ()\n",
                    [ "App.inspect",
                      "System.Collections, Version=10.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a | System.Collections.Generic.List`1"
                      "App.inspect",
                      "FSharp.Core, Version=10.1.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a | []" ],
                    "type"
                | _ -> failwith "Unknown nested review regression."

            File.WriteAllText(Path.Combine(root, "App.fs"), source)

            let reviewed =
                { policy with
                    Projects =
                        policy.Projects
                        |> List.map (fun project -> { project with Profile = "ADAPTER" })
                    Sources =
                        policy.Sources
                        |> List.map (fun entry ->
                            { entry with
                                Profile = "ADAPTER"
                                Digest = SafetyPolicy.fileDigest (Path.Combine(root, entry.File)) }) }

            let policyPath = Path.Combine(root, "fdull.json")
            File.WriteAllText(policyPath, Codec.encode reviewed)
            let policyBytes = File.ReadAllText policyPath
            let before = Workspace.review root
            Assert.True(before.AnalysisComplete, WorkspaceReview.format before)
            Assert.Equal(1, before.ExitCode)
            Assert.Equal(policyBytes, File.ReadAllText policyPath)
            let beforeLint = Workspace.check root
            Assert.True(beforeLint.Complete)
            Assert.Equal(1, beforeLint.ExitCode)

            let outerApproved =
                { reviewed with
                    Capabilities =
                        parents
                        |> List.map (fun (owner, identity) ->
                            { File = "App.fs"
                              Owner = owner
                              Kind = "type"
                              Identity = identity
                              Reason =
                                "Literal outer type approval exposes dependent findings without approving them." }) }

            File.WriteAllText(policyPath, Codec.encode outerApproved)
            let after = Workspace.review root
            Assert.True(after.AnalysisComplete, WorkspaceReview.format after)
            Assert.Equal(1, after.ExitCode)
            Assert.Empty(after.UnusedCapabilities)

            let keys report =
                report.Items
                |> List.filter (fun item ->
                    item.Kind = nestedKind
                    && (nestedKind = "capture"
                        || item.Identity.EndsWith(" | System.Uri", StringComparison.Ordinal)))
                |> List.map (fun item ->
                    item.File, item.Owner, item.Profile, item.Kind, item.Identity, item.Resolution)
                |> Set.ofList

            let dependentKeys = keys after
            Assert.NotEmpty(dependentKeys)
            Assert.True((dependentKeys = keys before), WorkspaceReview.format before)
            let afterLint = Workspace.check root
            Assert.True(afterLint.Complete)
            Assert.Equal(1, afterLint.ExitCode)
            Assert.NotEmpty((Assert.Single afterLint.Projects).Report.Diagnostics))

    [<Fact>]
    let ``review does not invent payload approvals behind an existing rule exception`` () =
        withDirectory (fun root ->
            prepare root
            let policy = read root
            let sourcePath = Path.Combine(root, "App.fs")
            File.WriteAllText(sourcePath, "module App\nlet inspect (_: System.Uri array) = ()\n")

            let reviewed =
                { policy with
                    Projects =
                        policy.Projects
                        |> List.map (fun project -> { project with Profile = "ADAPTER" })
                    Sources =
                        policy.Sources
                        |> List.map (fun entry ->
                            { entry with
                                Profile = "ADAPTER"
                                Digest = SafetyPolicy.fileDigest sourcePath })
                    Capabilities =
                        [ { File = "App.fs"
                            Owner = "App.inspect"
                            Kind = "rule"
                            Identity = "MUT008 | inspect"
                            Reason = "Literal scoped array exception tests the existing opaque rule-approval path." } ] }

            let policyPath = Path.Combine(root, "fdull.json")
            File.WriteAllText(policyPath, Codec.encode reviewed)
            let lint = Workspace.check root
            Assert.True(lint.Complete, Codec.encode lint)
            Assert.Equal(0, lint.ExitCode)
            let inventory = Workspace.review root
            Assert.True(inventory.AnalysisComplete, WorkspaceReview.format inventory)
            Assert.Equal(0, inventory.ExitCode)
            Assert.Empty(inventory.Items)

            let unused =
                { File = "App.fs"
                  Owner = "App.inspect"
                  Kind = "type"
                  Identity =
                    "System.Runtime, Version=10.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a | System.Uri"
                  Reason = "Unreachable child approval must remain unused under the existing rule exception." }

            File.WriteAllText(
                policyPath,
                Codec.encode
                    { reviewed with
                        Capabilities = reviewed.Capabilities @ [ unused ] }
            )

            let extra = Workspace.review root
            Assert.True(extra.AnalysisComplete, WorkspaceReview.format extra)
            Assert.Equal(1, extra.ExitCode)
            Assert.Empty(extra.Items)
            Assert.Equal(unused, Assert.Single extra.UnusedCapabilities)
            let rejected = Workspace.check root
            Assert.False(rejected.Complete)
            Assert.Equal(3, rejected.ExitCode)
            Assert.Contains(rejected.Errors, fun error -> error.Contains("Unused protected capability")))

    [<Fact>]
    let ``nested review exhaustion stays incomplete and makes no unused permission claims`` () =
        withDirectory (fun root ->
            prepare root
            let policy = read root
            let levels = SafetyLimits.typeDepth / 2 + 5

            let definitions =
                [ yield "module App"
                  yield
                      "[<NoEquality; NoComparison>]\ntype Level0 = { Next: System.Collections.Generic.List<System.Uri> }"
                  for level in 1..levels do
                      yield
                          $"[<NoEquality; NoComparison>]\ntype Level%d{level} = {{ Next: System.Collections.Generic.List<Level%d{level - 1}> }}"
                  yield $"let inspect (_: Level%d{levels}) = ()" ]

            let sourcePath = Path.Combine(root, "App.fs")
            File.WriteAllText(sourcePath, String.concat "\n" definitions + "\n")

            let reviewed =
                { policy with
                    Projects =
                        policy.Projects
                        |> List.map (fun project -> { project with Profile = "ADAPTER" })
                    Sources =
                        policy.Sources
                        |> List.map (fun entry ->
                            { entry with
                                Profile = "ADAPTER"
                                Digest = SafetyPolicy.fileDigest sourcePath })
                    Capabilities =
                        [ { File = "App.fs"
                            Owner = "App.inspect"
                            Kind = "type"
                            Identity =
                              "System.Runtime, Version=10.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a | System.Uri"
                            Reason = "The unreachable leaf approval must not be classified during partial traversal." } ] }

            File.WriteAllText(Path.Combine(root, "fdull.json"), Codec.encode reviewed)
            let report = Workspace.review root
            Assert.False(report.AnalysisComplete)
            Assert.Equal(3, report.ExitCode)
            Assert.Empty(report.UnusedCapabilities)

            Assert.True(
                (report.Items
                 |> List.exists (fun item ->
                     item.Evidence
                     |> List.exists (fun evidence -> evidence.Rule = "BUILD003" && evidence.Message.Contains("depth"))))
                || (report.Errors
                    |> List.exists (fun error -> error.Contains("BUILD003:") && error.Contains("output budget"))),
                WorkspaceReview.format report
            ))

    [<Fact>]
    let ``fold review guidance matches scoped permissions and retains pure restrictions`` () =
        withDirectory (fun root ->
            prepare root
            let policy = read root

            let source =
                "module App\n"
                + "let ordinary values = List.fold (+) 0 values\n"
                + "let helper values = List.fold (+) 0 values\n"
                + "let adapter values = List.fold (+) 0 values\n"
                + "let helperClock () = System.DateTime.UtcNow\n"

            File.WriteAllText(Path.Combine(root, "App.fs"), source)

            let reviewed =
                { policy with
                    Sources =
                        policy.Sources
                        |> List.map (fun entry ->
                            { entry with
                                Digest = SafetyPolicy.fileDigest (Path.Combine(root, entry.File)) })
                    Capabilities =
                        [ { File = "App.fs"
                            Owner = "App.helper"
                            Kind = "profile"
                            Identity = "PURE_HELPER"
                            Reason = "Reviewed accumulation helper; exact API permission is still required." }
                          { File = "App.fs"
                            Owner = "App.adapter"
                            Kind = "profile"
                            Identity = "ADAPTER"
                            Reason = "Reviewed adapter declaration with separately reviewed exact calls." }
                          { File = "App.fs"
                            Owner = "App.helperClock"
                            Kind = "profile"
                            Identity = "PURE_HELPER"
                            Reason = "Regression keeps ambient effects prohibited in pure helpers." } ] }

            File.WriteAllText(Path.Combine(root, "fdull.json"), Codec.encode reviewed)
            let before = Workspace.review root
            Assert.True(before.AnalysisComplete, WorkspaceReview.format before)
            Assert.Equal(4, before.Items.Length)

            let foldIdentity =
                "FSharp.Core, Version=10.1.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a | M:Microsoft.FSharp.Collections.ListModule.Fold``2(Microsoft.FSharp.Core.FSharpFunc{``1,Microsoft.FSharp.Core.FSharpFunc{``0,``1}},``1,Microsoft.FSharp.Collections.FSharpList{``0})"

            let approved =
                { reviewed with
                    Capabilities =
                        reviewed.Capabilities
                        @ ([ "App.ordinary"; "App.helper"; "App.adapter" ]
                           |> List.map (fun owner ->
                               { File = "App.fs"
                                 Owner = owner
                                 Kind = "member"
                                 Identity = foldIdentity
                                 Reason =
                                   "Literal fold approval tests profile eligibility; PURE must still reject the same API." }))
                        @ [ { File = "App.fs"
                              Owner = "App.helperClock"
                              Kind = "member"
                              Identity =
                                "System.Runtime, Version=10.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a | P:System.DateTime.UtcNow"
                              Reason = "Literal clock approval must not exempt an ambient effect in PURE_HELPER." } ] }

            File.WriteAllText(Path.Combine(root, "fdull.json"), Codec.encode approved)
            let after = Workspace.review root
            Assert.True(after.AnalysisComplete, WorkspaceReview.format after)
            Assert.Equal(2, after.Items.Length)
            Assert.DoesNotContain(after.Items, fun item -> item.Owner = "App.helper" || item.Owner = "App.adapter")
            Assert.All(after.Items, fun item -> Assert.Equal("code-change", item.Resolution))

            let clock =
                after.Items
                |> List.filter (fun item -> item.Owner = "App.helperClock")
                |> Assert.Single

            Assert.Contains("PURE_HELPER prohibits this ruled API", Assert.Single clock.Guidance)
            Assert.Contains("pass required data into pure code", Assert.Single clock.Guidance)
            let lint = Workspace.check root
            Assert.True(lint.Complete)
            Assert.Equal(1, lint.ExitCode)
            Assert.Equal(2, (Assert.Single lint.Projects).Report.Diagnostics.Length))

    [<Fact>]
    let ``initialization discovers reviewed mixed-language scope and exports Web SDK inputs without compiling`` () =
        withDirectory (fun root ->
            restoreConsumer root None "module App\nlet value: int = \"not an integer\"\n"

            Directory.CreateDirectory(Path.Combine(root, "external")) |> ignore
            File.WriteAllText(Path.Combine(root, "external/Dependency.cs"), "internal static class Dependency {}\n")
            File.WriteAllText(Path.Combine(root, "external/Transitive.cs"), "internal static class Transitive {}\n")

            File.WriteAllText(
                Path.Combine(root, "external/Transitive.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><RestorePackagesWithLockFile>true</RestorePackagesWithLockFile></PropertyGroup></Project>"
            )

            File.WriteAllText(
                Path.Combine(root, "external/Dependency.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><RestorePackagesWithLockFile>true</RestorePackagesWithLockFile></PropertyGroup><ItemGroup><ProjectReference Include=\"Transitive.csproj\"/></ItemGroup></Project>"
            )

            File.WriteAllText(
                Path.Combine(root, "App.fsproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup><ItemGroup><PackageReference Include=\"Swashbuckle.AspNetCore\" Version=\"7.2.0\"/><ProjectReference Include=\"external\\Dependency.csproj\"/><Compile Include=\"App.fs\"/></ItemGroup></Project>"
            )

            run root [ "restore"; "App.fsproj"; "--use-lock-file"; "-m:1"; "-nr:false" ]

            File.WriteAllText(
                Path.Combine(root, "fdull.scope.json"),
                "{\"Version\":1,\"External\":[{\"Path\":\"external\",\"Kind\":\"implementation\",\"Reason\":\"The CSharp dependency has separate required checks.\"}]}"
            )

            let policyFile = Project.initialize root
            let policy = File.ReadAllText policyFile |> Codec.decode<WorkspacePolicyDocument>
            Assert.Equal(1, policy.External |> Option.map List.length |> Option.defaultValue 0)
            Assert.Equal(1, policy.Sources.Length)
            Assert.Contains(policy.Sources, fun source -> source.File = "App.fs")
            Assert.Contains(policy.Inputs, fun input -> input.File = "fdull.scope.json")
            Assert.Contains(policy.Inputs, fun input -> input.File = "external/Dependency.csproj")
            Assert.Contains(policy.Inputs, fun input -> input.File = "external/Transitive.csproj"))

    [<Fact>]
    let ``supported Web test SDK xUnit v3 and Aspire generated-source profiles are exact`` () =
        withDirectory (fun root ->
            restoreConsumer root (Some "10.0.100") "module App\nlet increment value = value + 1\n"

            File.WriteAllText(
                Path.Combine(root, "App.fsproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><OutputType>Exe</OutputType><IsTestProject>true</IsTestProject><FSharpCoreImplicitPackageVersion>10.0.100</FSharpCoreImplicitPackageVersion></PropertyGroup><ItemGroup><PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"17.14.1\"/><PackageReference Include=\"Swashbuckle.AspNetCore\" Version=\"7.2.0\"/><Compile Include=\"App.fs\"/></ItemGroup></Project>"
            )

            run root [ "restore"; "App.fsproj"; "--use-lock-file"; "-m:1"; "-nr:false" ]

            run
                root
                [ "build"
                  "App.fsproj"
                  "-c"
                  "Release"
                  "--no-restore"
                  "-m:1"
                  "--disable-build-servers" ]

            Project.initialize root |> ignore
            let report = Workspace.check root
            Assert.True(report.Complete, String.concat "\n" report.Errors)
            Assert.Equal(0, report.ExitCode))

        // The separate fixture keeps the existing xUnit v2/Test SDK profile covered.
        withDirectory (fun root ->
            restoreConsumer root (Some "10.1.201") "module App\nlet increment value = value + 1\n"

            File.WriteAllText(
                Path.Combine(root, "App.fsproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><RootNamespace>Fixture.Generated</RootNamespace><IsTestProject>true</IsTestProject><FSharpCoreImplicitPackageVersion>10.1.201</FSharpCoreImplicitPackageVersion></PropertyGroup><ItemGroup><PackageReference Include=\"JunitXml.TestLogger\" Version=\"7.0.2\"/><PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"17.13.0\"/><PackageReference Include=\"xunit.v3\" Version=\"3.1.0\"/><Compile Include=\"App.fs\"/></ItemGroup></Project>"
            )

            run root [ "restore"; "App.fsproj"; "--use-lock-file"; "-m:1"; "-nr:false" ]

            run
                root
                [ "build"
                  "App.fsproj"
                  "-c"
                  "Release"
                  "--no-restore"
                  "-m:1"
                  "--disable-build-servers" ]

            Project.initialize root |> ignore

            let invocation =
                match Workspace.export root "App.fsproj" with
                | Ok invocation -> invocation
                | Error error -> failwith error

            Assert.Equal(5, invocation.Generated.Length)

            let containsGenerated expected =
                Assert.Contains(invocation.Generated, fun file -> Path.GetFileName file = expected)

            containsGenerated "SelfRegisteredExtensions.fs"
            containsGenerated "DefaultRunnerReporters.fs"
            containsGenerated "XunitAutoGeneratedEntryPoint.fs"

            let report = Workspace.check root
            Assert.True(report.Complete, Codec.encode report)
            Assert.Equal(0, report.ExitCode)

            let selfRegistered =
                invocation.Generated
                |> List.tryFind (fun file -> Path.GetFileName file = "SelfRegisteredExtensions.fs")
                |> Fixtures.requireSome "The self-registration source was not exported."

            File.AppendAllText(selfRegistered, "\nlet unreviewed = System.IO.File.ReadAllText \"secret\"\n")

            match Workspace.export root "App.fsproj" with
            | Error error -> Assert.StartsWith("BUILD003:", error)
            | Ok _ -> failwith "Modified generated xUnit source was accepted.")

        // The same xUnit profile remains compatible with the supported FSharp.Core 10.0 line.
        withDirectory (fun root ->
            restoreConsumer root (Some "10.0.100") "module App\nlet increment value = value + 1\n"

            File.WriteAllText(
                Path.Combine(root, "App.fsproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><RootNamespace>Fixture.Generated</RootNamespace><IsTestProject>true</IsTestProject><FSharpCoreImplicitPackageVersion>10.0.100</FSharpCoreImplicitPackageVersion></PropertyGroup><ItemGroup><PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"17.13.0\"/><PackageReference Include=\"xunit.v3\" Version=\"3.1.0\"/><Compile Include=\"App.fs\"/></ItemGroup></Project>"
            )

            run root [ "restore"; "App.fsproj"; "--use-lock-file"; "-m:1"; "-nr:false" ]

            run
                root
                [ "build"
                  "App.fsproj"
                  "-c"
                  "Release"
                  "--no-restore"
                  "-m:1"
                  "--disable-build-servers" ]

            Project.initialize root |> ignore
            let report = Workspace.check root
            Assert.True(report.Complete, Codec.encode report)
            Assert.Equal(0, report.ExitCode))

        withDirectory (fun root ->
            restoreConsumer root None "module App\nlet increment value = value + 1\n"

            File.WriteAllText(
                Path.Combine(root, "Directory.Build.targets"),
                "<Project><Target Name=\"ClearPackageFSharpSuppressions\" BeforeTargets=\"CoreCompile\"><PropertyGroup><NoWarn></NoWarn></PropertyGroup></Target></Project>"
            )

            // Pin the patched transitive dependency while retaining NuGet audit for this fixture.
            let host = Path.Combine(root, "Host")
            Directory.CreateDirectory host |> ignore

            File.WriteAllText(
                Path.Combine(host, "Host.fsproj"),
                "<Project Sdk=\"Aspire.AppHost.Sdk/13.4.6\"><PropertyGroup><OutputType>Library</OutputType><SkipValidateAspireHostProjectResources>true</SkipValidateAspireHostProjectResources></PropertyGroup><ItemGroup><Compile Include=\"Host.fs\"/></ItemGroup><ItemGroup><PackageReference Include=\"Aspire.Hosting.AppHost\" Version=\"13.4.6\"/><PackageReference Include=\"FSharp.Aspire.Hosting.AppHost\" Version=\"13.0.0\"/><PackageReference Include=\"MessagePack\" Version=\"2.5.303\"/></ItemGroup><ItemGroup><ProjectReference Include=\"../App.fsproj\"/></ItemGroup><Target Name=\"TamperAspireMetadata\" AfterTargets=\"WriteAspireProjectMetadataSources\" BeforeTargets=\"CoreCompile\" Condition=\"Exists('tamper-generated-source')\"><WriteLinesToFile File=\"$(IntermediateOutputPath)Aspire/references/App.ProjectMetadata.g.fs\" Lines=\"// tampered\" Overwrite=\"false\"/></Target></Project>"
            )

            File.WriteAllText(Path.Combine(host, "Host.fs"), "module AppHost\nlet value = 1\n")
            run root [ "restore"; "Host/Host.fsproj"; "--use-lock-file"; "-m:1"; "-nr:false" ]

            run
                root
                [ "build"
                  "Host/Host.fsproj"
                  "-c"
                  "Release"
                  "--no-restore"
                  "-m:1"
                  "--disable-build-servers" ]

            Project.initialize root |> ignore

            let invocation =
                match Workspace.export root "Host/Host.fsproj" with
                | Ok invocation -> invocation
                | Error error -> failwith error

            Assert.Equal(4, invocation.Generated.Length)
            Assert.Contains(invocation.Generated, fun file -> Path.GetFileName file = "App.ProjectMetadata.g.fs")

            let report = Workspace.check root
            Assert.True(report.Complete, Codec.encode report)
            Assert.Equal(0, report.ExitCode)

            File.WriteAllText(Path.Combine(host, "tamper-generated-source"), "tamper")

            match Workspace.export root "Host/Host.fsproj" with
            | Error error -> Assert.StartsWith("BUILD003:", error)
            | Ok _ -> failwith "Modified generated Aspire source was accepted.")

    let private write root policy =
        File.WriteAllText(Path.Combine(root, "fdull.json"), Codec.encode policy)

    [<Fact>]
    let ``captured callback permissions survive formatting without authorizing new effects`` () =
        withDirectory (fun root ->
            let source =
                "module App\ntype Service(callback: int -> int) =\n    let first value = callback value\n    let callback = first\n    member _.Run(value) = callback value\n"

            restoreConsumer root None source
            Project.initialize root |> ignore
            let policy = read root
            let owner = "App.Service.``.ctor``"

            let capability scope kind identity =
                { File = "App.fs"
                  Owner = scope
                  Kind = kind
                  Identity = identity
                  Reason =
                    "Reviewed callback storage and constructor ingress; authored callback bodies remain checked." }

            let captures =
                [ for role in [ "parameter"; "binding" ] ->
                      capability
                          owner
                          "capture"
                          ("EFFECT003 | "
                           + Codec.encode ([ "App.Service"; role; "callback" ], ([]: string list))) ]

            let reviewed =
                { policy with
                    Projects =
                        policy.Projects
                        |> List.map (fun project -> { project with Profile = "ADAPTER" })
                    Sources = policy.Sources |> List.map (fun item -> { item with Profile = "ADAPTER" })
                    Capabilities =
                        [ capability owner "type" "source:App.fs | App.Service"
                          capability "App.Service.Run" "type" "source:App.fs | App.Service"
                          capability owner "rule" ("EFFECT003 | " + owner) ]
                        @ captures }

            write root reviewed
            let before = Workspace.check root
            Assert.True(before.Complete && before.ExitCode = 0, Codec.encode before)

            let moved =
                source.Replace(
                    "    let callback = first",
                    "    // callback storage follows an unrelated comment\n\n    let callback =\n        first"
                )

            File.WriteAllText(Path.Combine(root, "App.fs"), moved)
            let stale = Workspace.check root
            Assert.False(stale.Complete)
            Assert.Equal(3, stale.ExitCode)
            let advisory = Workspace.review root
            Assert.True(advisory.AnalysisComplete, Codec.encode advisory)
            Assert.Empty(advisory.Items)
            Assert.Empty(advisory.UnusedCapabilities)
            Assert.Single(advisory.SourceChanges) |> ignore

            let refresh (text: string) =
                File.WriteAllText(Path.Combine(root, "App.fs"), text)

                write
                    root
                    { reviewed with
                        Sources =
                            reviewed.Sources
                            |> List.map (fun item ->
                                { item with
                                    Digest = SafetyPolicy.fileDigest (Path.Combine(root, item.File)) }) }

            refresh moved
            let after = Project.verify root
            Assert.True(after.Complete && after.ExitCode = 0, Codec.encode after)
            Assert.True(reviewed.Capabilities = (read root).Capabilities)

            refresh (
                moved.Replace(
                    "let first value = callback value",
                    "let first value = (fun next -> System.IO.File.ReadAllText \"secret\" |> ignore; callback next) value"
                )
            )

            let changed = Workspace.review root
            Assert.True(changed.AnalysisComplete, Codec.encode changed)

            Assert.Contains(
                changed.Items,
                fun item ->
                    item.Kind = "member"
                    && item.Identity.Contains("File.ReadAllText", StringComparison.Ordinal)
            )

            refresh (moved.Replace("Service", "OtherService"))
            let renamed = Workspace.review root
            Assert.True(renamed.AnalysisComplete, Codec.encode renamed)
            Assert.Contains(renamed.UnusedCapabilities, fun entry -> entry.Kind = "capture")
            Assert.Contains(renamed.Items, fun item -> item.Owner.Contains("OtherService", StringComparison.Ordinal)))

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    let ``generated capture keys distinguish bindings parameters and authored numbered names`` authored =
        withDirectory (fun root ->
            let binding = if authored then "callback4" else "callback"

            let source =
                "module App\ntype Service(callback: int -> int) =\n    let first value = callback value\n    let "
                + binding
                + " = first\n    member _.Run(value) = "
                + binding
                + " value\n"

            restoreConsumer root None source
            Project.initialize root |> ignore
            let policy = read root
            let owner = "App.Service.``.ctor``"

            let reviewed =
                { policy with
                    Projects =
                        policy.Projects
                        |> List.map (fun project -> { project with Profile = "ADAPTER" })
                    Sources = policy.Sources |> List.map (fun item -> { item with Profile = "ADAPTER" })
                    Capabilities =
                        [ for scope in [ owner; "App.Service.Run" ] ->
                              { File = "App.fs"
                                Owner = scope
                                Kind = "type"
                                Identity = "source:App.fs | App.Service"
                                Reason =
                                  "Review generated capture identities while preserving authored type permission scope." } ] }

            write root reviewed
            let report = Workspace.review root
            Assert.True(report.AnalysisComplete, Codec.encode report)
            let items = report.Items |> List.filter (fun item -> item.Kind = "capture")
            Assert.Equal(2, items.Length)
            Assert.All(items, fun item -> Assert.Contains("Kind=capture", Assert.Single item.Guidance))

            Assert.Contains(
                items,
                fun item ->
                    item.Identity = "EFFECT003 | "
                                    + Codec.encode ([ "App.Service"; "parameter"; "callback" ], ([]: string list))
            )

            Assert.Contains(
                items,
                fun item ->
                    item.Identity = "EFFECT003 | "
                                    + Codec.encode (
                                        [ "App.Service"; "binding"; (if authored then "callback4" else "callback") ],
                                        ([]: string list)
                                    )
            )

            let approved =
                { reviewed with
                    Capabilities =
                        reviewed.Capabilities
                        @ [ { File = "App.fs"
                              Owner = owner
                              Kind = "capture"
                              Identity =
                                "EFFECT003 | "
                                + Codec.encode ([ "App.Service"; "parameter"; "callback" ], ([]: string list))
                              Reason =
                                "Only the constructor parameter capture is approved; the shadowing binding stays unapproved." } ] }

            write root approved
            let remaining = Workspace.review root
            Assert.True(remaining.AnalysisComplete, Codec.encode remaining)

            Assert.Single(remaining.Items |> List.filter (fun item -> item.Kind = "capture"))
            |> ignore)

    [<Fact>]
    let ``auto property captures use an authored property key across source relocation`` () =
        withDirectory (fun root ->
            let source =
                "module App\ntype Service() =\n"
                + "    member val OnLoad: int -> int = (fun value -> value + 1) with get, set\n"
                + "    member this.Run(value) = this.OnLoad value\n"

            restoreConsumer root None source
            Project.initialize root |> ignore
            let policy = read root

            let reviewed =
                { policy with
                    Projects =
                        policy.Projects
                        |> List.map (fun project -> { project with Profile = "ADAPTER" })
                    Sources = policy.Sources |> List.map (fun item -> { item with Profile = "ADAPTER" })
                    Capabilities =
                        [ for scope in [ "App.Service.``.ctor``"; "App.Service.Run" ] ->
                              { File = "App.fs"
                                Owner = scope
                                Kind = "type"
                                Identity = "source:App.fs | App.Service"
                                Reason = "Review class storage while preserving exact capture identity evidence." } ] }

            write root reviewed

            let captureIdentity () =
                let report = Workspace.review root
                Assert.True(report.AnalysisComplete, Codec.encode report)

                report.Items
                |> List.find (fun item -> item.Kind = "capture" && item.Identity.StartsWith("EFFECT003 | "))
                |> _.Identity

            let expected =
                "EFFECT003 | "
                + Codec.encode ([ "App.Service"; "property"; "OnLoad" ], ([]: string list))

            Assert.Equal(expected, captureIdentity ())
            File.WriteAllText(Path.Combine(root, "App.fs"), "// Relocate the authored property.\n\n" + source)
            Assert.Equal(expected, captureIdentity ())

            let approved =
                { reviewed with
                    Capabilities =
                        reviewed.Capabilities
                        @ [ { File = "App.fs"
                              Owner = "App.Service.``.ctor``"
                              Kind = "capture"
                              Identity = expected
                              Reason = "Review only this authored callback property; its body remains checked." } ] }

            write root approved
            let accepted = Workspace.review root
            Assert.True(accepted.AnalysisComplete, Codec.encode accepted)
            Assert.DoesNotContain(accepted.Items, fun item -> item.Kind = "capture")

            File.WriteAllText(Path.Combine(root, "App.fs"), source.Replace("OnLoad", "OnRead"))
            let renamed = Workspace.review root
            Assert.True(renamed.AnalysisComplete, Codec.encode renamed)
            Assert.Contains(renamed.UnusedCapabilities, fun entry -> entry.Kind = "capture")

            Assert.Contains(
                renamed.Items,
                fun item ->
                    item.Kind = "capture"
                    && item.Identity = "EFFECT003 | "
                                       + Codec.encode ([ "App.Service"; "property"; "OnRead" ], ([]: string list))
            ))

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    let ``capture identities separate types and reject ambiguous shadowed bindings`` ambiguous =
        withDirectory (fun root ->
            let body =
                "type Service(callback: int -> int) =\n"
                + (if ambiguous then "    let callback = callback\n" else "")
                + "    let first value = callback value\n    let callback = first\n    member _.Run(value) = callback value\n"

            let source =
                "module App\n"
                + body
                + (if ambiguous then
                       ""
                   else
                       body.Replace("Service", "OtherService"))

            restoreConsumer root None source
            Project.initialize root |> ignore
            let policy = read root

            let reviewed =
                { policy with
                    Projects =
                        policy.Projects
                        |> List.map (fun project -> { project with Profile = "ADAPTER" })
                    Sources = policy.Sources |> List.map (fun item -> { item with Profile = "ADAPTER" })
                    Capabilities =
                        [ for name in
                              (if ambiguous then
                                   [ "Service" ]
                               else
                                   [ "Service"; "OtherService" ]) do
                              for suffix in [ ".``.ctor``"; ".Run" ] do
                                  yield
                                      { File = "App.fs"
                                        Owner = "App." + name + suffix
                                        Kind = "type"
                                        Identity = "source:App.fs | App." + name
                                        Reason =
                                          "Reviewed class shape allows capture mapping evidence; callbacks stay unapproved." } ] }

            write root reviewed
            let report = Workspace.review root

            if ambiguous then
                Assert.False(report.AnalysisComplete)
                Assert.Equal(3, report.ExitCode)

                Assert.Contains(
                    report.Items,
                    fun item ->
                        item.Evidence
                        |> List.exists (fun diagnostic ->
                            diagnostic.Rule = "BUILD003"
                            && diagnostic.Message.Contains("ambiguous authored identity", StringComparison.Ordinal))
                )

                Assert.Empty(report.UnusedCapabilities)
            else
                Assert.True(report.AnalysisComplete, Codec.encode report)
                let captures = report.Items |> List.filter (fun item -> item.Kind = "capture")
                Assert.Equal(4, captures.Length)
                Assert.Equal(4, (captures |> List.map _.Identity |> Set.ofList).Count))

    let private withDataConsumer definition source typeName action =
        withDirectory (fun root ->
            restoreConsumer root None source
            let shared = Path.Combine(root, "Shared")
            Directory.CreateDirectory shared |> ignore

            File.WriteAllText(
                Path.Combine(shared, "Shared.fsproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><Compile Include=\"Shared.fs\"/></ItemGroup></Project>"
            )

            File.WriteAllText(Path.Combine(shared, "Shared.fs"), "namespace Shared\n" + definition + "\n")

            File.WriteAllText(
                Path.Combine(root, "App.fsproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"Shared/Shared.fsproj\"/><Compile Include=\"App.fs\"/></ItemGroup></Project>"
            )

            run root [ "restore"; "App.fsproj"; "-m:1"; "-nr:false" ]

            run
                root
                [ "build"
                  "App.fsproj"
                  "-c"
                  "Release"
                  "--no-restore"
                  "-m:1"
                  "--disable-build-servers" ]

            Project.initialize root |> ignore

            let invocation =
                match Workspace.export root "App.fsproj" with
                | Ok value -> value
                | Error error -> failwith error

            let reference =
                invocation.References
                |> List.tryFind (fun file -> Path.GetFileName file = "Shared.dll")
                |> Fixtures.requireSome "Missing shared compiler reference."

            let assembly =
                System.Reflection.AssemblyName.GetAssemblyName(reference).FullName
                |> External.required "test.shared-assembly"

            let contract =
                { Project = "App.fsproj"
                  Identity = assembly + " | Shared." + typeName
                  Assemblies =
                    [ { Identity = assembly
                        Digest = DataContractPolicy.assemblyDigest reference } ]
                  Reason = "Reviewed immutable shared representation; all nested payloads remain checked." }

            action root (read root) contract)

    [<Fact>]
    let ``reusable data contracts allow helper extraction without declaration grants`` () =
        withDataConsumer
            "type Nested = { Count: int; Tags: string list }\ntype Payload = { Name: string; Nested: Nested; Outcome: Result<Nested, string> }"
            "module App\nlet first (value: Shared.Payload) = value.Name\nlet second (value: Shared.Payload) = value.Nested.Count\n"
            "Payload"
            (fun root policy contract ->
                let before = Workspace.check root
                Assert.NotEqual(0, before.ExitCode)

                let reviewed =
                    { policy with
                        DataContracts = Some [ contract ] }

                write root reviewed
                let initialCheck = Workspace.check root

                Assert.True(
                    initialCheck.Complete && initialCheck.ExitCode = 0,
                    String.concat
                        "\n"
                        (initialCheck.Errors
                         @ (initialCheck.Projects
                            |> List.collect (fun project -> project.Report.Diagnostics |> List.map _.Message)))
                )

                let approved = Project.verify root

                Assert.True(
                    approved.Complete,
                    String.concat
                        "\n"
                        (approved.Errors
                         @ (approved.Projects
                            |> List.collect (fun project -> project.Report.Diagnostics |> List.map _.Message)))
                )

                Assert.Equal(0, approved.ExitCode)
                Assert.Empty(reviewed.Capabilities)

                File.AppendAllText(Path.Combine(root, "App.fs"), "let third (value: Shared.Payload) = value\n")

                let extracted =
                    { reviewed with
                        Sources =
                            reviewed.Sources
                            |> List.map (fun source ->
                                if source.File = "App.fs" then
                                    { source with
                                        Digest = SafetyPolicy.fileDigest (Path.Combine(root, source.File)) }
                                else
                                    source) }

                write root extracted
                let after = Workspace.check root

                Assert.True(
                    after.Complete,
                    String.concat
                        "\n"
                        (after.Errors
                         @ (after.Projects
                            |> List.collect (fun project -> project.Report.Diagnostics |> List.map _.Message)))
                )

                Assert.Equal(0, after.ExitCode)
                Assert.Empty(extracted.Capabilities)
                Assert.Equal(reviewed.DataContracts, extracted.DataContracts)

                write
                    root
                    { extracted with
                        DataContracts =
                            Some
                                [ { contract with
                                      Assemblies =
                                          contract.Assemblies
                                          @ [ { Identity =
                                                  "Unused, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"
                                                Digest = "sha256:" + String.replicate 64 "a" } ] } ] }

                let unused = Workspace.review root
                Assert.True(unused.AnalysisComplete)
                Assert.Equal(1, unused.ExitCode)
                Assert.Single(unused.UnusedDataContracts) |> ignore

                let scoped =
                    { extracted with
                        DataContracts =
                            Some
                                [ { contract with
                                      Project = "Shared/Shared.fsproj" } ] }

                write root scoped
                let wrongProject = Workspace.check root
                Assert.NotEqual(0, wrongProject.ExitCode)

                Assert.Contains(
                    wrongProject.Projects
                    |> List.collect (fun project -> project.Report.Diagnostics),
                    fun diagnostic -> diagnostic.Rule = "MODEL001" && diagnostic.File = Path.Combine(root, "App.fs")
                )

                write
                    root
                    { extracted with
                        DataContracts =
                            Some
                                [ { contract with
                                      Assemblies =
                                          contract.Assemblies
                                          |> List.map (fun pin ->
                                              { pin with
                                                  Digest = "sha256:" + String.replicate 64 "0" }) } ] }

                let drift = Workspace.check root
                Assert.False(drift.Complete)
                Assert.Equal(3, drift.ExitCode)

                Assert.Contains(
                    drift.Projects |> List.collect (fun project -> project.Report.Diagnostics),
                    fun diagnostic -> diagnostic.Rule = "BUILD005"
                )

                File.WriteAllText(
                    Path.Combine(root, "Shared/Shared.fs"),
                    "namespace Shared\ntype Nested = { Count: int; Tags: string list; Revision: int }\ntype Payload = { Name: string; Nested: Nested; Outcome: Result<Nested, string> }\n"
                )

                run
                    root
                    [ "build"
                      "App.fsproj"
                      "-c"
                      "Release"
                      "--no-restore"
                      "--disable-build-servers" ]

                write
                    root
                    { extracted with
                        Sources =
                            extracted.Sources
                            |> List.map (fun source ->
                                if source.File = "Shared/Shared.fs" then
                                    { source with
                                        Digest = SafetyPolicy.fileDigest (Path.Combine(root, source.File)) }
                                else
                                    source) }

                let changed = Workspace.check root
                Assert.False(changed.Complete)
                Assert.Equal(3, changed.ExitCode)

                Assert.Contains(
                    changed.Projects |> List.collect (fun project -> project.Report.Diagnostics),
                    fun diagnostic ->
                        diagnostic.Rule = "BUILD005"
                        && diagnostic.Message.Contains("Reusable data contract assembly", StringComparison.Ordinal)
                ))

    [<Theory>]
    [<InlineData("array")>]
    [<InlineData("alias")>]
    [<InlineData("callback")>]
    [<InlineData("collection")>]
    [<InlineData("mutable-field")>]
    [<InlineData("cli-mutable")>]
    [<InlineData("resource")>]
    [<InlineData("generic")>]
    [<InlineData("custom-equality")>]
    [<InlineData("formatting-hook")>]
    let ``reusable data contracts reject unsafe nested payloads even with type grants`` variant =
        let definition, typeName, target =
            match variant with
            | "array" -> "type Payload = { Values: int array option }", "Payload", "Shared.Payload"
            | "alias" -> "type Values = int array\ntype Payload = { Values: Values list }", "Payload", "Shared.Payload"
            | "callback" ->
                "[<NoEquality; NoComparison>]\ntype Payload = { Invoke: int -> int }", "Payload", "Shared.Payload"
            | "collection" ->
                "[<NoComparison>]\ntype Payload = { Values: System.Collections.Generic.List<int> }",
                "Payload",
                "Shared.Payload"
            | "mutable-field" -> "type Payload = { mutable Value: int }", "Payload", "Shared.Payload"
            | "cli-mutable" -> "[<CLIMutable>]\ntype Payload = { Value: int }", "Payload", "Shared.Payload"
            | "resource" ->
                "type Payload = { Name: string } with\n    interface System.IDisposable with\n        member _.Dispose() = ()",
                "Payload",
                "Shared.Payload"
            | "generic" -> "type Box<'a> = { Value: 'a }", "Box`1", "Shared.Box<int array>"
            | "custom-equality" ->
                "[<CustomEquality; NoComparison>]\ntype Payload = { Value: int } with\n    override _.Equals(_) = System.IO.File.Exists \"secret\"\n    override _.GetHashCode() = 0",
                "Payload",
                "Shared.Payload"
            | "formatting-hook" ->
                "type Payload = { Value: int } with\n    override _.ToString() = System.IO.File.ReadAllText \"secret\"",
                "Payload",
                "Shared.Payload"
            | _ -> failwith "Unknown data regression."

        withDataConsumer
            definition
            ("module App\nlet inspect (value: " + target + ") = value\n")
            typeName
            (fun root policy contract ->
                let review =
                    { policy with
                        DataContracts = Some [ contract ]
                        Capabilities =
                            [ { File = "App.fs"
                                Owner = "App.inspect"
                                Kind = "type"
                                Identity = contract.Identity
                                Reason = "Existing declaration type approval must not bypass reusable data validation." } ] }

                write root review
                let report = Workspace.check root

                Assert.True(
                    report.Projects
                    |> List.exists (fun project ->
                        project.Project = Path.Combine(root, "App.fsproj") && project.Report.Complete),
                    Codec.encode report.Errors
                )

                Assert.NotEqual(0, report.ExitCode)

                Assert.Contains(
                    report.Projects |> List.collect (fun project -> project.Report.Diagnostics),
                    fun diagnostic ->
                        diagnostic.Rule = "MODEL001"
                        && diagnostic.File = Path.Combine(root, "App.fs")
                        && diagnostic.Message.Contains("recursive immutable-representation", StringComparison.Ordinal)
                )

                let advisory = Workspace.review root
                Assert.True(advisory.AnalysisComplete, Codec.encode advisory)
                Assert.NotEqual(0, advisory.ExitCode)
                Assert.Contains(advisory.Items, fun item -> item.Kind = "data" && item.Resolution = "code-change"))

    [<Fact>]
    let ``reusable data contracts do not approve methods or domain materialization`` () =
        withDataConsumer
            "type Quantity = private Quantity of int\ntype Payload = { Quantity: Quantity } with\n    member _.Read() = System.IO.File.ReadAllText \"secret\""
            "module App\nlet inspect (value: Shared.Payload) = value.Read()\nlet serialize (value: Shared.Payload) = System.Text.Json.JsonSerializer.Serialize(value)\nlet decode (text: string) = System.Text.Json.JsonSerializer.Deserialize<Shared.Payload>(text)\n"
            "Payload"
            (fun root policy contract ->
                write
                    root
                    { policy with
                        DataContracts = Some [ contract ]
                        Domains = [ "Shared.Quantity" ] }

                let report = Workspace.check root
                Assert.NotEqual(0, report.ExitCode)

                let diagnostics =
                    report.Projects
                    |> List.collect (fun project -> project.Report.Diagnostics)
                    |> List.filter (fun diagnostic -> diagnostic.File = Path.Combine(root, "App.fs"))

                Assert.Contains(
                    diagnostics,
                    fun diagnostic ->
                        diagnostic.Rule = "EFFECT001"
                        && diagnostic.Symbol.Contains("Shared.Payload.Read", StringComparison.Ordinal)
                )

                Assert.Contains(diagnostics, fun diagnostic -> diagnostic.Rule = "DATA001")
                Assert.Contains(diagnostics, fun diagnostic -> diagnostic.Rule = "DATA005"))

    [<Fact>]
    let ``an independent project verifies and mutation still fails after updating its fingerprint`` () =
        withDirectory (fun root ->
            prepare root
            let library = Path.Combine(root, "Library")
            Directory.CreateDirectory library |> ignore

            File.WriteAllText(
                Path.Combine(library, "Library.fsproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><Compile Include=\"Library.fs\"/></ItemGroup></Project>"
            )

            File.WriteAllText(Path.Combine(library, "Library.fs"), "module Library\nlet value = 1\n")
            run root [ "restore"; "Library/Library.fsproj"; "-m:1"; "-nr:false" ]

            run
                root
                [ "build"
                  "Library/Library.fsproj"
                  "-c"
                  "Release"
                  "--no-restore"
                  "-m:1"
                  "--disable-build-servers" ]

            File.WriteAllText(
                Path.Combine(root, "App.fsproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"Library/Library.fsproj\"/><Compile Include=\"App.fs\"/></ItemGroup></Project>"
            )

            run root [ "restore"; "App.fsproj"; "-m:1"; "-nr:false" ]
            let initial = read root

            let policy =
                { initial with
                    Projects =
                        (initial.Projects
                         |> List.map (fun project ->
                             { project with
                                 References = [ "Library/Library.fsproj" ] }))
                        @ [ { File = "Library/Library.fsproj"
                              Profile = "PURE"
                              References = [] } ]
                    Sources =
                        initial.Sources
                        @ [ { File = "Library/Library.fs"
                              Project = "Library/Library.fsproj"
                              Profile = "PURE"
                              Digest = SafetyPolicy.fileDigest (Path.Combine(library, "Library.fs")) } ]
                    Inputs =
                        (initial.Inputs
                         |> List.map (fun item ->
                             { item with
                                 Digest = SafetyPolicy.fileDigest (Path.Combine(root, item.File)) }))
                        @ ([ "Library/Library.fsproj"; "Library/packages.lock.json" ]
                           |> List.map (fun file ->
                               { File = file
                                 Digest = SafetyPolicy.fileDigest (Path.Combine(root, file)) })) }

            write root policy
            Assert.Empty policy.Capabilities
            Assert.Empty policy.Domains
            Assert.Empty policy.Constructors
            let report = Project.verify root
            Assert.True(report.Complete, Codec.encode report)
            Assert.Equal(0, report.ExitCode)

            let expectedProjects =
                [ Path.Combine(library, "Library.fsproj"); Path.Combine(root, "App.fsproj") ]

            Assert.True((report.Projects |> List.map _.Project) = expectedProjects)

            Assert.True(
                report.Projects
                |> List.forall (fun project -> Safety.verifyInputs project.Report)
            )

            Assert.Throws<InvalidOperationException>(fun () -> Project.initialize root |> ignore)
            |> ignore

            let file = Path.Combine(root, "App.fs")

            File.WriteAllText(
                file,
                "module App\nlet increment value =\n    let mutable count = value\n    count <- count + 1\n    count\n"
            )

            Assert.True(Workspace.validatePolicy root |> Result.isError)

            write
                root
                { policy with
                    Sources =
                        policy.Sources
                        |> List.map (fun source ->
                            if source.File = "App.fs" then
                                { source with
                                    Digest = SafetyPolicy.fileDigest file }
                            else
                                source) }

            let violation = Workspace.check root
            Assert.True(violation.Complete, Codec.encode violation)
            Assert.Equal(1, violation.ExitCode)

            Assert.Contains(
                violation.Projects |> List.collect (fun project -> project.Report.Diagnostics),
                fun diagnostic -> diagnostic.Rule = "MUT001"
            ))

    [<Fact>]
    let ``imported test project references cannot bypass the protected graph`` () =
        withDirectory (fun root ->
            prepare root
            let tests = Path.Combine(root, "Tests")
            Directory.CreateDirectory tests |> ignore

            File.WriteAllText(
                Path.Combine(tests, "Checks.fsproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><Compile Include=\"Checks.fs\"/></ItemGroup></Project>"
            )

            File.WriteAllText(Path.Combine(tests, "Checks.fs"), "module Checks\nlet value = 1\n")
            run root [ "restore"; "Tests/Checks.fsproj"; "-m:1"; "-nr:false" ]

            run
                root
                [ "build"
                  "Tests/Checks.fsproj"
                  "-c"
                  "Release"
                  "--no-restore"
                  "-m:1"
                  "--disable-build-servers" ]

            let targets = Path.Combine(root, "Directory.Build.targets")

            File.WriteAllText(
                targets,
                "<Project><ItemGroup Condition=\"'$(MSBuildProjectName)' == 'App'\"><ProjectReference Include=\"Tests/Checks.fsproj\"/></ItemGroup></Project>"
            )

            let policy = read root

            write
                root
                { policy with
                    Projects =
                        policy.Projects
                        @ [ { File = "Tests/Checks.fsproj"
                              Profile = "TEST"
                              References = [] } ]
                    Sources =
                        policy.Sources
                        @ [ { File = "Tests/Checks.fs"
                              Project = "Tests/Checks.fsproj"
                              Profile = "TEST"
                              Digest = SafetyPolicy.fileDigest (Path.Combine(tests, "Checks.fs")) } ]
                    Inputs =
                        policy.Inputs
                        @ ([ "Tests/Checks.fsproj"; "Tests/packages.lock.json"; "Directory.Build.targets" ]
                           |> List.map (fun file ->
                               { File = file
                                 Digest = SafetyPolicy.fileDigest (Path.Combine(root, file)) })) }

            Assert.Equal(Ok(), Workspace.validatePolicy root)
            let report = Workspace.check root
            Assert.NotEqual(0, report.ExitCode)

            Assert.Contains(
                report.Projects |> List.collect (fun project -> project.Report.Diagnostics),
                fun diagnostic -> diagnostic.Rule = "ARCH004"
            ))
