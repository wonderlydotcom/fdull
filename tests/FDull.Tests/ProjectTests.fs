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

    let private restoreConsumer (root: string) (coreVersion: string option) (source: string) =
        File.WriteAllText(
            Path.Combine(root, "global.json"),
            "{\"sdk\":{\"version\":\"10.0.200\",\"rollForward\":\"disable\"}}"
        )

        File.WriteAllText(Path.Combine(root, "Directory.Build.props"), Project.defaults ())

        File.WriteAllText(
            Path.Combine(root, "NuGet.config"),
            "<configuration><packageSources><clear/><add key=\"nuget.org\" value=\"https://api.nuget.org/v3/index.json\"/></packageSources></configuration>"
        )

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

    let private runCli root (arguments: string list) =
        let cli =
            Path.GetFullPath(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "../../../../../src/FDull.Tool/bin/Release/net10.0/FDull.Cli.dll"
                )
            )

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
            Assert.StartsWith("FDull verify: PASS — verification completed.", verified.Output)

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

            let helper =
                report.Items
                |> List.filter (fun item -> item.Owner = "App.helper")
                |> Assert.Single

            Assert.Equal("PURE_HELPER", helper.Profile)
            Assert.Equal("member", helper.Kind)
            Assert.Equal("contract-review", helper.Resolution)
            let text = WorkspaceReview.format report
            Assert.Contains("ADVISORY", text)
            Assert.Contains("Type approval does not approve the type's methods.", text)
            Assert.Contains("this finding cannot be resolved by an exemption in this profile", text)
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

            File.AppendAllText(Path.Combine(root, "Directory.Build.props"), "\n")
            let buildDrift = Workspace.review root
            Assert.False(buildDrift.AnalysisComplete)
            Assert.Equal(3, buildDrift.ExitCode)
            Assert.Empty(buildDrift.Projects)
            Assert.Contains(buildDrift.Errors, fun error -> error.StartsWith("BUILD005:", StringComparison.Ordinal)))

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

            let host = Path.Combine(root, "Host")
            Directory.CreateDirectory host |> ignore

            File.WriteAllText(
                Path.Combine(host, "Host.fsproj"),
                "<Project Sdk=\"Aspire.AppHost.Sdk/13.4.6\"><PropertyGroup><OutputType>Library</OutputType><SkipValidateAspireHostProjectResources>true</SkipValidateAspireHostProjectResources></PropertyGroup><ItemGroup><Compile Include=\"Host.fs\"/></ItemGroup><ItemGroup><PackageReference Include=\"Aspire.Hosting.AppHost\" Version=\"13.4.6\"/><PackageReference Include=\"FSharp.Aspire.Hosting.AppHost\" Version=\"13.0.0\"/></ItemGroup><ItemGroup><ProjectReference Include=\"../App.fsproj\"/></ItemGroup><Target Name=\"TamperAspireMetadata\" AfterTargets=\"WriteAspireProjectMetadataSources\" BeforeTargets=\"CoreCompile\" Condition=\"Exists('tamper-generated-source')\"><WriteLinesToFile File=\"$(IntermediateOutputPath)Aspire/references/App.ProjectMetadata.g.fs\" Lines=\"// tampered\" Overwrite=\"false\"/></Target></Project>"
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
                Assert.NotEqual(0, report.ExitCode)

                Assert.Contains(
                    report.Projects |> List.collect (fun project -> project.Report.Diagnostics),
                    fun diagnostic ->
                        diagnostic.Rule = "MODEL001"
                        && diagnostic.File = Path.Combine(root, "App.fs")
                        && diagnostic.Message.Contains("recursive immutable-representation", StringComparison.Ordinal)
                )

                let advisory = Workspace.review root
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
