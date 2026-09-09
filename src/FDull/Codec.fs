namespace FDull.Transport

open System.Text.Json
open System.Text.Json.Serialization

/// Strict JSON protocol adapter. Unknown fields and invalid DTO shapes fail.
module Codec =
    let options =
        let value =
            JsonSerializerOptions(MaxDepth = 64, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)

        value.Converters.Add(JsonFSharpConverter())
        value

    let encode<'a> (value: 'a) =
        JsonSerializer.Serialize(value, options)

    let decode<'a> (text: string) : 'a = Materializer.decode<'a> options text
