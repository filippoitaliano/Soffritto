using System.Text.RegularExpressions;
using Soffritto.Core.Configuration;

namespace Soffritto.Core.Env;

/// <summary>
/// Righe "@set NOME=valore" (o "set", o "NOME=valore") di un .bat. In lettura @REM e @CALL non
/// sono variabili; in scrittura i segreti possono restare nel loro file, richiamato con @CALL.
/// </summary>
public sealed class BatEnvAdapter : IEnvAdapter
{
    /// <summary>
    /// La riga di una variabile: gruppo "quoted" la virgoletta prima del nome (forma set "NOME=valore"),
    /// "name" il nome, "value" il testo dopo l'uguale, virgolette comprese; il valore come lo legge
    /// cmd lo da' ReadLines.
    /// </summary>
    public static readonly Regex Line = new(@"^\s*(@?set\s+)?(?<quoted>"")?(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<value>.*?)\s*$", RegexOptions.IgnoreCase);

    public EnvAdapterKind Kind => EnvAdapterKind.Bat;

    /// <summary>Le coppie nome e valore del file, nell'ordine e con i doppioni; vuoto se il file non c'e'.</summary>
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

    // Come cmd: set "NOME=v" resto -> v (fino all'ultima virgoletta, caret letterale); nelle altre
    // forme le virgolette restano e un ^ fuori dalle virgolette rende letterale il carattere dopo.
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

    // Un valore che inizia con " torna nella forma nuda, identico all'originale: le sue virgolette
    // proteggono gia' i metacaratteri. La porta del dev server (solo cifre) resta nuda perche'
    // FrontendEnv.ReadDevServerPort legge solo quella forma.
    private static bool NeedsQuotedForm(string value)
        => !value.StartsWith('"') && value.IndexOfAny(['&', '|', '<', '>', '^']) >= 0;
}

/// <summary>Riscrittura di un file di env a righe: tocca solo le righe delle variabili del set.</summary>
internal static class EnvLineFile
{
    /// <summary>
    /// La prima riga di ogni variabile del set prende il valore nuovo, i doppioni non commentati
    /// spariscono (vincerebbero sul valore nuovo), le variabili mancanti si aggiungono in fondo.
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
