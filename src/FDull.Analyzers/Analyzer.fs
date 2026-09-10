namespace FDull.Analyzers

open FSharp.Analyzers.SDK
open FSharp.Compiler.Text
open FDull

module Analyzer =
    let private source (text: ISourceText) =
        [ 0 .. text.GetLineCount() - 1 ]
        |> List.map text.GetLineString
        |> String.concat "\n"

    let private message (diagnostic: SafetyDiagnostic) =
        { Type = "FDull " + diagnostic.Rule
          Message = diagnostic.Message + " " + diagnostic.Alternative
          Code = diagnostic.Rule
          Severity = Severity.Error
          Range =
            Range.mkRange
                diagnostic.File
                (Position.mkPos diagnostic.StartLine (diagnostic.StartColumn - 1))
                (Position.mkPos diagnostic.EndLine (diagnostic.EndColumn - 1))
          Fixes = [] }

    [<EditorAnalyzer("FDull", "FDull source safety rules")>]
    let editorAnalyzer: Analyzer<EditorContext> =
        fun context ->
            async {
                return
                    EditorAnalysis.check context.FileName (source context.SourceText) context.ParseFileResults.ParseTree
                    |> List.map message
            }
