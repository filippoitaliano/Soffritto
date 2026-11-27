using System.Security.Cryptography;
using System.Text;
using Soffritto.Core.Configuration;

namespace Soffritto.Core.AzureDevOps;

/// <summary>
/// The Azure DevOps PAT kept in config.json encrypted with the Windows user (DPAPI): the app writes
/// it, the app and the MCP server, running as the same user, read it.
/// </summary>
public static class AzureDevOpsSecrets
{
    /// <summary>The plain-text PAT for Azure DevOps calls; null if missing or it cannot be decrypted (another user or another machine).</summary>
    public static string? PatOf(AzureDevOpsConfiguration azure)
    {
        if (!string.IsNullOrEmpty(azure.Pat))
        {
            return azure.Pat;
        }

        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return string.IsNullOrEmpty(azure.PatProtected)
                ? null
                : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(azure.PatProtected), null, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }

    public static string Protect(string secret)
        => OperatingSystem.IsWindows()
            ? Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser))
            : throw new PlatformNotSupportedException("the PAT is encrypted with the Windows user (DPAPI)");

    /// <summary>
    /// The connection of the configured project, or the reason there is none: not configured, or a
    /// PAT that cannot be read. Windows authentication needs no secret.
    /// </summary>
    public static AzureDevOpsConnection? Connect(AzureDevOpsConfiguration azure, out string? problem)
    {
        problem = null;
        if (!azure.IsConfigured)
        {
            problem = "Azure DevOps is not configured (Azure DevOps tab of the setup)";
            return null;
        }

        string? pat = null;
        if (azure.Authentication == AzureAuthentication.Pat && (pat = PatOf(azure)) is null)
        {
            problem = "the Azure DevOps PAT is missing or cannot be decrypted by this Windows user";
            return null;
        }

        return new AzureDevOpsConnection(azure.CollectionUrl, azure.Project, pat);
    }
}
