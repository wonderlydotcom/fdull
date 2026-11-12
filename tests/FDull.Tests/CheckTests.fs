namespace FDull.Tests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open Xunit
open FDull

/// `fdull check` against ordinary projects that have no fdull.json or FDull compiler preset.
module CheckTests =
    let private cli =
        Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "../../../../../src/FDull.Tool/bin/Release/net10.0/FDull.Cli.dll")
        )

    let private required name (value: string | null) =
        FDull.Transport.External.required ("test.check-" + name) value

    let private start root (arguments: string list) =
        let info = ProcessStartInfo("dotnet", WorkingDirectory = root)

        for argument in arguments do
            info.ArgumentList.Add argument

        info.Environment["MSBUILDDISABLENODEREUSE"] <- "1"

        match
            SafetyProcess.run
                { Milliseconds = 300000
                  OutputCharacters = 1048576
                  WorkingSetBytes = 4294967296L }
                info
                ""
        with
        | Ok result -> result
        | Error error -> failwith error

    let private dotnet root arguments =
        let result = start root arguments
        Assert.True(result.ExitCode = 0, result.Error + result.Output)

    type private Checked =
        { ExitCode: int
          Status: string
          Files: string list
          Rules: string list
          Claim: string
          Reason: string
          Findings: (string * string * string) list }

    /// Runs the CLI with JSON output and reads the fields CI consumers depend on.
    let private check root (arguments: string list) =
        let result = start root ([ cli; "check" ] @ arguments @ [ "--format"; "json" ])
        use document = JsonDocument.Parse result.Output
        let item = document.RootElement

        let strings (name: string) =
            item.GetProperty(name).EnumerateArray()
            |> Seq.map (fun value -> value.GetString() |> required name)
            |> Seq.toList

        let reason =
            match item.GetProperty("Reason").ValueKind with
            | JsonValueKind.Null -> ""
            | _ -> item.GetProperty("Reason").GetString() |> required "Reason"

        let checkedResult =
            { ExitCode = item.GetProperty("ExitCode").GetInt32()
              Status = item.GetProperty("Status").GetString() |> required "Status"
              Files = strings "Files"
              Rules = strings "Rules"
              Claim = item.GetProperty("Claim").GetString() |> required "Claim"
              Reason = reason
              Findings =
                item.GetProperty("Findings").EnumerateArray()
                |> Seq.map (fun finding ->
                    let text (name: string) =
                        finding.GetProperty(name).GetString() |> required name

                    text "Rule", text "File", text "Symbol")
                |> Seq.toList }

        Assert.True(checkedResult.ExitCode = result.ExitCode, result.Output + result.Error)
        checkedResult

    let private write root (path: string) (text: string) =
        let full = Path.Combine(root, path)

        Path.GetDirectoryName full
        |> required "directory"
        |> Directory.CreateDirectory
        |> ignore

        File.WriteAllText(full, text)

    let private types =
        "module Shop.Types\n\ntype Status =\n    | Open\n    | Closed\n    | Pending\n"

    let private domainProject =
        "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>Shop.Core</AssemblyName></PropertyGroup>"
        + "<ItemGroup><Compile Include=\"Types.fs\"/><Compile Include=\"Legacy.fs\"/>"
        + "<Compile Include=\"Rules/Pricing.fs\"/><Compile Include=\"Rules/Discount.fs\"/></ItemGroup>"
        + "<ItemGroup><ProjectReference Include=\"../Kernel/Kernel.fsproj\"/></ItemGroup></Project>"

    /// A three-project workspace without FDull settings: App references Domain, which references Kernel. Legacy.fs holds violations nobody selects;
    /// Rules/Discount.fs holds one violation per supported rule, two reached through aliases.
    let private workspace action =
        let root =
            Path.Combine(Path.GetTempPath(), "fdull-check-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory root |> ignore

        try
            write root "global.json" "{\"sdk\":{\"version\":\"10.0.403\",\"rollForward\":\"disable\"}}"

            write
                root
                "NuGet.config"
                "<configuration><packageSources><clear/><add key=\"nuget.org\" value=\"https://api.nuget.org/v3/index.json\"/></packageSources></configuration>"

            write
                root
                "Kernel/Kernel.fsproj"
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><Compile Include=\"Level.fs\"/></ItemGroup></Project>"

            write root "Kernel/Level.fs" "module Shop.Kernel\n\ntype Level =\n    | Low\n    | High\n"
            write root "Domain/Domain.fsproj" domainProject
            write root "Domain/Types.fs" types

            write
                root
                "Domain/Legacy.fs"
                "module Shop.Legacy\n\nlet first (items: int list) = List.head items\n\nlet label status =\n    match status with\n    | Types.Open -> \"open\"\n    | _ -> \"other\"\n"

            write
                root
                "Domain/Rules/Pricing.fs"
                "module Shop.Pricing\n\nopen Shop.Types\n\nlet price status =\n    match status with\n    | Open -> 10\n    | Closed -> 0\n    | Pending -> 5\n"

            write
                root
                "Domain/Rules/Discount.fs"
                ("module Shop.Discount\n\nopen Shop.Types\n\nmodule L = List\n\n"
                 + "let validate (amount: int) : Result<int, string> =\n    if amount > 0 then Ok amount else Error \"negative\"\n\n"
                 + "let rate status =\n    match status with\n    | Open -> 1\n    | _ -> 0\n\n"
                 + "let record amount =\n    validate amount |> ignore\n    amount\n\n"
                 + "let best (amounts: int list) = L.head amounts\n\n"
                 + "let chosen (value: int option) = Option.get value\n")

            write
                root
                "App/App.fsproj"
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><Compile Include=\"Handler.fs\"/></ItemGroup><ItemGroup><ProjectReference Include=\"../Domain/Domain.fsproj\"/></ItemGroup></Project>"

            write
                root
                "App/Handler.fs"
                ("module Shop.Handler\n\nlet describe status =\n    match status with\n    | Shop.Types.Closed -> \"closed\"\n    | other -> string other\n\n"
                 + "let rank level =\n    match level with\n    | Shop.Kernel.Low -> 0\n    | _ -> 1\n")

            dotnet root [ "build"; "App/App.fsproj"; "-c"; "Release"; "-m:1"; "-nr:false" ]
            Assert.False(File.Exists(Path.Combine(root, "fdull.json")))
            action root
        finally
            Directory.Delete(root, true)

    let private allRules = [ "--rules"; "MODEL004,ERR007,ERR006" ]

    [<Fact>]
    let ``check reports selected rules in selected files and nothing from unselected sources`` () =
        workspace (fun root ->
            let directory =
                check root ([ "Domain/Domain.fsproj" ] @ allRules @ [ "Domain/Rules" ])

            Assert.Equal(1, directory.ExitCode)
            Assert.Equal("fail", directory.Status)
            Assert.Equal<string list>([ "Rules/Pricing.fs"; "Rules/Discount.fs" ], directory.Files)
            Assert.Equal<string list>([ "MODEL004"; "ERR007"; "ERR006" ], directory.Rules)
            Assert.Contains("not FDull verification", directory.Claim)

            // The module alias and Option.get resolve to the throwing FSharp.Core operations.
            Assert.Equal<(string * string * string) list>(
                [ "MODEL004", "Rules/Discount.fs", "Shop.Types.Status"
                  "ERR007", "Rules/Discount.fs", "Microsoft.FSharp.Core.Operators.ignore"
                  "ERR006", "Rules/Discount.fs", "Microsoft.FSharp.Collections.List.head"
                  "ERR006", "Rules/Discount.fs", "Microsoft.FSharp.Core.Option.get" ]
                |> List.sort,
                directory.Findings |> List.sort
            )

            let clean =
                check root ([ "Domain/Domain.fsproj" ] @ allRules @ [ "Domain/Rules/Pricing.fs" ])

            Assert.Equal(0, clean.ExitCode)
            Assert.Equal("pass", clean.Status)
            Assert.Empty clean.Findings

            let partial =
                check root [ "Domain/Domain.fsproj"; "--rules"; "ERR007"; "Domain/Rules/Discount.fs" ]

            Assert.Equal<string list>([ "ERR007" ], partial.Findings |> List.map (fun (rule, _, _) -> rule))

            // A union from a directly referenced project is a closed domain decision too, found by
            // the assembly MSBuild resolved even though its name differs from the project file.
            // Kernel is referenced only through Domain, so its union is not.
            let app = check root ([ "App/App.fsproj" ] @ allRules @ [ "App" ])
            Assert.Equal(1, app.ExitCode)

            Assert.Equal<(string * string * string) list>(
                [ "MODEL004", "Handler.fs", "Shop.Types.Status" ],
                app.Findings
            )

            // A source added later joins its selected directory.
            write root "Domain/Rules/Later.fs" "module Shop.Later\n\nlet first (items: int list) = List.last items\n"

            write
                root
                "Domain/Domain.fsproj"
                (domainProject.Replace(
                    "<Compile Include=\"Rules/Discount.fs\"/>",
                    "<Compile Include=\"Rules/Discount.fs\"/><Compile Include=\"Rules/Later.fs\"/>"
                ))

            let later =
                check root ([ "Domain/Domain.fsproj"; "--rules"; "ERR006"; "Domain/Rules" ])

            Assert.Contains("Rules/Later.fs", later.Files)
            Assert.Contains(("ERR006", "Rules/Later.fs", "Microsoft.FSharp.Collections.List.last"), later.Findings))

    [<Fact>]
    let ``check fails a new union case missed by a selected explicit match`` () =
        workspace (fun root ->
            write root "Domain/Types.fs" (types + "    | Cancelled\n")

            let changed =
                check root ([ "Domain/Domain.fsproj" ] @ allRules @ [ "Domain/Rules/Pricing.fs" ])

            Assert.Equal(1, changed.ExitCode)

            Assert.Equal<(string * string * string) list>(
                [ "MODEL004", "Rules/Pricing.fs", "FS0025" ],
                changed.Findings
            )

            let other =
                check root [ "Domain/Domain.fsproj"; "--rules"; "ERR006"; "Domain/Rules/Pricing.fs" ]

            Assert.Equal(0, other.ExitCode)

            // The project's own warning settings cannot hide the missed case.
            [ "<NoWarn>$(NoWarn);25</NoWarn>"
              "<WarningLevel>0</WarningLevel>"
              "<TreatWarningsAsErrors>true</TreatWarningsAsErrors>" ]
            |> List.iter (fun settings ->
                write
                    root
                    "Domain/Domain.fsproj"
                    (domainProject.Replace("</AssemblyName>", "</AssemblyName>" + settings))

                let configured =
                    check root ([ "Domain/Domain.fsproj" ] @ allRules @ [ "Domain/Rules/Pricing.fs" ])

                Assert.Equal<(string * string * string) list>(
                    [ "MODEL004", "Rules/Pricing.fs", "FS0025" ],
                    configured.Findings
                ))

            write root "Domain/Domain.fsproj" domainProject

            // A suppression inside the selected file blocks the check instead of passing it.
            let pricing = Path.Combine(root, "Domain/Rules/Pricing.fs")
            File.WriteAllText(pricing, "#nowarn \"25\"\n" + File.ReadAllText pricing)

            let suppressed =
                check root ([ "Domain/Domain.fsproj" ] @ allRules @ [ "Domain/Rules/Pricing.fs" ])

            Assert.Equal(3, suppressed.ExitCode)
            Assert.Equal<(string * string * string) list>([ "FS0025", "Rules/Pricing.fs", "" ], suppressed.Findings))

    [<Fact>]
    let ``check never passes invalid requests or incomplete analysis`` () =
        workspace (fun root ->
            let invalid arguments =
                let result = check root arguments
                Assert.Equal(2, result.ExitCode)
                Assert.Equal("invalid", result.Status)
                Assert.Empty result.Findings
                Assert.False(String.IsNullOrWhiteSpace result.Reason)

            invalid [ "Domain/Domain.fsproj"; "Domain/Rules" ]
            invalid [ "Domain/Domain.fsproj"; "--rules"; "MUT001"; "Domain/Rules" ]
            invalid [ "Domain/Domain.fsproj"; "--rules"; "MODEL004,ERR999"; "Domain/Rules" ]
            invalid [ "Domain/Domain.fsproj"; "--rules"; "ERR006" ]
            invalid [ "Domain/Domain.fsproj"; "--rules"; "ERR006"; "Domain/Rules/Pricng.fs" ]

            invalid
                [ "Domain/Domain.fsproj"
                  "--rules"
                  "ERR006"
                  "Domain/Rules"
                  "Domain/Missing" ]

            invalid [ "Domain/Domain.fsproj"; "--rules"; "ERR006"; "Domain/obj" ]
            invalid [ "Domain/Missing.fsproj"; "--rules"; "ERR006"; "Domain/Rules" ]

            let usage =
                start root [ cli; "check"; "Domain/Domain.fsproj"; "--rules"; "ERR006"; "--baseline"; "x" ]

            Assert.Equal(2, usage.ExitCode)

            // An unselected compile error still blocks the selected clean file.
            write root "Domain/Legacy.fs" "module Shop.Legacy\n\nlet broken: int = \"text\"\n"

            let blocked =
                check root ([ "Domain/Domain.fsproj" ] @ allRules @ [ "Domain/Rules/Pricing.fs" ])

            Assert.Equal(3, blocked.ExitCode)
            Assert.Equal("incomplete", blocked.Status)
            Assert.Contains(("FS0001", "Legacy.fs", ""), blocked.Findings)

            // Missing reference outputs block instead of silently checking less.
            Directory.Delete(Path.Combine(root, "Domain/bin"), true)
            Directory.Delete(Path.Combine(root, "Domain/obj/Release/net10.0"), true)
            let unbuilt = check root ([ "App/App.fsproj" ] @ allRules @ [ "App/Handler.fs" ])
            Assert.Equal(3, unbuilt.ExitCode)
            Assert.Empty unbuilt.Findings)
