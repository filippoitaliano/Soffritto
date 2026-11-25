using System.Collections;

namespace Soffritto.Core.Env;

/// <summary>Da dove viene il valore di una variabile: decide dove puo' finire.</summary>
public enum EnvClass
{
    /// <summary>Dalla fonte dei segreti del repository: mai nei log, mai in un file versionato.</summary>
    SharedSecret,

    /// <summary>Dall'ambiente modello o dal setup.</summary>
    Constant,

    /// <summary>Calcolata per lo slot (porte, connessioni, URL del pool).</summary>
    Slot,
}

/// <param name="Origin">Il file o la voce di configurazione da cui viene il valore.</param>
public sealed record EnvVariable(string Name, string Value, EnvClass Class, string Origin);

/// <summary>Variabili in ordine, una per nome (senza distinzione di maiuscole): l'ultima scritta vince e tiene il posto della prima.</summary>
public sealed class EnvSet : IEnumerable<EnvVariable>
{
    private readonly List<EnvVariable> variables = [];

    public EnvSet()
    {
    }

    public EnvSet(IEnumerable<EnvVariable> variables)
    {
        foreach (var variable in variables)
        {
            Set(variable);
        }
    }

    public int Count => variables.Count;

    public EnvVariable? this[string name] => variables.FirstOrDefault(v => Same(v.Name, name));

    public bool Contains(string name) => this[name] is not null;

    public void Set(EnvVariable variable)
    {
        var index = variables.FindIndex(v => Same(v.Name, variable.Name));
        if (index >= 0)
        {
            variables[index] = variable;
        }
        else
        {
            variables.Add(variable);
        }
    }

    public bool Remove(string name) => variables.RemoveAll(v => Same(v.Name, name)) > 0;

    public void RemoveWhere(Func<EnvVariable, bool> predicate) => variables.RemoveAll(v => predicate(v));

    /// <summary>Aggiunge le variabili di <paramref name="other"/>: a parita' di nome vince <paramref name="other"/>.</summary>
    public EnvSet Merge(EnvSet other)
    {
        foreach (var variable in other)
        {
            Set(variable);
        }

        return this;
    }

    /// <summary>Le stesse variabili con un'altra classe e origine (es. i segreti letti con un adapter).</summary>
    public EnvSet As(EnvClass @class, string origin) => new(variables.Select(v => v with { Class = @class, Origin = origin }));

    /// <summary>Nome → valore, per le guardie che controllano l'ambiente di un container.</summary>
    public IReadOnlyDictionary<string, string> ToDictionary()
        => variables.ToDictionary(v => v.Name, v => v.Value, StringComparer.OrdinalIgnoreCase);

    public IEnumerator<EnvVariable> GetEnumerator() => variables.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
