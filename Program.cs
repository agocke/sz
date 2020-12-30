using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Resources;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mono.Options;

// these variables will be set when the command line is parsed
bool outputHtml = false;
bool showHelp = false;
List<string> paths;
try
{
    var options = new OptionSet {
        { "html", "output html", h => outputHtml = (h != null) },
        { "h|help", "show this message and exit", h => showHelp = (h != null) },
    };
    paths = options.Parse(args);
}
catch (OptionException e)
{
    Console.Write("sz: ");
    Console.WriteLine (e.Message);
    Console.WriteLine ("Try `sz --help' for more information.");
    return 1;
}

if (showHelp)
{
    Console.WriteLine("Usage: sz [-j|--json] path ...");
    return 0;
}

var asmBytes = ImmutableArray.Create(File.ReadAllBytes(paths[0]));
using var peReader = new PEReader(asmBytes);
var mdReader = peReader.GetMetadataReader();

NonTypeMemberWithSize GetFieldDefSize(FieldDefinition fieldDef)
{
    var name = mdReader.GetString(fieldDef.Name);
    int size = name.Length
        + mdReader.GetBlobReader(fieldDef.Signature).Length;
    return new(name, size);
}

NonTypeMemberWithSize GetPropertyDefSize(PropertyDefinition propDef)
{
    var name = mdReader.GetString(propDef.Name);
    int size = name.Length
        + mdReader.GetBlobReader(propDef.Signature).Length;
    return new(name, size);
}

NonTypeMemberWithSize GetMethodDefSize(MethodDefinition methodDef)
{
    // Name
    var name = mdReader.GetString(methodDef.Name);
    int size = name.Length;
    // Signature
    size += mdReader.GetBlobReader(methodDef.Signature).Length; 
    // Parameters
    foreach (var paramHandle in methodDef.GetParameters())
    {
        var param = mdReader.GetParameter(paramHandle);
        size += mdReader.GetString(param.Name).Length;
    }
    // IL
    var rva = methodDef.RelativeVirtualAddress;
    if (rva > 0)
    {
        size += peReader.GetMethodBody(rva).Size;
    }

    return new(name, size);
}

TypeWithSize GetTypeSize(TypeDefinition typeDef)
{
    var name = mdReader.GetString(typeDef.Name);
    var membersBuilder = ImmutableArray.CreateBuilder<MemberWithSize>();

    // Fields
    foreach (var fieldDefHandle in typeDef.GetFields())
    {
        var fieldDef = mdReader.GetFieldDefinition(fieldDefHandle);
        var m = GetFieldDefSize(fieldDef);
        membersBuilder.Add(m);
    }

    // Methods
    foreach (var methodDefHandle in typeDef.GetMethods())
    {
        var methodDef = mdReader.GetMethodDefinition(methodDefHandle);
        var m = GetMethodDefSize(methodDef);
        membersBuilder.Add(m);
    }

    // Properties
    foreach (var propHandle in typeDef.GetProperties())
    {
        var prop = mdReader.GetPropertyDefinition(propHandle);
        var m = GetPropertyDefSize(prop);
        membersBuilder.Add(m);
    }

    // Types
    foreach (var typeHandle in typeDef.GetNestedTypes())
    {
        var nested = mdReader.GetTypeDefinition(typeHandle);
        var m = GetTypeSize(nested);
        membersBuilder.Add(m);
    }

    var members = membersBuilder.ToImmutable();
    return new TypeWithSize(name, members);
}

ImmutableArray<NamespaceWithSize> GetNamespaceSizes()
{
    // Set of namespaces, with member types, each with sizes
    var nsSizesBuilder = new Dictionary<string, List<TypeWithSize>>();
    foreach (var typeDefHandle in mdReader.TypeDefinitions)
    {
        var typeDef = mdReader.GetTypeDefinition(typeDefHandle);
        if (!typeDef.IsNested)
        {
            var ns = mdReader.GetString(typeDef.Namespace);
            var nsKey = ns.Length == 0 ? "<global>" : ns;
            if (!nsSizesBuilder.TryGetValue(nsKey, out var types))
            {
                types = new List<TypeWithSize>();
                nsSizesBuilder.Add(nsKey, types);
            }
            types.Add(GetTypeSize(typeDef));
        }
    }

    return nsSizesBuilder
        .Select(kvp => new NamespaceWithSize(kvp.Key, kvp.Value.ToImmutableArray()))
        .ToImmutableArray();
}

var asmName = mdReader.GetString(mdReader.GetAssemblyDefinition().Name);
var asmSizes = new AssemblyWithSizes($"<assembly: {asmName}>", GetNamespaceSizes());
if (!outputHtml)
{
    foreach (var (nsName, types) in asmSizes.Namespaces)
    {
        foreach (var t in types)
        {
            int size = 0;
            Console.WriteLine($"{nsName}.{t.Name}: {size}");
        }
    }

    Console.WriteLine();
}
else
{
    using var templateStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("sz.template.html");
    using var streamReader = new StreamReader(templateStream, Encoding.UTF8);
    var template = streamReader.ReadToEnd();
    // Write to JsonFormat
    var json = JsonSerializer.Serialize(asmSizes);
    var html = template.Replace("{{{data}}}", json);
    File.WriteAllText("output.html", html);
}

return 0;

abstract record Entity([property: JsonPropertyName("name")]string Name);
[JsonConverter(typeof(MemberWithSizeConverter))]
abstract record MemberWithSize(string Name) : Entity(Name);
record NonTypeMemberWithSize(string Name, 
    [property: JsonPropertyName("value")]int Size) : MemberWithSize(Name);
record TypeWithSize(
    string Name,
    [property: JsonPropertyName("children")]ImmutableArray<MemberWithSize> Members) : MemberWithSize(Name);
record NamespaceWithSize(
    string Name,
    [property: JsonPropertyName("children")]ImmutableArray<TypeWithSize> Types) : Entity(Name);
record AssemblyWithSizes(
    string Name,
    [property: JsonPropertyName("children")]ImmutableArray<NamespaceWithSize> Namespaces) : Entity(Name);

class MemberWithSizeConverter : JsonConverter<MemberWithSize>
{
    public override bool CanConvert(Type typeToConvert)
        => typeToConvert.IsAssignableTo(typeof(MemberWithSize));

    public override MemberWithSize Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        throw new InvalidOperationException("Cannot deserialize");
    }

    public override void Write(
        Utf8JsonWriter writer,
        MemberWithSize value,
        JsonSerializerOptions options)
    {
        switch (value) {
            case NonTypeMemberWithSize m:
                JsonSerializer.Serialize(writer, m, options);
                break;
            case TypeWithSize t:
                JsonSerializer.Serialize(writer, t, options);
                break;
            default:
                throw new InvalidOperationException("unreachable");
        };
    }
}