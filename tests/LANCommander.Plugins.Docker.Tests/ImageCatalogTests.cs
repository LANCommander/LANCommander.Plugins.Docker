using System.Net;
using System.Text.Json;
using LANCommander.Plugins.Docker.Hosts;
using LANCommander.Plugins.Docker.Images;
using Microsoft.Extensions.Logging.Abstractions;

namespace LANCommander.Plugins.Docker.Tests;

public sealed class ImageCatalogTests
{
    private const string HubPage = """
        {
          "count": 6, "next": null,
          "results": [
            { "name": "lancommander", "description": "An open-source digital video game distribution system", "last_updated": "2026-11-02T06:14:46Z" },
            { "name": "udprelay", "description": "", "last_updated": "2026-01-21T00:47:28Z" },
            { "name": "quake3", "description": "", "last_updated": "2026-02-21T06:33:42Z" },
            { "name": "zandronum", "description": "A Docker container to host a Zandronum game server ", "last_updated": "2026-01-23T07:12:13Z" },
            { "name": "callofduty4", "description": null, "last_updated": "2026-11-02T23:44:34Z" },
            { "name": "steamcmd", "description": "", "last_updated": "2026-10-30T22:44:31Z" }
          ]
        }
        """;

    private const string GitHubPage = """
        [
          { "name": "LANCommander", "description": "The server" },
          { "name": "LANCommander.Servers.Quake3", "description": "A Docker container to host a Quake 3 game server" },
          { "name": "LANCommander.Servers.CallOfDuty4", "description": null },
          { "name": "LANCommander.Servers.SteamCMD", "description": "A Docker base image to host game servers via SteamCMD" }
        ]
        """;

    private const string IndexPage = """
        {
          "images": [
            { "name": "quake3", "labels": {
                "org.opencontainers.image.title": "Quake III Arena",
                "org.opencontainers.image.description": "Quake III Arena dedicated server",
                "com.lancommander.server.kind": "game",
                "com.lancommander.server.external-id.igdb": "355" } },
            { "repository": "lancommander/callofjuarezboundinblood", "labels": {
                "org.opencontainers.image.title": "Call of Juarez: Bound in Blood",
                "com.lancommander.server.external-id.steam": "21980" } },
            { "name": "zandronum", "labels": { "com.lancommander.server.kind": "tool" } },
            { "labels": { "org.opencontainers.image.title": "No name" } }
          ]
        }
        """;

    private static ImageCatalog Catalog(HttpMessageHandler handler, string indexUrl = "")
    {
        var settings = new MemorySettings<DockerSettings>();

        settings.Current.ImageCatalogUrl = indexUrl;

        return new ImageCatalog(TestHosts.Create(), settings, NullLogger<ImageCatalog>.Instance, new HttpClient(handler));
    }

    private static IReadOnlyList<PublishedImage> Hub()
    {
        using var hub = JsonDocument.Parse(HubPage);

        return ImageCatalog.ParseRepositories(hub.RootElement.GetProperty("results").EnumerateArray().Select(r => r.Clone()).ToList());
    }

    private static IReadOnlyDictionary<string, ImageLabels> Index()
    {
        using var index = JsonDocument.Parse(IndexPage);

        return ImageCatalog.ParseIndex(index.RootElement);
    }

    private static readonly IReadOnlyDictionary<string, ImageLabels> NoIndex = new Dictionary<string, ImageLabels>();
    private static readonly IReadOnlyDictionary<string, ImageCatalog.LocalImage> NothingLocal = new Dictionary<string, ImageCatalog.LocalImage>();
    private static readonly IReadOnlyDictionary<string, (string, string)> NoGitHub = new Dictionary<string, (string, string)>();

    // Labels

    [Fact]
    public void AnImagesLabelsSayWhatItIs()
    {
        var labels = ImageLabels.Parse(new Dictionary<string, string>
        {
            ["org.opencontainers.image.title"] = " Call of Juarez: Bound in Blood ",
            ["org.opencontainers.image.description"] = "Its dedicated server",
            ["com.lancommander.server.kind"] = "Game",
            ["com.lancommander.server.external-id.steam"] = "21980",
            ["com.lancommander.server.external-id.IGDB"] = " 1234 ",
            ["com.lancommander.server.external-id."] = "nameless",
            ["com.lancommander.server.external-id.gog"] = " ",
            ["maintainer"] = "LANCommander",
        });

        Assert.Equal("Call of Juarez: Bound in Blood", labels.Title);
        Assert.Equal("Its dedicated server", labels.Description);
        Assert.Equal(ImageKind.Game, labels.Kind);
        Assert.Equal("21980", labels.ExternalIds["Steam"]);
        Assert.Equal("1234", labels.ExternalIds["igdb"]);
        Assert.Equal(2, labels.ExternalIds.Count);
    }

    [Theory]
    [InlineData("game", ImageKind.Game)]
    [InlineData("BASE", ImageKind.Base)]
    [InlineData("tool", ImageKind.Hidden)]
    [InlineData("relay", ImageKind.Hidden)]
    [InlineData(" ", null)]
    public void AnImagesKindIsAGameABaseOrNeither(string kind, ImageKind? expected)
    {
        Assert.Equal(expected, ImageLabels.Parse(new Dictionary<string, string> { [ImageLabels.KindLabel] = kind }).Kind);
    }

    [Fact]
    public void LabelsAreFilledInFromOthersWhereTheyHaveNone()
    {
        var index = ImageLabels.Parse(new Dictionary<string, string> { [ImageLabels.TitleLabel] = "Quake III Arena" });
        var local = ImageLabels.Parse(new Dictionary<string, string>
        {
            [ImageLabels.TitleLabel] = "Quake 3 (old)",
            [ImageLabels.DescriptionLabel] = "From the host",
            [ImageLabels.KindLabel] = "game",
        });

        var labels = index.Or(local);

        Assert.Equal("Quake III Arena", labels.Title);
        Assert.Equal("From the host", labels.Description);
        Assert.Equal(ImageKind.Game, labels.Kind);
        Assert.Same(ImageLabels.None, ImageLabels.Parse(null));
    }

    [Fact]
    public void TheIndexNamesImagesByNameOrRepository()
    {
        var index = Index();

        Assert.Equal(["quake3", "callofjuarezboundinblood", "zandronum"], index.Keys);
        Assert.Equal("Call of Juarez: Bound in Blood", index["CallOfJuarezBoundInBlood"].Title);
    }

    // Combining what's known

    [Fact]
    public void LabelsInTheIndexComeFirst()
    {
        var local = ImageCatalog.LocalOf([("lancommander/quake3:latest", new Dictionary<string, string> { [ImageLabels.TitleLabel] = "Quake 3 (host)" })]);
        var gitHub = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase) { ["Quake3"] = ("Quake 3", "From GitHub") };

        var quake3 = ImageCatalog.Combine(Hub(), Index(), local, gitHub).Single(i => i.Name == "quake3");

        Assert.Equal("Quake III Arena", quake3.Title);
        Assert.Equal("Quake III Arena dedicated server", quake3.Description);
        Assert.Equal("355", quake3.ExternalIds!["igdb"]);
        Assert.True(quake3.OnHost);
    }

    [Fact]
    public void WithoutAnIndexTheHostsLabelsAreUsed()
    {
        var local = ImageCatalog.LocalOf([("lancommander/quake3:latest", new Dictionary<string, string>
        {
            [ImageLabels.TitleLabel] = "Quake III Arena",
            [ImageLabels.KindLabel] = "game",
        })]);
        var gitHub = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase) { ["Quake3"] = ("Quake 3", "From GitHub") };

        var images = ImageCatalog.Combine(Hub(), NoIndex, local, gitHub);

        Assert.Equal("Quake III Arena", images.Single(i => i.Name == "quake3").Title);

        // Docker Hub's description, then GitHub's, where the labels have none
        Assert.Equal("From GitHub", images.Single(i => i.Name == "quake3").Description);
    }

    [Fact]
    public void WithoutLabelsImagesAreTitledFromGitHubOrTheirNames()
    {
        var gitHub = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["Quake3"] = ("Quake 3", "A Docker container to host a Quake 3 game server"),
            ["CallOfDuty4"] = ("Call Of Duty 4", ""),
        };

        var images = ImageCatalog.Combine(Hub(), NoIndex, NothingLocal, gitHub);

        // Without kind labels, the names known not to be servers are left out, and the base images last
        Assert.Equal(["callofduty4", "quake3", "zandronum", "steamcmd"], images.Select(i => i.Name));
        Assert.Equal("Quake 3", images.Single(i => i.Name == "quake3").Title);
        Assert.Equal("zandronum", images.Single(i => i.Name == "zandronum").Title);
        Assert.Equal("A Docker container to host a Zandronum game server", images.Single(i => i.Name == "zandronum").Description);
        Assert.Equal(new DateTime(2026, 2, 21, 6, 33, 42, DateTimeKind.Utc), images.Single(i => i.Name == "quake3").Updated);
        Assert.True(images.Single(i => i.Name == "steamcmd").IsBase);
    }

    [Fact]
    public void AKindLabelDecidesWhereAnImageIsListed()
    {
        var index = new Dictionary<string, ImageLabels>(StringComparer.OrdinalIgnoreCase)
        {
            // Labelled as a tool, so left out though it's a game by name
            ["zandronum"] = ImageLabels.Parse(new Dictionary<string, string> { [ImageLabels.KindLabel] = "tool" }),
            // Labelled as a game, so listed though LANCommander's own image isn't by name
            ["lancommander"] = ImageLabels.Parse(new Dictionary<string, string> { [ImageLabels.KindLabel] = "game" }),
            // A new base image, listed with the others
            ["proton"] = ImageLabels.Parse(new Dictionary<string, string> { [ImageLabels.KindLabel] = "base", [ImageLabels.TitleLabel] = "Proton" }),
        };

        var images = ImageCatalog.Combine(Hub(), index, NothingLocal, NoGitHub);

        Assert.DoesNotContain(images, i => i.Name == "zandronum");
        Assert.Contains(images, i => i.Name == "lancommander" && !i.IsBase);
        Assert.Contains(images, i => i.Name == "proton" && i.IsBase && i.Updated == null);
    }

    [Fact]
    public void TheHostsImagesAreMarkedAndAddedWhenNotPublished()
    {
        var local = ImageCatalog.LocalOf([
            ("lancommander/quake3:latest", null),
            ("docker.io/lancommander/quake3:1.36", null),
            ("lancommander/mygame:dev", new Dictionary<string, string> { [ImageLabels.TitleLabel] = "My Game" }),
            ("lancommander/lancommander:latest", null),
            ("itzg/minecraft-server:latest", null),
        ]);

        var images = ImageCatalog.Combine(Hub(), NoIndex, local, NoGitHub);

        Assert.Equal(["latest", "1.36"], images.Single(i => i.Name == "quake3").LocalTags);
        Assert.Equal("My Game", images.Single(i => i.Name == "mygame").Title);
        Assert.True(images.Single(i => i.Name == "mygame").OnHost);
        Assert.False(images.Single(i => i.Name == "callofduty4").OnHost);
        Assert.DoesNotContain(images, i => i.Name == "lancommander");
        Assert.DoesNotContain(images, i => i.Name.Contains("minecraft"));
    }

    [Fact]
    public void TheHostsLatestTagHasTheLabelsThatCount()
    {
        var local = ImageCatalog.LocalOf([
            ("lancommander/quake3:1.0", new Dictionary<string, string> { [ImageLabels.TitleLabel] = "Quake 3 (old)" }),
            ("lancommander/quake3:latest", new Dictionary<string, string> { [ImageLabels.TitleLabel] = "Quake III Arena" }),
            ("lancommander/quake3:0.9", new Dictionary<string, string> { [ImageLabels.TitleLabel] = "Quake 3 (older)" }),
        ]);

        Assert.Equal("Quake III Arena", local["quake3"].Labels.Title);
        Assert.Equal(["1.0", "latest", "0.9"], local["quake3"].Tags);
    }

    [Fact]
    public void TagsAreNewestFirst()
    {
        using var page = JsonDocument.Parse("""
            { "results": [
              { "name": "1.0", "last_updated": "2026-01-01T00:00:00Z" },
              { "name": "latest", "last_updated": "2026-03-01T00:00:00Z" },
              { "name": "1.1", "last_updated": "2026-02-01T00:00:00Z" }
            ] }
            """);

        Assert.Equal(["latest", "1.1", "1.0"], ImageCatalog.ParseTags(page.RootElement));
    }

    // Titles from names

    [Theory]
    [InlineData("CallOfDuty4", "Call Of Duty 4")]
    [InlineData("HalfLife2Deathmatch", "Half Life 2 Deathmatch")]
    [InlineData("XIII", "XIII")]
    [InlineData("UT2004", "UT 2004")]
    [InlineData("SteamCMD", "Steam CMD")]
    [InlineData("RTCW", "RTCW")]
    [InlineData("Minimum.Master", "Minimum Master")]
    [InlineData("deusex", "deusex")]
    public void RepositoryNamesBecomeTitles(string name, string title)
    {
        Assert.Equal(title, ImageCatalog.TitleOf(name));
    }

    // Suggesting the game's image

    private static CatalogImage Image(string name, string title, Dictionary<string, string>? ids = null) =>
        new($"lancommander/{name}", name, title, "", null, false, [], ids);

    [Theory]
    [InlineData("Deus Ex", true)]
    [InlineData("deus-ex", true)]
    [InlineData("Call of Duty 4", false)]
    [InlineData("Deus Ex: Invisible War", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void WithoutIdsAnImageIsSuggestedForTheGameItsNamedFor(string? game, bool suggested)
    {
        Assert.Equal(suggested, ImageCatalog.IsFor(Image("deusex", "deusex"), game));
    }

    [Fact]
    public void ATitleLabelMatchesAGameWhoseNameIsntTheImages()
    {
        Assert.True(ImageCatalog.IsFor(Image("quake3", "Quake III Arena"), "Quake III Arena"));
        Assert.False(ImageCatalog.IsFor(Image("quake3", "quake3"), "Quake III Arena"));
    }

    [Fact]
    public void IdsWithTheSameProviderDecide()
    {
        var quake3 = Image("quake3", "Quake III Arena", new() { ["igdb"] = "355" });

        // The same game by IGDB, whatever it's called in LANCommander
        Assert.True(ImageCatalog.IsFor(quake3, "Q3A", new Dictionary<string, string> { ["IGDB"] = "355" }));

        // Another game by IGDB, though the titles are the same
        Assert.False(ImageCatalog.IsFor(quake3, "Quake III Arena", new Dictionary<string, string> { ["IGDB"] = "1234" }));

        // No provider in common: the titles decide
        Assert.True(ImageCatalog.IsFor(quake3, "Quake III Arena", new Dictionary<string, string> { ["Steam"] = "2200" }));
    }

    [Fact]
    public void AnyIdInCommonIsEnough()
    {
        var image = Image("cojbib", "Call of Juarez: Bound in Blood", new() { ["steam"] = "21980", ["igdb"] = "1" });

        Assert.True(ImageCatalog.IsFor(image, null, new Dictionary<string, string> { ["Steam"] = "21980", ["IGDB"] = "2" }));
    }

    // Reading it all

    [Fact]
    public async Task TheCatalogIsReadFromDockerHubAndTitledFromGitHub()
    {
        var handler = new FakeHandler(request => request.RequestUri!.Host switch
        {
            "hub.docker.com" => HubPage,
            "api.github.com" => GitHubPage,
            _ => null,
        });

        var catalog = Catalog(handler);

        // The host isn't one the plugin knows, so only its images are missing
        var result = await catalog.ListAsync(Guid.NewGuid());

        Assert.Equal(["Call Of Duty 4", "Quake 3", "zandronum", "Steam CMD"], result.Images.Select(i => i.Title));
        Assert.Contains("host's images could not be listed", result.Error);

        // Listed again from what was read, rather than asking again
        var requests = handler.Requests.Count;

        await catalog.ListAsync(Guid.NewGuid());

        Assert.Equal(requests, handler.Requests.Count);
        Assert.All(handler.UserAgents, agent => Assert.Contains("LANCommander", agent));
    }

    [Fact]
    public async Task WithAnIndexGitHubIsntAsked()
    {
        var handler = new FakeHandler(request => request.RequestUri!.Host switch
        {
            "hub.docker.com" => HubPage,
            "catalog.example" => IndexPage,
            _ => null,
        });

        var result = await Catalog(handler, "https://catalog.example/images.json").ListAsync(Guid.NewGuid());

        Assert.DoesNotContain(handler.Requests, uri => uri.Host == "api.github.com");
        Assert.Equal("Quake III Arena", result.Images.Single(i => i.Name == "quake3").Title);

        // An image only in the index is listed too
        Assert.Contains(result.Images, i => i.Name == "callofjuarezboundinblood" && i.Title == "Call of Juarez: Bound in Blood");
        Assert.DoesNotContain(result.Images, i => i.Name == "zandronum");
    }

    [Fact]
    public async Task AnIndexThatCantBeReadFallsBackOnGitHub()
    {
        var handler = new FakeHandler(request => request.RequestUri!.Host switch
        {
            "hub.docker.com" => HubPage,
            "api.github.com" => GitHubPage,
            "catalog.example" => "not json",
            _ => null,
        });

        var result = await Catalog(handler, "https://catalog.example/images.json").ListAsync(Guid.NewGuid());

        Assert.Equal("Quake 3", result.Images.Single(i => i.Name == "quake3").Title);
        Assert.Contains("image catalog at https://catalog.example/images.json couldn't be read", result.Error);
    }

    [Fact]
    public async Task WithoutGitHubImagesAreListedByName()
    {
        var result = await Catalog(new FakeHandler(request => request.RequestUri!.Host == "hub.docker.com" ? HubPage : null)).ListAsync(Guid.NewGuid());

        Assert.Contains(result.Images, i => i.Name == "quake3" && i.Title == "quake3");
    }

    [Fact]
    public async Task WithoutDockerHubTheReasonIsGiven()
    {
        var result = await Catalog(new FakeHandler(_ => null)).ListAsync(Guid.NewGuid());

        Assert.Empty(result.Images);
        Assert.Contains("Docker Hub couldn't be reached", result.Error);
    }

    [Fact]
    public async Task AnImageWithoutPublishedTagsIsLatest()
    {
        Assert.Equal(["latest"], await Catalog(new FakeHandler(_ => null)).TagsAsync(Guid.NewGuid(), "quake3"));
    }

    /// <summary>Answers with the JSON given for a request, or fails as an unreachable server does.</summary>
    private sealed class FakeHandler(Func<HttpRequestMessage, string?> answer) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        public List<string> UserAgents { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(request.RequestUri!);
                UserAgents.Add(request.Headers.UserAgent.ToString());
            }

            return answer(request) is { } json
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) })
                : throw new HttpRequestException("No such host is known.");
        }
    }
}
