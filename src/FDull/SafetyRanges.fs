namespace FDull

type SafetyRegion =
    { File: string
      Start: int * int
      End: int * int }

[<NoEquality; NoComparison>]
type SafetyRangeLookup<'a> =
    { Inside: SafetyRegion -> 'a list
      Enclosing: SafetyRegion -> 'a list }

/// Tooling interval index. Queries retain original input order and duplicate
/// entries, so indexing cannot change first-match resolution or omit tied ranges.
module SafetyRanges =
    type private Entry<'a> =
        { Region: SafetyRegion
          Ordinal: int
          Value: 'a }

    type private Tree<'a> =
        | Empty
        | Branch of
            minimumStart: (int * int) *
            maximumEnd: (int * int) *
            entry: Entry<'a> *
            left: Tree<'a> *
            right: Tree<'a>

    let create visit regionOf values =
        let entries =
            values
            |> Seq.mapi (fun ordinal value ->
                visit 0
                let region = regionOf value

                if region.End < region.Start then
                    invalidArg "values" "Invalid source range."

                { Region = region
                  Ordinal = ordinal
                  Value = value })
            |> Seq.toArray

        let maximumEnd fallback =
            function
            | Empty -> fallback
            | Branch(_, last, _, _, _) -> last

        let trees =
            entries
            |> Array.groupBy (fun entry -> entry.Region.File)
            |> Array.map (fun (file, entries) ->
                let ordered =
                    entries |> Array.sortBy (fun entry -> entry.Region.Start, entry.Ordinal)

                let rec build depth first after =
                    visit depth

                    if first = after then
                        Empty
                    else
                        let middle = first + (after - first) / 2
                        let entry = ordered[middle]
                        let left = build (depth + 1) first middle
                        let right = build (depth + 1) (middle + 1) after

                        let last =
                            max
                                entry.Region.End
                                (max (maximumEnd entry.Region.End left) (maximumEnd entry.Region.End right))

                        Branch(ordered[first].Region.Start, last, entry, left, right)

                file, build 0 0 ordered.Length)
            |> Map.ofArray

        let query enclosing region =
            let found = ResizeArray<Entry<'a>>()

            let rec walk depth tree =
                visit depth

                match tree with
                | Empty -> ()
                | Branch(first, last, entry, left, right) ->
                    let overlaps =
                        if enclosing then
                            first <= region.Start && last >= region.End
                        else
                            first <= region.End && last >= region.Start

                    if overlaps then
                        let selected =
                            if enclosing then
                                entry.Region.Start <= region.Start && entry.Region.End >= region.End
                            else
                                entry.Region.Start >= region.Start && entry.Region.End <= region.End

                        if selected then
                            found.Add entry

                        walk (depth + 1) left
                        walk (depth + 1) right

            if region.End < region.Start then
                invalidArg "region" "Invalid query range."

            Map.tryFind region.File trees |> Option.iter (walk 0)
            found |> Seq.sortBy _.Ordinal |> Seq.map _.Value |> Seq.toList

        { Inside = query false
          Enclosing = query true }
