namespace FDull.Tests

open System.Text.Json
open Xunit
open FDull.Transport

type PrivateDomain = private { Value: string }
type TransportEnvelope = { Values: PrivateDomain list }

type RequiredTransport =
    { Name: string
      Optional: string option }

type NestedTransport = { Value: RequiredTransport }

module TransportTests =
    [<Fact>]
    let ``generic materializers reject direct and nested domain targets`` () =
        Assert.False(Materializer.validateType typeof<PrivateDomain>)
        Assert.False(Materializer.validateType typeof<TransportEnvelope>)
        Assert.False(Materializer.validateType typeof<int -> int>)
        let target = typeof<PrivateDomain>

        Assert.Throws<JsonException>(fun () ->
            Materializer.decodeType target Codec.options "{\"Value\":\"forged\"}" |> ignore)
        |> ignore

    [<Fact>]
    let ``required DTO references are normalized before materialization`` () =
        for json in
            [ "null"
              "{}"
              "{\"Value\":null}"
              "{\"Value\":{\"Name\":null}}"
              "{\"Value\":{\"Name\":\"valid\",\"Unrecognized\":1}}" ] do
            Assert.Throws<JsonException>(fun () -> Codec.decode<NestedTransport> json |> ignore)
            |> ignore

        let result =
            Codec.decode<NestedTransport> "{\"Value\":{\"Name\":\"valid\",\"Optional\":null}}"

        Assert.Equal("valid", result.Value.Name)
        Assert.Equal(None, result.Value.Optional)
