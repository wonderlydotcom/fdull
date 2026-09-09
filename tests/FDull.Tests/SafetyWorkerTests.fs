namespace FDull.Tests

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Text
open FsCheck
open Xunit
open FDull
open FDull.Transport

module SafetyWorkerTests =
    let private temporary action =
        let directory =
            Path.Combine(Path.GetTempPath(), "fdull-worker-test-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory directory |> ignore

        try
            action directory
        finally
            Directory.Delete(directory, true)

    let private digest (text: string) =
        Encoding.UTF8.GetBytes text |> SafetyPolicy.digest

    let private protocol action =
        temporary (fun directory ->
            let source = Path.Combine(directory, "App.fs")
            File.WriteAllText(source, "module Fixture\nlet value = 1\n")

            let snapshot path =
                { Path = path
                  Digest = SafetyPolicy.fileDigest path }

            let request =
                { Version = 1
                  Directory = directory
                  Output = Path.Combine(directory, "Product.dll")
                  Sources = [ snapshot source ]
                  References = [ snapshot typeof<unit>.Assembly.Location ] }

            let text = Codec.encode request
            let tool = SafetyPolicy.fileDigest typeof<SafetyReport>.Assembly.Location

            let report =
                { Policy = SafetyPolicy.version
                  Tool = tool
                  Complete = true
                  ExitCode = 0
                  Sources = request.Sources
                  References = request.References
                  Arguments = SafetyPolicy.arguments (request.References |> List.map _.Path) directory request.Output
                  Diagnostics = [] }

            let response =
                { Version = 1
                  Request = digest text
                  Report = report }

            let validate (response: SafetyResponse) exitCode =
                Safety.validateResponse
                    request
                    text
                    tool
                    { ExitCode = exitCode
                      Output = Codec.encode response
                      Error = "" }

            action request text tool response validate)

    [<Fact>]
    let ``a bound complete response preserves its report`` () =
        protocol (fun _ _ _ response validate -> Assert.Equal<SafetyReport>(response.Report, validate response 0))

    [<Fact>]
    let ``changing any report authority binding cannot preserve success`` () =
        protocol (fun _ _ _ response validate ->
            Check.One(
                { Config.QuickThrowOnFailure with
                    MaxTest = 250 },
                fun (NonNegativeInt field) (NonEmptyString salt) ->
                    let different text = text + ":changed:" + salt

                    let changed =
                        match field % 10 with
                        | 0 -> { response with Version = 2 }
                        | 1 ->
                            { response with
                                Request = different response.Request }
                        | 2 ->
                            { response with
                                Report =
                                    { response.Report with
                                        Policy = different response.Report.Policy } }
                        | 3 ->
                            { response with
                                Report =
                                    { response.Report with
                                        Tool = different response.Report.Tool } }
                        | 4 ->
                            { response with
                                Report = { response.Report with Sources = [] } }
                        | 5 ->
                            let source =
                                response.Report.Sources
                                |> List.tryHead
                                |> Fixtures.requireSome "reported source"

                            { response with
                                Report =
                                    { response.Report with
                                        Sources =
                                            [ { source with
                                                  Digest = different source.Digest } ] } }
                        | 6 ->
                            { response with
                                Report = { response.Report with References = [] } }
                        | 7 ->
                            { response with
                                Report =
                                    { response.Report with
                                        Arguments = response.Report.Arguments @ [ different "--nowarn" ] } }
                        | 8 ->
                            { response with
                                Report =
                                    { response.Report with
                                        Complete = false } }
                        | _ ->
                            { response with
                                Report = { response.Report with ExitCode = 1 } }

                    let report = validate changed 0
                    not report.Complete && (report.ExitCode = 3 || report.ExitCode = 4)
            ))

    [<Theory>]
    [<InlineData(0, "")>]
    [<InlineData(0, "{}")>]
    [<InlineData(0, "null")>]
    [<InlineData(0, "{\"Version\":1")>]
    [<InlineData(137, "")>]
    [<InlineData(-1, "out of memory")>]
    let ``crash or malformed output cannot certify inputs`` exitCode output =
        protocol (fun request text tool _ _ ->
            let report =
                Safety.validateResponse
                    request
                    text
                    tool
                    { ExitCode = exitCode
                      Output = output
                      Error = "" }

            Assert.False report.Complete
            Assert.Equal(3, report.ExitCode)
            Assert.Contains(report.Diagnostics, fun d -> d.Rule = "BUILD003"))

    [<Fact>]
    let ``a valid report cannot hide a failing process exit`` () =
        protocol (fun _ _ _ response validate ->
            let report = validate response 17
            Assert.False report.Complete
            Assert.Equal(3, report.ExitCode))

    [<Theory>]
    [<InlineData("rule")>]
    [<InlineData("profile")>]
    [<InlineData("severity")>]
    [<InlineData("range")>]
    let ``unknown or invalid diagnostic classifications make analysis incomplete`` field =
        protocol (fun request text tool response validate ->
            let failure =
                Safety.validateResponse
                    request
                    text
                    tool
                    { ExitCode = 3
                      Output = ""
                      Error = "" }

            let originalDiagnostic =
                failure.Diagnostics |> List.tryHead |> Fixtures.requireSome "failure diagnostic"

            let diagnostic =
                { originalDiagnostic with
                    Rule = "MUT001" }

            let changed =
                match field with
                | "rule" -> { diagnostic with Rule = "UNKNOWN001" }
                | "profile" -> { diagnostic with Profile = "TRUSTED" }
                | "severity" -> { diagnostic with Severity = "warning" }
                | _ ->
                    { diagnostic with
                        StartLine = 10
                        EndLine = 1 }

            let report =
                validate
                    { response with
                        Report =
                            { response.Report with
                                ExitCode = 1
                                Diagnostics = [ changed ] } }
                    1

            Assert.False report.Complete
            Assert.Equal(3, report.ExitCode))

    [<Fact>]
    let ``changing source after worker analysis invalidates its response`` () =
        protocol (fun request _ _ response validate ->
            let source =
                request.Sources |> List.tryHead |> Fixtures.requireSome "requested source"

            File.AppendAllText(source.Path, "let mutable state = 1\n")
            let report = validate response 0
            Assert.False report.Complete
            Assert.Equal(4, report.ExitCode))

    [<Fact>]
    let ``duplicate or empty inventories cannot be authority`` () =
        protocol (fun request _ tool response _ ->
            for sources in [ []; request.Sources @ request.Sources ] do
                let changed = { request with Sources = sources }
                let text = Codec.encode changed

                let reply =
                    { response with
                        Request = digest text
                        Report =
                            { response.Report with
                                Sources = sources } }

                let report =
                    Safety.validateResponse
                        changed
                        text
                        tool
                        { ExitCode = 0
                          Output = Codec.encode reply
                          Error = "" }

                Assert.False report.Complete
                Assert.Equal(3, report.ExitCode))

    let private runtimeRoot =
        let rec parent count (directory: DirectoryInfo) =
            if count = 0 then
                directory.FullName
            else
                directory.Parent
                |> FDull.Transport.External.required "test.runtime-parent"
                |> parent (count - 1)

        parent 3 (DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory()))

    let private limits =
        { Milliseconds = 5000
          OutputCharacters = 4096
          WorkingSetBytes = SafetyLimits.workingSetBytes }

    let private runFixture mode processLimits action =
        temporary (fun directory ->
            let marker = Path.Combine(directory, "pid")

            let start =
                ProcessStartInfo(Path.Combine(runtimeRoot, "dotnet"), WorkingDirectory = directory)
            // The compiled, linted test harness uses the production worker runtime
            // configuration, including its heap limit.
            for argument in
                [ "exec"
                  "--runtimeconfig"
                  Path.Combine(AppContext.BaseDirectory, "safety-worker/fdull-worker.runtimeconfig.json")
                  Path.GetFullPath(
                      Path.Combine(
                          __SOURCE_DIRECTORY__,
                          "../FDull.ProcessFixture/bin/Release/net10.0/FDull.ProcessFixture.dll"
                      )
                  )
                  mode
                  marker ] do
                start.ArgumentList.Add argument

            start.Environment.Clear()
            start.Environment["DOTNET_ROOT"] <- runtimeRoot
            start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] <- "1"
            let result = SafetyProcess.run processLimits start ""
            action result marker)

    let private assertStopped marker =
        Assert.True(File.Exists marker, "The controlled process failure fixture did not start.")

        let pid =
            match Int32.TryParse(File.ReadAllText marker) with
            | true, value when value > 0 -> value
            | _ -> failwith "The process fixture did not write a valid process identifier."

        let running =
            try
                use child = Process.GetProcessById pid
                not child.HasExited
            with :? ArgumentException ->
                false

        Assert.False(running, "The rejected worker survived supervision.")

    [<Theory>]
    [<InlineData("timeout", "time budget")>]
    [<InlineData("stdout", "output")>]
    [<InlineData("stderr", "output")>]
    let ``bounded process failures terminate the worker`` mode reason =
        runFixture mode limits (fun result marker ->
            match result with
            | Ok value -> Assert.Fail $"Unexpected successful supervision: %d{value.ExitCode}"
            | Error message -> Assert.Contains(reason, message)

            assertStopped marker)

    [<Fact>]
    let ``working set overflow stops the process`` () =
        runFixture "timeout" { limits with WorkingSetBytes = 1L } (fun result _ ->
            match result with
            | Ok _ -> Assert.Fail "The working set limit was ignored."
            | Error message -> Assert.Contains("working-set budget", message))

    [<Fact>]
    let ``worker runtime configuration enforces the managed heap limit`` () =
        runFixture "heap-limit" limits (fun result marker ->
            match result with
            | Ok value ->
                Assert.Equal(0, value.ExitCode)
                Assert.Equal(string SafetyLimits.managedHeapBytes, value.Output)
            | Error message -> Assert.Fail message

            assertStopped marker)

    [<Fact>]
    let ``supervisor retains nonzero exit and error output`` () =
        runFixture "exit" limits (fun result marker ->
            match result with
            | Ok value ->
                Assert.Equal(17, value.ExitCode)
                Assert.Equal("deliberate worker failure", value.Error)
            | Error message -> Assert.Fail message

            assertStopped marker)

    [<Fact>]
    let ``an allocation beyond the worker heap ceiling fails inside the child`` () =
        runFixture "oom" limits (fun result marker ->
            match result with
            | Ok value ->
                Assert.NotEqual(0, value.ExitCode)
                Assert.Contains("OutOfMemoryException", value.Error)
            | Error message -> Assert.Fail message

            assertStopped marker)

    let private compilerPolicy properties action =
        temporary (fun directory ->
            let platform = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../.."))
            let project = Path.Combine(directory, "Policy.proj")

            File.WriteAllText(
                project,
                $"<Project><Import Project=\"%s{platform}/Directory.Build.props\"/><Import Project=\"%s{platform}/Directory.Build.targets\"/></Project>"
            )

            let start =
                ProcessStartInfo(Path.Combine(runtimeRoot, "dotnet"), WorkingDirectory = directory)

            for argument in [ "msbuild"; project; "--nologo"; "-t:FDullCompilerPolicy" ] @ properties do
                start.ArgumentList.Add argument

            start.Environment.Clear()
            start.Environment["DOTNET_ROOT"] <- runtimeRoot
            start.Environment["DOTNET_GCHeapHardLimit"] <- SafetyLimits.managedHeapBytes.ToString("X")
            SafetyProcess.run { limits with OutputCharacters = 16384 } start "" |> action)

    [<Fact>]
    let ``compiler policy target accepts the pinned platform defaults`` () =
        compilerPolicy [] (fun result ->
            match result with
            | Ok value -> Assert.Equal(0, value.ExitCode)
            | Error message -> Assert.Fail message)

    [<Theory>]
    [<InlineData("LangVersion=preview")>]
    [<InlineData("TreatWarningsAsErrors=false")>]
    [<InlineData("WarningsNotAsErrors=25")>]
    [<InlineData("NoWarn=25")>]
    [<InlineData("WarningLevel=0")>]
    [<InlineData("WarnOn=25")>]
    [<InlineData("OtherFlags=--checked+")>]
    [<InlineData("OtherFlags=--checknulls+")>]
    [<InlineData("OtherFlags=--checked-")>]
    let ``compiler policy target rejects drift and downgrades`` property =
        compilerPolicy [ "-p:" + property ] (fun result ->
            match result with
            | Ok value ->
                Assert.NotEqual(0, value.ExitCode)
                Assert.Contains("BUILD002", value.Output + value.Error)
            | Error message -> Assert.Fail message)
