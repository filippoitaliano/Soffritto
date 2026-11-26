namespace Soffritto.Core;

public static class MigrationApplier
{
    /// <summary>
    /// Runs <c>dotnet ef database update</c> on the given context: up to
    /// <paramref name="targetMigrationId"/> (backwards too), or all pending ones if null.
    /// </summary>
    /// <remarks>
    /// ASPNETCORE_ENVIRONMENT is forced to Development because it decides WHICH
    /// database gets touched: without it, EF would read appsettings.json instead of
    /// appsettings.Development.json and migrate a different database from the one the panel
    /// shows. The launchSettings.json profiles set Development today, but relying on their
    /// order for a destructive operation is not acceptable.
    ///
    /// The build is not skipped (no --no-build): applying migrations compiled from stale
    /// sources is worse than waiting a few tens of seconds.
    ///
    /// With <paramref name="connection"/> the database is the given one (--connection, plus the
    /// process variable ConnectionStrings__&lt;name&gt; if <paramref name="connectionStringName"/>
    /// is set), not the one in the clone's files: the seed needs this, since its container is not in the files.
    /// </remarks>
    public static Task<ProcessOutcome> UpdateDatabaseAsync(
        string solutionRoot,
        ContextDefinition context,
        string? targetMigrationId = null,
        Action<string>? onOutputLine = null,
        CancellationToken cancellationToken = default,
        string? connection = null,
        string? connectionStringName = null)
    {
        var environment = new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["DOTNET_ENVIRONMENT"] = "Development",
        };

        if (connection is not null && !string.IsNullOrWhiteSpace(connectionStringName))
        {
            environment[$"ConnectionStrings__{connectionStringName}"] = connection;
        }

        return ProcessRunner.RunAsync(
            "dotnet",
            BuildArguments(solutionRoot, context, targetMigrationId, connection),
            solutionRoot,
            timeoutSeconds: 600,
            environment: environment,
            onOutputLine: onOutputLine,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// The readable reason for a failed <see cref="UpdateDatabaseAsync"/>: the error lines of
    /// <c>dotnet build</c> on the startup project when EF stopped at compilation,
    /// otherwise the error lines of EF's output.
    /// </summary>
    /// <remarks>
    /// When the build fails, <c>dotnet ef</c> only prints "Build failed. Use dotnet build to see
    /// the errors.": the end of the output is left with NuGet warnings. The second build finds the
    /// same errors as the first, because the sources have not changed.
    /// </remarks>
    public static async Task<string> FailureReasonAsync(
        string solutionRoot,
        ContextDefinition context,
        ProcessOutcome update,
        CancellationToken cancellationToken = default)
    {
        if (!(update.StandardOutput + update.StandardError).Contains("Build failed", StringComparison.OrdinalIgnoreCase))
        {
            return update.BuildError(4);
        }

        var build = await ProcessRunner.RunAsync(
            "dotnet",
            $"build {Quote(Path.Combine(solutionRoot, context.RelativeStartupProjectPath))} -nologo -clp:ErrorsOnly",
            solutionRoot,
            timeoutSeconds: 600,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return build.Success
            ? $"{update.BuildError(4)} (a second dotnet build succeeds: try again)"
            : $"build failed: {build.BuildError(4)}";
    }

    /// <summary>
    /// The same command as the button, as a line ready to paste into a terminal:
    /// <paramref name="targetMigrationId"/> stops the update at that migration (or rolls back
    /// to it), null applies all pending ones.
    /// </summary>
    /// <remarks>
    /// The two environment variables travel with the line for the same reason the button
    /// forces them: without them, EF reads appsettings.json and migrates a different database from the
    /// one shown in the panel. The prefix syntax is PowerShell, Visual Studio's default
    /// terminal; paths are absolute, so the line works from any folder.
    /// </remarks>
    public static string BuildUpdateCommandLine(
        string solutionRoot,
        ContextDefinition context,
        string? targetMigrationId = null,
        string? connection = null)
    {
        return "$env:ASPNETCORE_ENVIRONMENT='Development'; $env:DOTNET_ENVIRONMENT='Development'; "
            + $"dotnet {BuildArguments(solutionRoot, context, targetMigrationId, connection)}";
    }

    private static string BuildArguments(string solutionRoot, ContextDefinition context, string? targetMigrationId, string? connection)
    {
        // The line does not go through a shell that can escape it: a quote would break it.
        if (connection is not null && connection.Contains('"'))
        {
            throw new ArgumentException("The connection string cannot contain double quotes.", nameof(connection));
        }

        var parts = new List<string> { "ef", "database", "update" };

        // EF wants the target right after "update", before the options.
        if (!string.IsNullOrWhiteSpace(targetMigrationId))
        {
            parts.Add(targetMigrationId);
        }

        parts.AddRange(
        [
            "--project", Quote(Path.Combine(solutionRoot, context.RelativeProjectPath)),
            "--startup-project", Quote(Path.Combine(solutionRoot, context.RelativeStartupProjectPath)),
            "--context", context.Name,
        ]);

        if (connection is not null)
        {
            parts.AddRange(["--connection", Quote(connection)]);
        }

        return string.Join(' ', parts);
    }

    private static string Quote(string path) => $"\"{path}\"";
}
