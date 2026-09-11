namespace FDull.Tests

open System
open System.Diagnostics
open System.IO
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

    [<Fact>]
    let ``initialization exports restored Web SDK inputs and reviewed mixed-language scope without compiling`` () =
        withDirectory (fun root ->
            restoreConsumer root None "module App\nlet value: int = \"not an integer\"\n"

            File.WriteAllText(
                Path.Combine(root, "App.fsproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup><ItemGroup><PackageReference Include=\"Swashbuckle.AspNetCore\" Version=\"7.2.0\"/><Compile Include=\"App.fs\"/></ItemGroup></Project>"
            )

            run root [ "restore"; "App.fsproj"; "--use-lock-file"; "-m:1"; "-nr:false" ]
            Directory.CreateDirectory(Path.Combine(root, "automation")) |> ignore
            File.WriteAllText(Path.Combine(root, "automation/check.py"), "print('checked')\n")
            File.WriteAllText(Path.Combine(root, "automation/Check.cs"), "internal static class Check {}\n")
            File.WriteAllText(Path.Combine(root, "automation/Check.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />")

            File.WriteAllText(
                Path.Combine(root, "fdull.scope.json"),
                "{\"Version\":1,\"External\":[{\"Path\":\"automation\",\"Kind\":\"automation\",\"Reason\":\"Checks a deployment artifact outside the FSharp certification.\"}]}"
            )

            let policyFile = Project.initializeWithScope root (Some "fdull.scope.json")
            let policy = File.ReadAllText policyFile |> Codec.decode<WorkspacePolicyDocument>
            Assert.Equal(1, policy.External |> Option.map List.length |> Option.defaultValue 0)
            Assert.Equal(1, policy.Sources.Length)
            Assert.Contains(policy.Sources, fun source -> source.File = "App.fs")
            Assert.Contains(policy.Inputs, fun input -> input.File = "fdull.scope.json")
            Assert.Contains(policy.Inputs, fun input -> input.File = "automation/Check.csproj"))

    [<Fact>]
    let ``the FSharp Core 10 Web test SDK and xUnit v3 consumer profiles are supported`` () =
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

    let private write root policy =
        File.WriteAllText(Path.Combine(root, "fdull.json"), Codec.encode policy)

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
