using System.Text.RegularExpressions;
using Soffritto.Core.Configuration;

namespace Soffritto.Core.Env;

/// <summary>
/// "@set NAME=value" lines (or "set", or "NAME=value") of a .bat. When reading, @REM and @CALL are not
/// variables; when writing, secrets can stay in their own file, invoked with @CALL.
/// </summary>
public sealed class BatEnvAdapter : IEnvAdapter
{
    /// <summary>
    /// A variable line: group "quoted" is the quote before the name (the set "NAME=value" form),
    /// "name" the name, "value" the text after the equals sign, quotes included; ReadLines gives
    /// the value as cmd reads it.
    /// </summary>
    public static readonly Regex Line = new(@"^\s*(@?set\s+)?(?<quoted>"")?(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<value>.*?)\s*$", RegexOptions.IgnoreCase);

    public EnvAdapterKind Kind => EnvAdapterKind.Bat;

    /// <summary>The file's name/value pairs, in order and including duplicates; empty if the file does not exist.</summary>
    public static IEnumerable<(string Name, string Value)> ReadLines(string path)
    {
        if (!File.Exists(path))
        {
            yield break;
        }

        foreach (var text in File.ReadLines(path))
        {
            if (Line.Match(text) is { Success: true } match)
            {
                yield return (match.Groups["name"].Value, ValueOf(match));
            }
        }
    }

    // Like cmd: set "NAME=v" rest -> v (up to the last quote, caret taken literally); in the other
    // forms the quotes stay and a ^ outside quotes makes the next character literal.
    private static string ValueOf(Match match)
    {
        var value = match.Groups["value"].Value;
        if (match.Groups["quoted"].Success)
        {
            var last = value.LastIndexOf('"');
            return last >= 0 ? value[..last] : value;
        }

        var result = new System.Text.StringBuilder(value.Length);
        var inQuotes = false;
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (ch == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (ch == '^' && !inQuotes && i + 1 < value.Length)
            {
                i++;
                ch = value[i];
            }

            result.Append(ch);
        }

        return result.ToString();
    }

    public EnvSet Read(string path)
        => new(ReadLines(path).Select(p => new EnvVariable(p.Name, p.Value, EnvClass.Constant, path)));

    public void Write(string path, EnvSet set, EnvWriteOptions options)
    {
        var call = options.SecretsCall is { } secrets ? $"@CALL \"{secrets}\"" : null;
        EnvLineFile.Write(
            path,
            set,
            text => Line.Match(text) is { Success: true } match ? match.Groups["name"].Value : null,
            v => NeedsQuotedForm(v.Value) ? $"@set \"{v.Name}={v.Value}\"" : $"@set {v.Name}={v.Value}",
            call,
            defaultNewLine: "\r\n");
    }

    // A value starting with " goes back in the bare form, identical to the original: its quotes
    // already protect the metacharacters. The dev server port (digits only) stays bare because
    // FrontendEnv.ReadDevServerPort reads only that form.
    private static bool NeedsQuotedForm(string value)
        => !value.StartsWith('"') && value.IndexOfAny(['&', '|', '<', '>', '^']) >= 0;
}

/// <summary>Rewrites a line-based env file: touches only the lines of the set's variables.</summary>
internal static class EnvLineFile
{
    /// <summary>
    /// The first line of each variable in the set gets the new value, uncommented duplicates
    /// are dropped (they would override the new value), missing variables are appended at the end.
    /// </summary>
    public static void Write(string path, EnvSet set, Func<string, string?> nameOf, Func<EnvVariable, string> format, string? requiredLine, string defaultNewLine)
    {
        var text = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : text.Length > 0 ? "\n" : defaultNewLine;
        var lines = text.Length > 0 ? text.Split('\n').Select(l => l.TrimEnd('\r')).ToList() : [];
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var line in lines)
        {
            if (nameOf(line) is { } name && set[name] is { } variable)
            {
                if (written.Add(variable.Name))
                {
                    result.Add(format(variable));
                }

                continue;
            }

            result.Add(line);
        }

        result.AddRange(set.Where(v => !written.Contains(v.Name)).Select(format));
        if (requiredLine is not null && !result.Any(l => string.Equals(l.Trim(), requiredLine, StringComparison.OrdinalIgnoreCase)))
        {
            result.Add(requiredLine);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, string.Join(newLine, result) + newLine);
        File.Move(temporary, path, overwrite: true);
    }
}
