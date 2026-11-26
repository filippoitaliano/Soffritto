using System.Collections;

namespace Soffritto.Core.Env;

/// <summary>Where a variable's value comes from: decides where it may end up.</summary>
public enum EnvClass
{
    /// <summary>From the repository's secrets source: never in logs, never in a versioned file.</summary>
    SharedSecret,

    /// <summary>From the template environment or from setup.</summary>
    Constant,

    /// <summary>Computed for the slot (ports, connections, pool URLs).</summary>
    Slot,
}

/// <param name="Origin">The file or configuration entry the value comes from.</param>
public sealed record EnvVariable(string Name, string Value, EnvClass Class, string Origin);

/// <summary>Ordered variables, one per name (case-insensitive): the last one written wins and takes the place of the first.</summary>
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

    /// <summary>Adds the variables of <paramref name="other"/>: on equal names <paramref name="other"/> wins.</summary>
    public EnvSet Merge(EnvSet other)
    {
        foreach (var variable in other)
        {
            Set(variable);
        }

        return this;
    }

    /// <summary>The same variables with another class and origin (e.g. secrets read with an adapter).</summary>
    public EnvSet As(EnvClass @class, string origin) => new(variables.Select(v => v with { Class = @class, Origin = origin }));

    /// <summary>Name → value, for the guards that check a container's environment.</summary>
    public IReadOnlyDictionary<string, string> ToDictionary()
        => variables.ToDictionary(v => v.Name, v => v.Value, StringComparer.OrdinalIgnoreCase);

    public IEnumerator<EnvVariable> GetEnumerator() => variables.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
