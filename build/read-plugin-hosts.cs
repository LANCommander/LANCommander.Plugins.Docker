// Prints the hosts a LANCommander plugin assembly declares in its [LANCommanderPlugin] attribute, as
// PluginHost flags (1 server, 2 launcher), reading metadata so the assembly is never loaded.
// Run by PluginPackage.targets: dotnet run read-plugin-hosts.cs -- <assembly>

using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

const string AttributeNamespace = "LANCommander.SDK.Plugins";
const string AttributeName = "LANCommanderPluginAttribute";

// PluginHost.Server | PluginHost.Launcher, the attribute's default
const int DefaultHosts = 3;

using var stream = File.OpenRead(args[0]);
using var pe = new PEReader(stream);

var reader = pe.GetMetadataReader();

foreach (var handle in reader.GetAssemblyDefinition().GetCustomAttributes())
{
    var attribute = reader.GetCustomAttribute(handle);

    if (attribute.Constructor.Kind != HandleKind.MemberReference)
        continue;

    var parent = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent;

    if (parent.Kind != HandleKind.TypeReference)
        continue;

    var type = reader.GetTypeReference((TypeReferenceHandle)parent);

    if (!reader.StringComparer.Equals(type.Name, AttributeName) || !reader.StringComparer.Equals(type.Namespace, AttributeNamespace))
        continue;

    var hosts = DefaultHosts;

    foreach (var argument in attribute.DecodeValue(new NameProvider()).NamedArguments)
    {
        if (argument.Name == "Hosts" && argument.Value is int value)
            hosts = value;
    }

    Console.WriteLine(hosts);
    return 0;
}

Console.Error.WriteLine($"{Path.GetFileName(args[0])} has no [LANCommanderPlugin] attribute.");
return 1;

/// <summary>Decodes attribute arguments with types as full names; the attribute's only enum, PluginHost, is an int.</summary>
sealed class NameProvider : ICustomAttributeTypeProvider<string>
{
    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
    public string GetSystemType() => "System.Type";
    public string GetSZArrayType(string elementType) => elementType + "[]";
    public string GetTypeFromSerializedName(string name) => name;
    public PrimitiveTypeCode GetUnderlyingEnumType(string type) => PrimitiveTypeCode.Int32;
    public bool IsSystemType(string type) => type == "System.Type";

    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        var definition = reader.GetTypeDefinition(handle);
        return $"{reader.GetString(definition.Namespace)}.{reader.GetString(definition.Name)}";
    }

    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        var reference = reader.GetTypeReference(handle);
        return $"{reader.GetString(reference.Namespace)}.{reader.GetString(reference.Name)}";
    }
}
