namespace FDull.Tests

module Fixtures =
    let requireSome context =
        function
        | Some value -> value
        | None -> failwith context
