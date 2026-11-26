namespace Soffritto.Core;

/// <summary>The tool's git conventions, the same for every repository in the catalog.</summary>
public static class GitConventions
{
    /// <summary>
    /// The main development branch: Reset brings repositories back to it, new Docker environment
    /// branches are created from it, and "Verify seed" and "Align seed" compare against its
    /// migrations. Every repository in the catalog must have it on origin.
    /// </summary>
    public const string DevelopmentBranch = "dev";

    /// <summary>The remote-tracking branch of <see cref="DevelopmentBranch"/>.</summary>
    public const string RemoteDevelopmentBranch = "origin/" + DevelopmentBranch;

    /// <summary>The full ref of <see cref="RemoteDevelopmentBranch"/>.</summary>
    public const string RemoteDevelopmentReference = "refs/remotes/" + RemoteDevelopmentBranch;
}
