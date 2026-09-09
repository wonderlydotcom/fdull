namespace FDull

open FSharp.Compiler.Symbols
open FSharp.Compiler.Text
open FSharp.Compiler.Syntax

/// Platform policy supplies this context; application source cannot select it.
[<NoEquality; NoComparison>]
type internal SafetyContext =
    { Project: string
      Profile: range -> string
      Permit: string -> range -> string -> bool
      Member: range -> FSharpMemberOrFunctionOrValue -> bool
      Type: range -> FSharpEntity -> bool
      Domain: FSharpEntity -> bool
      Constructor: FSharpMemberOrFunctionOrValue -> bool
      Attribute: SynAttribute -> FSharpEntity -> bool }

module internal SafetyContext =
    let application =
        { Project = "fdull.source"
          Profile = fun _ -> "PURE"
          Permit = fun _ _ _ -> false
          Member = fun _ _ -> false
          Type = fun _ _ -> false
          Domain = fun _ -> false
          Constructor = fun _ -> false
          Attribute = fun _ _ -> false }
