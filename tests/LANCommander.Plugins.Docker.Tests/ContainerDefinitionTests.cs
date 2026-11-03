using Docker.DotNet.Models;
using LANCommander.Plugins.Docker.Containers;

namespace LANCommander.Plugins.Docker.Tests;

public sealed class ContainerDefinitionTests
{
    private static ContainerInspectResponse Inspected() => new()
    {
        ID = "0123456789abcdef0123456789abcdef",
        Name = "/arena",
        Config = new ContainerConfig
        {
            Image = "example/arena:1.2",
            Hostname = "0123456789ab",
            User = "steam",
            Entrypoint = ["/entry.sh"],
            Cmd = ["+map", "de dust2"],
            Env = ["PATH=/usr/bin", "MAX_PLAYERS=16", "EMPTY="],
            ExposedPorts = new Dictionary<string, EmptyStruct> { ["27015/udp"] = default, ["27020/tcp"] = default },
            OpenStdin = true,
        },
        HostConfig = new HostConfig
        {
            NetworkMode = "games",
            Memory = 1024,
            PortBindings = new Dictionary<string, IList<PortBinding>>
            {
                ["27015/udp"] = [new PortBinding { HostIP = "", HostPort = "27015" }],
            },
            RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.UnlessStopped },
            Mounts = [new Mount { Type = "volume", Source = "named", Target = "/old" }],
        },
        Mounts =
        [
            new MountPoint { Type = "volume", Name = "f00dfeedbeef", Source = "/var/lib/docker/volumes/f00dfeedbeef/_data", Destination = "/data", RW = true },
            new MountPoint { Type = "bind", Source = "/srv/arena/maps", Destination = "/maps", RW = false },
            new MountPoint { Type = "tmpfs", Destination = "/tmp" },
        ],
        State = new State { Running = true },
    };

    [Fact]
    public void AContainerIsReadAsItIsConfigured()
    {
        var spec = ContainerDefinition.From(Inspected());

        Assert.Equal("arena", spec.Name);
        Assert.Equal("example/arena:1.2", spec.Image);
        Assert.Equal("+map 'de dust2'", spec.Command);
        Assert.Equal(["PATH=/usr/bin", "MAX_PLAYERS=16", "EMPTY="], spec.Environment.Select(v => $"{v.Name}={v.Value}"));
        Assert.Equal(["27015:27015/udp", "27020/tcp"], spec.Ports.Select(p => p.ToString()));
        Assert.Equal(["f00dfeedbeef:/data", "/srv/arena/maps:/maps:ro"], spec.Mounts.Select(m => $"{m.Source}:{m.Destination}{(m.ReadOnly ? ":ro" : "")}"));
        Assert.Equal("unless-stopped", spec.RestartPolicy);
        Assert.True(spec.Interactive);
        Assert.False(spec.Terminal);
    }

    [Fact]
    public void RecreatingKeepsWhatTheSpecDoesNotCoverAndEveryVolume()
    {
        var old = Inspected();
        var spec = ContainerDefinition.From(old);

        spec.Environment.Single(v => v.Name == "MAX_PLAYERS").Value = "32";
        spec.Ports.Single(p => p.ContainerPort == 27020).HostPort = 27020;

        var parameters = spec.ToCreateParameters(old);

        Assert.Equal("arena", parameters.Name);
        Assert.Equal("steam", parameters.User);
        Assert.Equal(["/entry.sh"], parameters.Entrypoint);
        Assert.Equal(["+map", "de dust2"], parameters.Cmd);
        Assert.Contains("MAX_PLAYERS=32", parameters.Env);
        Assert.Equal("games", parameters.HostConfig.NetworkMode);
        Assert.Equal(1024, parameters.HostConfig.Memory);

        // The anonymous volume is named, so the new container has the old one's data
        Assert.Equal(["f00dfeedbeef:/data", "/srv/arena/maps:/maps:ro"], parameters.HostConfig.Binds);
        Assert.Null(parameters.HostConfig.Mounts);

        Assert.Equal(["27015/udp", "27020/tcp"], parameters.ExposedPorts.Keys.Order());
        Assert.Equal("27020", parameters.HostConfig.PortBindings["27020/tcp"].Single().HostPort);
        Assert.Equal(RestartPolicyKind.UnlessStopped, parameters.HostConfig.RestartPolicy.Name);

        // Docker made the hostname up from the old container's id
        Assert.Null(parameters.Hostname);
    }

    [Fact]
    public void ANewContainerHasOnlyWhatTheSpecSays()
    {
        var parameters = new ContainerDefinition
        {
            Name = "new-server",
            Image = "busybox",
            Ports = [new PortMapping { ContainerPort = 7777, HostPort = 7777 }],
        }.ToCreateParameters();

        Assert.Equal("busybox", parameters.Image);
        Assert.Null(parameters.Cmd);
        Assert.Null(parameters.User);
        Assert.Empty(parameters.HostConfig.CapAdd);
        Assert.Equal(["7777/udp"], parameters.ExposedPorts.Keys);
        Assert.True(parameters.OpenStdin);
        Assert.Equal(RestartPolicyKind.No, parameters.HostConfig.RestartPolicy.Name);
    }

    /// <summary>The image <see cref="Inspected"/> was created from: the container sets only MAX_PLAYERS and the port binding itself.</summary>
    private static ImageInspectResponse ItsImage() => new()
    {
        ID = "sha256:1111",
        Config = new DockerOCIImageConfig
        {
            User = "steam",
            Entrypoint = ["/entry.sh"],
            Cmd = ["+map", "de dust2"],
            Env = ["PATH=/usr/bin", "MAX_PLAYERS=8", "EMPTY="],
            ExposedPorts = new Dictionary<string, EmptyStruct> { ["27015/udp"] = default, ["27020/tcp"] = default },
            Labels = new Dictionary<string, string> { ["org.opencontainers.image.title"] = "Arena" },
        },
    };

    [Fact]
    public void WhatTheContainerOnlyHasFromItsImageIsLeftOut()
    {
        var spec = ContainerDefinition.From(Inspected(), ItsImage());

        Assert.Equal("", spec.Command);
        Assert.Equal("", spec.User);
        Assert.Equal(["MAX_PLAYERS=16"], spec.Environment.Select(v => $"{v.Name}={v.Value}"));

        // and is there to show beside it
        Assert.NotNull(spec.Defaults);
        Assert.Equal("+map 'de dust2'", spec.Defaults.Command);
        Assert.Equal("steam", spec.Defaults.User);
        Assert.Contains(spec.Defaults.Environment, v => v.Name == "MAX_PLAYERS" && v.Value == "8");
    }

    [Fact]
    public void ARecreatedContainerTakesWhatItDoesntSetFromItsNewImage()
    {
        var old = Inspected();
        var image = ItsImage();

        old.Config.WorkingDir = "/srv";
        old.Config.Labels = new Dictionary<string, string> { ["org.opencontainers.image.title"] = "Arena", ["com.example.owner"] = "lan" };
        image.Config.WorkingDir = "/srv";

        var parameters = ContainerDefinition.From(old, image).ToCreateParameters(old, image);

        Assert.Null(parameters.Entrypoint);
        Assert.Null(parameters.Cmd);
        Assert.Null(parameters.User);
        Assert.Null(parameters.WorkingDir);
        Assert.Equal(["MAX_PLAYERS=16"], parameters.Env);
        Assert.Equal(["com.example.owner"], parameters.Labels.Keys);

        // What isn't the image's is still kept
        Assert.Equal("games", parameters.HostConfig.NetworkMode);
        Assert.Equal("27015", parameters.HostConfig.PortBindings["27015/udp"].Single().HostPort);
    }

    [Fact]
    public void ACommandIsKeptWithAnEntryPointOfTheContainersOwn()
    {
        var old = Inspected();
        var image = ItsImage();

        // Docker drops the image's command for a container with its own entry point; a container's
        // command matching the image's is then its own
        old.Config.Entrypoint = ["/custom.sh"];

        var spec = ContainerDefinition.From(old, image);
        var parameters = spec.ToCreateParameters(old, image);

        Assert.Equal("+map 'de dust2'", spec.Command);
        Assert.Equal(["/custom.sh"], parameters.Entrypoint);
        Assert.Equal(["+map", "de dust2"], parameters.Cmd);
    }

    [Fact]
    public void AUserOtherThanTheImagesIsKept()
    {
        var old = Inspected();

        old.Config.User = "root";

        var spec = ContainerDefinition.From(old, ItsImage());

        Assert.Equal("root", spec.User);
        Assert.Equal("root", spec.ToCreateParameters(old, ItsImage()).User);
    }

    [Fact]
    public void PrivilegesAreReadAndKept()
    {
        var old = Inspected();

        old.HostConfig.CapAdd = ["SYS_ADMIN", "NET_ADMIN"];
        old.HostConfig.SecurityOpt = ["apparmor=unconfined"];

        var spec = ContainerDefinition.From(old);

        Assert.True(spec.CanMountOverlay);
        Assert.Equal(["NET_ADMIN"], spec.OtherPrivileges);

        var parameters = spec.ToCreateParameters(old);

        Assert.Equal(["SYS_ADMIN", "NET_ADMIN"], parameters.HostConfig.CapAdd);
        Assert.Equal(["apparmor=unconfined"], parameters.HostConfig.SecurityOpt);

        spec.CanMountOverlay = false;

        parameters = spec.ToCreateParameters(old);

        Assert.Equal(["NET_ADMIN"], parameters.HostConfig.CapAdd);
        Assert.Empty(parameters.HostConfig.SecurityOpt);
    }

    [Fact]
    public void HalfOfTheOverlaysPrivilegesIsNotEnough()
    {
        var spec = new ContainerDefinition { Capabilities = ["SYS_ADMIN"] };

        Assert.False(spec.CanMountOverlay);
        Assert.Equal(["SYS_ADMIN"], spec.OtherPrivileges);

        spec.CanMountOverlay = true;

        Assert.Equal(["SYS_ADMIN"], spec.Capabilities);
        Assert.Equal(["apparmor:unconfined"], spec.SecurityOptions);
    }

    [Fact]
    public void ANewServerImageContainerCanMountItsOverlay()
    {
        var spec = new ContainerDefinition { Name = "quake3", Image = "lancommander/quake3:latest", User = "root" };

        spec.CanMountOverlay = true;

        var parameters = spec.ToCreateParameters();

        Assert.Equal("root", parameters.User);
        Assert.Equal(["SYS_ADMIN"], parameters.HostConfig.CapAdd);
        Assert.Equal(["apparmor:unconfined"], parameters.HostConfig.SecurityOpt);
    }

    [Theory]
    [InlineData("lancommander/deusex:latest", true)]
    [InlineData("docker.io/lancommander/quake3", true)]
    [InlineData("itzg/minecraft-server", false)]
    public void LANCommandersServerImagesAreRecognized(string image, bool expected)
    {
        Assert.Equal(expected, LANCommander.Plugins.Docker.Components.DockerServerEditor.IsServerImage(image));
    }

    [Theory]
    [InlineData("", "busybox", "needs a name")]
    [InlineData("-bad", "busybox", "must start with a letter or digit")]
    [InlineData("has space", "busybox", "can only have")]
    [InlineData("ok", " ", "needs an image")]
    public void ASpecThatCannotBeCreatedIsRefused(string name, string image, string message)
    {
        var error = Assert.Throws<ArgumentException>(() => new ContainerDefinition { Name = name, Image = image }.Validate());

        Assert.Contains(message, error.Message);
    }

    [Fact]
    public void APortOutOfRangeIsRefused()
    {
        var spec = new ContainerDefinition { Name = "ok", Image = "busybox", Ports = [new PortMapping { ContainerPort = 70000 }] };

        Assert.Throws<ArgumentException>(spec.Validate);
    }

    public static TheoryData<string, string[]> CommandLines => new()
    {
        { "a b  c", ["a", "b", "c"] },
        { "'a b' \"c \\\"d\\\"\" e\\ f", ["a b", "c \"d\"", "e f"] },
        { "''", [""] },
        { "'it'\\''s'", ["it's"] },
    };

    [Theory]
    [MemberData(nameof(CommandLines))]
    public void CommandLinesSplitAsAShellWould(string commandLine, string[] arguments)
    {
        Assert.Equal(arguments, ShellWords.Split(commandLine));
    }

    public static TheoryData<string[]> Arguments => new()
    {
        new[] { "+map", "de dust2", "it's", "" },
        new[] { "--port=27015", "/data/config.yml" },
    };

    [Theory]
    [MemberData(nameof(Arguments))]
    public void JoinedCommandLinesSplitBackIntoTheSameArguments(string[] arguments)
    {
        Assert.Equal(arguments, ShellWords.Split(ShellWords.Join(arguments)));
    }

    [Fact]
    public void AnUnclosedQuoteIsRefused()
    {
        Assert.Throws<ArgumentException>(() => ShellWords.Split("echo 'oops"));
    }

    [Theory]
    [InlineData("busybox", "busybox", "latest")]
    [InlineData("itzg/minecraft-server:java21", "itzg/minecraft-server", "java21")]
    [InlineData("registry:5000/game", "registry:5000/game", "latest")]
    [InlineData("registry:5000/game:1.0", "registry:5000/game", "1.0")]
    [InlineData("game@sha256:abc", "game@sha256:abc", null)]
    public void ImagesSplitIntoRepositoryAndTag(string image, string name, string? tag)
    {
        Assert.Equal((name, tag), ContainerService.SplitImage(image));
    }
}
