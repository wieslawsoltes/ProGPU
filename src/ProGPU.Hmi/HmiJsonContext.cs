using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProGPU.Hmi;

/// <summary>
/// Closed serialization graph for project persistence, cloning and designer history.
/// No reflection resolver or dynamically constructed enum converter is admitted.
/// </summary>
[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    MaxDepth = 32,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(HmiProject))]
internal sealed partial class HmiJsonContext : JsonSerializerContext
{
    internal static HmiJsonContext ProjectContext { get; } = new(CreateProjectOptions());

    private static JsonSerializerOptions CreateProjectOptions() => new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        MaxDepth = 32,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        // Closed generic converters preserve the schema's string-only enum contract
        // without JsonStringEnumConverter's runtime MakeGenericType path.
        Converters =
        {
            new JsonStringEnumConverter<HmiTagType>(allowIntegerValues: false),
            new JsonStringEnumConverter<HmiQuality>(allowIntegerValues: false),
            new JsonStringEnumConverter<HmiSimulationKind>(allowIntegerValues: false),
            new JsonStringEnumConverter<HmiSymbol>(allowIntegerValues: false),
            new JsonStringEnumConverter<HmiPresentation>(allowIntegerValues: false),
            new JsonStringEnumConverter<HmiActionKind>(allowIntegerValues: false),
            new JsonStringEnumConverter<HmiAlarmCondition>(allowIntegerValues: false),
            new JsonStringEnumConverter<HmiAlarmSeverity>(allowIntegerValues: false),
            new JsonStringEnumConverter<HmiVisualTone>(allowIntegerValues: false),
            new JsonStringEnumConverter<HmiStateCondition>(allowIntegerValues: false),
            new JsonStringEnumConverter<HmiConnectionProtocol>(allowIntegerValues: false),
            new JsonStringEnumConverter<HmiModbusArea>(allowIntegerValues: false),
            new JsonStringEnumConverter<HmiRegisterEncoding>(allowIntegerValues: false),
            new JsonStringEnumConverter<HmiRegisterOrder>(allowIntegerValues: false),
            new JsonStringEnumConverter<HmiOpcUaSecurityMode>(allowIntegerValues: false),
            new JsonStringEnumConverter<HmiOpcUaSecurityPolicy>(allowIntegerValues: false),
            new JsonStringEnumConverter<HmiOpcUaDataType>(allowIntegerValues: false)
        }
    };
}
