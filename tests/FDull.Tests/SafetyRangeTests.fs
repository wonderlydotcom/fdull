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
            fun (raw: (bool * int * int) list) (queries: (bool * int * int) list) ->
                let values =
                    raw |> List.mapi (fun i (file, first, last) -> region file first last, i)

                let index = SafetyRanges.create ignore fst values

                let matches (file, first, last) =
                    let query = region file first last

                    let inside =
                        values
                        |> List.filter (fun (r, _) ->
                            r.File = query.File && r.Start >= query.Start && r.End <= query.End)

                    let enclosing =
                        values
                        |> List.filter (fun (r, _) ->
                            r.File = query.File && r.Start <= query.Start && r.End >= query.End)

                    index.Inside query = inside && index.Enclosing query = enclosing

                // Memoized answers must stay keyed by region and direction when
                // queries repeat in a different order.
                List.forall matches queries && List.forall matches (List.rev queries)
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

    [<Fact>]
    let ``repeated range queries return the first answer for one step each`` () =
        let visits = ref 0
        let values = [ 0..4095 ] |> List.map (fun i -> region true i (8192 - i), i)

        let index =
            SafetyRanges.create (fun _ -> visits.Value <- visits.Value + 1) fst values

        let query = region true 4096 4096
        let enclosing = index.Enclosing query
        Assert.Equal(4096, enclosing.Length)
        visits.Value <- 0
        let repeated = [ 1..10000 ] |> List.map (fun _ -> index.Enclosing query)
        Assert.Equal(10000, visits.Value)
        Assert.Equal<(SafetyRegion * int) list>(enclosing, List.last repeated)
