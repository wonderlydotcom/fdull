namespace FDull.Ci

open System
open System.Diagnostics
open System.IO
open FDull
open FDull.Transport

/// CI integration checks run from the same strict, reviewed F# graph as the guard.
module Program =
    let private require condition message =
        if not condition then
            invalidOp message

    let private run expected directory packages (arguments: string list) =
        let start = ProcessStartInfo("dotnet", WorkingDirectory = directory)

        for argument in arguments do
            start.ArgumentList.Add argument

        start.Environment["NUGET_PACKAGES"] <- packages
        start.Environment["DOTNET_CLI_HOME"] <- packages + "-cli"
        start.Environment["DOTNET_NOLOGO"] <- "1"
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] <- "1"
        start.Environment["DOTNET_GCHeapHardLimit"] <- "80000000"
        start.Environment["MSBUILDDISABLENODEREUSE"] <- "1"
        Console.WriteLine("dotnet " + String.concat " " arguments)

        match
            SafetyProcess.run
                { Milliseconds = 600000
                  OutputCharacters = 4 * 1024 * 1024
                  WorkingSetBytes = 2147483648L }
                start
                ""
        with
        | Error error -> invalidOp error
        | Ok result ->
            Console.Write result.Output
            Console.Error.Write result.Error
            require (result.ExitCode = expected) $"Expected exit %d{expected}; got %d{result.ExitCode}."
            result.Output

    let private write directory name (content: string) =
        File.WriteAllText(Path.Combine(directory, name), content)

    let private coverage root =
        let cli = Path.Combine(root, "src/FDull.Tool/bin/Release/net10.0/FDull.Cli.dll")

        let current =
            run 0 root (Path.Combine(root, "artifacts/coverage-packages")) [ cli; "coverage" ]
            |> Codec.decode<SafetyCoverageInventory>

        let published =
            File.ReadAllText(Path.Combine(root, "site/public/coverage.json"))
            |> Codec.decode<SafetyCoverageInventory>

        require (current = published) "site/public/coverage.json differs from the built CLI; review and refresh it."
        Console.WriteLine "Coverage inventory matches the built CLI."

    let private configuration (feed: string) =
        let escaped = feed.Replace("&", "&amp;").Replace("\"", "&quot;")

        "<configuration><packageSources><clear/>"
        + "<add key=\"local\" value=\""
        + escaped
        + "\"/><add key=\"nuget.org\" value=\"https://api.nuget.org/v3/index.json\"/>"
        + "</packageSources><packageSourceMapping>"
        + "<packageSource key=\"local\"><package pattern=\"FDull\"/><package pattern=\"FDull.Tool\"/>"
        + "<package pattern=\"FDull.Analyzers\"/></packageSource>"
        + "<packageSource key=\"nuget.org\"><package pattern=\"*\"/></packageSource>"
        + "</packageSourceMapping></configuration>"

    let private prepare directory defaults config source project =
        Directory.CreateDirectory directory |> ignore
        write directory "global.json" "{\"sdk\":{\"version\":\"10.0.403\",\"rollForward\":\"disable\"}}"
        write directory "Directory.Build.props" defaults
        write directory "NuGet.config" config
        write directory "App.fs" source
        write directory "App.fsproj" project

    let private checkConsumer directory packages defaults config source =
        prepare
            directory
            defaults
            config
            source
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><Compile Include=\"App.fs\"/></ItemGroup></Project>"

        run 0 directory packages [ "restore"; "App.fsproj"; "--use-lock-file"; "-m:1"; "-nr:false" ]
        |> ignore

        run 0 directory packages [ "fdull"; "init"; "." ] |> ignore

    /// Install the packed tool from `feed` as a local tool of a fresh temporary directory with an isolated
    /// package cache, then run `check` with that directory, the cache, the package version and the NuGet
    /// configuration. The directory is deleted afterwards, whatever the outcome.
    let private withTool root feed (label: string) check =
        let temporary =
            Path.Combine(Path.GetTempPath(), "fdull-" + label + "-" + Guid.NewGuid().ToString("N"))

        let packages = Path.Combine(temporary, "packages")
        Directory.CreateDirectory temporary |> ignore

        try
            let version =
                run 0 root packages [ "msbuild"; "src/FDull.Tool/FDull.Tool.fsproj"; "-getProperty:Version" ]
                |> fun output -> output.Trim()

            for package in [ "FDull"; "FDull.Analyzers"; "FDull.Tool" ] do
                require
                    (File.Exists(Path.Combine(feed, package + "." + version + ".nupkg")))
                    ("Missing package: " + package)

            let config = configuration feed
            write temporary "NuGet.config" config
            write temporary "global.json" (File.ReadAllText(Path.Combine(root, "global.json")))
            run 0 temporary packages [ "new"; "tool-manifest" ] |> ignore

            run
                0
                temporary
                packages
                [ "tool"
                  "install"
                  "FDull.Tool"
                  "--version"
                  version
                  "--configfile"
                  "NuGet.config" ]
            |> ignore

            let installedVersion = run 0 temporary packages [ "fdull"; "--version" ]
            require (installedVersion.Trim() = version) "The installed tool version does not match the local package."
            check temporary packages version config
        finally
            Directory.Delete(temporary, true)

    let private smoke root feed =
        withTool root feed "package-smoke" (fun temporary packages version config ->
            let defaults = run 0 temporary packages [ "fdull"; "defaults" ]
            let valid = Path.Combine(temporary, "valid")
            checkConsumer valid packages defaults config "module App\nlet increment value = value + 1\n"
            let verified = run 0 valid packages [ "fdull"; "verify"; "." ]

            require
                (verified.Contains("FDull verify: PASS — verification completed for the included projects."))
                "Valid consumer did not verify."

            // A changed source must remain blocked, even when it would otherwise be valid.
            write valid "App.fs" "module App\nlet increment value = value + 2\n"

            let drift =
                run 3 valid packages [ "fdull"; "lint"; "."; "--format"; "json" ]
                |> Codec.decode<WorkspaceReport>

            require (not drift.Complete && drift.ExitCode = 3) "Source drift was accepted as complete analysis."

            let invalid = Path.Combine(temporary, "invalid")
            checkConsumer invalid packages defaults config "module App\nlet mutable count = 0\n"

            let rejected =
                run 1 invalid packages [ "fdull"; "lint"; "."; "--format"; "json" ]
                |> Codec.decode<WorkspaceReport>

            require
                (rejected.Complete
                 && rejected.ExitCode = 1
                 && rejected.Projects
                    |> List.exists (fun project ->
                        project.Report.Diagnostics |> List.exists (fun item -> item.Rule = "MUT001")))
                "Mutation did not produce a complete MUT001 failure."

            let failed = run 1 invalid packages [ "fdull"; "verify"; "." ]

            require
                (failed.Contains("FDull verify: FAIL") && not (failed.Contains("BLOCKED")))
                "Mutation verification was incomplete."

            // The installed tool checks selected rules in an ordinary project without FDull settings.
            let scoped = Path.Combine(temporary, "scoped")

            prepare
                scoped
                "<Project/>"
                config
                "module App\nlet first (items: int list) = List.head items\n"
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><Compile Include=\"App.fs\"/></ItemGroup></Project>"

            run 0 scoped packages [ "build"; "App.fsproj"; "-c"; "Release"; "-m:1"; "-nr:false" ]
            |> ignore

            let checkedScope =
                run
                    1
                    scoped
                    packages
                    [ "fdull"
                      "check"
                      "App.fsproj"
                      "--rules"
                      "ERR006"
                      "App.fs"
                      "--format"
                      "json" ]

            require
                (checkedScope.Contains("\"Status\":\"fail\"")
                 && checkedScope.Contains("\"Rule\":\"ERR006\"")
                 && not (File.Exists(Path.Combine(scoped, "fdull.json"))))
                "Scoped check did not report ERR006 in an ordinary project."

            let consumer = Path.Combine(temporary, "library")

            prepare
                consumer
                defaults
                config
                "module Consumer\nopen FDull\n[<EntryPoint>]\nlet main _ =\n    let inventory = SafetyRules.inventory SafetyPolicy.version\n    if inventory.Rules.Length <> 69 || inventory.FullStandardComplete then failwith \"Invalid coverage inventory\"\n    0\n"
                ("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup><ItemGroup>"
                 + "<PackageReference Include=\"FDull\" Version=\"["
                 + version
                 + "]\"/><PackageReference Include=\"FDull.Analyzers\" Version=\"["
                 + version
                 + "]\" PrivateAssets=\"All\" IncludeAssets=\"analyzers\"/>"
                 + "<Compile Include=\"App.fs\"/></ItemGroup></Project>")

            run 0 consumer packages [ "restore"; "App.fsproj"; "--use-lock-file"; "-m:1"; "-nr:false" ]
            |> ignore

            run 0 consumer packages [ "restore"; "App.fsproj"; "--locked-mode"; "-m:1"; "-nr:false" ]
            |> ignore

            run
                0
                consumer
                packages
                [ "build"
                  "App.fsproj"
                  "-c"
                  "Release"
                  "--no-restore"
                  "-m:1"
                  "--disable-build-servers" ]
            |> ignore

            run 0 consumer packages [ "bin/Release/net10.0/App.dll" ] |> ignore

            for assembly in [ "FDull.Analyzers.dll"; "FDull.dll" ] do
                let path =
                    Path.Combine(packages, "fdull.analyzers", version, "analyzers/dotnet/fs", assembly)

                require (File.Exists path) ("Missing packaged analyzer assembly: " + assembly)

            Console.WriteLine
                "Package smoke passed: local tool, worker, positive/negative/drift checks, scoped check, library and analyzer payload.")

    /// The arguments rules_dotnet passes for an optimized net10.0 library, in its own spellings and
    /// order, with paths relative to the execution root.
    let private rulesDotnetArguments (references: string list) (sources: string list) =
        [ "--noframework"
          "--utf8output"
          "--deterministic+"
          "--nowin32manifest"
          "--nocopyfsharpcore"
          "--simpleresolution"
          "--targetprofile:netcore"
          "--nologo"
          "--highentropyva+"
          "/warnaserror+"
          "/warn:5"
          "--target:library"
          "--langversion:10.0"
          "--checknulls+"
          "--debug-"
          "--optimize+"
          "--define:TRACE"
          "--define:RELEASE"
          "--debug:portable"
          "--out:bazel-out/bin/pkg/lib/net10.0/lib.dll"
          "--pdb:bazel-out/bin/pkg/lib/net10.0/lib.pdb"
          "--refout:bazel-out/bin/pkg/lib/net10.0/ref/lib.dll"
          "--doc:bazel-out/bin/pkg/lib/net10.0/lib.xml" ]
        @ (references |> List.map (fun reference -> "-r:" + reference))
        @ sources
        @ [ "-d:NET"
            "-d:NET10_0"
            "-d:NETCOREAPP"
            "-d:NET5_0_OR_GREATER"
            "-d:NET6_0_OR_GREATER"
            "-d:NET7_0_OR_GREATER"
            "-d:NET8_0_OR_GREATER"
            "-d:NET9_0_OR_GREATER"
            "-d:NET10_0_OR_GREATER"
            "-d:NETCOREAPP1_0_OR_GREATER"
            "-d:NETCOREAPP1_1_OR_GREATER"
            "-d:NETCOREAPP2_0_OR_GREATER"
            "-d:NETCOREAPP2_1_OR_GREATER"
            "-d:NETCOREAPP2_2_OR_GREATER"
            "-d:NETCOREAPP3_0_OR_GREATER"
            "-d:NETCOREAPP3_1_OR_GREATER"
            "-d:NULLABLE"
            "--warnon:21,22,52,1178,1182,3180,3218,3391,3395,3559,3570,3579,3582,3878"
            "--checked+" ]

    /// A Bazel build runs the packed tool from the extracted package with a host `dotnet` and the one
    /// runtime its toolchain carries, which is newer than the runtime the tool pins. This check does the
    /// same on `runtime`: a Bazel-style workspace must pass, and a seeded violation must fail.
    let private bazel root feed (runtime: string) =
        withTool root feed "bazel-smoke" (fun temporary packages version config ->
            let cli =
                Path.Combine(packages, "fdull.tool/" + version + "/tools/net10.0/any/FDull.Cli.dll")

            require (File.Exists cli) "Missing packaged CLI."

            // A restored project supplies the FSharp.Core and framework references a Bazel build passes.
            let donor = Path.Combine(temporary, "donor")

            prepare
                donor
                (run 0 temporary packages [ "fdull"; "defaults" ])
                config
                "module App\nlet increment value = value + 1\n"
                "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><Compile Include=\"App.fs\"/></ItemGroup></Project>"

            run 0 donor packages [ "restore"; "App.fsproj"; "--use-lock-file"; "-m:1"; "-nr:false" ]
            |> ignore

            let references =
                match Workspace.export donor "App.fsproj" with
                | Ok invocation -> invocation.References
                | Error error -> invalidOp error

            // The execution root holds the package `pkg`, rules_dotnet's version file and the build's outputs.
            let execution = Path.Combine(temporary, "execroot")
            let package = Path.Combine(execution, "pkg")
            let generated = Path.Combine(execution, "bazel-out/bin/pkg/lib/lib")
            Directory.CreateDirectory package |> ignore
            Directory.CreateDirectory generated |> ignore
            write package "BUILD.bazel" "# Smoke package.\n"

            write
                generated
                "assemblyinfo.fs"
                ("// Generated by rules_dotnet from the target's `version` attribute.\nmodule RulesDotnetAssemblyVersionInfo\n\n"
                 + "[<assembly: System.Reflection.AssemblyVersion(\"0.1.0.0\")>]\n[<assembly: System.Reflection.AssemblyFileVersion(\"0.1.0.0\")>]\n"
                 + "[<assembly: System.Reflection.AssemblyInformationalVersion(\"0.1.0\")>]\ndo ()\n")

            write
                execution
                "lib.params"
                (rulesDotnetArguments references [ "bazel-out/bin/pkg/lib/lib/assemblyinfo.fs"; "pkg/Lib.fs" ]
                 |> String.concat "\n")

            write
                execution
                "manifest.json"
                (Codec.encode
                    { Version = 1
                      Projects =
                        [ { Name = ":lib"
                            Arguments = "lib.params" } ]
                      Files = [ "pkg/BUILD.bazel"; "pkg/Lib.fs"; "pkg/fdull.json" ] })

            // Review the source's fingerprint, then lint exactly as a Bazel action launches the tool.
            let lint expected source =
                write package "Lib.fs" source

                let pin name =
                    SafetyPolicy.fileDigest (Path.Combine(package, name))

                write
                    package
                    "fdull.json"
                    (Codec.encode
                        { Version = 3
                          Repository = None
                          Projects =
                            [ { File = ":lib"
                                Profile = "PURE"
                                References = [] } ]
                          Sources =
                            [ { File = "Lib.fs"
                                Project = ":lib"
                                Profile = "PURE"
                                Digest = pin "Lib.fs" } ]
                          Inputs =
                            [ { File = "BUILD.bazel"
                                Digest = pin "BUILD.bazel" } ]
                          External = None
                          ExcludedProjects = None
                          DataContracts = None
                          Capabilities = []
                          Domains = []
                          Constructors = [] })

                let output =
                    run
                        expected
                        execution
                        packages
                        [ "--fx-version"
                          runtime
                          cli
                          "lint"
                          "pkg"
                          "--manifest"
                          "manifest.json"
                          "--report"
                          "report.json"
                          "--format"
                          "json" ]

                require
                    (File.ReadAllText(Path.Combine(execution, "report.json")) = output)
                    "The report file differs from the tool's standard output."

                Codec.decode<WorkspaceReport> output

            let passed = lint 0 "module Lib\nlet increment value = value + 1\n"

            require
                (passed.Complete
                 && passed.ExitCode = 0
                 && passed.Projects.Length = 1
                 && passed.Projects
                    |> List.forall (fun project -> project.Report.Diagnostics.IsEmpty))
                "The Bazel-style workspace did not pass."

            let failed = lint 1 "module Lib\nlet mutable count = 0\n"

            require
                (failed.Complete
                 && failed.ExitCode = 1
                 && failed.Projects
                    |> List.exists (fun project ->
                        project.Report.Diagnostics |> List.exists (fun item -> item.Rule = "MUT001")))
                "Mutation did not produce a complete MUT001 failure."

            Console.WriteLine(
                "Bazel smoke passed on runtime "
                + runtime
                + ": packed tool, bundled worker, manifest, report file, positive and negative checks."
            ))

    [<EntryPoint>]
    let main arguments =
        try
            match Array.toList arguments with
            | [ "coverage"; root ] ->
                coverage (Path.GetFullPath root)
                0
            | [ "smoke"; root; feed ] ->
                smoke (Path.GetFullPath root) (Path.GetFullPath feed)
                0
            | [ "bazel"; root; feed; runtime ] ->
                bazel (Path.GetFullPath root) (Path.GetFullPath feed) runtime
                0
            | _ ->
                Console.Error.WriteLine
                    "Usage: FDull.Ci coverage <root> | smoke <root> <package-directory> | bazel <root> <package-directory> <runtime-version>"

                2
        with error ->
            Console.Error.WriteLine("CI check failed: " + error.Message)
            1
