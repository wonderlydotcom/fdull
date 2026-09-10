namespace FDull

open FSharp.Compiler.Syntax
open FSharp.Compiler.Text

/// Advisory source analysis for editor hosts. This never certifies a project or build.
module EditorAnalysis =
    let check file source (tree: ParsedInput) =
        let budget = SafetyBudget()
        let diagnostics = ResizeArray<SafetyDiagnostic>()

        let emit rule (location: range) symbol message alternative =
            if diagnostics.Count < SafetyLimits.diagnosticCount then
                diagnostics.Add
                    { Rule = rule
                      Severity = "error"
                      File = location.FileName
                      StartLine = max 1 location.StartLine
                      StartColumn = location.StartColumn + 1
                      EndLine = max 1 location.EndLine
                      EndColumn = location.EndColumn + 1
                      Project = "fdull.editor"
                      Profile = "PURE"
                      Symbol = symbol
                      Message = message
                      Alternative = alternative
                      Policy = SafetyPolicy.version }

        SafetySyntax.inspect budget file source tree emit |> ignore

        diagnostics
        |> Seq.distinct
        |> Seq.sortBy (fun item -> item.StartLine, item.StartColumn, item.Rule)
        |> Seq.toList
