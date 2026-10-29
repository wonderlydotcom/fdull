namespace FDull.Tests

open System
open System.Diagnostics
open System.IO
open Xunit
open FDull
open FDull.Transport

module RepositoryTests =
    let private withRepository action =
        let root =
            Path.Combine(AppContext.BaseDirectory, "fdull-repository-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory root |> ignore

        try
            action root
        finally
            Directory.Delete(root, true)

    let private command root executable arguments =
        let start = ProcessStartInfo(executable, WorkingDirectory = root)

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
        | Error error -> failwith error
        | Ok result ->
            Assert.True(
                result.ExitCode = 0,
                executable
                + " "
                + String.concat " " arguments
                + ": "
                + result.Error
                + result.Output
            )

            result.Output.Trim()

    let private cli root arguments =
        let location =
            Path.GetFullPath(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "../../../../../src/FDull.Tool/bin/Release/net10.0/FDull.Cli.dll"
                )
            )

        let start = ProcessStartInfo("dotnet", WorkingDirectory = root)
        start.ArgumentList.Add location

        for argument in arguments do
            start.ArgumentList.Add argument

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

    let private git root arguments = command root "git" arguments

    let private write root relative (contents: string) =
        let path = Path.Combine(root, relative)

        Path.GetDirectoryName path
        |> External.required "repository-tests.parent-directory"
        |> Directory.CreateDirectory
        |> ignore

        File.WriteAllText(path, contents)

    let private service root name =
        let directory = Path.Combine(root, "services", name)
        Directory.CreateDirectory directory |> ignore
        write directory "global.json" "{\"sdk\":{\"version\":\"10.0.403\",\"rollForward\":\"disable\"}}"
        write directory "Directory.Build.props" (Project.defaults ())

        write
            directory
            "NuGet.config"
            "<configuration><packageSources><clear/><add key=\"nuget.org\" value=\"https://api.nuget.org/v3/index.json\"/></packageSources></configuration>"

        write
            directory
            (name + ".fsproj")
            ("<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><Compile Include=\""
             + name
             + ".fs\"/></ItemGroup></Project>")

        write directory (name + ".fs") ("module " + name + "\nlet value = 1\n")

        command directory "dotnet" [ "restore"; name + ".fsproj"; "-m:1"; "-nr:false" ]
        |> ignore

        Project.initialize directory |> ignore
        let policyPath = Path.Combine(directory, "fdull.json")
        let policy = File.ReadAllText policyPath |> Codec.decode<WorkspacePolicyDocument>

        File.WriteAllText(
            policyPath,
            Codec.encode
                { policy with
                    Version = 2
                    Repository =
                        Some
                            { Root = "../.."
                              SharedInputs = []
                              References = [] } }
        )

        directory

    let private initializeGit root =
        write root ".gitignore" "**/bin/\n**/obj/\n.fdull/\nDirectory.Build.targets\n"
        git root [ "init"; "-q" ] |> ignore
        git root [ "config"; "user.name"; "FDull test" ] |> ignore
        git root [ "config"; "user.email"; "fdull@example.invalid" ] |> ignore
        git root [ "add"; "-A" ] |> ignore
        git root [ "commit"; "-qm"; "baseline" ] |> ignore
        git root [ "rev-parse"; "HEAD" ]

    let private plan root selection =
        match RepositoryPlan.plan root selection with
        | Ok result -> result
        | Error error -> failwith error

    let private assertAffected expected result =
        Assert.Equal<string list>(expected, result.Affected)

    let private readPolicy directory =
        File.ReadAllText(Path.Combine(directory, "fdull.json"))
        |> Codec.decode<WorkspacePolicyDocument>

    let private savePolicy directory policy =
        File.WriteAllText(Path.Combine(directory, "fdull.json"), Codec.encode policy)

    let private withSharedInput root directory =
        let policy = readPolicy directory

        savePolicy
            directory
            { policy with
                Version = 2
                Repository =
                    Some
                        { Root = "../.."
                          SharedInputs =
                            [ { File = "Directory.Build.targets"
                                Digest = SafetyPolicy.fileDigest (Path.Combine(root, "Directory.Build.targets")) } ]
                          References = [] } }

    let private crossLink restore owner ownerName (edge: WorkspaceRepositoryReference) =
        write
            owner
            (ownerName + ".fsproj")
            ("<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><Compile Include=\""
             + ownerName
             + ".fs\"/><ProjectReference Include=\"../../"
             + edge.Target
             + "\"/></ItemGroup></Project>")

        if restore then
            command owner "dotnet" [ "restore"; ownerName + ".fsproj"; "--force-evaluate"; "-m:1"; "-nr:false" ]
            |> ignore

        let policy = readPolicy owner

        savePolicy
            owner
            { policy with
                Version = 2
                Inputs =
                    policy.Inputs
                    |> List.map (fun input ->
                        if input.File = ownerName + ".fsproj" || input.File = "packages.lock.json" then
                            { input with
                                Digest = SafetyPolicy.fileDigest (Path.Combine(owner, input.File)) }
                        else
                            input)
                Repository =
                    Some
                        { Root = "../.."
                          SharedInputs = []
                          References = [ edge ] } }

    let private link restore owner ownerName target targetName =
        crossLink
            restore
            owner
            ownerName
            { Project = ownerName + ".fsproj"
              Target = "services/" + targetName + "/" + targetName + ".fsproj"
              Policy = "services/" + targetName + "/fdull.json"
              PolicyDigest = WorkspacePolicyDigest.file (Path.Combine(target, "fdull.json"))
              Profile = Some (readPolicy target).Projects.Head.Profile
              Kind = None }

    let private updateEdges directory change =
        let policy = readPolicy directory

        savePolicy
            directory
            { policy with
                Repository =
                    policy.Repository
                    |> Option.map (fun repository ->
                        { repository with
                            References = repository.References |> List.map change }) }

    let private repin directory =
        updateEdges directory (fun edge ->
            { edge with
                PolicyDigest =
                    WorkspacePolicyDigest.file (
                        Path.Combine(Path.GetFullPath(Path.Combine(directory, "../..")), edge.Policy)
                    ) })

    let private reviewSources directory =
        let policy = readPolicy directory

        savePolicy
            directory
            { policy with
                Sources =
                    policy.Sources
                    |> List.map (fun source ->
                        { source with
                            Digest = SafetyPolicy.fileDigest (Path.Combine(directory, source.File)) }) }

    let private validationError directory =
        match Workspace.validatePolicy directory with
        | Error error -> error
        | Ok() -> failwith ("Unexpectedly accepted policy: " + directory)

    [<Fact>]
    let ``root discovery selects all independent services and a changed owner only`` () =
        withRepository (fun root ->
            let billing = service root "Billing"
            let orders = service root "Orders"
            let baseline = initializeGit root
            let all = plan root All
            assertAffected [ "services/Billing"; "services/Orders" ] all
            Assert.Empty all.Skipped

            write billing "Billing.fs" "module Billing\nlet value = 2\n"
            let changed = plan root (Since baseline)
            Assert.Equal(Some baseline, changed.Baseline)
            assertAffected [ "services/Billing" ] changed
            Assert.Equal<string list>([ "services/Orders" ], changed.Skipped)
            Assert.True(RepositoryPlan.revalidate changed |> Result.isOk)

            write orders "Orders.fs" "module Orders\nlet value = 2\n"
            Assert.True(RepositoryPlan.revalidate changed |> Result.isError)
            write orders "Orders.fs" "module Orders\nlet value = 1\n"

            let skippedPolicy = Path.Combine(orders, "fdull.json")
            let originalPolicy = File.ReadAllText skippedPolicy
            File.AppendAllText(skippedPolicy, "\n")
            Assert.True(RepositoryPlan.revalidate changed |> Result.isError)
            File.WriteAllText(skippedPolicy, originalPolicy)

            write root "unowned/New.fs" "module New\nlet value = 1\n"
            Assert.True(RepositoryPlan.revalidate changed |> Result.isError)
            File.Delete(Path.Combine(root, "unowned/New.fs"))
            Assert.True(RepositoryPlan.revalidate changed |> Result.isOk)

            write billing "Billing.fs" "module Billing\nlet value = 3\n"
            Assert.True(RepositoryPlan.revalidate changed |> Result.isError))

    [<Fact>]
    let ``git selection includes staged committed untracked deleted and renamed paths`` () =
        withRepository (fun root ->
            let billing = service root "Billing"
            let orders = service root "Orders"
            write orders "tracked-note.txt" "tracked file\n"
            let baseline = initializeGit root

            write billing "Billing.fs" "module Billing\nlet value = 2\n"
            assertAffected [ "services/Billing" ] (plan root (Since baseline))
            git root [ "add"; "services/Billing/Billing.fs" ] |> ignore
            assertAffected [ "services/Billing" ] (plan root (Since baseline))
            git root [ "commit"; "-qm"; "changed billing" ] |> ignore
            assertAffected [ "services/Billing" ] (plan root (Since baseline))

            write orders "unusual name [x].txt" "new file\n"
            assertAffected [ "services/Billing"; "services/Orders" ] (plan root (Since baseline))
            File.Delete(Path.Combine(orders, "unusual name [x].txt"))

            git root [ "mv"; "services/Orders/tracked-note.txt"; "services/Orders/moved-note.txt" ]
            |> ignore

            assertAffected [ "services/Billing"; "services/Orders" ] (plan root (Since baseline))
            git root [ "reset"; "--hard"; baseline ] |> ignore
            File.Delete(Path.Combine(orders, "tracked-note.txt"))
            assertAffected [ "services/Orders" ] (plan root (Since baseline))
            git root [ "reset"; "--hard"; baseline ] |> ignore
            git root [ "checkout"; "-qb"; "feature-selection" ] |> ignore
            write orders "tracked-note.txt" "changed on feature branch\n"
            git root [ "add"; "services/Orders/tracked-note.txt" ] |> ignore
            git root [ "commit"; "-qm"; "branch change" ] |> ignore
            assertAffected [ "services/Orders" ] (plan root (Since baseline)))

    [<Fact>]
    let ``selection refuses missing baselines unowned F sharp files and nested policies`` () =
        withRepository (fun root ->
            Assert.True(RepositoryPlan.plan root All |> Result.isError)
            service root "Billing" |> ignore
            let baseline = initializeGit root
            Assert.True(RepositoryPlan.plan root (Since "unknown-revision") |> Result.isError)

            write root "notes.txt" "outside service scope\n"
            assertAffected [] (plan root (Since baseline))
            File.Delete(Path.Combine(root, "notes.txt"))

            write root "orphan/Unexpected.fs" "module Unexpected\nlet value = 1\n"
            Assert.True(RepositoryPlan.plan root (Since baseline) |> Result.isError)
            File.Delete(Path.Combine(root, "orphan/Unexpected.fs"))

            let alias = Path.Combine(root, "alias.txt")

            File.CreateSymbolicLink(alias, Path.Combine(AppContext.BaseDirectory, "FDull.Tests.dll"))
            |> ignore

            Assert.True(RepositoryPlan.plan root All |> Result.isError)
            File.Delete alias

            write root "fdull.json" "{}"
            Assert.True(RepositoryPlan.plan root All |> Result.isError)
            File.Delete(Path.Combine(root, "fdull.json"))

            File.Delete(Path.Combine(root, "services/Billing/fdull.json"))
            Assert.True(RepositoryPlan.plan root All |> Result.isError))

    [<Fact>]
    let ``reviewed shared build input selects every owner and changed digest blocks verification`` () =
        withRepository (fun root ->
            write root "Directory.Build.targets" "<Project />\n"
            let billing = service root "Billing"
            let orders = service root "Orders"
            withSharedInput root billing
            withSharedInput root orders
            let baseline = initializeGit root
            assertAffected [ "services/Billing"; "services/Orders" ] (plan root (Since baseline))

            write root "NuGet.config" "<configuration />\n"
            Assert.True(RepositoryPlan.plan root All |> Result.isError)

            for directory in [ billing; orders ] do
                let current = readPolicy directory

                savePolicy
                    directory
                    { current with
                        Repository =
                            current.Repository
                            |> Option.map (fun repository ->
                                { repository with
                                    SharedInputs =
                                        repository.SharedInputs
                                        @ [ { File = "NuGet.config"
                                              Digest = SafetyPolicy.fileDigest (Path.Combine(root, "NuGet.config")) } ] }) }

            assertAffected [ "services/Billing"; "services/Orders" ] (plan root All)
            write root "Directory.Build.targets" "<Project><!-- changed --></Project>\n"
            assertAffected [ "services/Billing"; "services/Orders" ] (plan root (Since baseline))
            let report = Repository.verify root (Since baseline)
            Assert.Equal(3, report.ExitCode)
            Assert.False report.Complete

            write root "bin/hidden.props" "<Project />\n"
            let current = readPolicy billing
            let hidden = Path.Combine(root, "bin/hidden.props")

            savePolicy
                billing
                { current with
                    Repository =
                        current.Repository
                        |> Option.map (fun repository ->
                            { repository with
                                SharedInputs =
                                    repository.SharedInputs
                                    @ [ { File = "bin/hidden.props"
                                          Digest = SafetyPolicy.fileDigest hidden } ] }) }

            Assert.True(RepositoryPlan.plan root All |> Result.isError))

    [<Fact>]
    let ``repository imports require an exact reviewed pin and nested generated paths are not exempt`` () =
        withRepository (fun root ->
            let billing = service root "Billing"
            write root "imported.props" "<Project />\n"
            let import = Path.Combine(root, "imported.props")
            let policy = readPolicy billing

            let invocation =
                { Sources = []
                  References = []
                  Arguments = []
                  Generated = []
                  ProjectReferences = []
                  ImportedBuildInputs = [ import ] }

            Assert.ThrowsAny<Exception>(fun () ->
                Workspace.validateImports billing "Billing.fsproj" policy invocation |> ignore)
            |> ignore

            let reviewed =
                { policy with
                    Repository =
                        policy.Repository
                        |> Option.map (fun repository ->
                            { repository with
                                SharedInputs =
                                    [ { File = "imported.props"
                                        Digest = SafetyPolicy.fileDigest import } ] }) }

            Assert.Single(Workspace.validateImports billing "Billing.fsproj" reviewed invocation)
            |> ignore

            let generated = Path.Combine(billing, "obj", "Billing.fsproj.nuget.g.props")

            Assert.Empty(
                Workspace.validateImports
                    billing
                    "Billing.fsproj"
                    policy
                    { invocation with
                        ImportedBuildInputs = [ generated ] }
            )

            let nested = Path.Combine(billing, "obj", "nested", "Billing.fsproj.nuget.g.props")

            Assert.ThrowsAny<Exception>(fun () ->
                Workspace.validateImports
                    billing
                    "Billing.fsproj"
                    policy
                    { invocation with
                        ImportedBuildInputs = [ nested ] }
                |> ignore)
            |> ignore)

    [<Fact>]
    let ``evaluated custom imports require reviewed shared pins and dynamic imports block`` () =
        withRepository (fun root ->
            let billing = service root "Billing"
            write root "unreviewed.props" "<Project />\n"

            let projectText import =
                "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><Compile Include=\"Billing.fs\"/></ItemGroup>"
                + import
                + "</Project>"

            write billing "Billing.fsproj" (projectText "<Import Project=\"../../unreviewed.props\"/>")

            command billing "dotnet" [ "restore"; "Billing.fsproj"; "--force-evaluate"; "-m:1"; "-nr:false" ]
            |> ignore

            let repin project =
                let current = readPolicy billing

                savePolicy
                    billing
                    { current with
                        Inputs =
                            current.Inputs
                            |> List.map (fun input ->
                                if input.File = "Billing.fsproj" || input.File = "packages.lock.json" then
                                    { input with
                                        Digest = SafetyPolicy.fileDigest (Path.Combine(billing, input.File)) }
                                else
                                    input)
                        Repository = project current.Repository }

            repin id

            match Workspace.export billing "Billing.fsproj" with
            | Ok _ -> ()
            | Error error -> failwith error

            Assert.True(Workspace.validatePolicy billing |> Result.isError)

            repin (
                Option.map (fun repository ->
                    { repository with
                        SharedInputs =
                            [ { File = "unreviewed.props"
                                Digest = SafetyPolicy.fileDigest (Path.Combine(root, "unreviewed.props")) } ] })
            )

            Assert.Equal(Ok(), Workspace.validatePolicy billing)

            write
                billing
                "Billing.fsproj"
                (projectText
                    "<PropertyGroup><CustomImport>../../unreviewed.props</CustomImport></PropertyGroup><Import Project=\"$(CustomImport)\"/>")

            repin id
            Assert.True(Workspace.validatePolicy billing |> Result.isError))

    [<Fact>]
    let ``cross policy library change selects its consumer and dependency closure`` () =
        withRepository (fun root ->
            let contracts = service root "Contracts"
            let contractsPolicy = readPolicy contracts

            savePolicy
                contracts
                { contractsPolicy with
                    Projects =
                        contractsPolicy.Projects
                        |> List.map (fun item -> { item with Profile = "CONTRACTS" })
                    Sources =
                        contractsPolicy.Sources
                        |> List.map (fun item -> { item with Profile = "CONTRACTS" }) }

            let billing = service root "Billing"
            link true billing "Billing" contracts "Contracts"

            let baseline = initializeGit root
            write contracts "Contracts.fs" "module Contracts\nlet value = 2\n"
            assertAffected [ "services/Billing"; "services/Contracts" ] (plan root (Since baseline))

            let changedContracts = readPolicy contracts

            savePolicy
                contracts
                { changedContracts with
                    Sources =
                        changedContracts.Sources
                        |> List.map (fun item ->
                            if item.File = "Contracts.fs" then
                                { item with
                                    Digest = SafetyPolicy.fileDigest (Path.Combine(contracts, item.File)) }
                            else
                                item) }

            let changedBilling = readPolicy billing

            let targetDigest =
                WorkspacePolicyDigest.file (Path.Combine(contracts, "fdull.json"))

            savePolicy
                billing
                { changedBilling with
                    Repository =
                        changedBilling.Repository
                        |> Option.map (fun repository ->
                            { repository with
                                References =
                                    repository.References
                                    |> List.map (fun edge ->
                                        { edge with
                                            PolicyDigest = targetDigest }) }) }

            let report = Repository.verify root (Since baseline)
            Assert.True(report.Complete, String.concat "\n" report.Errors)
            Assert.Equal(0, report.ExitCode)
            Assert.Equal(2, report.Services.Length)

            let serial, _ = Repository.verifyTimed 1 root (Since baseline)
            Assert.True(serial.Complete, String.concat "\n" serial.Errors)
            Assert.Equal(0, serial.ExitCode)
            Assert.Equal<string list>(report.Services |> List.map _.Service, serial.Services |> List.map _.Service)

            let originalBilling = readPolicy billing

            for foreign in
                [ { originalBilling with
                      Domains = [ "Foreign.Domain" ] }
                  { originalBilling with
                      Constructors = [ "Foreign.create" ] } ] do
                savePolicy billing foreign

                match RepositoryPlan.plan root (Since baseline) with
                | Error _ -> ()
                | Ok _ ->
                    let rejected = Repository.verify root (Since baseline)
                    Assert.Equal(3, rejected.ExitCode)

            savePolicy billing originalBilling

            write billing "Billing.fs" "module Billing\nlet value = Contracts.value + 1\n"
            let consumingPolicy = readPolicy billing

            savePolicy
                billing
                { consumingPolicy with
                    Sources =
                        consumingPolicy.Sources
                        |> List.map (fun item ->
                            if item.File = "Billing.fs" then
                                { item with
                                    Digest = SafetyPolicy.fileDigest (Path.Combine(billing, item.File)) }
                            else
                                item) }

            let unreviewed = Repository.verify root (Since baseline)
            Assert.True(unreviewed.Complete, String.concat "\n" unreviewed.Errors)
            Assert.Equal(1, unreviewed.ExitCode))

    [<Fact>]
    let ``cross policy references require exact target identity and an acyclic graph`` () =
        withRepository (fun root ->
            let contracts = service root "Contracts"
            let billing = service root "Billing"
            link true billing "Billing" contracts "Contracts"
            let valid = readPolicy billing
            let selection = plan root (Service "services/Billing")
            assertAffected [ "services/Billing" ] selection
            Assert.Equal<string list>([ "services/Contracts" ], selection.Dependencies)

            let replaceEdge change =
                savePolicy
                    billing
                    { valid with
                        Repository =
                            valid.Repository
                            |> Option.map (fun repository ->
                                { repository with
                                    References = repository.References |> List.map change }) }

            for invalid in
                [ fun (edge: WorkspaceRepositoryReference) -> { edge with Profile = Some "CONTRACTS" }
                  fun edge -> { edge with Profile = None }
                  fun edge ->
                      { edge with
                          Kind = Some "implementation" } ] do
                replaceEdge invalid
                Assert.True(RepositoryPlan.plan root All |> Result.isError)

            replaceEdge (fun edge ->
                { edge with
                    Policy = "services/Billing/fdull.json" })

            Assert.True(RepositoryPlan.plan root All |> Result.isError)

            savePolicy billing valid
            let targetPolicy = readPolicy contracts

            savePolicy
                contracts
                { targetPolicy with
                    Projects = targetPolicy.Projects |> List.map (fun item -> { item with Profile = "TEST" })
                    Sources = targetPolicy.Sources |> List.map (fun item -> { item with Profile = "TEST" }) }

            let testTargetDigest =
                WorkspacePolicyDigest.file (Path.Combine(contracts, "fdull.json"))

            replaceEdge (fun edge ->
                { edge with
                    Profile = Some "TEST"
                    PolicyDigest = testTargetDigest })

            Assert.True(RepositoryPlan.plan root All |> Result.isError)
            savePolicy contracts targetPolicy
            savePolicy billing valid
            link false contracts "Contracts" billing "Billing"
            Assert.True(RepositoryPlan.plan root All |> Result.isError))

    [<Fact>]
    let ``library changes select transitive consumers and removed edges stay conservative`` () =
        withRepository (fun root ->
            let contracts = service root "Contracts"
            let billing = service root "Billing"
            let orders = service root "Orders"
            link true billing "Billing" contracts "Contracts"
            link true orders "Orders" billing "Billing"
            let baseline = initializeGit root

            write contracts "Contracts.fs" "module Contracts\nlet value = 2\n"
            assertAffected [ "services/Billing"; "services/Contracts"; "services/Orders" ] (plan root (Since baseline))

            write contracts "Contracts.fs" "module Contracts\nlet value = 1\n"

            write
                orders
                "Orders.fsproj"
                "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><Compile Include=\"Orders.fs\"/></ItemGroup></Project>"

            command orders "dotnet" [ "restore"; "Orders.fsproj"; "--force-evaluate"; "-m:1"; "-nr:false" ]
            |> ignore

            let current = readPolicy orders

            savePolicy
                orders
                { current with
                    Inputs =
                        current.Inputs
                        |> List.map (fun input ->
                            if input.File = "Orders.fsproj" || input.File = "packages.lock.json" then
                                { input with
                                    Digest = SafetyPolicy.fileDigest (Path.Combine(orders, input.File)) }
                            else
                                input)
                    Repository =
                        current.Repository
                        |> Option.map (fun repository -> { repository with References = [] }) }

            assertAffected [ "services/Billing"; "services/Contracts"; "services/Orders" ] (plan root (Since baseline)))

    [<Fact>]
    let ``cross policy reference cannot target an excluded F sharp project`` () =
        withRepository (fun root ->
            let contracts = service root "Contracts"
            let billing = service root "Billing"
            link true billing "Billing" contracts "Contracts"

            write
                contracts
                "Safe.fsproj"
                "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><Compile Include=\"Safe.fs\"/></ItemGroup></Project>"

            write contracts "Safe.fs" "module Safe\nlet value = 1\n"

            command contracts "dotnet" [ "restore"; "Safe.fsproj"; "--force-evaluate"; "-m:1"; "-nr:false" ]
            |> ignore

            let original = readPolicy contracts

            savePolicy
                contracts
                { original with
                    Projects =
                        [ { File = "Safe.fsproj"
                            Profile = "PURE"
                            References = [] } ]
                    Sources =
                        [ { File = "Safe.fs"
                            Project = "Safe.fsproj"
                            Profile = "PURE"
                            Digest = SafetyPolicy.fileDigest (Path.Combine(contracts, "Safe.fs")) } ]
                    Inputs =
                        { File = "Safe.fsproj"
                          Digest = SafetyPolicy.fileDigest (Path.Combine(contracts, "Safe.fsproj")) }
                        :: (original.Inputs
                            |> List.map (fun input ->
                                if input.File = "packages.lock.json" then
                                    { input with
                                        Digest = SafetyPolicy.fileDigest (Path.Combine(contracts, input.File)) }
                                else
                                    input))
                    ExcludedProjects =
                        Some
                            [ { File = "Contracts.fsproj"
                                Reason = "The fixture excludes this F# project from analysis." } ] }

            let owner = readPolicy billing

            let changedDigest =
                WorkspacePolicyDigest.file (Path.Combine(contracts, "fdull.json"))

            savePolicy
                billing
                { owner with
                    Repository =
                        owner.Repository
                        |> Option.map (fun repository ->
                            { repository with
                                References =
                                    repository.References
                                    |> List.map (fun edge ->
                                        { edge with
                                            PolicyDigest = changedDigest }) }) }

            Assert.True(RepositoryPlan.plan root All |> Result.isError))

    [<Fact>]
    let ``root verify executes selected independent service with its own policy`` () =
        withRepository (fun root ->
            service root "Billing" |> ignore
            service root "Orders" |> ignore
            let baseline = initializeGit root
            let billing = Path.Combine(root, "services/Billing")
            let ordersDll = Path.Combine(root, "services/Orders/bin/Release/net10.0/Orders.dll")

            let beforeOrders =
                if File.Exists ordersDll then
                    Some(SafetyPolicy.fileDigest ordersDll)
                else
                    None

            write billing "Billing.fs" "module Billing\nlet value = 2\n"
            let policyPath = Path.Combine(billing, "fdull.json")
            let policy = File.ReadAllText policyPath |> Codec.decode<WorkspacePolicyDocument>

            let reviewed =
                { policy with
                    Sources =
                        policy.Sources
                        |> List.map (fun source ->
                            if source.File = "Billing.fs" then
                                { source with
                                    Digest = SafetyPolicy.fileDigest (Path.Combine(billing, source.File)) }
                            else
                                source) }

            File.WriteAllText(policyPath, Codec.encode reviewed)
            let report = Repository.verify root (Since baseline)
            Assert.True(report.Complete, String.concat "\n" report.Errors)
            Assert.Equal(0, report.ExitCode)
            assertAffected [ "services/Billing" ] report.Plan
            Assert.Equal("services/Billing", (Assert.Single report.Services).Service)

            Assert.Equal(
                beforeOrders,
                if File.Exists ordersDll then
                    Some(SafetyPolicy.fileDigest ordersDll)
                else
                    None
            ))

    [<Fact>]
    let ``CLI routes all since and explicit service verification and rejects conflicting options`` () =
        withRepository (fun root ->
            service root "Billing" |> ignore
            service root "Orders" |> ignore
            let baseline = initializeGit root

            let full = cli root [ "verify"; "."; "--all" ]
            Assert.Equal(0, full.ExitCode)
            Assert.Contains("all scope", full.Output)
            Assert.Contains("services/Billing: affected", full.Output)
            Assert.Contains("services/Orders: affected", full.Output)

            let empty = cli root [ "verify"; "."; "--since"; baseline ]
            Assert.Equal(0, empty.ExitCode)
            Assert.Contains("no affected services", empty.Output)
            Assert.Contains(baseline, empty.Output)

            let one = cli root [ "verify"; "services/Billing" ]
            Assert.Equal(0, one.ExitCode)
            Assert.Contains("Service: services/Billing", one.Output)
            Assert.Contains("services/Orders: skipped (not verified)", one.Output)

            Assert.Equal(2, (cli root [ "verify"; "."; "--all"; "--since"; baseline ]).ExitCode))

    let private constructorIdentity directory =
        let review = Workspace.review directory

        let constructors =
            review.Items |> List.filter (fun item -> item.Kind = "constructor")

        (Assert.Single constructors).Identity

    let private domainSource name =
        "module "
        + name
        + "\ntype Quantity = private Quantity of int\nlet create value = if value > 0 then Ok(Quantity value) else Error \"invalid\"\n"

    let private approveConstructors directory identities =
        let policy = readPolicy directory

        savePolicy
            directory
            { policy with
                Constructors = identities }

    let private serviceReport (report: RepositoryReport) service =
        (report.Services |> List.find (fun item -> item.Service = service)).Report

    let private failures (report: RepositoryReport) =
        report.Errors
        @ (report.Services
           |> List.collect (fun item ->
               item.Report.Errors
               @ (item.Report.Projects
                  |> List.collect (fun project ->
                      project.Report.Diagnostics
                      |> List.map (fun diagnostic -> diagnostic.Rule + " " + diagnostic.Message)))
               |> List.map (fun error -> item.Service + ": " + error)))
        |> String.concat "\n"

    [<Fact>]
    let ``constructor approvals bind to their owning policy on both sides of an edge`` () =
        withRepository (fun root ->
            let contracts = service root "Contracts"
            let billing = service root "Billing"
            write contracts "Contracts.fs" (domainSource "Contracts")
            write billing "Billing.fs" (domainSource "Billing")
            reviewSources contracts
            reviewSources billing
            let supplied = constructorIdentity contracts
            let owned = constructorIdentity billing
            Assert.NotEqual<string>(supplied, owned)
            approveConstructors contracts [ supplied ]
            link true billing "Billing" contracts "Contracts"
            approveConstructors billing [ owned ]
            initializeGit root |> ignore

            let approved = Repository.verify root All
            Assert.True(approved.Complete && approved.ExitCode = 0, failures approved)

            // A consumer cannot approve its dependency's constructor for it.
            approveConstructors contracts []
            approveConstructors billing [ owned; supplied ]
            repin billing
            let borrowed = Repository.verify root All
            Assert.Equal(3, borrowed.ExitCode)

            Assert.Contains(
                "BUILD005: Unused protected constructor: " + supplied,
                (serviceReport borrowed "services/Billing").Errors
            )

            Assert.Contains(
                (serviceReport borrowed "services/Contracts").Projects
                |> List.collect _.Report.Diagnostics,
                fun diagnostic -> diagnostic.Rule = "MODEL003"
            )

            // Unused approvals are checked per owning policy, even when another policy uses the same identity.
            approveConstructors contracts [ supplied ]
            repin billing
            let duplicated = Repository.verify root All
            Assert.Equal(3, duplicated.ExitCode)

            Assert.Contains(
                "BUILD005: Unused protected constructor: " + supplied,
                (serviceReport duplicated "services/Billing").Errors
            )

            Assert.Empty((serviceReport duplicated "services/Contracts").Errors)
            Assert.Equal(0, (serviceReport duplicated "services/Contracts").ExitCode)

            // Domain visibility stays blocked on both sides of the edge.
            approveConstructors billing [ owned ]
            let consumer = readPolicy billing

            savePolicy
                billing
                { consumer with
                    Domains = [ "Billing.Quantity" ] }

            match RepositoryPlan.plan root All with
            | Error error -> Assert.Contains("domain visibility", error)
            | Ok _ -> failwith "A consumer with a cross-policy edge declared a domain."

            savePolicy billing consumer
            let dependency = readPolicy contracts

            savePolicy
                contracts
                { dependency with
                    Domains = [ "Contracts.Quantity" ] }

            repin billing
            Assert.Contains("domain visibility", validationError billing))

    let private implementationEdge native =
        { Project = "Billing.fsproj"
          Target = "services/Native/Interop/Interop.csproj"
          Policy = "services/Native/fdull.json"
          PolicyDigest = WorkspacePolicyDigest.file (Path.Combine(native, "fdull.json"))
          Profile = None
          Kind = Some "implementation" }

    [<Fact>]
    let ``implementation references require a pinned target inside its owner's implementation scope`` () =
        withRepository (fun root ->
            let native = service root "Native"

            let valueSource =
                "namespace Interop { public static class Value { public static int One => 1; } }\n"

            write native "Interop/Interop.csproj" "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>"
            // The F# compiler policy targets of an enclosing checkout do not apply to C#.
            write native "Interop/Directory.Build.targets" "<Project />\n"
            write native "Interop/Value.cs" valueSource

            command native "dotnet" [ "restore"; "Interop/Interop.csproj"; "-m:1"; "-nr:false" ]
            |> ignore

            let initial = readPolicy native

            let scope =
                { Path = "Interop"
                  Kind = "implementation"
                  Reason = "The C# interop library has separate required checks." }

            savePolicy
                native
                { initial with
                    Inputs =
                        initial.Inputs
                        @ ([ "Interop/Interop.csproj"
                             "Interop/Directory.Build.targets"
                             "Interop/packages.lock.json" ]
                           |> List.map (fun file ->
                               { File = file
                                 Digest = SafetyPolicy.fileDigest (Path.Combine(native, file)) }))
                    External = Some [ scope ] }

            let reviewed = readPolicy native
            let billing = service root "Billing"
            crossLink true billing "Billing" (implementationEdge native)
            let baseline = initializeGit root
            Assert.Equal(Ok(), Workspace.validatePolicy billing)
            let selection = plan root (Service "services/Billing")
            Assert.Equal<string list>([ "services/Native" ], selection.Dependencies)

            write native "Interop/Value.cs" (valueSource.Replace("=> 1", "=> 2"))
            assertAffected [ "services/Billing"; "services/Native" ] (plan root (Since baseline))
            write native "Interop/Value.cs" valueSource

            let report = Repository.verify root All
            Assert.True(report.Complete && report.ExitCode = 0, failures report)

            let rejectTarget change =
                savePolicy native (change reviewed)
                repin billing
                let error = validationError billing
                Assert.True(RepositoryPlan.plan root All |> Result.isError)
                savePolicy native reviewed
                repin billing
                error

            Assert.Contains(
                "Cross-policy target",
                rejectTarget (fun policy ->
                    { policy with
                        Inputs = policy.Inputs |> List.filter (fun pin -> pin.File <> "Interop/Interop.csproj") })
            )

            Assert.Contains(
                "Cross-policy target",
                rejectTarget (fun policy ->
                    { policy with
                        External = Some [ { scope with Path = "Interop/Value.cs" } ] })
            )

            Assert.Contains(
                "Cross-policy target",
                rejectTarget (fun policy ->
                    { policy with
                        External = Some [ { scope with Kind = "automation" } ] })
            )

            Assert.Equal(Ok(), Workspace.validatePolicy billing)

            let rejectEdge change =
                let current = readPolicy billing
                updateEdges billing change
                let error = validationError billing
                savePolicy billing current
                error

            Assert.Contains(
                "Dependency policy differs",
                rejectEdge (fun edge ->
                    { edge with
                        PolicyDigest = "sha256:" + String('0', 64) })
            )

            for invalid in
                [ fun (edge: WorkspaceRepositoryReference) -> { edge with Kind = Some "automation" }
                  fun edge -> { edge with Kind = None }
                  fun edge ->
                      { edge with
                          Kind = None
                          Profile = Some "PURE" }
                  fun edge -> { edge with Profile = Some "PURE" } ] do
                Assert.Contains("Invalid cross-policy", rejectEdge invalid)

            Assert.Contains(
                "Cross-policy target",
                rejectEdge (fun edge ->
                    { edge with
                        Target = "services/Native/Interop/Missing.csproj" })
            )

            Assert.Equal(Ok(), Workspace.validatePolicy billing))

    [<Fact>]
    let ``mutual service dependencies verify with normalized policy pins and block project cycles`` () =
        withRepository (fun root ->
            let contracts = service root "Contracts"

            let probeProject reference =
                "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><Compile Include=\"Probe.fs\"/>"
                + reference
                + "</ItemGroup></Project>"

            write contracts "Probe/Probe.fsproj" (probeProject "")
            write contracts "Probe/Probe.fs" "module Probe\nlet value = 1\n"

            command contracts "dotnet" [ "restore"; "Probe/Probe.fsproj"; "-m:1"; "-nr:false" ]
            |> ignore

            File.Delete(Path.Combine(contracts, "fdull.json"))
            Project.initialize contracts |> ignore
            let initial = readPolicy contracts

            savePolicy
                contracts
                { initial with
                    Version = 2
                    Repository =
                        Some
                            { Root = "../.."
                              SharedInputs = []
                              References = [] } }

            let billing = service root "Billing"
            link true billing "Billing" contracts "Contracts"

            write
                contracts
                "Probe/Probe.fsproj"
                (probeProject "<ProjectReference Include=\"../../Billing/Billing.fsproj\"/>")

            command contracts "dotnet" [ "restore"; "Probe/Probe.fsproj"; "--force-evaluate"; "-m:1"; "-nr:false" ]
            |> ignore

            let billingPolicy = Path.Combine(billing, "fdull.json")

            let probeEdge =
                { Project = "Probe/Probe.fsproj"
                  Target = "services/Billing/Billing.fsproj"
                  Policy = "services/Billing/fdull.json"
                  PolicyDigest = WorkspacePolicyDigest.file billingPolicy
                  Profile = Some "PURE"
                  Kind = None }

            let current = readPolicy contracts

            // The evaluated closure of Probe includes Contracts.fsproj through Billing, so the
            // protected graph declares that transitive local reference explicitly.
            savePolicy
                contracts
                { current with
                    Inputs =
                        current.Inputs
                        |> List.map (fun input ->
                            if input.File.StartsWith("Probe/", StringComparison.Ordinal) then
                                { input with
                                    Digest = SafetyPolicy.fileDigest (Path.Combine(contracts, input.File)) }
                            else
                                input)
                    Projects =
                        current.Projects
                        |> List.map (fun project ->
                            if project.File = "Probe/Probe.fsproj" then
                                { project with
                                    References = [ "Contracts.fsproj" ] }
                            else
                                project)
                    Repository =
                        Some
                            { Root = "../.."
                              SharedInputs = []
                              References = [ probeEdge ] } }

            // Re-pinning the consumer changes only its own PolicyDigest, so the reverse pin still holds.
            let rawBefore = SafetyPolicy.fileDigest billingPolicy
            repin billing
            Assert.NotEqual<string>(rawBefore, SafetyPolicy.fileDigest billingPolicy)
            Assert.Equal(probeEdge.PolicyDigest, WorkspacePolicyDigest.file billingPolicy)
            Assert.Equal(Ok(), Workspace.validatePolicy billing)
            Assert.Equal(Ok(), Workspace.validatePolicy contracts)
            let baseline = initializeGit root

            let printed = cli root [ "policy-digest"; "services/Billing/fdull.json" ]
            Assert.Equal(0, printed.ExitCode)
            Assert.Equal(probeEdge.PolicyDigest, printed.Output.Trim())

            write billing "Billing.fs" "module Billing\nlet value = 2\n"
            assertAffected [ "services/Billing"; "services/Contracts" ] (plan root (Since baseline))
            write billing "Billing.fs" "module Billing\nlet value = 1\n"

            let report = Repository.verify root All
            Assert.True(report.Complete && report.ExitCode = 0, failures report)

            // Every byte outside the dependency pins is covered by the consumer's pin.
            let original = File.ReadAllText billingPolicy
            File.WriteAllText(billingPolicy, original + "\n")
            Assert.Contains("Dependency policy differs: services/Billing/fdull.json", validationError contracts)
            File.WriteAllText(billingPolicy, original)
            Assert.Equal(Ok(), Workspace.validatePolicy contracts)

            // A project-level cycle across the two policies stays blocked.
            link false contracts "Contracts" billing "Billing"
            let cyclic = readPolicy contracts

            savePolicy
                contracts
                { cyclic with
                    Repository =
                        cyclic.Repository
                        |> Option.map (fun repository ->
                            { repository with
                                References = probeEdge :: repository.References }) }

            repin billing
            Assert.StartsWith("ARCH001:", validationError contracts)

            match RepositoryPlan.plan root All with
            | Error error -> Assert.StartsWith("ARCH001:", error)
            | Ok _ -> failwith "A cross-policy project cycle was planned.")

    [<Fact>]
    let ``policy digest clears only dependency pins and rejects ambiguous JSON`` () =
        let bytes (text: string) = Text.Encoding.UTF8.GetBytes text

        let digest text =
            WorkspacePolicyDigest.ofBytes (bytes text)

        let policy (pin: string) =
            "{\"Version\":2,\"Repository\":{\"Root\":\"../..\",\"References\":[{\"Policy\":\"a/fdull.json\",\"PolicyDigest\":\""
            + pin
            + "\"},{\"PolicyDigest\":\""
            + pin
            + "\"}]},\"Projects\":[{\"PolicyDigest\":\"kept\"}],\"Domains\":[]}"

        let first = "sha256:" + String('a', 64)
        let second = "sha256:" + String('b', 64)
        Assert.Equal(digest (policy first), digest (policy second))
        Assert.Equal(digest (policy first), digest (policy "with \\\"escaped\\\" quotes"))
        Assert.Equal<byte array>(bytes (policy ""), WorkspacePolicyDigest.canonical (bytes (policy first)))
        Assert.Equal(SafetyPolicy.digest (bytes (policy "")), digest (policy first))

        for changed in
            [ policy first + "\n"
              (policy first).Replace("\"Root\":", "\"Root\" :")
              (policy first).Replace("a/fdull.json", "b/fdull.json")
              (policy first).Replace("\"kept\"", "\"other\"")
              "\uFEFF" + policy first ] do
            Assert.NotEqual<string>(digest (policy first), digest changed)

        for rejected in
            [ "{\"Version\":2,\"Version\":2}"
              "{\"Repository\":{\"References\":[{\"PolicyDigest\":\"a\",\"PolicyDigest\":\"b\"}]}}"
              "{\"Projects\":[{\"File\":\"a\",\"File\":\"b\"}]}"
              "{\"Repository\":{\"References\":[{\"Policy\\u0044igest\":\"a\"}]}}"
              "{\"Repository\":{\"References\":[{\"PolicyDigest\":1}]}}"
              "{\"Version\":2,}"
              "{\"Version\":2}/*comment*/"
              "{\"Version\":2} {}" ] do
            Assert.ThrowsAny<Exception>(fun () -> digest rejected |> ignore) |> ignore

        Assert.ThrowsAny<Exception>(fun () ->
            WorkspacePolicyDigest.ofBytes [| 0x7Buy; 0xC3uy; 0x28uy; 0x7Duy |] |> ignore)
        |> ignore
