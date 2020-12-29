using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Mono.Options;

// these variables will be set when the command line is parsed
bool outputJson = false;
bool showHelp = false;
List<string> paths;
try
{
    var options = new OptionSet {
        { "j|json", "output json.", j => outputJson = (j != null) },
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

int GetFieldDefSize(FieldDefinition fieldDef)
{
    return mdReader.GetString(fieldDef.Name).Length
        + mdReader.GetBlobReader(fieldDef.Signature).Length;
}

int GetPropertyDefSize(PropertyDefinition propDef)
{
    return mdReader.GetString(propDef.Name).Length
        + mdReader.GetBlobReader(propDef.Signature).Length;
}

int GetMethodDefSize(MethodDefinition methodDef)
{
    // Name
    int size = mdReader.GetString(methodDef.Name).Length;
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

    return size;
}

Console.WriteLine("sizes: ");
int totalSize = 0;
foreach (var typeDefHandle in mdReader.TypeDefinitions)
{
    var typeDef = mdReader.GetTypeDefinition(typeDefHandle);

    // Namespace name size + type name size
    var ns = mdReader.GetString(typeDef.Namespace);
    var name = mdReader.GetString(typeDef.Name);
    int size = ns.Length + name.Length;

    // Fields
    foreach (var fieldDefHandle in typeDef.GetFields())
    {
        var fieldDef = mdReader.GetFieldDefinition(fieldDefHandle);
        size += GetFieldDefSize(fieldDef);
    }

    // Methods
    foreach (var methodDefHandle in typeDef.GetMethods())
    {
        var methodDef = mdReader.GetMethodDefinition(methodDefHandle);
        size += GetMethodDefSize(methodDef);
    }

    // Properties
    foreach (var propHandle in typeDef.GetProperties())
    {
        var prop = mdReader.GetPropertyDefinition(propHandle);
        size += GetPropertyDefSize(prop);
    }

    Console.WriteLine($"{ns}.{name}\t{size}");
    totalSize += size;
}
Console.WriteLine();
Console.WriteLine("Total size: " + totalSize);

return 0;