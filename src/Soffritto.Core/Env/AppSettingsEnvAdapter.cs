using System.Text.Json;
using System.Text.Json.Nodes;
using Soffritto.Core.Configuration;

namespace Soffritto.Core.Env;

/// <summary>
/// Un appsettings*.json visto come chiavi "A:B" (come la configurazione di .NET, con gli indici
/// degli array). In scrittura cambia solo le chiavi del set; i commenti del file non sopravvivono.
/// </summary>
public sealed class AppSettingsEnvAdapter : IEnvAdapter
{
    public EnvAdapterKind Kind => EnvAdapterKind.AppSettings;

    public EnvSet Read(string path)
    {
        var set = new EnvSet();
        if (!File.Exists(path))
        {
            return set;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path), JsonSettingsFiles.Options);
        Flatten(document.RootElement, string.Empty, set, path);
        return set;
    }

    public void Write(string path, EnvSet set, EnvWriteOptions options)
    {
        var root = File.Exists(path)
            ? JsonNode.Parse(File.ReadAllText(path), documentOptions: JsonSettingsFiles.Options) as JsonObject ?? new JsonObject()
            : new JsonObject();

        foreach (var variable in set)
        {
            var parts = variable.Name.Split(':');
            var node = root;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (node[parts[i]] is not JsonObject child)
                {
                    child = new JsonObject();
                    node[parts[i]] = child;
                }

                node = child;
            }

            node[parts[^1]] = variable.Value switch
            {
                "true" => JsonValue.Create(true),
                "false" => JsonValue.Create(false),
                _ => JsonValue.Create(variable.Value),
            };
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, overwrite: true);
    }

    private static void Flatten(JsonElement element, string prefix, EnvSet set, string origin)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    Flatten(property.Value, prefix.Length == 0 ? property.Name : $"{prefix}:{property.Name}", set, origin);
                }

                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    Flatten(item, $"{prefix}:{index++}", set, origin);
                }

                break;
            case JsonValueKind.String:
                set.Set(new EnvVariable(prefix, element.GetString() ?? string.Empty, EnvClass.Constant, origin));
                break;
            case JsonValueKind.Null:
                set.Set(new EnvVariable(prefix, string.Empty, EnvClass.Constant, origin));
                break;
            default:
                set.Set(new EnvVariable(prefix, element.GetRawText(), EnvClass.Constant, origin));
                break;
        }
    }
}
