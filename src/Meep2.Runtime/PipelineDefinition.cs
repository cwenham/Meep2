using Meep2.Runtime.Internal;
using Meep2.SDK.Modules;

namespace Meep2.Runtime;

/// <summary>A pipeline's modules, connections and settings, as a builder has them.</summary>
internal sealed record PipelineDefinition(string Name, PipelineOptions Options, NodeSpec[] Specs, (ModuleInfo From, ModuleInfo To)[] Connections);
