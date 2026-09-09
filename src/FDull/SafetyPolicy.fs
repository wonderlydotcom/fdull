namespace FDull

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open FSharp.Compiler.Symbols

type SafetyDiagnostic =
    { Rule: string
      Severity: string
      File: string
      StartLine: int
      StartColumn: int
      EndLine: int
      EndColumn: int
      Project: string
      Profile: string
      Symbol: string
      Message: string
      Alternative: string
      Policy: string }

type SafetyInput = { Path: string; Digest: string }

type SafetyReport =
    { Policy: string
      Tool: string
      Complete: bool
      ExitCode: int
      Sources: SafetyInput list
      References: SafetyInput list
      Arguments: string list
      Diagnostics: SafetyDiagnostic list }

/// The policy is shipped inside the platform compiler. App/instance files cannot
/// change profiles, select another policy, register helpers, or suppress a rule.
module SafetyPolicy =
    let version = "fdull.strict.v1"

    let fcsIdentity =
        "FSharp.Compiler.Service, Version=43.12.100.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a"

    let coreIdentity =
        "FSharp.Core, Version=11.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a"


    let frameworkIdentities =
        set
            [ "System.Runtime, Version=10.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a"
              "netstandard, Version=2.1.0.0, Culture=neutral, PublicKeyToken=cc7b13ffcd2ddd51"
              "System.Collections, Version=10.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a" ]

    let compilerFlags =
        [ "--nologo"
          "--target:library"
          "--targetprofile:netcore"
          "--noframework"
          "--langversion:10.0"
          "--warnaserror+"
          "--warn:5"
          "--checknulls+"
          "--warnon:21,22,52,1178,1182,3180,3186,3218,3391,3395,3559,3570,3579,3582,3878"
          "--deterministic+"
          "--checked+"
          "--nocopyfsharpcore" ]

    let digest (bytes: byte array) =
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData bytes)

    let fileDigest path =
        use stream = File.OpenRead path
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData stream)

    let arguments references directory (output: string) =
        compilerFlags
        @ [ "--debug:embedded"
            "--pdb:/_/product/" + Path.ChangeExtension(Path.GetFileName output, ".pdb")
            "--pathmap:" + directory + "=/_/product"
            "--out:" + output ]
        @ (references |> List.map (fun reference -> "-r:" + reference))

    let private readCatalog () =
        use stream =
            typeof<SafetyReport>.Assembly.GetManifestResourceStream("FDull.approved-apis.json")
            |> FDull.Transport.External.required "BUILD003: embedded API policy is missing."

        use json = JsonDocument.Parse stream

        let entries =
            json.RootElement.EnumerateArray()
            |> Seq.map (fun item ->
                let keys = item.EnumerateObject() |> Seq.map (fun p -> p.Name) |> Set.ofSeq

                if keys <> set [ "Assembly"; "Signature" ] then
                    invalidOp "BUILD003: invalid API policy keys."

                let assembly =
                    item.GetProperty("Assembly").GetString()
                    |> FDull.Transport.External.required "policy.assembly"

                let signature =
                    item.GetProperty("Signature").GetString()
                    |> FDull.Transport.External.required "policy.signature"

                if String.IsNullOrWhiteSpace assembly || String.IsNullOrWhiteSpace signature then
                    invalidOp "BUILD003: unresolved API identity."

                assembly, signature)
            |> Seq.toList

        if entries.IsEmpty || (Set.ofList entries).Count <> entries.Length then
            invalidOp "BUILD003: empty or duplicate API catalog."

        Set.ofList entries

    let private apis = lazy (readCatalog ())

    let approvedApi (symbol: FSharpMemberOrFunctionOrValue) =
        Set.contains (symbol.Assembly.QualifiedName, symbol.XmlDocSig) apis.Value

    let validate () =
        SafetyRules.validate ()
        apis.Force() |> ignore

    let entityName (entity: FSharpEntity) =
        defaultArg entity.TryFullName entity.DisplayName

    let purePrimitiveTypes =
        set
            [ "System.String"
              "System.Boolean"
              "System.Char"
              "System.Byte"
              "System.SByte"
              "System.Int16"
              "System.UInt16"
              "System.Int32"
              "System.UInt32"
              "System.Int64"
              "System.UInt64"
              "System.Single"
              "System.Double"
              "System.Decimal"
              "System.DateTimeOffset"
              "System.DateTime"
              "System.TimeSpan"
              "System.Guid"
              "System.StringComparison"
              "Microsoft.FSharp.Core.Unit" ]

    let immutableContainers =
        set
            [ "Microsoft.FSharp.Collections.FSharpList`1"
              "Microsoft.FSharp.Collections.FSharpMap`2"
              "Microsoft.FSharp.Collections.FSharpSet`1"
              "Microsoft.FSharp.Core.FSharpOption`1"
              "Microsoft.FSharp.Core.FSharpValueOption`1"
              "Microsoft.FSharp.Core.FSharpResult`2" ]

    let attributes =
        set
            [ "Microsoft.FSharp.Core.RequireQualifiedAccessAttribute"
              "Microsoft.FSharp.Core.NoEqualityAttribute"
              "Microsoft.FSharp.Core.NoComparisonAttribute"
              "Microsoft.FSharp.Core.StructuralEqualityAttribute"
              "Microsoft.FSharp.Core.StructuralComparisonAttribute" ]

    let format (diagnostic: SafetyDiagnostic) =
        $"%s{diagnostic.File}(%d{diagnostic.StartLine},%d{diagnostic.StartColumn}): error %s{diagnostic.Rule}: %s{diagnostic.Message} %s{diagnostic.Alternative} [%s{diagnostic.Profile}; %s{diagnostic.Policy}]"

    let toJson report =
        JsonSerializer.Serialize(report, JsonSerializerOptions(WriteIndented = true))

    let toSarif (report: SafetyReport) =
        JsonSerializer.Serialize(
            {| version = "2.1.0"
               ``$schema`` = "https://json.schemastore.org/sarif-2.1.0.json"
               runs =
                [ {| tool = {| driver = {| name = "FDull"; version = version |} |}
                     invocations =
                      [ {| executionSuccessful = report.Complete
                           exitCode = report.ExitCode |} ]
                     results =
                      report.Diagnostics
                      |> List.map (fun d ->
                          {| ruleId = d.Rule
                             level = "error"
                             message = {| text = d.Message + " " + d.Alternative |}
                             locations =
                              [ {| physicalLocation =
                                    {| artifactLocation = {| uri = Uri(Path.GetFullPath d.File).AbsoluteUri |}
                                       region =
                                        {| startLine = max 1 d.StartLine
                                           startColumn = max 1 d.StartColumn
                                           endLine = max 1 d.EndLine
                                           endColumn = max 1 d.EndColumn |} |} |} ] |}) |} ] |},
            JsonSerializerOptions(WriteIndented = true)
        )
