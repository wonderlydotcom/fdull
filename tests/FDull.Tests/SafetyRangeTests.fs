namespace FDull.Tests

open FsCheck
open Xunit
open FDull

module SafetyRangeTests =
    let private region file first last =
        { File = if file then "App.fs" else "Other.fs"
          Start = min first last, 0
          End = max first last, 0 }

    [<Fact>]
    let ``indexed range selection equals a full scan including order and duplicates`` () =
        Check.One(
            { Config.QuickThrowOnFailure with
                MaxTest = 500 },
            fun (raw: (bool * int * int) list) (file: bool) (first: int) (last: int) ->
                let values =
                    raw |> List.mapi (fun i (file, first, last) -> region file first last, i)

                let query = region file first last
                let index = SafetyRanges.create ignore fst values

                let inside =
                    values
                    |> List.filter (fun (r, _) -> r.File = query.File && r.Start >= query.Start && r.End <= query.End)

                let enclosing =
                    values
                    |> List.filter (fun (r, _) -> r.File = query.File && r.Start <= query.Start && r.End >= query.End)

                index.Inside query = inside && index.Enclosing query = enclosing
        )

    [<Fact>]
    let ``a local range query does not scan thousands of unrelated declarations`` () =
        let visits = ref 0
        let values = [ 0..8191 ] |> List.map (fun i -> region true (2 * i) (2 * i + 1), i)

        let index =
            SafetyRanges.create (fun _ -> visits.Value <- visits.Value + 1) fst values

        let query = region true 8192 8193
        visits.Value <- 0
        Assert.Equal<(SafetyRegion * int) list>([ query, 4096 ], index.Inside query)
        Assert.True(visits.Value < 100, $"A local query visited %d{visits.Value} tree nodes.")
