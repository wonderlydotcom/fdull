namespace FDull.Tests

open System
open System.Collections.Generic
open System.IO
open System.Reflection
open System.Text.RegularExpressions
open Xunit
open FDull

module SafetyCoverageTests =
    [<Theory>]
    [<InlineData("--warn:0")>]
    [<InlineData("--warnaserror-")>]
    [<InlineData("--warnaserror-:25")>]
    [<InlineData("--warnaserror-:3180,25")>]
    [<InlineData("--nowarn:3180")>]
    [<InlineData("--nowarn:25")>]
    [<InlineData("--checknulls-")>]
    [<InlineData("--checked-")>]
    [<InlineData("--langversion:preview")>]
    [<InlineData("@hidden.rsp")>]
    [<InlineData("--define:UNREVIEWED")>]
    [<InlineData("--sourcelink:")>]
    [<InlineData("--sourcelink")>]
    let ``late compiler flags cannot weaken exported policy`` flag =
        let baseline =
            // A GitHub checkout adds SDK source-debugging metadata to the invocation.
            WorkspaceCompilerPolicy.required
            @ [ "--warnaserror"
                "--target:library"
                "--sourcelink:obj/Release/net10.0/FDull.sourcelink.json"
                "--pathmap:/checkout/=/_/"
                "--embed:obj/Release/net10.0/FDull.AssemblyInfo.fs" ]

        Assert.Equal(Ok(), WorkspaceCompilerPolicy.validate baseline)
        Assert.True(WorkspaceCompilerPolicy.validate (baseline @ [ flag ]) |> Result.isError)

    [<Fact>]
    let ``every required exported compiler setting is mandatory`` () =
        let baseline = WorkspaceCompilerPolicy.required @ [ "--warnaserror" ]

        for required in baseline do
            Assert.True(
                WorkspaceCompilerPolicy.validate (baseline |> List.filter ((<>) required))
                |> Result.isError
            )

        Assert.Equal(Ok(), WorkspaceCompilerPolicy.validate (baseline @ [ "--doc:obj/Release/net10.0/App.xml" ]))
        Assert.Equal(Ok(), WorkspaceCompilerPolicy.validate (baseline @ [ "--define:NULLABLE" ]))
        Assert.Equal(Ok(), WorkspaceCompilerPolicy.validate (baseline @ [ "--crossoptimize-" ]))

    [<Fact>]
    let ``only canonical task compatibility downgrade is accepted`` () =
        let baseline = WorkspaceCompilerPolicy.required @ [ "--warnaserror" ]

        Assert.Equal(Ok(), WorkspaceCompilerPolicy.validate (baseline @ [ "--warnaserror-:3180" ]))

        for downgrade in
            [ "--warnaserror-:3186"
              "--warnaserror-:3180,3186"
              "--warnaserror-:3186,3180"
              "--warnaserror-:3180" ] do
            Assert.True(
                WorkspaceCompilerPolicy.validate (baseline @ [ downgrade; downgrade ])
                |> Result.isError
            )

        Assert.True(
            WorkspaceCompilerPolicy.validate (baseline @ [ "--warnaserror-:3186" ])
            |> Result.isError
        )

        let message =
            "The mutable local 'awaiter' is implicitly allocated as a reference cell because it has been captured by a closure. This warning is for informational purposes only to indicate where implicit allocations are performed."

        let matches number category file startLine startColumn endLine endColumn text =
            WorkspaceCompilerPolicy.compatibleTaskAwaiterWarning
                number
                category
                file
                startLine
                startColumn
                endLine
                endColumn
                text

        Assert.True(matches 3180 "optimize" "unknown" 1 0 1 0 message)
        Assert.False(matches 3186 "optimize" "unknown" 1 0 1 0 message)
        Assert.False(matches 3180 "typecheck" "unknown" 1 0 1 0 message)
        Assert.False(matches 3180 "optimize" "/workspace/Lib.fs" 3 16 3 23 message)
        Assert.False(matches 3180 "optimize" "unknown" 1 1 1 1 message)
        Assert.False(matches 3180 "optimize" "unknown" 1 0 1 0 (message + " changed"))

    let private platform =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))

    let private testAssembly = Assembly.GetExecutingAssembly()

    [<Fact>]
    let ``coverage contains every canonical specification ID exactly once`` () =
        let specification =
            File.ReadAllText(Path.Combine(platform, "docs/fsharp-safety-standard.md"))

        let start =
            specification.IndexOf("## 3. Canonical diagnostic catalog", StringComparison.Ordinal)

        let finish =
            specification.IndexOf("## 4. Mutation and immutable data", start, StringComparison.Ordinal)

        let catalog = specification.Substring(start, finish - start)

        let expected =
            Regex.Matches(catalog, @"\| `([A-Z]+[0-9]{3})` \|")
            |> Seq.map (fun item -> item.Groups[1].Value)
            |> Set.ofSeq

        let actual = SafetyRules.all |> List.map _.Rule |> Set.ofList
        Assert.Equal<Set<string>>(expected, actual)
        Assert.Equal(expected.Count, SafetyRules.all.Length)
        SafetyRules.validate ()

    [<Fact>]
    let ``every claimed compiling negative has an executable fixture`` () =
        let testType =
            testAssembly.GetType("FDull.Tests.SafetyTests", true)
            |> FDull.Transport.External.required "test.fixture-type"

        let method =
            testType.GetMethod("valid FSharp violations have canonical diagnostics and physical locations")
            |> FDull.Transport.External.required "test.fixture-method"

        let exercised =
            method.GetCustomAttributesData()
            |> Seq.filter (fun attribute -> attribute.AttributeType = typeof<InlineDataAttribute>)
            |> Seq.map (fun attribute ->
                let values =
                    (attribute.ConstructorArguments[0].Value
                     |> FDull.Transport.External.required "test.fixture-arguments")
                    :?> IEnumerable<CustomAttributeTypedArgument>

                let first =
                    values
                    |> Seq.tryHead
                    |> Fixtures.requireSome "Empty diagnostic fixture arguments."

                (first.Value |> FDull.Transport.External.required "test.fixture-rule") :?> string)
            |> Set.ofSeq

        let claimed =
            SafetyRules.all
            |> List.filter _.CompilingNegativeFixture
            |> List.map _.Rule
            |> Set.ofList

        Assert.Equal<Set<string>>(exercised, claimed)

    [<Fact>]
    let ``coverage links resolve to implementations and executable tests`` () =
        for rule in SafetyRules.all do
            for path in rule.Implementation do
                Assert.True(File.Exists(Path.Combine(platform, path)), rule.Rule + ": " + path)

            for evidence in rule.OtherEvidence do
                let separator = evidence.IndexOf('.')

                let testType =
                    testAssembly.GetType("FDull.Tests." + evidence.Substring(0, separator), true)
                    |> FDull.Transport.External.required "test.evidence-type"

                let method =
                    testType.GetMethod(evidence.Substring(separator + 1))
                    |> FDull.Transport.External.required "test.evidence-method"

                Assert.NotNull method

                Assert.True(
                    method.IsDefined(typeof<FactAttribute>)
                    || method.IsDefined(typeof<TheoryAttribute>),
                    evidence
                )

        Assert.False((SafetyRules.inventory SafetyPolicy.version).FullStandardComplete)

    [<Theory>]
    [<InlineData("UNKNOWN001")>]
    [<InlineData("BUILD007")>]
    [<InlineData("MUT000")>]
    [<InlineData("FSoops")>]
    [<InlineData("")>]
    let ``invented rule IDs are not recognized diagnostics`` id =
        Assert.False(SafetyRules.knownDiagnostic id)
