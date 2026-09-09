namespace FDull.Tests

open System
open System.IO
open Xunit
open FDull
open FDull.Transport

module WorkspaceTests =
    let private root =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))

    let private policyFile = "fdull.json"

    let private withPolicy action =
        let policy =
            File.ReadAllText(Path.Combine(root, policyFile))
            |> Codec.decode<WorkspacePolicyDocument>

        let directory =
            Path.Combine(Path.GetTempPath(), "fdull-policy-" + Guid.NewGuid().ToString("N"))

        try
            for file in
                policyFile :: (policy.Sources |> List.map _.File)
                @ (policy.Inputs |> List.map _.File)
                |> List.distinct do
                let destination = Path.Combine(directory, file)

                Path.GetDirectoryName destination
                |> FDull.Transport.External.required "test.policy-parent"
                |> Directory.CreateDirectory
                |> ignore

                File.Copy(Path.Combine(root, file), destination)

            action directory policy
        finally
            if Directory.Exists directory then
                Directory.Delete(directory, true)

    [<Fact>]
    let ``reviewed policy inventory is sufficient without generated directories`` () =
        withPolicy (fun directory _ -> Assert.Equal(Ok(), Workspace.validatePolicy directory))

    [<Theory>]
    [<InlineData("source", "BUILD005")>]
    [<InlineData("build", "BUILD005")>]
    [<InlineData("extra-source", "ARCH002")>]
    [<InlineData("script", "ARCH003")>]
    [<InlineData("pure-exemption", "BUILD005")>]
    [<InlineData("unknown-rule", "BUILD005")>]
    [<InlineData("duplicate-profile", "BUILD005")>]
    [<InlineData("duplicate-domain", "BUILD005")>]
    [<InlineData("duplicate-constructor", "BUILD005")>]
    let ``policy and inventory drift cannot certify a workspace`` mutation expected =
        withPolicy (fun directory policy ->
            let save changed =
                File.WriteAllText(Path.Combine(directory, policyFile), Codec.encode changed)

            match mutation with
            | "source" -> File.AppendAllText(Path.Combine(directory, "src/FDull/SafetyRules.fs"), "\n")
            | "build" -> File.AppendAllText(Path.Combine(directory, "Directory.Build.targets"), "\n")
            | "extra-source" ->
                File.WriteAllText(Path.Combine(directory, "Unclassified.fs"), "module Unclassified\nlet value = 1\n")
            | "script" -> File.WriteAllText(Path.Combine(directory, "Unclassified.fsx"), "printfn \"unclassified\"\n")
            | "duplicate-domain" ->
                save
                    { policy with
                        Domains = "Fixture.Id" :: "Fixture.Id" :: policy.Domains }
            | "duplicate-constructor" ->
                save
                    { policy with
                        Constructors = "duplicate" :: "duplicate" :: policy.Constructors }
            | "pure-exemption"
            | "unknown-rule" ->
                let entry =
                    { File = "src/FDull/SafetyRules.fs"
                      Owner = "Fixture.Unreviewed"
                      Kind = "rule"
                      Identity = (if mutation = "unknown-rule" then "UNKNOWN001" else "MUT001") + " | value"
                      Reason = "Negative acceptance fixture; must never be accepted." }

                save
                    { policy with
                        Capabilities = entry :: policy.Capabilities
                        Sources = policy.Sources |> List.map (fun item -> { item with Profile = "PURE" })
                        Projects = policy.Projects |> List.map (fun item -> { item with Profile = "PURE" }) }
            | "duplicate-profile" ->
                let entry profile =
                    { File = "src/FDull/SafetyRules.fs"
                      Owner = "Fixture.Unreviewed"
                      Kind = "profile"
                      Identity = profile
                      Reason = "Conflicting profile acceptance fixture." }

                save
                    { policy with
                        Capabilities = entry "PURE" :: entry "ADAPTER" :: policy.Capabilities }
            | _ -> failwith "Unknown workspace mutation fixture."

            match Workspace.validatePolicy directory with
            | Error message -> Assert.StartsWith(expected + ":", message)
            | Ok() -> failwith ("Unexpectedly accepted policy mutation: " + mutation))

    [<Fact>]
    let ``permissions bind exact owners and cannot survive as unused entries`` () =
        let policy =
            File.ReadAllText(Path.Combine(root, policyFile))
            |> Codec.decode<WorkspacePolicyDocument>

        let grant =
            policy.Capabilities
            |> List.tryHead
            |> Fixtures.requireSome "Missing protected contract."

        let policy = { policy with Capabilities = [ grant ] }

        let observed: WorkspaceUse =
            { File = grant.File
              Owner = grant.Owner
              Kind = grant.Kind
              Identity = grant.Identity }

        Assert.Empty(WorkspaceAudit.unused policy [ observed ])

        Assert.Single(
            WorkspaceAudit.unused
                policy
                [ { observed with
                      Owner = observed.Owner + ".different" } ]
        )
        |> ignore

        let identity =
            "fixture.fs | fixture | M:Fixture.create -> int -> Result<Fixture.Value, string>"

        let policy =
            { policy with
                Constructors = [ identity ] }

        let constructor =
            { observed with
                Kind = "constructor"
                Identity = identity }

        Assert.Empty(WorkspaceAudit.unusedConstructors policy [ constructor ])

        Assert.Single(
            WorkspaceAudit.unusedConstructors
                policy
                [ { constructor with
                      Identity = identity + " widened" } ]
        )
        |> ignore

    [<Fact>]
    let ``workspace reports reject inconsistent process diagnostics and observations`` () =
        let policy =
            { Version = 1
              Projects =
                [ { File = "Fixture.fsproj"
                    Profile = "PURE"
                    References = [] } ]
              Sources =
                [ { File = "Fixture.fs"
                    Project = "Fixture.fsproj"
                    Profile = "PURE"
                    Digest = "source" } ]
              Inputs = []
              Capabilities = []
              Domains = []
              Constructors = [] }

        let request =
            { Root = root
              Project = Path.Combine(root, "Fixture.fsproj")
              ProjectReferences = []
              Arguments = []
              Sources =
                [ { Path = Path.Combine(root, "Fixture.fs")
                    Digest = "source" } ]
              References = []
              Generated = []
              BuildInputs = []
              Policy = { Path = policyFile; Digest = "policy" } }

        let report =
            { Policy = "policy"
              Tool = "tool"
              Complete = true
              ExitCode = 0
              Sources = request.Sources
              References = []
              Arguments = []
              Diagnostics = [] }

        let useSite =
            { File = "Fixture.fs"
              Owner = "Fixture.value"
              Kind = "member"
              Identity = "exact API" }

        let result =
            { Project = request.Project
              ProjectReferences = []
              Report = report
              Uses = [ useSite ] }

        let validate code stderr value =
            WorkspaceProtocol.validate request policy code stderr value

        Assert.Equal(Ok(), validate 0 "" result)

        let diagnostic =
            { Rule = "MUT001"
              Severity = "error"
              File = Path.Combine(root, "Fixture.fs")
              StartLine = 1
              StartColumn = 1
              EndLine = 1
              EndColumn = 2
              Project = request.Project
              Profile = "PURE"
              Symbol = "value"
              Message = "Mutation is forbidden."
              Alternative = "Use an immutable value."
              Policy = "policy" }

        let violation =
            { result with
                Report =
                    { report with
                        ExitCode = 1
                        Diagnostics = [ diagnostic ] } }

        Assert.Equal(Ok(), validate 1 "" violation)
        Assert.True(validate 1 "" result |> Result.isError)
        Assert.True(validate 0 "worker failure" result |> Result.isError)

        Assert.True(
            validate
                -1
                ""
                { result with
                    Report = { report with ExitCode = -1 } }
            |> Result.isError
        )

        Assert.True(
            validate
                3
                ""
                { result with
                    Report =
                        { report with
                            Complete = false
                            ExitCode = 3 } }
            |> Result.isError
        )

        for malformed in
            [ { diagnostic with Severity = "warning" }
              { diagnostic with Rule = "UNKNOWN001" }
              { diagnostic with Policy = "different" }
              { diagnostic with
                  Project = "different" }
              { diagnostic with Profile = "TRUSTED" }
              { diagnostic with
                  File = "different.fs" }
              { diagnostic with Message = "" }
              { diagnostic with StartLine = 0 }
              { diagnostic with StartColumn = 0 }
              { diagnostic with EndColumn = 0 }
              { diagnostic with EndLine = 0 }
              { diagnostic with Rule = "BUILD003" } ] do
            Assert.True(
                validate
                    1
                    ""
                    { violation with
                        Report =
                            { violation.Report with
                                Diagnostics = [ malformed ] } }
                |> Result.isError
            )

        for malformed in
            [ { useSite with File = "different.fs" }
              { useSite with Owner = "" }
              { useSite with Kind = "permission" }
              { useSite with Identity = "" } ] do
            Assert.True(validate 0 "" { result with Uses = [ malformed ] } |> Result.isError)
