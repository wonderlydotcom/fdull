namespace FDull

open System
open System.IO
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open System.Reflection.PortableExecutable

/// Stable review fingerprint for F# reference assemblies. Analysis inputs retain raw SHA-256 bindings.
module DataContractPolicy =
    let assemblyDigest file =
        if FileInfo(file).Length > 64L * 1024L * 1024L then
            invalidOp "BUILD003: A data contract assembly exceeds the supported 64 MiB size."

        let bytes = File.ReadAllBytes file
        use stream = new MemoryStream(bytes, false)
        use image = new PEReader(stream)
        let headers = image.PEHeaders

        let section =
            headers.SectionHeaders |> Seq.tryFind (fun section -> section.Name = ".mvid")

        match section with
        | None -> SafetyPolicy.digest bytes
        | Some section ->
            let metadata = image.GetMetadataReader()
            let moduleDefinition = metadata.GetModuleDefinition()

            let referenceAssembly =
                moduleDefinition.GetCustomAttributes()
                |> Seq.exists (fun handle ->
                    let attribute = metadata.GetCustomAttribute handle
                    let constructor = attribute.Constructor

                    if constructor.Kind = HandleKind.MethodDefinition then
                        let methodInfo =
                            metadata.GetMethodDefinition(
                                MetadataTokens.MethodDefinitionHandle(MetadataTokens.GetRowNumber constructor)
                            )

                        let target = metadata.GetTypeDefinition(methodInfo.GetDeclaringType())

                        metadata.GetString(target.Namespace) = "System.Runtime.CompilerServices"
                        && metadata.GetString(target.Name) = "ReferenceAssemblyAttribute"
                    elif constructor.Kind = HandleKind.MemberReference then
                        let memberInfo =
                            metadata.GetMemberReference(
                                MetadataTokens.MemberReferenceHandle(MetadataTokens.GetRowNumber constructor)
                            )

                        let parent = memberInfo.Parent

                        if parent.Kind <> HandleKind.TypeReference then
                            false
                        else
                            let target =
                                metadata.GetTypeReference(
                                    MetadataTokens.TypeReferenceHandle(MetadataTokens.GetRowNumber parent)
                                )

                            metadata.GetString(target.Namespace) = "System.Runtime.CompilerServices"
                            && metadata.GetString(target.Name) = "ReferenceAssemblyAttribute"
                    else
                        false)

            let mvid = moduleDefinition.Mvid
            let guid = metadata.GetGuid(mvid).ToByteArray()

            let guidOffset =
                headers.MetadataStartOffset
                + metadata.GetHeapMetadataOffset(HeapIndex.Guid)
                + (MetadataTokens.GetHeapOffset(mvid) - 1) * 16

            if
                not referenceAssembly
                || mvid.IsNil
                || section.VirtualSize <> 16
                || section.PointerToRawData < 0
                || section.PointerToRawData > bytes.Length - 16
                || guidOffset < 0
                || guidOffset > bytes.Length - 16
                || guid
                   |> Array.indexed
                   |> Array.exists (fun (index, value) -> bytes[section.PointerToRawData + index] <> value)
            then
                invalidOp "BUILD005: Unsupported F# reference assembly module-ID layout."

            Array.Clear(bytes, headers.CoffHeaderStartOffset + 4, 4)
            Array.Clear(bytes, section.PointerToRawData, 16)
            Array.Clear(bytes, guidOffset, 16)
            SafetyPolicy.digest bytes
