namespace GameDeck.Core.Config;

/// <summary>The logo designs and finishes offered in Settings. web/src/lib/logo.ts uses the same ids.</summary>
public static class LogoCatalog
{
    public const string DefaultModel = "bigkey";
    public const string DefaultFinish = "cobalt";

    public static readonly IReadOnlyList<string> Models =
        ["original", "fine", "bigd", "bigkey", "straight", "dynamic", "outline", "litd", "core", "noble", "mono"];

    public static readonly IReadOnlyList<string> Finishes =
        ["champagne", "platinum", "cobalt", "arctic", "emerald", "steel", "bronze", "monochrome"];

    public static string NormalizeModel(string? id) => id is not null && Models.Contains(id) ? id : DefaultModel;
    public static string NormalizeFinish(string? id) => id is not null && Finishes.Contains(id) ? id : DefaultFinish;
}
