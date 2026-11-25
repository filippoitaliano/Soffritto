using System.Text.Json;

namespace Soffritto.Core.Env;

/// <summary>Come si leggono gli appsettings dei repository: JSONC, con commenti e virgole finali.</summary>
public static class JsonSettingsFiles
{
    public static readonly JsonDocumentOptions Options = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}
