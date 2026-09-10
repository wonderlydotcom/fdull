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

type WorkspacePolicyDocument =
    { Version: int
      Projects: WorkspaceProject list
      Sources: WorkspaceSource list
      Inputs: WorkspacePin list
      External: WorkspaceExternal list option
      Capabilities: WorkspaceCapability list
      Domains: string list
      Constructors: string list }

type WorkspaceUse =
    { File: string
      Owner: string
      Kind: string
      Identity: string }

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

    let private decodeDocument (text: string) =
        let json = JsonNode.Parse text |> External.required "workspace.policy"
        let fields = json.AsObject()

        if not (fields.ContainsKey "External") then
            fields.Add("External", JsonArray())

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

    let read root =
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

            if SafetyPolicy.fileDigest (Path.Combine(root, source.File)) <> source.Digest then
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
                && List.contains effective [ "PURE"; "PURE_HELPER"; "CONTRACTS" ]
                && not (
                    effective = "PURE_HELPER"
                    && entry.Identity.StartsWith("STYLE003 | ", StringComparison.Ordinal)
                )
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

    let private region (r: range) =
        { File = r.FileName
          Start = r.StartLine, r.StartColumn
          End = r.EndLine, r.EndColumn }

    let context
        root
        project
        generated
        (budget: SafetyBudget)
        (document: WorkspacePolicyDocument)
        (checkedProject: FSharpCheckProjectResults)
        (syntax: Map<string, SourceSyntax>)
        =
        let owners = ResizeArray<range * string>()
        let observed = ResizeArray<WorkspaceUse>()

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

        let memberAllowed (r: range) (memberInfo: FSharpMemberOrFunctionOrValue) =
            if
                Set.contains r.FileName generated
                && memberInfo.Assembly.QualifiedName = "System.Runtime, Version=10.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a"
                && memberInfo.XmlDocSig = "P:System.Runtime.Versioning.TargetFrameworkAttribute.FrameworkDisplayName"
            then
                true
            else
                allowed "member" (memberInfo.Assembly.QualifiedName + " | " + memberInfo.XmlDocSig) r

        let typeAllowed r (entity: FSharpEntity) =
            let identity =
                if String.IsNullOrWhiteSpace entity.Assembly.QualifiedName then
                    let location = entity.DeclarationLocation
                    "source:" + relative location.FileName
                else
                    entity.Assembly.QualifiedName

            allowed "type" (identity + " | " + SafetyPolicy.entityName entity) r

        let generatedAttribute symbol =
            List.contains
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
                  "System.Runtime.Versioning.TargetFrameworkAttribute" ]

        let context =
            { Project = project
              Profile = profile
              Permit =
                fun rule r symbol ->
                    if
                        Set.contains r.FileName generated
                        && rule = "ATTR001"
                        && generatedAttribute symbol
                    then
                        true
                    else
                        allowed "rule" (rule + " | " + symbol) r
              Member = memberAllowed
              Type = typeAllowed
              Domain = fun entity -> List.contains (SafetyPolicy.entityName entity) document.Domains
              Constructor =
                fun memberInfo ->
                    let location = memberInfo.DeclarationLocation

                    let identity =
                        relative location.FileName
                        + " | "
                        + memberInfo.Assembly.QualifiedName
                        + " | "
                        + memberInfo.XmlDocSig
                        + " -> "
                        + memberInfo.FullType.Format(FSharpDisplayContext.Empty)

                    observed.Add
                        { File = relative location.FileName
                          Owner = memberInfo.FullName
                          Kind = "constructor"
                          Identity = identity }

                    Set.contains identity constructors
              Attribute =
                fun attribute entity ->
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

                    match argument attribute.ArgExpr with
                    | None -> false
                    | Some arguments ->
                        let identity =
                            entity.Assembly.QualifiedName
                            + " | "
                            + SafetyPolicy.entityName entity
                            + " | "
                            + (attribute.Target |> Option.map _.idText |> Option.defaultValue "")
                            + " | "
                            + arguments

                        allowedIn
                            (if attribute.Target |> Option.exists (fun target -> target.idText = "assembly") then
                                 "<assembly>"
                             else
                                 owner attribute.Range)
                            "attribute"
                            identity
                            attribute.Range }

        context, (fun () -> observed |> Seq.distinct |> Seq.sort |> Seq.toList)
