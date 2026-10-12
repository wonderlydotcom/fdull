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
        write directory "global.json" "{\"sdk\":{\"version\":\"10.0.200\",\"rollForward\":\"disable\"}}"
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

    let private smoke root feed =
        let temporary =
            Path.Combine(Path.GetTempPath(), "fdull-package-smoke-" + Guid.NewGuid().ToString("N"))

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
            let defaults = run 0 temporary packages [ "fdull"; "defaults" ]
            let valid = Path.Combine(temporary, "valid")
            checkConsumer valid packages defaults config "module App\nlet increment value = value + 1\n"
            let verified = run 0 valid packages [ "fdull"; "verify"; "." ]
            require (verified.Contains("FDull verify: PASS — verification completed.")) "Valid consumer did not verify."

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
                "Package smoke passed: local tool, worker, positive/negative/drift checks, library and analyzer payload."
        finally
            Directory.Delete(temporary, true)

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
            | _ ->
                Console.Error.WriteLine "Usage: FDull.Ci coverage <root> | smoke <root> <package-directory>"
                2
        with error ->
            Console.Error.WriteLine("CI check failed: " + error.Message)
            1
