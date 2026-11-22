using System.Text.Json.Serialization;

namespace Meep2.Modules;

[JsonSerializable(typeof(SavedTree))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class TreeJson : JsonSerializerContext;
