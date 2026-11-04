using System.Text;
using System.Text.Json;
using Docker.DotNet.Models;
using LANCommander.Plugins.Docker.Hosts;
using LANCommander.SDK.Plugins;
using Microsoft.Extensions.Logging;

namespace LANCommander.Plugins.Docker.Images;

/// <summary>One of LANCommander's server images, as the catalog lists it.</summary>
/// <param name="Repository">Its repository, e.g. <c>lancommander/quake3</c>.</param>
/// <param name="Name">Its name in the repository's namespace, e.g. <c>quake3</c>.</param>
/// <param name="Title">What it's for, e.g. <c>Quake III Arena</c>; its name if that isn't known.</param>
/// <param name="IsBase">One of the images games are built on, which runs a game it's told to.</param>
/// <param name="LocalTags">Its tags the Docker host has already; empty if it has none.</param>
/// <param name="ExternalIds">Its game's ids, by provider as LANCommander names them, e.g. <c>igdb</c>.</param>
public sealed record CatalogImage(
    string Repository,
    string Name,
    string Title,
    string Description,
    DateTime? Updated,
    bool IsBase,
    IReadOnlyList<string> LocalTags,
    IReadOnlyDictionary<string, string>? ExternalIds = null)
{
    public bool OnHost => LocalTags.Count > 0;
}

/// <summary>The catalog, and what couldn't be read for it, if anything.</summary>
public sealed record ImageCatalogResult(IReadOnlyList<CatalogImage> Images, string? Error);

/// <summary>An image as Docker Hub lists it.</summary>
internal sealed record PublishedImage(string Name, string Description, DateTime? Updated);

/// <summary>
/// LANCommander's server images: those published on Docker Hub, and those the Docker host already
/// has. What each is comes from its labels (see <see cref="ImageLabels"/>): from the catalog index,
/// if one is set, then from the host's copy of it; failing those, from its repository on GitHub, and
/// failing that, from its name.
/// </summary>
public sealed class ImageCatalog
{
    /// <summary>The Docker Hub namespace, and GitHub organization, the images are published under.</summary>
    public const string Namespace = "lancommander";

    private const string GitHubOrganization = "LANCommander";
    private const string GitHubPrefix = "LANCommander.Servers.";

    // For images without a kind label: those published under the namespace that aren't game servers
    // (LANCommander itself, its relay, and an image the base images replaced), and the base images
    private static readonly HashSet<string> NotServers = new(StringComparer.OrdinalIgnoreCase) { "lancommander", "udprelay", "basewinegameserver" };
    private static readonly HashSet<string> BaseImages = new(StringComparer.OrdinalIgnoreCase) { "base", "steamcmd", "wine" };

    // Long enough not to ask GitHub, which allows 60 requests an hour without signing in, too often
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(30);

    // What couldn't be read is tried again sooner, as a connection that was down may be back
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(2);

    private readonly DockerHosts _hosts;
    private readonly IPluginSettings<DockerSettings> _settings;
    private readonly ILogger<ImageCatalog> _logger;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _loading = new(1, 1);
    private Published? _published;
    private readonly Dictionary<string, (DateTime At, IReadOnlyList<string> Tags)> _tags = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What was read from outside the Docker host, and when, and what couldn't be.</summary>
    private sealed record Published(
        DateTime At,
        string IndexUrl,
        IReadOnlyList<PublishedImage> Images,
        IReadOnlyDictionary<string, ImageLabels> Index,
        IReadOnlyDictionary<string, (string Title, string Description)> GitHub,
        string? Error);

    public ImageCatalog(DockerHosts hosts, IPluginSettings<DockerSettings> settings, ILogger<ImageCatalog> logger)
        : this(hosts, settings, logger, new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
    {
    }

    internal ImageCatalog(DockerHosts hosts, IPluginSettings<DockerSettings> settings, ILogger<ImageCatalog> logger, HttpClient http)
    {
        _hosts = hosts;
        _settings = settings;
        _logger = logger;
        _http = http;

        // GitHub refuses requests without one
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("LANCommander.Plugins.Docker");
    }

    /// <summary>The images, game servers first by title, then the base images, with the host's marked.</summary>
    public async Task<ImageCatalogResult> ListAsync(Guid hostId, CancellationToken cancellationToken = default)
    {
        var published = await PublishedAsync(cancellationToken);
        var errors = new List<string>();

        if (published.Error != null)
            errors.Add(published.Error);

        IReadOnlyDictionary<string, LocalImage> local;

        try
        {
            local = await LocalAsync(hostId, cancellationToken);
        }
        catch (Exception ex)
        {
            local = new Dictionary<string, LocalImage>();
            errors.Add($"The host's images could not be listed: {ex.Message}");
        }

        return new ImageCatalogResult(
            Combine(published.Images, published.Index, local, published.GitHub),
            errors.Count > 0 ? string.Join(" ", errors) : null);
    }

    /// <summary>An image's tags, newest first: those on Docker Hub and the host's.</summary>
    public async Task<IReadOnlyList<string>> TagsAsync(Guid hostId, string name, CancellationToken cancellationToken = default)
    {
        var tags = new List<string>();

        try
        {
            tags.AddRange(await PublishedTagsAsync(name, cancellationToken));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "Could not list the tags of {Image} on Docker Hub", name);
        }

        try
        {
            if ((await LocalAsync(hostId, cancellationToken)).TryGetValue(name, out var local))
                tags.AddRange(local.Tags);
        }
        catch (Exception)
        {
            // Docker Hub's are enough
        }

        if (tags.Count == 0)
            tags.Add("latest");

        return tags.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>Docker Hub's images, the index's labels and GitHub's titles, each as far as they could be read.</summary>
    private async Task<Published> PublishedAsync(CancellationToken cancellationToken)
    {
        var indexUrl = _settings.Current.ImageCatalogUrl?.Trim() ?? "";

        if (_published is { } cached && IsCurrent(cached, indexUrl))
            return cached;

        await _loading.WaitAsync(cancellationToken);

        try
        {
            if (_published is { } loaded && IsCurrent(loaded, indexUrl))
                return loaded;

            var errors = new List<string>();
            IReadOnlyList<PublishedImage> images = [];
            IReadOnlyDictionary<string, ImageLabels> index = new Dictionary<string, ImageLabels>();
            IReadOnlyDictionary<string, (string, string)> gitHub = new Dictionary<string, (string, string)>();

            try
            {
                images = await DockerHubAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
            {
                _logger.LogWarning(ex, "Could not list LANCommander's images on Docker Hub");

                errors.Add($"Docker Hub couldn't be reached, so only images this host already has are listed: {ex.Message}");
            }

            var indexRead = false;

            if (indexUrl.Length > 0)
            {
                try
                {
                    using var document = JsonDocument.Parse(await _http.GetStringAsync(indexUrl, cancellationToken));

                    index = ParseIndex(document.RootElement);
                    indexRead = true;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or UriFormatException)
                {
                    _logger.LogWarning(ex, "Could not read the image catalog at {Url}", indexUrl);

                    errors.Add($"The image catalog at {indexUrl} couldn't be read, so images are titled from their own labels where this host has them: {ex.Message}");
                }
            }

            // Titles from the repositories' names are only wanted where the index doesn't give any
            if (!indexRead && images.Count > 0)
            {
                try
                {
                    gitHub = await GitHubTitlesAsync(cancellationToken);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
                {
                    _logger.LogInformation(ex, "Could not read the titles of LANCommander's images from GitHub");
                }
            }

            // Kept even when something couldn't be read, so an offline LAN isn't kept waiting on each listing
            var published = new Published(DateTime.UtcNow, indexUrl, images, index, gitHub, errors.Count > 0 ? string.Join(" ", errors) : null);

            _published = published;

            return published;
        }
        finally
        {
            _loading.Release();
        }
    }

    private static bool IsCurrent(Published published, string indexUrl) =>
        published.IndexUrl == indexUrl && DateTime.UtcNow - published.At < (published.Error == null ? CacheFor : RetryAfter);

    private async Task<IReadOnlyList<PublishedImage>> DockerHubAsync(CancellationToken cancellationToken)
    {
        var repositories = new List<JsonElement>();
        var next = $"https://hub.docker.com/v2/repositories/{Namespace}/?page_size=100";

        // A page at a time, for as long as it says there's another
        for (var page = 0; next != null && page < 10; page++)
        {
            using var document = JsonDocument.Parse(await _http.GetStringAsync(next, cancellationToken));

            repositories.AddRange(document.RootElement.GetProperty("results").EnumerateArray().Select(r => r.Clone()));

            next = document.RootElement.TryGetProperty("next", out var link) && link.ValueKind == JsonValueKind.String ? link.GetString() : null;
        }

        return ParseRepositories(repositories);
    }

    private async Task<IReadOnlyDictionary<string, (string Title, string Description)>> GitHubTitlesAsync(CancellationToken cancellationToken)
    {
        var titles = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);

        for (var page = 1; page <= 5; page++)
        {
            using var document = JsonDocument.Parse(await _http.GetStringAsync($"https://api.github.com/orgs/{GitHubOrganization}/repos?per_page=100&page={page}", cancellationToken));
            var count = 0;

            foreach (var repository in document.RootElement.EnumerateArray())
            {
                count++;

                var name = repository.GetProperty("name").GetString() ?? "";

                if (!name.StartsWith(GitHubPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                var game = name[GitHubPrefix.Length..];
                var description = repository.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : "";

                titles[game.Replace(".", "")] = (TitleOf(game), description);
            }

            if (count < 100)
                break;
        }

        return titles;
    }

    private async Task<IReadOnlyList<string>> PublishedTagsAsync(string name, CancellationToken cancellationToken)
    {
        lock (_tags)
        {
            if (_tags.TryGetValue(name, out var cached) && DateTime.UtcNow - cached.At < CacheFor)
                return cached.Tags;
        }

        using var document = JsonDocument.Parse(await _http.GetStringAsync($"https://hub.docker.com/v2/repositories/{Namespace}/{Uri.EscapeDataString(name)}/tags?page_size=100&ordering=last_updated", cancellationToken));

        var tags = ParseTags(document.RootElement);

        lock (_tags)
            _tags[name] = (DateTime.UtcNow, tags);

        return tags;
    }

    /// <summary>An image the Docker host has: its tags, and the labels of the one most likely current.</summary>
    internal sealed record LocalImage(IReadOnlyList<string> Tags, ImageLabels Labels);

    /// <summary>The host's images in the namespace, by name.</summary>
    private async Task<IReadOnlyDictionary<string, LocalImage>> LocalAsync(Guid hostId, CancellationToken cancellationToken)
    {
        var images = await _hosts.GetClient(hostId).Images.ListImagesAsync(new ImagesListParameters(), cancellationToken);

        return LocalOf(images.SelectMany(i => (i.RepoTags ?? []).Select(tag => (tag, (IDictionary<string, string>?)i.Labels))));
    }

    /// <summary>
    /// Tags such as <c>lancommander/quake3:latest</c>, with their images' labels, as each image's name,
    /// tags and labels: <c>latest</c>'s if it has that, else the first tag's.
    /// </summary>
    internal static IReadOnlyDictionary<string, LocalImage> LocalOf(IEnumerable<(string RepoTag, IDictionary<string, string>? Labels)> repoTags)
    {
        var local = new Dictionary<string, (List<string> Tags, ImageLabels Labels)>(StringComparer.OrdinalIgnoreCase);

        foreach (var (repoTag, labels) in repoTags)
        {
            var reference = repoTag.StartsWith("docker.io/", StringComparison.OrdinalIgnoreCase) ? repoTag["docker.io/".Length..] : repoTag;

            if (!reference.StartsWith(Namespace + "/", StringComparison.OrdinalIgnoreCase))
                continue;

            var colon = reference.LastIndexOf(':');
            var name = (colon > 0 ? reference[..colon] : reference)[(Namespace.Length + 1)..];
            var tag = colon > 0 ? reference[(colon + 1)..] : "latest";

            if (name.Length == 0 || name.Contains('/'))
                continue;

            if (!local.TryGetValue(name, out var image))
                local[name] = image = ([], ImageLabels.Parse(labels));
            else if (tag == "latest")
                local[name] = image = (image.Tags, ImageLabels.Parse(labels));

            image.Tags.Add(tag);
        }

        return local.ToDictionary(e => e.Key, e => new LocalImage(e.Value.Tags, e.Value.Labels), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Docker Hub's repositories in the namespace.</summary>
    internal static IReadOnlyList<PublishedImage> ParseRepositories(IEnumerable<JsonElement> repositories) =>
        repositories
            .Select(repository => new PublishedImage(
                repository.GetProperty("name").GetString() ?? "",
                repository.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString()?.Trim() ?? "" : "",
                repository.TryGetProperty("last_updated", out var u) && u.ValueKind == JsonValueKind.String && u.TryGetDateTime(out var at) ? at.ToUniversalTime() : null))
            .Where(image => image.Name.Length > 0)
            .ToList();

    /// <summary>
    /// The catalog index: <c>{ "images": [ { "name": "quake3", "labels": { ... } } ] }</c>, each image
    /// named as in the namespace (<c>"repository": "lancommander/quake3"</c> is taken too) with its labels.
    /// </summary>
    internal static IReadOnlyDictionary<string, ImageLabels> ParseIndex(JsonElement index)
    {
        var images = new Dictionary<string, ImageLabels>(StringComparer.OrdinalIgnoreCase);

        foreach (var image in index.GetProperty("images").EnumerateArray())
        {
            var name = image.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()
                : image.TryGetProperty("repository", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString()?.Split('/')[^1]
                : null;

            if (string.IsNullOrWhiteSpace(name))
                continue;

            var labels = image.TryGetProperty("labels", out var l) && l.ValueKind == JsonValueKind.Object
                ? l.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).ToDictionary(p => p.Name, p => p.Value.GetString()!)
                : [];

            images[name.Trim()] = ImageLabels.Parse(labels);
        }

        return images;
    }

    /// <summary>Docker Hub's tags of an image, newest first.</summary>
    internal static IReadOnlyList<string> ParseTags(JsonElement page) =>
        page.GetProperty("results").EnumerateArray()
            .Select(t => (
                Name: t.GetProperty("name").GetString() ?? "",
                Updated: t.TryGetProperty("last_updated", out var u) && u.ValueKind == JsonValueKind.String && u.TryGetDateTime(out var at) ? at : DateTime.MinValue))
            .Where(t => t.Name.Length > 0)
            .OrderByDescending(t => t.Updated)
            .Select(t => t.Name)
            .ToList();

    /// <summary>
    /// The images Docker Hub publishes, the index lists and the host has, as one list. Each is what its
    /// labels say, from the index first and then the host's copy; without labels, its title is from
    /// GitHub or its name, and whether it's a game or a base image from the names known for those.
    /// Images whose kind is neither are left out. Games by title, then the base images.
    /// </summary>
    internal static IReadOnlyList<CatalogImage> Combine(
        IReadOnlyList<PublishedImage> published,
        IReadOnlyDictionary<string, ImageLabels> index,
        IReadOnlyDictionary<string, LocalImage> local,
        IReadOnlyDictionary<string, (string Title, string Description)> gitHub)
    {
        var hub = published.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var names = hub.Keys.Concat(index.Keys).Concat(local.Keys).Distinct(StringComparer.OrdinalIgnoreCase);
        var images = new List<CatalogImage>();

        foreach (var name in names)
        {
            var onHost = local.TryGetValue(name, out var l) ? l : null;
            var labels = (index.TryGetValue(name, out var indexed) ? indexed : ImageLabels.None).Or(onHost?.Labels ?? ImageLabels.None);

            var kind = labels.Kind
                ?? (NotServers.Contains(name) ? ImageKind.Hidden : BaseImages.Contains(name) ? ImageKind.Base : ImageKind.Game);

            if (kind == ImageKind.Hidden)
                continue;

            var fromGitHub = gitHub.TryGetValue(name, out var g) ? g : default;
            var fromHub = hub.TryGetValue(name, out var h) ? h : null;

            images.Add(new CatalogImage(
                $"{Namespace}/{name}",
                name,
                labels.Title ?? fromGitHub.Title ?? name,
                labels.Description ?? FirstOf(fromHub?.Description, fromGitHub.Description?.Trim()),
                fromHub?.Updated,
                kind == ImageKind.Base,
                onHost?.Tags ?? [],
                labels.ExternalIds));
        }

        return images
            .OrderBy(i => i.IsBase)
            .ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static string FirstOf(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

    /// <summary>A repository's name as words: <c>CallOfDuty4</c> is <c>Call Of Duty 4</c>, <c>XIII</c> stays.</summary>
    internal static string TitleOf(string name)
    {
        var title = new StringBuilder();

        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];

            if (c is '.' or '_' or '-')
            {
                title.Append(' ');
                continue;
            }

            if (i > 0 && title.Length > 0 && title[^1] != ' ')
            {
                var previous = name[i - 1];
                var next = i + 1 < name.Length ? name[i + 1] : '\0';

                // A word starts at a capital after a small letter, at a capital before a small letter
                // after capitals (an acronym's end), and where letters and digits meet
                var startsWord =
                    (char.IsUpper(c) && char.IsLower(previous))
                    || (char.IsUpper(c) && char.IsUpper(previous) && char.IsLower(next))
                    || (char.IsDigit(c) && char.IsLetter(previous))
                    || (char.IsLetter(c) && char.IsDigit(previous));

                if (startsWord)
                    title.Append(' ');
            }

            title.Append(c);
        }

        return title.ToString().Trim();
    }

    /// <summary>
    /// Whether the image is for the game. Where both have an id with the same provider, the ids decide;
    /// otherwise the names do, as far as they tell: <c>Deus Ex</c> is <c>deusex</c>, and an image titled
    /// <c>Quake III Arena</c> by its label is that game.
    /// </summary>
    /// <param name="gameIds">The game's ids, by provider as LANCommander names them, e.g. <c>IGDB</c>.</param>
    public static bool IsFor(CatalogImage image, string? gameTitle, IEnumerable<KeyValuePair<string, string>>? gameIds = null)
    {
        // Providers are named as LANCommander names them (IGDB) or as labels do (igdb)
        var imageIds = (image.ExternalIds ?? new Dictionary<string, string>())
            .GroupBy(id => id.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase);

        var shared = (gameIds ?? [])
            .Where(id => imageIds.ContainsKey(id.Key.Trim()))
            .ToList();

        if (shared.Count > 0)
            return shared.Any(id => string.Equals(imageIds[id.Key.Trim()], id.Value.Trim(), StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(gameTitle))
            return false;

        var game = Letters(gameTitle);

        return game.Length > 0 && (game == Letters(image.Name) || game == Letters(image.Title));
    }

    private static string Letters(string text) =>
        new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
