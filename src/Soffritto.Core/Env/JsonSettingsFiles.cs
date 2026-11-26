using System.Text.Json;

namespace Soffritto.Core.Env;

/// <summary>How repository appsettings are read: JSONC, with comments and trailing commas.</summary>
public static class JsonSettingsFiles
{
    public static readonly JsonDocumentOptions Options = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}
