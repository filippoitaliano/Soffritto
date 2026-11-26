namespace Soffritto.Presentation;

/// <summary>
/// The free-text note of an environment, shared between the app and the VSIX: one text file per environment in
/// %LOCALAPPDATA%\Soffritto\Note. Edits are saved shortly after the last
/// keystroke; those made by the other process reach <c>externalChanged</c>.
/// </summary>
public sealed class EnvironmentNote : IDisposable
{
    private static readonly string Folder = Path.Combine(Soffritto.Core.Configuration.ConfigurationFiles.DataFolder, "Note");

    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);

    // A single write reaches the watcher as several events, and the file may still be open
    // in the other process: it is reread only once, after the events settle.
    private static readonly TimeSpan ReloadDelay = TimeSpan.FromMilliseconds(200);

    private readonly string path;
    private readonly Action<string> externalChanged;
    private readonly FileSystemWatcher? watcher;
    private readonly Timer saveTimer;
    private readonly Timer reloadTimer;
    private readonly object gate = new();
    private string text;
    private string saved;
    private bool savePending;
    private bool disposed;

    /// <param name="key">The environment number, or the folder name if it has no number (see <see cref="BackendTools.EnvironmentKey"/>).</param>
    /// <param name="externalChanged">Called from a thread-pool thread when the file is changed by the other process.</param>
    public EnvironmentNote(string key, Action<string> externalChanged)
    {
        path = PathOf(key);
        this.externalChanged = externalChanged;
        text = saved = ReadFile() ?? string.Empty;
        saveTimer = new Timer(_ => Flush());
        reloadTimer = new Timer(_ => Reload());

        try
        {
            Directory.CreateDirectory(Folder);
            watcher = new FileSystemWatcher(Folder, Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            watcher.Changed += (_, _) => ScheduleReload();
            watcher.Created += (_, _) => ScheduleReload();
            watcher.Deleted += (_, _) => ScheduleReload();
            watcher.Renamed += (_, _) => ScheduleReload();
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception)
        {
            // Folder cannot be created: the note stays editable, but does not sync with the other process.
            watcher = null;
        }
    }

    /// <summary>The note file of an environment, by number or folder name (see the constructor).</summary>
    public static string PathOf(string key) => Path.Combine(Folder, $"ambiente-{key}.txt");

    /// <summary>
    /// Deletes the note of an environment that no longer exists: a new environment with the same
    /// number starts without it. Null if done or if there was no note, otherwise the error.
    /// </summary>
    public static string? Delete(string key)
    {
        try
        {
            File.Delete(PathOf(key));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    public string Text
    {
        get
        {
            lock (gate)
            {
                return text;
            }
        }
    }

    /// <summary>Text typed by the user: saved after <see cref="SaveDelay"/> with no further keystrokes.</summary>
    public void Edit(string value)
    {
        lock (gate)
        {
            if (disposed || value == text)
            {
                return;
            }

            text = value;
            savePending = true;
            saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Immediately saves any pending text, for example on close.</summary>
    public void Flush()
    {
        string toSave;
        lock (gate)
        {
            savePending = false;
            if (text == saved)
            {
                return;
            }

            toSave = text;
            saved = text;
        }

        if (WriteFile(toSave))
        {
            return;
        }

        // File still locked: retry later, as long as the note is open.
        lock (gate)
        {
            if (saved == toSave)
            {
                saved = string.Empty;
            }

            if (!disposed)
            {
                savePending = true;
                saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
        }

        watcher?.Dispose();
        reloadTimer.Dispose();
        saveTimer.Dispose();
        Flush();
    }

    private void ScheduleReload()
    {
        lock (gate)
        {
            if (!disposed)
            {
                reloadTimer.Change(ReloadDelay, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void Reload()
    {
        var current = ReadFile();
        if (current is null)
        {
            return;
        }

        lock (gate)
        {
            // The echo of our own save matches saved. Local text not yet saved
            // wins: the upcoming save will rewrite the file.
            if (disposed || savePending || current == saved)
            {
                return;
            }

            text = saved = current;
        }

        externalChanged(current);
    }

    /// <returns>The content, empty if the file does not exist; null if it stays locked by the other process.</returns>
    private string? ReadFile()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        return null;
    }

    private bool WriteFile(string value)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(path, value);
                return true;
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        return false;
    }
}
