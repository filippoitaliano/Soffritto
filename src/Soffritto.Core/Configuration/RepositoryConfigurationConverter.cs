using System.Text.Json;
using System.Text.Json.Serialization;

namespace Soffritto.Core.Configuration;

/// <summary>Reads and writes a catalog entry, choosing the class from the "type" field.</summary>
public sealed class RepositoryConfigurationConverter : JsonConverter<RepositoryConfiguration>
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert == typeof(RepositoryConfiguration);

    // "type" is searched across the whole object: in a hand-written file it can be in any position.
    public override RepositoryConfiguration Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var element = document.RootElement;

        var value = element.ValueKind == JsonValueKind.Object
            ? element.EnumerateObject()
                .Where(p => p.Name.Equals("type", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                .Select(p => p.Value.GetString())
                .FirstOrDefault()
            : null;

        var concrete = value?.ToLowerInvariant() switch
        {
            "backend" => typeof(BackendConfiguration),
            "frontend" => typeof(FrontendConfiguration),
            "app" => typeof(AppRepoConfiguration),
            "generic" => typeof(GenericRepoConfiguration),
            _ => throw new JsonException($"Unknown repository type '{value}': allowed values are backend, frontend, app, generic."),
        };

        return (RepositoryConfiguration)(element.Deserialize(concrete, options)
            ?? throw new JsonException("Empty catalog entry."));
    }

    public override void Write(Utf8JsonWriter writer, RepositoryConfiguration value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, value, value.GetType(), options);
}
