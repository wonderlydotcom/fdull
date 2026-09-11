namespace FDull.Tests

open System
open System.IO
open System.Runtime.InteropServices
open Xunit
open FDull

module SafetyTests =
    let private describe report =
        report.Diagnostics
        |> List.map (fun d -> SafetyPolicy.format d + " " + d.Symbol)
        |> String.concat "\n"

    let private references =
        let runtimeRoot =
            let rec parent count (directory: DirectoryInfo) =
                if count = 0 then
                    directory.FullName
                else
                    directory.Parent
                    |> FDull.Transport.External.required "test.runtime-parent"
                    |> parent (count - 1)

            parent 3 (DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory()))

        Directory.GetFiles(Path.Combine(runtimeRoot, "packs/Microsoft.NETCore.App.Ref/10.0.4/ref/net10.0"), "*.dll")
        |> Array.sort
        |> Array.toList
        |> fun refs ->
            refs
            @ [ Path.Combine(
                    runtimeRoot,
                    "packs/Microsoft.AspNetCore.App.Ref/10.0.4/ref/net10.0/Microsoft.AspNetCore.Mvc.Core.dll"
                )
                typeof<unit>.Assembly.Location ]

    let private checkFilesWith analyze (sources: (string * string) list) checkReport =
        let root =
            Path.Combine(Path.GetTempPath(), "fdull-safety-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory root |> ignore

        try
            let files =
                sources
                |> List.map (fun (name, source) ->
                    let file = Path.Combine(root, name)
                    File.WriteAllText(file, source)
                    file)

            analyze references root (Path.Combine(root, "Fixture.dll")) files |> checkReport
        finally
            Directory.Delete(root, true)

    let private checkFiles sources checkReport =
        checkFilesWith SafetyEngine.check sources checkReport

    let private check source =
        checkFiles [ "App.fs", "module SafetyFixture\n" + source + "\n" ] id

    [<Theory>]
    [<InlineData("type Quantity = Quantity of int", true)>]
    [<InlineData("type Quantity = private Quantity of int", false)>]
    let ``protected invariant designations require private representations`` source rejected =
        let analyze (references: string list) directory output (files: string list) =
            let input path =
                { Path = path
                  Digest = SafetyPolicy.fileDigest path }

            let project = Path.Combine(directory, "Fixture.fsproj")
            File.WriteAllText(project, "<Project />")
            let generatedDirectory = Path.Combine(directory, "obj/Release/net10.0")
            Directory.CreateDirectory generatedDirectory |> ignore

            let standardGenerated =
                [ Path.Combine(generatedDirectory, ".NETCoreApp,Version=v10.0.AssemblyAttributes.fs")
                  Path.Combine(generatedDirectory, "Fixture.AssemblyInfo.fs") ]

            for file in standardGenerated do
                File.WriteAllText(file, "namespace FixtureMetadata\n")

            let mvcGenerated =
                Path.Combine(generatedDirectory, "Fixture.MvcApplicationPartsAssemblyInfo.fs")

            File.WriteAllText(
                mvcGenerated,
                "namespace FSharp\n[<assembly: Microsoft.AspNetCore.Mvc.ApplicationParts.ApplicationPartAttribute(\"Fixture.Part\")>]\ndo ()\n"
            )

            let generated = standardGenerated @ [ mvcGenerated ]

            let policy: WorkspacePolicyDocument =
                { Version = 1
                  Projects =
                    [ { File = "Fixture.fsproj"
                        Profile = "PURE"
                        References = [] } ]
                  Sources =
                    files
                    |> List.map (fun file ->
                        { File =
                            Path.GetFileName file
                            |> FDull.Transport.External.required "test.workspace-source"
                          Project = "Fixture.fsproj"
                          Profile = "PURE"
                          Digest = SafetyPolicy.fileDigest file })
                  Inputs =
                    [ { File = "Fixture.fsproj"
                        Digest = SafetyPolicy.fileDigest project } ]
                  External = None
                  Capabilities = []
                  Domains = [ "SafetyFixture.Quantity" ]
                  Constructors = [] }

            let policyPath = Path.Combine(directory, "fdull.json")

            Path.GetDirectoryName policyPath
            |> FDull.Transport.External.required "test.workspace-policy-parent"
            |> Directory.CreateDirectory
            |> ignore

            File.WriteAllText(policyPath, FDull.Transport.Codec.encode policy)

            let request =
                { Root = directory
                  Project = project
                  ProjectReferences = []
                  Arguments =
                    WorkspaceCompilerPolicy.required
                    @ [ "--warnaserror"; "--target:library"; "-o:" + output ]
                    @ (references |> List.map (fun path -> "-r:" + path))
                  Sources = generated @ files |> List.map input
                  References = references |> List.map input
                  Generated = generated
                  BuildInputs = [ input project ]
                  Policy = input policyPath }

            (WorkspaceEngine.check request).Report

        let report =
            checkFilesWith analyze [ "App.fs", "module SafetyFixture\n" + source + "\n" ] id

        Assert.True(report.Complete, describe report)

        Assert.Equal(
            rejected,
            report.Diagnostics
            |> List.exists (fun diagnostic -> diagnostic.Rule = "MODEL002")
        )

        if not rejected then
            Assert.Equal(0, report.ExitCode)

    [<Fact>]
    let ``a domain value inside a predicate does not change the list match type`` () =
        let report =
            check
                "type Key = Key of string\ntype Row = { Key: Key }\nlet exactlyOne key rows =\n    match List.filter (fun row -> row.Key = key) rows with\n    | [_] -> true\n    | _ -> false"

        Assert.DoesNotContain(report.Diagnostics, fun diagnostic -> diagnostic.Rule = "MODEL004")
        Assert.True(report.Complete, describe report)

    [<Fact>]
    let ``constructing a union in match branches does not change the option scrutinee`` () =
        let report =
            check
                "type Decision = Yes | No\nlet decide previous =\n    match previous with\n    | Some true -> Yes\n    | _ -> No"

        Assert.DoesNotContain(report.Diagnostics, fun diagnostic -> diagnostic.Rule = "MODEL004")
        Assert.True(report.Complete, describe report)

    [<Theory>]
    [<InlineData("MUT001", "let unused () =\n    let mutable x = 1\n    x <- x + 1\n    x")>]
    [<InlineData("MUT002", "let write (values: int array) = values[0] <- 2")>]
    [<InlineData("MUT003", "type Value = { mutable Count: int }")>]
    [<InlineData("MUT004", "let counter = ref 0")>]
    [<InlineData("MUT005", "type Bag<'a> = System.Collections.Generic.List<'a>\nlet bag = Bag<int>()")>]
    [<InlineData("MUT006", "let values = System.Collections.Generic.List<int>()\nlet append = values.Add")>]
    [<InlineData("MUT007", "let mutable shared = 1")>]
    [<InlineData("MUT010", "[<NoComparison>]\ntype Nested = { Items: System.Collections.Generic.List<int> list }")>]
    [<InlineData("MUT008", "type Nested = { Values: int array list }")>]
    [<InlineData("MUT008", "[<NoComparison>]\ntype View = { Values: System.Collections.Generic.IReadOnlyList<int> }")>]
    [<InlineData("MUT009", "let loop () = while false do ()")>]
    [<InlineData("MUT009", "let loop () = for _i = 0 to 2 do ()")>]
    [<InlineData("CAST001", "let cast (value: obj) = value :?> string")>]
    [<InlineData("CAST001", "let cast (value: obj) : string = downcast value")>]
    [<InlineData("CAST002", "module O = Microsoft.FSharp.Core.Operators\nlet recover = O.unbox<string>")>]
    [<InlineData("CAST002", "let recover values = List.map (unbox<string>) values")>]
    [<InlineData("CAST003", "let cast values = Seq.cast<string> values")>]
    [<InlineData("CAST004", "[<NoComparison>]\ntype Payload = { Value: Map<string, obj> }")>]
    [<InlineData("CAST004", "let erase (value: string) : obj = value")>]
    [<InlineData("CAST005", "let cast value = tryUnbox<string> value")>]
    [<InlineData("CAST006",
                 "type Value =\n    | Value of int\n    static member op_Implicit(Value value) : int = value\nlet convert value = Value.op_Implicit value")>]
    [<InlineData("CAST007", "let narrow value = byte value")>]
    [<InlineData("NULL001", "let text: string | null = null")>]
    [<InlineData("NULL002", "let empty = Unchecked.defaultof<int>")>]
    [<InlineData("NULL003", "let force (value: string | null) = Unchecked.nonNull value")>]
    [<InlineData("NULL004", "type Value = { Name: string }\nlet empty = Array.zeroCreate<Value> 1")>]
    [<InlineData("NULL005", "type Data = { Name: string | null }")>]
    [<InlineData("ATTR001", "[<System.Obsolete>]\nlet value = 1")>]
    [<InlineData("ATTR002", "[<CLIMutable>]\ntype Value = { Name: string }")>]
    [<InlineData("ATTR003",
                 "[<CompilationRepresentation(CompilationRepresentationFlags.UseNullAsTrueValue)>]\ntype Value = Missing | Present of string")>]
    [<InlineData("ATTR004", "[<System.CodeDom.Compiler.GeneratedCode(\"fake\", \"1\")>]\nlet value = 1")>]
    [<InlineData("REFL001", "let metadata = typeof<string>")>]
    [<InlineData("REFL002", "let construct () = System.Activator.CreateInstance<int>()")>]
    [<InlineData("REFL003", "let construct t = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject t")>]
    [<InlineData("REFL004", "let code = <@ 1 + 2 @>")>]
    [<InlineData("NATIVE003", "let native (value: nativeint) = value")>]
    [<InlineData("NATIVE001", "[<System.Runtime.InteropServices.DllImport(\"missing\")>]\nextern int nativeCall()")>]
    [<InlineData("NATIVE002", "let size () = System.Runtime.CompilerServices.Unsafe.SizeOf<int>()")>]
    [<InlineData("DATA002", "let decode text = System.Text.Json.JsonSerializer.Deserialize<string>(text: string)")>]
    [<InlineData("DATA005",
                 "type Quantity = private Quantity of int\nlet encode (value: Quantity) = System.Text.Json.JsonSerializer.Serialize value")>]
    [<InlineData("DATA005",
                 "type Quantity = private Quantity of int\ntype Envelope = { Items: Quantity list }\nlet encode (value: Envelope) = System.Text.Json.JsonSerializer.Serialize value")>]
    [<InlineData("DATA001",
                 "type Quantity = private Quantity of int\nlet decode text = System.Text.Json.JsonSerializer.Deserialize<Quantity>(text: string)")>]
    [<InlineData("DATA001",
                 "type Quantity = private Quantity of int\ntype Envelope = { Items: Quantity list }\nlet decode text = System.Text.Json.JsonSerializer.Deserialize<Envelope>(text: string)")>]
    [<InlineData("DATA003",
                 "let decode<'a when 'a: not struct and 'a: not null> (text: string) : 'a | null = System.Text.Json.JsonSerializer.Deserialize<'a>(text)")>]
    [<InlineData("DATA004",
                 "[<NoComparison; NoEquality>]\ntype Payload = { Decode: int -> int }\nlet decode (text: string) = System.Text.Json.JsonSerializer.Deserialize<Payload> text")>]
    [<InlineData("MODEL001", "type MutableLike() = member _.Value = 1")>]
    [<InlineData("MODEL003", "type Quantity = private Quantity of int\nlet innocent value = Ok (Quantity value)")>]
    [<InlineData("MODEL004",
                 "type Status = Draft | Approved\nlet accept (status: Status) = match status with _ -> false")>]
    [<InlineData("MODEL004", "type Status = Draft | Approved\nlet accept: Status -> bool = function _ -> false")>]
    [<InlineData("MODEL004",
                 "type Status = Draft | Approved\nlet accept (status: Status) = match status, 1 with _, _ -> false")>]
    [<InlineData("MODEL004",
                 "type Status = Draft | Approved | Rejected\nlet accept = function Approved -> true | _ -> false")>]
    [<InlineData("MODEL004",
                 "type Status = Draft | Approved | Rejected\nlet accept status = match status with Approved -> true | other -> other = Draft")>]
    [<InlineData("EFFECT001", "let output = System.String.Intern \"value\"")>]
    [<InlineData("EFFECT002", "let now = System.DateTimeOffset.UtcNow")>]
    [<InlineData("EFFECT003", "[<NoComparison; NoEquality>]\ntype Context = { Clock: unit -> int64 }")>]
    [<InlineData("EFFECT004", "let (|Positive|Negative|) value = if value > 0 then Positive else Negative")>]
    [<InlineData("ERR001", "let catch () = try 1 with _ -> 0")>]
    [<InlineData("ERR004", "let recover () = try 1 with _ -> 0")>]
    [<InlineData("ERR002", "let fail () : int = failwith \"failure\"")>]
    [<InlineData("ERR003", "let catch operation = Async.Catch operation")>]
    [<InlineData("ERR005", "[<NoComparison>]\ntype Error = Error of exn")>]
    [<InlineData("ERR006", "let first values = List.head values")>]
    [<InlineData("ERR006", "let first (values: int list) = values.Head")>]
    [<InlineData("ERR006", "let lookup key (values: Map<string,int>) = values[key]")>]
    [<InlineData("ERR006", "let last values = List.findBack (fun value -> value > 0) values")>]
    [<InlineData("ERR006", "let parse value = Ok (System.Int32.Parse(value: string))")>]
    [<InlineData("ERR007", "let discard () = (Ok 1: Result<int,string>) |> ignore")>]
    [<InlineData("ERR007", "let discard = ignore\nlet test () = discard (Error \"bad\": Result<int,string>)")>]
    [<InlineData("ERR007", "let discard () = let _ = (Error \"bad\": Result<int,string>) in ()")>]
    [<InlineData("ERR008", "let recover value = Result.defaultValue 1 value")>]
    [<InlineData("ERR009", "let cleanup () = try 1 finally ()")>]
    [<InlineData("ERR009", "let cleanup () = use file = new System.IO.MemoryStream() in file.Length")>]
    [<InlineData("ERR010", "let wait (task: System.Threading.Tasks.Task<int>) = task.Result")>]
    [<InlineData("STYLE001", "let backwards = (<|)")>]
    [<InlineData("STYLE001", "let value = id <| 1")>]
    [<InlineData("STYLE002", "let value = (1, 2) ||> (+)")>]
    [<InlineData("STYLE002", "let (|>) value functionValue = functionValue value")>]
    [<InlineData("STYLE003", "module L = Microsoft.FSharp.Collections.List\nlet total values = L.fold (+) 0 values")>]
    [<InlineData("STYLE003", "let scan values = List.scan (+) 0 values")>]
    [<InlineData("BUILD001", "#nowarn \"25\"\nlet value = 1")>]
    [<InlineData("BUILD001", "// fsharplint:disable-next-line\nlet value = 1")>]
    [<InlineData("BUILD001", "// fsharpanalyzer: ignore-line STYLE001\nlet value = 1")>]
    [<InlineData("BUILD001", "#if DEBUG\nlet value = 1\n#else\nlet value = 2\n#endif")>]
    let ``valid FSharp violations have canonical diagnostics and physical locations`` rule source =
        let report = check source
        Assert.NotEqual(0, report.ExitCode)
        Assert.True(report.Complete, describe report)
        Assert.True(report.Diagnostics |> List.exists (fun d -> d.Rule = rule), describe report)

        let diagnostic =
            report.Diagnostics
            |> List.tryFind (fun d -> d.Rule = rule)
            |> Fixtures.requireSome ("Missing diagnostic: " + rule)

        Assert.EndsWith("App.fs", diagnostic.File)
        Assert.True(diagnostic.StartLine >= 2)
        Assert.Equal("error", diagnostic.Severity)

    [<Theory>]
    [<InlineData("type Value = { Count: int }\nlet increment value = { value with Count = value.Count + 1 }")>]
    [<InlineData("let doubled (values: int list) = [ for value in values do yield value * 2 ]")>]
    [<InlineData("let transform values = values |> List.map (fun value -> value + 1) |> List.filter (fun value -> value > 2)")>]
    [<InlineData("let map = Map.ofList [ \"one\", 1 ] |> Map.add \"two\" 2\nlet set = Set.ofList [1;2] |> Set.add 3")>]
    [<InlineData("type Status = Draft | Approved of int | Rejected\nlet accept = function Approved _ -> true | Draft | Rejected -> false")>]
    [<InlineData("let compareNumber value = match value with 1 -> true | _ -> false")>]
    [<InlineData("let text = \"unsupported | value\"")>]
    [<InlineData("let text = \"<| ||> let mutable x #nowarn\"\n// An explanation of <| and #nowarn is not a suppression.\nlet value = Some 1 |> Option.defaultValue 0")>]
    [<InlineData("let first values = List.tryFind (fun value -> value > 0) values\nlet last values = List.tryFindBack (fun value -> value > 0) values")>]
    let ``approved immutable source remains valid`` source =
        let report = check source
        Assert.True(report.Complete && report.ExitCode = 0, describe report)

    [<Fact>]
    let ``use disposal lowering does not introduce authored boxing`` () =
        let report =
            check "let length () = use stream = new System.IO.MemoryStream() in stream.Length"

        Assert.True(report.Complete, describe report)
        Assert.Contains(report.Diagnostics, fun diagnostic -> diagnostic.Rule = "ERR009")
        Assert.DoesNotContain(report.Diagnostics, fun diagnostic -> diagnostic.Rule = "CAST004")

    [<Fact>]
    let ``authored boxing inside a use body is still rejected`` () =
        let report =
            check "let erase () = use stream = new System.IO.MemoryStream() in box stream"

        Assert.True(report.Complete, describe report)
        Assert.Contains(report.Diagnostics, fun diagnostic -> diagnostic.Rule = "CAST004")

    [<Fact>]
    let ``missing input fails closed with an analysis exit code`` () =
        let report =
            Safety.check references (Path.GetTempPath()) "Unused.dll" [ "/missing/App.fs" ]

        Assert.False(report.Complete)
        Assert.Equal(3, report.ExitCode)
        Assert.Contains(report.Diagnostics, fun d -> d.Rule = "BUILD003")

    [<Fact>]
    let ``unsupported type nesting cannot report complete analysis`` () =
        let report = check ("type Payload = int" + String.replicate 70 " list")
        Assert.False(report.Complete)
        Assert.Equal(3, report.ExitCode)
        Assert.Contains(report.Diagnostics, fun d -> d.Rule = "BUILD003")

    [<Fact>]
    let ``unsupported expression depth fails before typed traversal`` () =
        let report =
            check ("let value = " + String.replicate 300 "(" + "1" + String.replicate 300 ")")

        Assert.False report.Complete
        Assert.Equal(3, report.ExitCode)
        Assert.Contains(report.Diagnostics, fun d -> d.Rule = "BUILD003" && d.Message.Contains("depth"))

    [<Fact>]
    let ``diagnostic overflow cannot be a complete truncated report`` () =
        let source =
            [ 1..700 ]
            |> List.map (fun i -> $"let mutable value%d{i} = %d{i}")
            |> String.concat "\n"

        let report = check source
        Assert.False report.Complete
        Assert.Equal(3, report.ExitCode)
        Assert.True(report.Diagnostics.Length <= SafetyLimits.diagnosticCount + 1)
        Assert.Contains(report.Diagnostics, fun d -> d.Rule = "BUILD003")

    [<Fact>]
    let ``large flat declarations do not consume recursive list depth`` () =
        let source =
            [ 1..300 ]
            |> List.map (fun i -> $"let value%d{i} = %d{i}")
            |> String.concat "\n"

        let report = check source
        Assert.True(report.Complete && report.ExitCode = 0, describe report)

    [<Theory>]
    [<InlineData(false, false)>]
    [<InlineData(true, false)>]
    [<InlineData(false, true)>]
    [<InlineData(true, true)>]
    let ``many independent domain decisions stay within the supported analysis budget`` functionPattern invalidLast =
        let decisions =
            [ 1..200 ]
            |> List.map (fun i ->
                let cases =
                    if invalidLast && i = 200 then
                        "_ -> false"
                    else
                        "Approved -> true | Rejected -> false"

                if functionPattern then
                    $"let choose%d{i}: Status -> bool = function %s{cases}"
                else
                    $"let choose%d{i} (value: Status) = match value with %s{cases}")
            |> String.concat "\n"

        let report =
            checkFilesWith
                Safety.check
                [ "App.fs", "module SafetyFixture\ntype Status = Approved | Rejected\n" + decisions + "\n" ]
                id

        Assert.True(report.Complete, describe report)
        Assert.Equal((if invalidLast then 1 else 0), report.ExitCode)

        if invalidLast then
            Assert.Contains(report.Diagnostics, fun d -> d.Rule = "MODEL004")

    [<Fact>]
    let ``the allocation incident also stays bounded in the authoritative worker`` () =
        checkFilesWith
            Safety.check
            [ "App.fs",
              "module Incident\n[<CompilationRepresentation(CompilationRepresentationFlags.UseNullAsTrueValue)>]\ntype Value = Missing | Present of string\n" ]
            (fun report ->
                Assert.True(report.Complete, describe report)
                Assert.Equal(1, report.ExitCode)
                Assert.Contains(report.Diagnostics, fun d -> d.Rule = "ATTR003"))

    [<Fact>]
    let ``oversized sources are rejected before starting analysis`` () =
        checkFilesWith
            Safety.check
            [ "App.fs", "module Oversized\n// " + String.replicate (int SafetyLimits.sourceBytes) "x" ]
            (fun report ->
                Assert.False report.Complete
                Assert.Equal(3, report.ExitCode)
                Assert.Contains(report.Diagnostics, fun d -> d.Rule = "BUILD003" && d.Message.Contains("size")))

    [<Fact>]
    let ``signatures do not hide implementation violations`` () =
        let report =
            checkFiles
                [ "App.fsi", "module SafetyFixture\nval value: int\n"
                  "App.fs", "module SafetyFixture\nlet mutable private state = 1\nlet value = state\n" ]
                id

        Assert.True(report.Complete, describe report)
        Assert.Equal(1, report.ExitCode)

        Assert.Contains(
            report.Diagnostics,
            fun d -> d.Rule = "MUT001" && d.File.EndsWith("App.fs", StringComparison.Ordinal)
        )

    [<Fact>]
    let ``changing analyzed inputs invalidates the report`` () =
        checkFiles [ "App.fs", "module SafetyFixture\nlet value = 1\n" ] (fun report ->
            Assert.True(Safety.verifyInputs report, describe report)

            let source =
                report.Sources |> List.tryHead |> Fixtures.requireSome "analyzed source"

            File.AppendAllText(source.Path, "let mutable state = 2\n")
            Assert.False(Safety.verifyInputs report))

    [<Fact>]
    let ``line remapping is attributed to the physical source`` () =
        let report = check "#line 100 \"/trusted/Generated.fs\"\nlet mutable state = 1"
        Assert.NotEqual(0, report.ExitCode)

        Assert.Contains(
            report.Diagnostics,
            fun d ->
                d.Rule = "BUILD001"
                && d.File.EndsWith("App.fs", StringComparison.Ordinal)
                && d.StartLine = 2
        )
