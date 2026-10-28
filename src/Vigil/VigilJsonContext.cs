using System.Text.Json.Serialization;
using Vigil.Core.Abstractions;
using Vigil.Ipc;

namespace Vigil;

/// <summary>Source-generated JSON metadata for the IPC protocol and the activity log (AOT-safe, no reflection).</summary>
[JsonSourceGenerationOptions(
    UseStringEnumConverter = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(IpcRequest))]
[JsonSerializable(typeof(IpcResponse))]
[JsonSerializable(typeof(ActivityEntry))]
internal sealed partial class VigilJsonContext : JsonSerializerContext;
