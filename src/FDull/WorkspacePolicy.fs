namespace FDull

open System
open System.IO
open System.Text.Json.Nodes
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Symbols
open FSharp.Compiler.Text
open FSharp.Compiler.Syntax
open FDull.Transport

type WorkspaceSource =
    { File: string
      Project: string
      Profile: string
      Digest: string }

type WorkspacePin = { File: string; Digest: string }

type WorkspaceExternal =
    { Path: string
      Kind: string
      Reason: string }

type WorkspaceScopeDocument =
    { Version: int
      External: WorkspaceExternal list }

type WorkspaceCapability =
    { File: string
      Owner: string
      Kind: string
      Identity: string
      Reason: string }

type WorkspaceProject =
    { File: string
      Profile: string
      References: string list }

type WorkspaceAssemblyPin = { Identity: string; Digest: string }

/// One reviewed data vocabulary entry for a consuming project, independent of declaration count.
type WorkspaceDataContract =
    { Project: string
      Identity: string
      Assemblies: WorkspaceAssemblyPin list
      Reason: string }

type WorkspacePolicyDocument =
    { Version: int
      Projects: WorkspaceProject list
      Sources: WorkspaceSource list
      Inputs: WorkspacePin list
      External: WorkspaceExternal list option
      DataContracts: WorkspaceDataContract list option
      Capabilities: WorkspaceCapability list
      Domains: string list
      Constructors: string list }

type WorkspaceUse =
    { File: string
      Owner: string
      Kind: string
      Identity: string }

/// Advisory evidence for an actual unsatisfied finding, not a permission entry.
type WorkspaceReviewFinding =
    { Diagnostic: SafetyDiagnostic
      Owner: string
      Kind: string
      Identity: string
      Resolution: string }

/// Validate the exported invocation as well as the MSBuild property defaults.
module WorkspaceCompilerPolicy =
    let required =
        [ "--langversion:10.0"
          "--noframework"
          "--warn:5"
          "--warnon:21,22,52,1178,1182,3180,3186,3218,3391,3395,3559,3570,3579,3582,3878"
          "--checked+"
          "--checknulls+"
          "--deterministic+"
          "--targetprofile:netcore"
          "--nocopyfsharpcore" ]

    let validate (arguments: string list) =
        let known =
            required
            @ [ "-g"
                "--debug:portable"
                "--optimize+"
                "--target:library"
                "--target:exe"
                "--warnaserror"
                "--warnaserror+"
                "--warnaserror:3239"
                "--fullpaths"
                "--flaterrors"
                "--highentropyva+"
                "--simpleresolution"
                "--define:TRACE"
                "--define:RELEASE"
                "--define:NULLABLE"
                "--define:NET"
                "--define:NET10_0"
                "--define:NETCOREAPP"
                "--define:NET5_0_OR_GREATER"
                "--define:NET6_0_OR_GREATER"
                "--define:NET7_0_OR_GREATER"
                "--define:NET8_0_OR_GREATER"
                "--define:NET9_0_OR_GREATER"
                "--define:NET10_0_OR_GREATER"
                "--define:NETCOREAPP1_0_OR_GREATER"
                "--define:NETCOREAPP1_1_OR_GREATER"
                "--define:NETCOREAPP2_0_OR_GREATER"
                "--define:NETCOREAPP2_1_OR_GREATER"
                "--define:NETCOREAPP2_2_OR_GREATER"
                "--define:NETCOREAPP3_0_OR_GREATER"
                "--define:NETCOREAPP3_1_OR_GREATER" ]
            |> Set.ofList

        let approved argument =
            Set.contains argument known
            || [ "-o:"; "-r:"; "--doc:"; "--embed:"; "--pathmap:"; "--sourcelink:" ]
               |> List.exists (fun prefix ->
                   argument.StartsWith(prefix, StringComparison.Ordinal)
                   && argument.Length > prefix.Length)

        if
            required |> List.exists (fun flag -> not (List.contains flag arguments))
            || not (
                List.contains "--warnaserror" arguments
                || List.contains "--warnaserror+" arguments
            )
        then
            Error "BUILD002: The exported compiler invocation omits a required safety setting."
        elif arguments |> List.exists (fun argument -> not (approved argument)) then
            Error "BUILD002: The exported compiler invocation contains an unsupported or disabling flag."
        else
            Ok()

module internal WorkspacePolicy =
    let profiles =
        set
            [ "PURE"
              "PURE_HELPER"
              "CONTRACTS"
              "BOUNDARY"
              "ADAPTER"
              "TEST"
              "GUARD_TOOLING" ]

    let externalKinds = [ "implementation"; "automation"; "tooling" ]

    let ruleReviewable profile (rule: string) =
        SafetyRules.knownDiagnostic rule
        && not (rule.StartsWith("BUILD", StringComparison.Ordinal))
        && not (rule.StartsWith("ATTR", StringComparison.Ordinal))
        && not (rule.StartsWith("FS", StringComparison.Ordinal))
        && not (List.contains rule [ "DATA001"; "DATA005"; "MODEL001"; "MODEL002"; "MODEL003"; "EFFECT001" ])
        && (not (List.contains profile [ "PURE"; "PURE_HELPER"; "CONTRACTS" ])
            || (profile = "PURE_HELPER" && rule = "STYLE003"))

    let reviewResolution profile (rule: string) kind =
        if
            rule.StartsWith("BUILD", StringComparison.Ordinal)
            || rule.StartsWith("ARCH", StringComparison.Ordinal)
        then
            "analysis-blocked"
        else
            let reviewable =
                match kind with
                | "type"
                | "attribute"
                | "constructor" -> true
                | "member" ->
                    not (List.contains profile [ "PURE"; "PURE_HELPER" ])
                    || rule = "EFFECT001"
                    || (profile = "PURE_HELPER" && rule = "STYLE003")
                | "rule" -> ruleReviewable profile rule
                | _ -> false

            if reviewable then "contract-review" else "code-change"

    let internal localFile (file: string) =
        not (String.IsNullOrWhiteSpace file)
        && not (Path.IsPathRooted file)
        && not (file.Contains('\\'))
        && file.Split('/')
           |> Array.forall (fun part -> part <> "" && part <> "." && part <> "..")

    let externalEntries (document: WorkspacePolicyDocument) =
        match document.External with
        | Some entries -> entries
        | None -> []

    let dataContracts (document: WorkspacePolicyDocument) =
        document.DataContracts |> Option.defaultValue []

    let private decodeDocument (text: string) =
        let json = JsonNode.Parse text |> External.required "workspace.policy"
        let fields = json.AsObject()

        if not (fields.ContainsKey "External") then
            fields.Add("External", JsonArray())

        if not (fields.ContainsKey "DataContracts") then
            fields.Add("DataContracts", JsonArray())

        fields.ToJsonString() |> Codec.decode<WorkspacePolicyDocument>

    let validateExternal entries =
        let contains (parent: string) (child: string) =
            child = parent || child.StartsWith(parent + "/", StringComparison.Ordinal)

        entries
        |> List.iter (fun entry ->
            if
                not (localFile entry.Path)
                || not (List.contains entry.Kind externalKinds)
                || String.IsNullOrWhiteSpace entry.Reason
            then
                invalidOp "BUILD005: Invalid external workspace classification.")

        let paths = entries |> List.map _.Path

        if
            paths.Length <> (Set.ofList paths).Count
            || paths
               |> List.exists (fun path -> paths |> List.exists (fun other -> path <> other && contains other path))
        then
            invalidOp "BUILD005: Duplicate or overlapping external workspace classification."

    let private readWithSourceDrift allowSourceDrift root =
        let path = Path.Combine(root, "fdull.json")
        let document = File.ReadAllText path |> decodeDocument

        if document.Version <> 1 || document.Sources.IsEmpty || document.Projects.IsEmpty then
            invalidOp "BUILD005: Missing or unsupported workspace policy."

        validateExternal (externalEntries document)

        for project in document.Projects do
            if
                not (localFile project.File)
                || Path.GetExtension project.File <> ".fsproj"
                || not (Set.contains project.Profile profiles)
                || project.References |> List.exists (fun file -> not (localFile file))
            then
                invalidOp "BUILD005: Invalid workspace project assignment."

        let keys = document.Sources |> List.map _.File

        let contracts = dataContracts document

        let digest (value: string) =
            value.Length = 71
            && value.StartsWith("sha256:", StringComparison.Ordinal)
            && value.Substring(7)
               |> Seq.forall (fun character ->
                   (character >= '0' && character <= '9') || (character >= 'a' && character <= 'f'))

        for contract in contracts do
            let identity = contract.Identity.Split(" | ")

            if
                not (
                    document.Projects
                    |> List.exists (fun project -> project.File = contract.Project)
                )
                || identity.Length <> 2
                || identity |> Array.exists String.IsNullOrWhiteSpace
                || contract.Identity.Contains('*')
                || contract.Identity.Contains('?')
                || contract.Identity.StartsWith("source:", StringComparison.Ordinal)
                || String.IsNullOrWhiteSpace contract.Reason
                || contract.Assemblies.IsEmpty
                || contract.Assemblies
                   |> List.exists (fun pin ->
                       String.IsNullOrWhiteSpace pin.Identity
                       || pin.Identity.Contains('*')
                       || pin.Identity.Contains('?')
                       || not (digest pin.Digest))
                || contract.Assemblies.Length
                   <> (contract.Assemblies |> List.map _.Identity |> Set.ofList).Count
                || not (contract.Assemblies |> List.exists (fun pin -> pin.Identity = identity[0]))
            then
                invalidOp "BUILD005: Invalid reusable data contract or assembly fingerprint."

        if
            contracts.Length
            <> (contracts
                |> List.map (fun contract -> contract.Project, contract.Identity)
                |> Set.ofList)
                .Count
        then
            invalidOp "BUILD005: Duplicate reusable data contract."

        if keys.Length <> (Set.ofList keys).Count then
            invalidOp "BUILD005: Duplicate workspace source assignment."

        for identities in [ document.Domains; document.Constructors ] do
            if
                identities.Length <> (Set.ofList identities).Count
                || identities |> List.exists String.IsNullOrWhiteSpace
            then
                invalidOp "BUILD005: Empty or duplicate domain/constructor identity."

        for source in document.Sources do
            if
                not (Set.contains source.Profile profiles)
                || not (localFile source.File)
                || not (localFile source.Project)
            then
                invalidOp "BUILD005: Invalid workspace source assignment."

            let current = SafetyPolicy.fileDigest (Path.Combine(root, source.File))

            if not allowSourceDrift && current <> source.Digest then
                invalidOp (
                    "BUILD005: Source changed since its exact contracts were reviewed: "
                    + source.File
                )

        if
            document.Inputs.IsEmpty
            || document.Inputs.Length
               <> (document.Inputs |> List.map _.File |> Set.ofList).Count
        then
            invalidOp "BUILD005: Missing or duplicate protected build inputs."

        for pin in document.Inputs do
            if
                not (localFile pin.File)
                || SafetyPolicy.fileDigest (Path.Combine(root, pin.File)) <> pin.Digest
            then
                invalidOp ("BUILD005: Protected build input differs: " + pin.File)

        for entry in document.Capabilities do
            if
                not (Set.contains entry.File (Set.ofList keys))
                || String.IsNullOrWhiteSpace entry.Owner
                || String.IsNullOrWhiteSpace entry.Identity
                || String.IsNullOrWhiteSpace entry.Reason
                || not (List.contains entry.Kind [ "member"; "type"; "rule"; "profile"; "attribute" ])
                || (entry.Kind = "profile" && not (Set.contains entry.Identity profiles))
                || (entry.Kind = "rule"
                    && (not (entry.Identity.Contains(" | ", StringComparison.Ordinal))
                        || entry.Identity.StartsWith("BUILD", StringComparison.Ordinal)
                        || entry.Identity.StartsWith("ATTR", StringComparison.Ordinal)
                        || entry.Identity.StartsWith("FS", StringComparison.Ordinal)
                        || not (SafetyRules.knownDiagnostic (entry.Identity.Split(" | ")[0]))
                        || [ "DATA001"; "DATA005"; "MODEL001"; "MODEL002"; "MODEL003"; "EFFECT001" ]
                           |> List.exists (fun rule ->
                               entry.Identity.StartsWith(rule + " | ", StringComparison.Ordinal))))
            then
                invalidOp "BUILD005: Invalid or overbroad workspace capability."

        let assigned =
            document.Sources
            |> List.map (fun source -> source.File, source.Profile)
            |> Map.ofList

        let profileEntries =
            document.Capabilities |> List.filter (fun entry -> entry.Kind = "profile")

        if
            profileEntries.Length
            <> (profileEntries |> List.map (fun entry -> entry.File, entry.Owner) |> Set.ofList).Count
        then
            invalidOp "BUILD005: Conflicting declaration profiles."

        for entry in document.Capabilities do
            let effective =
                profileEntries
                |> List.tryFind (fun item -> item.File = entry.File && item.Owner = entry.Owner)
                |> Option.map _.Identity
                |> Option.defaultValue (Map.tryFind entry.File assigned |> Option.defaultValue "UNCLASSIFIED")

            if
                entry.Kind = "rule"
                && not (ruleReviewable effective (entry.Identity.Split(" | ")[0]))
            then
                invalidOp "BUILD005: Data and pure profiles cannot receive safety exemptions."

        if
            document.Capabilities.Length
            <> (document.Capabilities
                |> List.map (fun entry -> entry.File, entry.Owner, entry.Kind, entry.Identity)
                |> Set.ofList)
                .Count
        then
            invalidOp "BUILD005: Duplicate workspace capability."

        document

    let read root = readWithSourceDrift false root

    /// Only authored source drift is allowed. Build fingerprints and policy structure remain exact.
    let readForReview root = readWithSourceDrift true root

    let private region (r: range) =
        { File = r.FileName
          Start = r.StartLine, r.StartColumn
          End = r.EndLine, r.EndColumn }

    let context
        root
        project
        generated
        (references: SafetyInput list)
        (budget: SafetyBudget)
        (document: WorkspacePolicyDocument)
        (checkedProject: FSharpCheckProjectResults)
        (syntax: Map<string, SourceSyntax>)
        =
        let owners = ResizeArray<range * string>()
        let observed = ResizeArray<WorkspaceUse>()

        let dataDigests =
            references
            |> List.map (fun input -> input.Path, lazy (DataContractPolicy.assemblyDigest input.Path))
            |> Map.ofList

        let relative file =
            Path.GetRelativePath(root, file).Replace('\\', '/')

        let rec declarations items =
            for item in items do
                budget.Visit 0

                match item with
                | FSharpImplementationFileDeclaration.Entity(entity, nested) ->
                    if not entity.IsNamespace then
                        let r = entity.DeclarationLocation

                        match Map.tryFind r.FileName syntax with
                        | Some source ->
                            let spans =
                                if entity.IsFSharpModule then
                                    source.Modules
                                else
                                    source.Types

                            for span in spans do
                                if
                                    (span.StartLine, span.StartColumn) <= (r.StartLine, r.StartColumn)
                                    && (r.EndLine, r.EndColumn) <= (span.EndLine, span.EndColumn)
                                then
                                    owners.Add(span, SafetyPolicy.entityName entity)
                        | None -> ()

                    declarations nested
                | FSharpImplementationFileDeclaration.MemberOrFunctionOrValue(memberInfo, _, body) when
                    not memberInfo.IsCompilerGenerated
                    ->
                    let start = memberInfo.DeclarationLocation
                    let bodyRange = body.Range

                    let sourceRange =
                        Map.tryFind start.FileName syntax
                        |> Option.bind (fun source ->
                            source.Bindings
                            |> List.filter (fun span ->
                                (span.StartLine, span.StartColumn) <= (start.StartLine, start.StartColumn)
                                && (start.EndLine, start.EndColumn) <= (span.EndLine, span.EndColumn))
                            |> List.sortBy (fun span ->
                                span.EndLine - span.StartLine, span.EndColumn - span.StartColumn)
                            |> List.tryHead)
                        |> Option.defaultValue (
                            Range.mkRange bodyRange.FileName (Position.mkPos start.StartLine 0) bodyRange.End
                        )

                    owners.Add(sourceRange, memberInfo.FullName)
                | FSharpImplementationFileDeclaration.MemberOrFunctionOrValue _ -> ()
                | FSharpImplementationFileDeclaration.InitAction _ -> ()

        for file in checkedProject.AssemblyContents.ImplementationFiles do
            declarations file.Declarations

        let ownerIndex = SafetyRanges.create budget.Visit (fun (r, _) -> region r) owners

        let owner r =
            ownerIndex.Enclosing(region r)
            |> List.sortBy (fun (r, _) -> r.EndLine - r.StartLine, r.EndColumn - r.StartColumn)
            |> List.tryHead
            |> Option.map snd
            |> Option.defaultValue "<declarations>"

        let assignments =
            document.Sources |> List.map (fun source -> source.File, source) |> Map.ofList

        let permissions =
            document.Capabilities
            |> List.map (fun entry -> entry.File, entry.Owner, entry.Kind, entry.Identity)
            |> Set.ofList

        let profileAssignments =
            document.Capabilities
            |> List.filter (fun entry -> entry.Kind = "profile")
            |> List.map (fun entry -> (entry.File, entry.Owner), entry.Identity)
            |> Map.ofList

        let constructors = Set.ofList document.Constructors

        let profile (r: range) =
            if Set.contains r.FileName generated then
                "GUARD_TOOLING"
            else
                let file = relative r.FileName

                match Map.tryFind file assignments with
                | Some source when source.Project = relative project ->
                    let scope = owner r

                    match Map.tryFind (file, scope) profileAssignments with
                    | None -> source.Profile
                    | Some profile ->
                        observed.Add
                            { File = file
                              Owner = scope
                              Kind = "profile"
                              Identity = profile }

                        profile
                | Some _
                | None -> "UNCLASSIFIED"

        let allowedIn scope kind identity (r: range) =
            let useSite =
                { File = relative r.FileName
                  Owner = scope
                  Kind = kind
                  Identity = identity }

            observed.Add useSite

            Set.contains (useSite.File, useSite.Owner, kind, identity) permissions

        let allowed kind identity r = allowedIn (owner r) kind identity r

        let xunitV3Generated =
            generated |> Set.exists WorkspaceGenerated.isXunitDefaultReporters

        let aspireGenerated =
            generated |> Set.exists WorkspaceGenerated.isAspireProjectMetadata

        let generatedMemberAllowed (r: range) (memberInfo: FSharpMemberOrFunctionOrValue) =
            let identity = memberInfo.Assembly.QualifiedName + " | " + memberInfo.XmlDocSig

            Set.contains r.FileName generated
            && ((WorkspaceGenerated.isAspireProjectMetadata r.FileName
                 && identity = "Aspire.Hosting, Version=13.4.6.0, Culture=neutral, PublicKeyToken=cc7b13ffcd2ddd51 | M:Aspire.Hosting.IProjectMetadata.get_ProjectPath")
                || (WorkspaceGenerated.isTestingPlatformExtensions r.FileName
                    && List.contains
                        identity
                        [ "Microsoft.Testing.Extensions.MSBuild, Version=1.8.4.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a | M:Microsoft.Testing.Platform.MSBuild.TestingPlatformBuilderHook.AddExtensions(Microsoft.Testing.Platform.Builder.ITestApplicationBuilder,System.String[])"
                          "Microsoft.VisualStudio.TestPlatform.Extension.JUnit.Xml.TestLogger, Version=7.0.1.0, Culture=neutral, PublicKeyToken=49effd6976780fee | M:Spekt.TestReporter.JUnit.TestingPlatformBuilderHook.AddExtensions(Microsoft.Testing.Platform.Builder.ITestApplicationBuilder,System.String[])" ])
                || (WorkspaceGenerated.isXunitEntryPoint r.FileName
                    && List.contains
                        identity
                        [ "FSharp.Core, Version=10.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a | M:Microsoft.FSharp.Collections.ArrayModule.Exists``1(Microsoft.FSharp.Core.FSharpFunc{``0,System.Boolean},``0[])"
                          "FSharp.Core, Version=10.1.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a | M:Microsoft.FSharp.Collections.ArrayModule.Exists``1(Microsoft.FSharp.Core.FSharpFunc{``0,System.Boolean},``0[])"
                          "System.Runtime, Version=10.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a | M:System.Runtime.CompilerServices.TaskAwaiter`1.GetResult"
                          "System.Runtime, Version=10.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a | M:System.Threading.Tasks.Task`1.GetAwaiter"
                          "xunit.v3.runner.inproc.console, Version=3.1.0.0, Culture=neutral, PublicKeyToken=8d05b1bb7a6fdb6c | M:Xunit.Runner.InProc.SystemConsole.TestingPlatform.TestPlatformTestFramework.RunAsync(System.String[],System.Action{Microsoft.Testing.Platform.Builder.ITestApplicationBuilder,System.String[]})"
                          "xunit.v3.runner.inproc.console, Version=3.1.0.0, Culture=neutral, PublicKeyToken=8d05b1bb7a6fdb6c | M:Xunit.Runner.InProc.SystemConsole.ConsoleRunner.Run(System.String[])" ]))

        let memberAllowed (r: range) (memberInfo: FSharpMemberOrFunctionOrValue) =
            if
                generatedMemberAllowed r memberInfo
                || (Set.contains r.FileName generated
                    && memberInfo.Assembly.QualifiedName = "System.Runtime, Version=10.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a"
                    && memberInfo.XmlDocSig = "P:System.Runtime.Versioning.TargetFrameworkAttribute.FrameworkDisplayName")
            then
                true
            else
                allowed "member" (memberInfo.Assembly.QualifiedName + " | " + memberInfo.XmlDocSig) r

        let typeIdentity (entity: FSharpEntity) =
            let identity =
                if String.IsNullOrWhiteSpace entity.Assembly.QualifiedName then
                    let location = entity.DeclarationLocation
                    "source:" + relative location.FileName
                else
                    entity.Assembly.QualifiedName

            identity + " | " + SafetyPolicy.entityName entity

        let typeAllowed r entity = allowed "type" (typeIdentity entity) r

        let dataAllowed (r: range) (entity: FSharpEntity) =
            let projectName = relative project
            let identity = typeIdentity entity

            dataContracts document
            |> List.tryFind (fun contract -> contract.Project = projectName && contract.Identity = identity)
            |> Option.map (fun contract ->
                observed.Add
                    { File = relative r.FileName
                      Owner = projectName
                      Kind = "data"
                      Identity = identity }

                fun (nested: FSharpEntity) ->
                    let local =
                        if String.IsNullOrWhiteSpace nested.Assembly.QualifiedName then
                            let location = nested.DeclarationLocation

                            document.Sources
                            |> List.exists (fun source ->
                                source.Project = projectName
                                && Path.GetFullPath(source.File, root) = location.FileName)
                        else
                            false

                    if not local then
                        let assembly = nested.Assembly

                        let reference =
                            assembly.FileName
                            |> Option.bind (fun file -> references |> List.tryFind (fun input -> input.Path = file))

                        let pin =
                            contract.Assemblies
                            |> List.tryFind (fun pin -> pin.Identity = assembly.QualifiedName)

                        match reference, pin with
                        | Some reference, Some pin when (Map.find reference.Path dataDigests).Value = pin.Digest ->
                            observed.Add
                                { File = relative r.FileName
                                  Owner = projectName
                                  Kind = "data-assembly"
                                  Identity = identity + " | " + pin.Identity }
                        | _ ->
                            let expected = pin |> Option.map _.Digest |> Option.defaultValue "missing pin"

                            let actual =
                                reference
                                |> Option.map (fun reference -> (Map.find reference.Path dataDigests).Value)
                                |> Option.defaultValue "missing compiler reference"

                            invalidOp (
                                "BUILD005: Reusable data contract assembly is missing or changed: "
                                + assembly.QualifiedName
                                + " (reviewed="
                                + expected
                                + ", current="
                                + actual
                                + ")"
                            ))

        let constructorIdentity (memberInfo: FSharpMemberOrFunctionOrValue) =
            let location = memberInfo.DeclarationLocation

            relative location.FileName
            + " | "
            + memberInfo.Assembly.QualifiedName
            + " | "
            + memberInfo.XmlDocSig
            + " -> "
            + memberInfo.FullType.Format(FSharpDisplayContext.Empty)

        let attributeIdentity (attribute: SynAttribute) (entity: FSharpEntity) =
            let rec argument expression =
                match expression with
                | SynExpr.Paren(expression, _, _, _)
                | SynExpr.Typed(expression, _, _) -> argument expression
                | SynExpr.Const(SynConst.Unit, _) -> Some "unit"
                | SynExpr.Const(SynConst.String(value, _, _), _) -> Some("string:" + Codec.encode value)
                | SynExpr.Const(SynConst.Bool value, _) -> Some("bool:" + string value)
                | SynExpr.Const(SynConst.Int32 value, _) -> Some("int32:" + string value)
                | SynExpr.Tuple(_, expressions, _, _) ->
                    let arguments = expressions |> List.map argument

                    if arguments |> List.exists Option.isNone then
                        None
                    else
                        arguments |> List.choose id |> Codec.encode |> Some
                | _ -> None

            argument attribute.ArgExpr
            |> Option.map (fun arguments ->
                entity.Assembly.QualifiedName
                + " | "
                + SafetyPolicy.entityName entity
                + " | "
                + (attribute.Target |> Option.map _.idText |> Option.defaultValue "")
                + " | "
                + arguments)

        let generatedPermission rule (r: range) symbol =
            let normalized = r.FileName.Replace('\\', '/')

            (rule = "ATTR001"
             && List.contains
                 symbol
                 [ "System.Reflection.AssemblyCompanyAttribute"
                   "System.Reflection.AssemblyCopyrightAttribute"
                   "System.Reflection.AssemblyDescriptionAttribute"
                   "System.Reflection.AssemblyConfigurationAttribute"
                   "System.Reflection.AssemblyFileVersionAttribute"
                   "System.Reflection.AssemblyInformationalVersionAttribute"
                   "System.Reflection.AssemblyProductAttribute"
                   "System.Reflection.AssemblyTitleAttribute"
                   "System.Reflection.AssemblyVersionAttribute"
                   "System.Runtime.Versioning.TargetFrameworkAttribute" ])
            || ((xunitV3Generated || aspireGenerated)
                && normalized.EndsWith(".AssemblyInfo.fs", StringComparison.Ordinal)
                && rule = "ATTR001"
                && symbol = "System.Reflection.AssemblyMetadataAttribute")
            || (rule = "ATTR001"
                && normalized.EndsWith(".MvcApplicationPartsAssemblyInfo.fs", StringComparison.Ordinal)
                && symbol = "Microsoft.AspNetCore.Mvc.ApplicationParts.ApplicationPartAttribute")
            || (WorkspaceGenerated.isAspireProjectMetadata r.FileName
                && ((rule = "ATTR001" && symbol = "System.Diagnostics.DebuggerDisplayAttribute")
                    || (rule = "MODEL001"
                        && (not (String.IsNullOrWhiteSpace symbol)
                            && (not (symbol.Contains(" -> ", StringComparison.Ordinal))
                                || symbol.StartsWith("``.ctor`` -> Projects.", StringComparison.Ordinal)
                                || symbol.StartsWith("_ -> Projects.", StringComparison.Ordinal)
                                || symbol.StartsWith("this -> Projects.", StringComparison.Ordinal)
                                || symbol.StartsWith("ProjectPath -> Projects.", StringComparison.Ordinal))))))
            || (WorkspaceGenerated.isXunitDefaultReporters r.FileName
                && ((rule = "ATTR001"
                     && symbol = "Xunit.Runner.Common.RegisterRunnerReporterAttribute")
                    || (rule = "REFL001" && symbol = "Microsoft.FSharp.Core.Operators.typeof")))
            || (WorkspaceGenerated.isTestingPlatformExtensions r.FileName
                && ((rule = "ATTR001"
                     && List.contains
                         symbol
                         [ "System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute"
                           "System.Runtime.CompilerServices.ExtensionAttribute" ])
                    || (rule = "MODEL001"
                        && (symbol = "SelfRegisteredExtensions"
                            || symbol.EndsWith(".SelfRegisteredExtensions", StringComparison.Ordinal)
                            || symbol.StartsWith("AddSelfRegisteredExtensions.0 -> ", StringComparison.Ordinal)
                            || symbol.StartsWith("builder -> ", StringComparison.Ordinal)))
                    || (rule = "MUT008"
                        && List.contains symbol [ "AddSelfRegisteredExtensions.1"; "args" ])))
            || (WorkspaceGenerated.isXunitEntryPoint r.FileName
                && ((rule = "ATTR001"
                     && List.contains
                         symbol
                         [ "Microsoft.FSharp.Core.EntryPointAttribute"
                           "System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute" ])
                    || (rule = "MUT008" && List.contains symbol [ "main"; "args"; "a" ])
                    || (rule = "MODEL001"
                        && symbol = "b -> Microsoft.Testing.Platform.Builder.ITestApplicationBuilder")))
            || (normalized.EndsWith(
                    "/microsoft.net.test.sdk/17.14.1/build/net8.0/Microsoft.NET.Test.Sdk.Program.fs",
                    StringComparison.OrdinalIgnoreCase
                )
                && ((rule = "ATTR001" && symbol = "Microsoft.FSharp.Core.EntryPointAttribute")
                    || (rule = "ATTR004"
                        && symbol = "Microsoft.VisualStudio.TestPlatform.TestSDKAutoGeneratedCode")
                    || (rule = "MUT008" && symbol = "main")))

        let context =
            { Project = project
              Profile = profile
              Permit =
                fun rule r symbol ->
                    if Set.contains r.FileName generated && generatedPermission rule r symbol then
                        true
                    else
                        allowed "rule" (rule + " | " + symbol) r
              Member = memberAllowed
              Type = typeAllowed
              Data = dataAllowed
              Domain = fun entity -> List.contains (SafetyPolicy.entityName entity) document.Domains
              Constructor =
                fun memberInfo ->
                    let location = memberInfo.DeclarationLocation
                    let identity = constructorIdentity memberInfo

                    observed.Add
                        { File = relative location.FileName
                          Owner = memberInfo.FullName
                          Kind = "constructor"
                          Identity = identity }

                    Set.contains identity constructors
              Attribute =
                fun attribute entity ->
                    match attributeIdentity attribute entity with
                    | None -> false
                    | Some identity ->
                        allowedIn
                            (if attribute.Target |> Option.exists (fun target -> target.idText = "assembly") then
                                 "<assembly>"
                             else
                                 owner attribute.Range)
                            "attribute"
                            identity
                            attribute.Range }

        let describe (diagnostic: SafetyDiagnostic) (r: range) contract =
            let scope = owner r

            let kind, identity =
                match contract with
                | Some(TypeContract entity) -> "type", typeIdentity entity
                | Some(DataContract entity) -> "data", typeIdentity entity
                | Some(MemberContract memberInfo) ->
                    "member", memberInfo.Assembly.QualifiedName + " | " + memberInfo.XmlDocSig
                | Some(ConstructorContract memberInfo) -> "constructor", constructorIdentity memberInfo
                | Some(AttributeContract(attribute, entity)) ->
                    match attributeIdentity attribute entity with
                    | Some identity -> "attribute", identity
                    | None -> "source", diagnostic.Symbol
                | None -> "rule", diagnostic.Rule + " | " + diagnostic.Symbol

            { Diagnostic = diagnostic
              Owner =
                match contract with
                | Some(AttributeContract(attribute, _)) when
                    attribute.Target |> Option.exists (fun target -> target.idText = "assembly")
                    ->
                    "<assembly>"
                | _ -> scope
              Kind = kind
              Identity = identity
              Resolution = reviewResolution diagnostic.Profile diagnostic.Rule kind }

        context, (fun () -> observed |> Seq.distinct |> Seq.sort |> Seq.toList), describe
