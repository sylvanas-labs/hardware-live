using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace HardwareLive.Protocol;

public sealed record SensorFrame(
    int Version,
    long Sequence,
    long TimestampUnixMs,
    bool PawnIoInstalled,
    bool Elevated,
    string LhmVersion,
    IReadOnlyList<HardwareInfo> Hardware,
    IReadOnlyList<SensorReading> Sensors)
{
    public const int CurrentVersion = 1;
}

public sealed record HardwareInfo(
    string Id,
    string Name,
    string Type,
    string? ParentId);

public sealed record SensorReading(
    string Id,
    string HardwareId,
    string Name,
    string Type,
    float? Value,
    float? Min,
    float? Max);

public static class SensorFrameJson
{
    private static readonly JsonSerializerOptions SharedOptions = CreateOptions();

    public static JsonSerializerOptions Options => SharedOptions;

    public static JsonSerializerOptions CreateIndentedOptions() =>
        new(SharedOptions) { WriteIndented = true };

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            // A sampler frame crosses a process boundary; treat a JSON null landing on a
            // non-nullable member (e.g. a required id/name string) as malformed input
            // (JsonException) rather than silently coercing it, so FrameProtocol.ReadAsync
            // can classify it as an invalid frame instead of corrupting protocol state.
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
        };
        options.TypeInfoResolver = new DefaultJsonTypeInfoResolver();
        options.Converters.Add(new FiniteNullableSingleConverter());
        options.MakeReadOnly();
        return options;
    }

    private sealed class FiniteNullableSingleConverter : JsonConverter<float?>
    {
        public override bool HandleNull => true;

        public override float? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return null;
            }

            var value = reader.GetSingle();
            return float.IsFinite(value) ? value : null;
        }

        public override void Write(Utf8JsonWriter writer, float? value, JsonSerializerOptions options)
        {
            if (value is null || !float.IsFinite(value.Value))
            {
                writer.WriteNullValue();
                return;
            }

            writer.WriteNumberValue(value.Value);
        }
    }
}
