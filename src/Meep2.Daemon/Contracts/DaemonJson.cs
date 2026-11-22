using System.Text.Json.Serialization;

namespace Meep2.Daemon;

[JsonSerializable(typeof(DaemonInfo))]
[JsonSerializable(typeof(ListenInfo))]
[JsonSerializable(typeof(StartRequest))]
[JsonSerializable(typeof(PipelineSummary))]
[JsonSerializable(typeof(IReadOnlyList<PipelineSummary>))]
[JsonSerializable(typeof(PipelineDetail))]
[JsonSerializable(typeof(Problem))]
[JsonSerializable(typeof(Injected))]
[JsonSerializable(typeof(EditRequest))]
[JsonSerializable(typeof(ChangeResult))]
[JsonSerializable(typeof(SourceText))]
[JsonSerializable(typeof(PipelineGraph))]
[JsonSerializable(typeof(GraphNotice))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class DaemonJson : JsonSerializerContext;
