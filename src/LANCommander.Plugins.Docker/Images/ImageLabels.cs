namespace LANCommander.Plugins.Docker.Images;

/// <summary>What an image is, as it says in the image's labels.</summary>
public enum ImageKind
{
    /// <summary>A game's dedicated server.</summary>
    Game,

    /// <summary>An image games are built on, which runs a game it's told to.</summary>
    Base,

    /// <summary>Not a server to offer, such as a tool.</summary>
    Hidden,
}

/// <summary>
/// What LANCommander's server images say about themselves in their labels. Each is optional; the
/// catalog falls back on what it can tell otherwise.
/// </summary>
/// <remarks>
/// <list type="table">
/// <item><term><c>org.opencontainers.image.title</c></term><description>The game's name, e.g. <c>Quake III Arena</c>.</description></item>
/// <item><term><c>org.opencontainers.image.description</c></term><description>A line about it.</description></item>
/// <item><term><c>com.lancommander.server.kind</c></term><description><c>game</c>, <c>base</c>, or anything else to leave it out.</description></item>
/// <item><term><c>com.lancommander.server.external-id.&lt;provider&gt;</c></term><description>
/// The game's id with a provider LANCommander keeps games' ids for, as its game records name them:
/// <c>igdb</c>, <c>steam</c> (the store's app id, not the dedicated server's), <c>gog</c>.
/// </description></item>
/// </list>
/// </remarks>
public sealed record ImageLabels(string? Title, string? Description, ImageKind? Kind, IReadOnlyDictionary<string, string> ExternalIds)
{
    public const string TitleLabel = "org.opencontainers.image.title";
    public const string DescriptionLabel = "org.opencontainers.image.description";
    public const string KindLabel = "com.lancommander.server.kind";
    public const string ExternalIdPrefix = "com.lancommander.server.external-id.";

    public static readonly ImageLabels None = new(null, null, null, new Dictionary<string, string>());

    public static ImageLabels Parse(IDictionary<string, string>? labels)
    {
        if (labels == null || labels.Count == 0)
            return None;

        return new ImageLabels(
            Value(labels, TitleLabel),
            Value(labels, DescriptionLabel),
            Value(labels, KindLabel)?.ToLowerInvariant() switch
            {
                null => null,
                "game" => ImageKind.Game,
                "base" => ImageKind.Base,
                _ => ImageKind.Hidden,
            },
            labels
                .Where(l => l.Key.StartsWith(ExternalIdPrefix, StringComparison.OrdinalIgnoreCase) && l.Key.Length > ExternalIdPrefix.Length && !string.IsNullOrWhiteSpace(l.Value))
                .GroupBy(l => l.Key[ExternalIdPrefix.Length..].Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Value.Trim(), StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>These labels, with <paramref name="fallback"/>'s where these have none.</summary>
    public ImageLabels Or(ImageLabels fallback) => new(
        Title ?? fallback.Title,
        Description ?? fallback.Description,
        Kind ?? fallback.Kind,
        ExternalIds.Count > 0 ? ExternalIds : fallback.ExternalIds);

    private static string? Value(IDictionary<string, string> labels, string key) =>
        labels.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
}
