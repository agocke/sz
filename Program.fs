
module Sz

open System;
open System.Collections.Generic;
open System.Collections.Immutable;
open System.IO;
open System.Linq;
open System.Reflection;
open System.Reflection.Metadata;
open System.Reflection.PortableExecutable;
open System.Resources;
open System.Text;
open System.Text.Json;
open System.Text.Json.Serialization;
open Mono.Options;
open System.Diagnostics

[<JsonConverter(typeof<MemberWithSizeConverter>)>]
type MemberWithSize =
    | NonTypeMemberWithSize of NonTypeMemberWithSize
    | TypeWithSize of TypeWithSize
and NonTypeMemberWithSize =
    {
        [<JsonPropertyName("name")>]
        Name : string ;
        [<JsonPropertyName("value")>]
        Size : int
    }
and TypeWithSize =
    {
        [<JsonPropertyName("name")>]
        Name : string ;
        [<JsonPropertyName("children")>]
        Members : ImmutableArray<MemberWithSize>
    }
and NamespaceWithSize =
    {
        [<JsonPropertyName("name")>]
        Name : string ;
        [<JsonPropertyName("children")>]
        Members : ImmutableArray<MemberWithSize>
    }
and AssemblyWithSize =
    {
        [<JsonPropertyName("name")>]
        Name : string ;
        [<JsonPropertyName("children")>]
        Members : ImmutableArray<NamespaceWithSize>
    }
and MemberWithSizeConverter() =
    inherit JsonConverter<MemberWithSize>()

    override _.Read(reader : byref<Utf8JsonReader>,
        typeToConvert : Type,
        options : JsonSerializerOptions) : MemberWithSize = failwith "Cannot deserialize"

    override _.Write(writer : Utf8JsonWriter,
        value : MemberWithSize,
        options : JsonSerializerOptions) =
        match value with
        | NonTypeMemberWithSize m -> JsonSerializer.Serialize(writer, m, options)
        | TypeWithSize t -> JsonSerializer.Serialize(writer, t, options)

let getFieldDefSize (peReader : PEReader) (fieldDef : FieldDefinition) =
    let mdReader = peReader.GetMetadataReader()
    let name = mdReader.GetString(fieldDef.Name)
    let size = name.Length + mdReader.GetBlobReader(fieldDef.Signature).Length
    { NonTypeMemberWithSize.Name = name ; Size = size }

let getPropertyDefSize (peReader : PEReader) (propDef : PropertyDefinition) =
    let mdReader = peReader.GetMetadataReader()
    let name = mdReader.GetString(propDef.Name)
    let size = name.Length + mdReader.GetBlobReader(propDef.Signature).Length
    { NonTypeMemberWithSize.Name = name ; Size = size }

let getMethodDefSize (peReader : PEReader) (methodDef : MethodDefinition) =
    let mdReader = peReader.GetMetadataReader()
    let name = mdReader.GetString(methodDef.Name)
    // Name + signature
    let mutable size = name.Length + mdReader.GetBlobReader(methodDef.Signature).Length
    // Parameters
    for paramHandle in methodDef.GetParameters() do
        let param = mdReader.GetParameter(paramHandle)
        size <- size + mdReader.GetString(param.Name).Length
    // IL
    let rva = methodDef.RelativeVirtualAddress;
    if rva > 0 then
        size <- size + peReader.GetMethodBody(rva).Size;
    { NonTypeMemberWithSize.Name = name ; Size = size }

let rec getTypeSize (peReader : PEReader) (typeDef : TypeDefinition) =
    let mdReader = peReader.GetMetadataReader()
    let name = mdReader.GetString(typeDef.Name)
    let members = seq {
        // Fields
        for fieldDefHandle in typeDef.GetFields() do
            let fieldDef = mdReader.GetFieldDefinition(fieldDefHandle)
            NonTypeMemberWithSize <| getFieldDefSize peReader fieldDef

        // Methods
        for methodDefHandle in typeDef.GetMethods() do
            let methodDef = mdReader.GetMethodDefinition(methodDefHandle)
            NonTypeMemberWithSize <| getMethodDefSize peReader methodDef
        
        // Properties
        for propHandle in typeDef.GetProperties() do
            let prop = mdReader.GetPropertyDefinition(propHandle)
            NonTypeMemberWithSize <| getPropertyDefSize peReader prop

        // Types
        for typeHandle in typeDef.GetNestedTypes() do
            let nested = mdReader.GetTypeDefinition(typeHandle)
            TypeWithSize <| getTypeSize peReader nested
    }
    { TypeWithSize.Name = name ; Members = members.ToImmutableArray() }

let getNamespaceSizes (peReader : PEReader) =
    let mdReader = peReader.GetMetadataReader()
    let items : seq<string * MemberWithSize> = seq {
        for typeDefHandle in mdReader.TypeDefinitions do
            let typeDef = mdReader.GetTypeDefinition(typeDefHandle)
            if not typeDef.IsNested then
                let ns = mdReader.GetString(typeDef.Namespace)
                let nsKey = if ns.Length = 0 then "<global>" else ns
                yield (nsKey, getTypeSize peReader typeDef |> TypeWithSize)
    }
    items
    |> Seq.groupBy fst
    |> Seq.map (fun (k,v) -> { NamespaceWithSize.Name = k 
        ; Members = (Seq.map snd v).ToImmutableArray() })
    |> ImmutableArray.CreateRange

let getAssemblySize (path : string) =
    let asmBytes = ImmutableArray.Create<byte>(File.ReadAllBytes(path))
    use peReader = new PEReader(asmBytes)
    let mdReader = peReader.GetMetadataReader()
    let asmName = mdReader.GetString(mdReader.GetAssemblyDefinition().Name)
    let asmSizes = { Name = asmName; Members = getNamespaceSizes peReader }
    use templateStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("sz.template.html")
    use streamReader = new StreamReader(templateStream, Encoding.UTF8)
    let template = streamReader.ReadToEnd()
    // Write to JsonFormat
    let json = JsonSerializer.Serialize(asmSizes)
    let html = template.Replace("{{{data}}}", json)
    File.WriteAllText("output.html", html)

[<EntryPoint>]
let main args =
// these variables will be set when the command line is parsed
    let mutable showHelp = false;
    let options =
        OptionSet()
            .Add("h|help", "show this message and exit", fun h -> showHelp <- (not (isNull h)))
    try
        let paths = options.Parse(args)
        if showHelp then
            printfn "Usage: sz [-j|--json] path ..."
        else
            getAssemblySize paths.[0]
        0
    with 
        | :? OptionException as e ->
            printfn "sz: "
            printfn "%s" e.Message
            printfn "Try `sz --help` for more information"
            1