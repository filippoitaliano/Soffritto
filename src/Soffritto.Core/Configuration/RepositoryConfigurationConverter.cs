using System.Text.Json;
using System.Text.Json.Serialization;

namespace Soffritto.Core.Configuration;

/// <summary>Legge e scrive una voce del catalogo scegliendo la classe dal campo "type".</summary>
public sealed class RepositoryConfigurationConverter : JsonConverter<RepositoryConfiguration>
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert == typeof(RepositoryConfiguration);

    // Il "type" si cerca in tutto l'oggetto: in un file scritto a mano puo' stare in qualunque posizione.
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
            _ => throw new JsonException($"Repository con tipo '{value}' sconosciuto: ammessi backend, frontend, app, generic."),
        };

        return (RepositoryConfiguration)(element.Deserialize(concrete, options)
            ?? throw new JsonException("Voce del catalogo vuota."));
    }

    public override void Write(Utf8JsonWriter writer, RepositoryConfiguration value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, value, value.GetType(), options);
}
