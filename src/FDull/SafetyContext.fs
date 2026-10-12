namespace FDull

open FSharp.Compiler.Symbols
open FSharp.Compiler.Text
open FSharp.Compiler.Syntax

/// Resolved evidence attached only to an emitted finding, never to a permission lookup.
[<NoEquality; NoComparison>]
type internal SafetyReviewContract =
    | TypeContract of FSharpEntity
    | MemberContract of FSharpMemberOrFunctionOrValue
    | AttributeContract of SynAttribute * FSharpEntity
    | ConstructorContract of FSharpMemberOrFunctionOrValue
    | DataContract of FSharpEntity
    | CaptureContract of sourcePath: string list * payloadPath: string list

/// Platform policy supplies this context; application source cannot select it.
[<NoEquality; NoComparison>]
type internal SafetyContext =
    { Project: string
      Profile: range -> string
      Permit: string -> range -> string -> SafetyReviewContract option -> bool
      Capture: FSharpField -> string list option
      Member: range -> FSharpMemberOrFunctionOrValue -> bool
      Type: range -> FSharpEntity -> bool
      Data: range -> FSharpEntity -> (FSharpEntity -> unit) option
      Domain: FSharpEntity -> bool
      Constructor: FSharpMemberOrFunctionOrValue -> bool
      Attribute: SynAttribute -> FSharpEntity -> bool }

module internal SafetyContext =
    let application =
        { Project = "fdull.source"
          Profile = fun _ -> "PURE"
          Permit = fun _ _ _ _ -> false
          Capture = fun _ -> None
          Member = fun _ _ -> false
          Type = fun _ _ -> false
          Data = fun _ _ -> None
          Domain = fun _ -> false
          Constructor = fun _ -> false
          Attribute = fun _ _ -> false }
