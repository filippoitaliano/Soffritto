using System.Text.RegularExpressions;
using Soffritto.Core.Configuration;

namespace Soffritto.Core.Env;

/// <summary>Righe "NOME=valore" di un .env; le righe che iniziano con # sono commenti.</summary>
public sealed class DotenvEnvAdapter : IEnvAdapter
{
    private static readonly Regex Line = new(@"^\s*(?:export\s+)?([A-Za-z_][A-Za-z0-9_.]*)\s*=\s*(.*?)\s*$");

    public EnvAdapterKind Kind => EnvAdapterKind.Dotenv;

    public EnvSet Read(string path)
    {
        var set = new EnvSet();
        if (!File.Exists(path))
        {
            return set;
        }

        foreach (var text in File.ReadLines(path))
        {
            if (NameOf(text) is not null && Line.Match(text) is { Success: true } match)
            {
                set.Set(new EnvVariable(match.Groups[1].Value, Unquote(match.Groups[2].Value), EnvClass.Constant, path));
            }
        }

        return set;
    }

    public void Write(string path, EnvSet set, EnvWriteOptions options)
        => EnvLineFile.Write(path, set, NameOf, v => $"{v.Name}={Quote(v.Value)}", requiredLine: null, defaultNewLine: "\n");

    private static string? NameOf(string text)
        => text.TrimStart().StartsWith('#') ? null : Line.Match(text) is { Success: true } match ? match.Groups[1].Value : null;

    private static string Unquote(string value)
        => value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0] ? value[1..^1] : value;

    private static string Quote(string value)
        => value.Length > 0 && (value.Any(char.IsWhiteSpace) || value.Contains('#') || value[0] is '"' or '\'') && !value.Contains('"')
            ? $"\"{value}\""
            : value;
}
