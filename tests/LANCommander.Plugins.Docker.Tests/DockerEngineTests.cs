using Docker.DotNet;
using Docker.DotNet.Models;
using LANCommander.Plugins.Docker.Containers;
using LANCommander.Plugins.Docker.Engine;
using LANCommander.Plugins.Docker.Hosts;
using LANCommander.Server.Plugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LANCommander.Plugins.Docker.Tests;

/// <summary>A test that needs the local Docker daemon, and the busybox image; skipped without them.</summary>
public sealed class DockerFactAttribute : FactAttribute
{
    private static readonly Lazy<string?> Unavailable = new(() =>
    {
        try
        {
            using var client = new DockerClientConfiguration(new Uri(DockerHost.DefaultAddress)).CreateClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            client.System.PingAsync(timeout.Token).GetAwaiter().GetResult();
            client.Images.InspectImageAsync(DockerEngineTests.Image, timeout.Token).GetAwaiter().GetResult();

            return null;
        }
        catch (Exception ex)
        {
            return $"Needs the local Docker daemon with the {DockerEngineTests.Image} image: {ex.Message}";
        }
    });

    public DockerFactAttribute()
    {
        Skip = Unavailable.Value;
    }
}

/// <summary>The engine against real containers on the local Docker daemon.</summary>
public sealed class DockerEngineTests : IAsyncLifetime
{
    public const string Image = "busybox:latest";

    private readonly DockerHost _host = new() { Name = "Local", Address = DockerHost.DefaultAddress };
    private readonly DockerHosts _hosts;
    private readonly ContainerService _containers;
    private readonly DockerServerEngine _engine;
    private readonly RecordingEngineHost _engineHost = new();
    private readonly List<string> _created = [];
    private readonly string _name = $"lancommander-test-{Guid.NewGuid():N}";

    public DockerEngineTests()
    {
        ContainerWatcher.IdleInterval = TimeSpan.FromMilliseconds(250);

        _hosts = TestHosts.Create(_host);
        _containers = new ContainerService(_hosts, NullLogger<ContainerService>.Instance);
        _engine = new DockerServerEngine(_hosts, new ConfigurationBuilder().Build(), NullLogger<DockerServerEngine>.Instance);
    }

    public Task InitializeAsync() => _engine.InitializeAsync(_engineHost, CancellationToken.None);

    public async Task DisposeAsync()
    {
        await _engine.DisposeAsync();

        var client = _hosts.GetClient(_host.Id);

        foreach (var container in await client.Containers.ListContainersAsync(new ContainersListParameters { All = true }))
        {
            if (container.Names.Any(name => name.TrimStart('/').StartsWith(_name, StringComparison.Ordinal)))
                await client.Containers.RemoveContainerAsync(container.ID, new ContainerRemoveParameters { Force = true, RemoveVolumes = true });
        }

        _hosts.Dispose();
    }

    private async Task<ServerEngineServer> AddServerAsync(string command, string name = "")
    {
        name = _name + name;

        await _containers.CreateAsync(_host.Id, new ContainerDefinition { Name = name, Image = Image, Command = command, Interactive = true });

        var server = new ServerEngineServer
        {
            Id = Guid.NewGuid(),
            Name = name,
            Settings = ServerEngineSettings.Write(new DockerServerSettings { HostId = _host.Id, ContainerId = name }),
        };

        await _engine.ServersChangedAsync([server], CancellationToken.None);
        await _engine.WatchingAsync(server.Id).WaitAsync(TimeSpan.FromSeconds(10));

        return server;
    }

    private bool HasOutput(ServerEngineServer server, string line) =>
        _engineHost.Output.Any(o => o.ServerId == server.Id && o.Line == line);

    [DockerFact]
    public async Task AContainerIsStartedFollowedTalkedToAndStopped()
    {
        var server = await AddServerAsync("sh -c 'trap \"exit 0\" TERM; echo ready; while read line; do echo \"got $line\"; done'");

        Assert.Equal(ServerEngineStatus.Stopped, await _engine.GetStatusAsync(server, CancellationToken.None));

        await _engine.StartAsync(server, CancellationToken.None);

        await TestHosts.WaitUntilAsync(() => HasOutput(server, "ready"), "the container's output");

        Assert.Equal(ServerEngineStatus.Running, await _engine.GetStatusAsync(server, CancellationToken.None));

        var sample = await _engine.GetProcessInfoAsync(server, CancellationToken.None);

        Assert.NotNull(sample);
        Assert.True(sample.MemoryBytes > 0);
        Assert.NotNull(sample.StartTime);

        await TestHosts.WaitUntilAsync(() => _engine.CanSendInput(server), "the container to take input");

        Assert.True(await _engine.SendInputAsync(server, "hello", CancellationToken.None));

        await TestHosts.WaitUntilAsync(() => HasOutput(server, "got hello"), "the container to answer");

        await _engine.StopAsync(server, CancellationToken.None);

        Assert.Equal(ServerEngineStatus.Stopped, await _engine.GetStatusAsync(server, CancellationToken.None));

        await TestHosts.WaitUntilAsync(() => _engineHost.Statuses.Any(s => s.ServerId == server.Id && s.Status == ServerEngineStatus.Stopped), "the stop to be reported");

        Assert.False(_engine.CanSendInput(server));
    }

    [DockerFact]
    public async Task AContainersLogIsReadBackWithItsTimes()
    {
        var server = await AddServerAsync("sh -c 'echo first; echo second >&2; sleep 600'");

        await _engine.StartAsync(server, CancellationToken.None);
        await TestHosts.WaitUntilAsync(() => HasOutput(server, "second"), "the container's output");

        var lines = new List<ServerEngineLogLine>();

        await foreach (var line in _engine.ReadLogAsync(server, ContainerConsoles.LogId, new ServerEngineLogRequest(Follow: false), CancellationToken.None))
            lines.Add(line);

        // Docker copies a container's output and error output separately, so lines written to each a
        // moment apart can be logged in either order
        Assert.Equal(["first", "second"], lines.Select(l => l.Text).Order());
        Assert.True(lines.Single(l => l.Text == "second").IsError);
        Assert.False(lines.Single(l => l.Text == "first").IsError);
        Assert.All(lines, l => Assert.NotNull(l.Timestamp));
    }

    [DockerFact]
    public async Task ALogFileIsFollowedWhileRunningAndReadWhenStopped()
    {
        var server = await AddServerAsync("sh -c 'mkdir -p /srv; echo first > /srv/server.log; echo second >> /srv/server.log; sleep 600'");
        var console = ContainerConsoles.FilePrefix + "/srv/server.log";

        server = server with { Settings = ServerEngineSettings.Write(new DockerServerSettings { HostId = _host.Id, ContainerId = _name, LogFiles = [new ContainerLogFile { Name = "Server", Path = "/srv/server.log" }] }) };

        await _engine.StartAsync(server, CancellationToken.None);

        var client = _hosts.GetClient(_host.Id);

        // Followed: the end of the file, then what's written after
        using (var following = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            var lines = new List<string>();

            await foreach (var line in _engine.ReadLogAsync(server, console, new ServerEngineLogRequest(Follow: true, Tail: 1), following.Token))
            {
                lines.Add(line.Text);

                if (lines.Count == 1)
                {
                    var exec = await client.Exec.CreateContainerExecAsync(_name, new ContainerExecCreateParameters { Cmd = ["sh", "-c", "echo third >> /srv/server.log"] }, CancellationToken.None);

                    await client.Exec.StartContainerExecAsync(exec.ID, new ContainerExecStartParameters(), CancellationToken.None);
                }
                else
                {
                    break;
                }
            }

            Assert.Equal(["second", "third"], lines);
        }

        // Closing the console ends the tail following it
        await TestHosts.WaitUntilAsync(() => !ProcessesAsync(client).GetAwaiter().GetResult().Contains("tail"), "the tail to end");

        // Stopped, it's copied out as it was left
        await _engine.StopAsync(server, CancellationToken.None);

        var stopped = new List<ServerEngineLogLine>();

        await foreach (var line in _engine.ReadLogAsync(server, console, new ServerEngineLogRequest(Follow: true, Tail: 2), CancellationToken.None))
            stopped.Add(line);

        Assert.Equal(["second", "third"], stopped.Select(l => l.Text));
    }

    /// <summary>What the test container is running, as busybox's ps lists it.</summary>
    private async Task<string> ProcessesAsync(DockerClient client)
    {
        var exec = await client.Exec.CreateContainerExecAsync(_name, new ContainerExecCreateParameters { Cmd = ["ps"], AttachStdout = true }, CancellationToken.None);

        using var stream = await client.Exec.StartContainerExecAsync(exec.ID, new ContainerExecStartParameters(), CancellationToken.None);

        return (await stream.ReadOutputToEndAsync(CancellationToken.None)).stdout;
    }

    [DockerFact]
    public async Task AShellIsOpenedInTheRunningContainer()
    {
        var server = await AddServerAsync("sleep 600");

        await Assert.ThrowsAsync<DockerHostException>(() => _engine.OpenTerminalAsync(server, ContainerConsoles.ShellId, new(80, 24), CancellationToken.None));

        await _engine.StartAsync(server, CancellationToken.None);

        await using var shell = await _engine.OpenTerminalAsync(server, ContainerConsoles.ShellId, new(80, 24), CancellationToken.None);
        var output = Collect(shell);

        await shell.WriteAsync("echo $((6 * 7))\r"u8.ToArray(), CancellationToken.None);

        await TestHosts.WaitUntilAsync(() => output.ToString().Contains("\n42"), "the shell to answer");
    }

    [DockerFact]
    public async Task AttachingJoinsTheServersOwnConsole()
    {
        var server = await AddServerAsync("sh -c 'while read line; do echo \"got $line\"; done'");

        await _engine.StartAsync(server, CancellationToken.None);

        await using var console = await _engine.OpenTerminalAsync(server, ContainerConsoles.AttachId, new(80, 24), CancellationToken.None);
        var output = Collect(console);

        await console.WriteAsync("hello\r"u8.ToArray(), CancellationToken.None);

        // Echoed as typed, and answered on a line of its own
        await TestHosts.WaitUntilAsync(() => output.ToString().Contains("hello\r\ngot hello\r\n"), "the server to answer");
    }

    /// <summary>Everything a terminal writes, as it writes it, until it ends.</summary>
    private static TerminalOutput Collect(IServerEngineTerminal terminal)
    {
        var output = new TerminalOutput();

        _ = Task.Run(async () =>
        {
            var buffer = new byte[1024];
            var decoder = System.Text.Encoding.UTF8.GetDecoder();

            try
            {
                int read;

                while ((read = await terminal.ReadAsync(buffer, CancellationToken.None)) > 0)
                {
                    var chars = new char[decoder.GetCharCount(buffer, 0, read)];

                    decoder.GetChars(buffer, 0, read, chars, 0);
                    output.Append(chars);
                }
            }
            catch (Exception)
            {
                // The test has finished with it
            }
        });

        return output;
    }

    private sealed class TerminalOutput
    {
        private readonly System.Text.StringBuilder _text = new();

        public void Append(char[] chars)
        {
            lock (_text)
                _text.Append(chars);
        }

        public override string ToString()
        {
            lock (_text)
                return _text.ToString();
        }
    }

    [DockerFact]
    public async Task AContainerThatFailsOnItsOwnIsReportedAsAnError()
    {
        var server = await AddServerAsync("sh -c 'echo giving up; exit 3'");

        await _engine.StartAsync(server, CancellationToken.None);

        await TestHosts.WaitUntilAsync(() => _engineHost.Statuses.Any(s => s.ServerId == server.Id && s.Status == ServerEngineStatus.Error), "the failure to be reported");

        Assert.Contains("code 3", _engineHost.Statuses.First(s => s.Status == ServerEngineStatus.Error).Error);
        Assert.Equal(ServerEngineStatus.Error, await _engine.GetStatusAsync(server, CancellationToken.None));
        Assert.True(HasOutput(server, "giving up"));
    }

    [DockerFact]
    public async Task AServerWhoseContainerIsGoneCannotBeStarted()
    {
        var server = new ServerEngineServer
        {
            Id = Guid.NewGuid(),
            Settings = ServerEngineSettings.Write(new DockerServerSettings { HostId = _host.Id, ContainerId = _name + "-missing" }),
        };

        var error = await Assert.ThrowsAsync<DockerHostException>(() => _engine.StartAsync(server, CancellationToken.None));

        Assert.Contains("doesn't exist", error.Message);
        Assert.Equal(ServerEngineStatus.Error, await _engine.GetStatusAsync(server, CancellationToken.None));
    }

    [DockerFact]
    public async Task AServerNotSetUpYetIsStopped()
    {
        Assert.Equal(ServerEngineStatus.Stopped, await _engine.GetStatusAsync(new ServerEngineServer { Id = Guid.NewGuid() }, CancellationToken.None));
    }

    [DockerFact]
    public async Task ReplacingAContainerKeepsItsDataAndRunsItAgain()
    {
        var client = _hosts.GetClient(_host.Id);

        // A volume Docker names itself, as an image's VOLUME gets
        await client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Name = _name,
            Image = Image,
            Cmd = ["sh", "-c", "[ -f /data/first ] || echo $GREETING > /data/first; sleep 600"],
            Env = ["GREETING=hello"],
            Volumes = new Dictionary<string, EmptyStruct> { ["/data"] = default },
        });
        await client.Containers.StartContainerAsync(_name, new ContainerStartParameters());

        var old = await client.Containers.InspectContainerAsync(_name);
        var volume = Assert.Single(old.Mounts).Name;

        var spec = ContainerDefinition.From(old);

        spec.Environment.Single(v => v.Name == "GREETING").Value = "changed";
        spec.Ports.Add(new PortMapping { ContainerPort = 7777, Protocol = "udp" });

        var messages = new List<string>();
        var name = await _containers.RecreateAsync(_host.Id, _name, spec, new SynchronousProgress(messages));

        Assert.Equal(_name, name);

        var replaced = await client.Containers.InspectContainerAsync(_name);

        Assert.NotEqual(old.ID, replaced.ID);
        Assert.True(replaced.State.Running);
        Assert.Contains("GREETING=changed", replaced.Config.Env);
        Assert.Contains("7777/udp", replaced.Config.ExposedPorts.Keys);
        Assert.Equal(volume, Assert.Single(replaced.Mounts).Name);

        // The file the old container wrote is still there
        var exec = await client.Exec.CreateContainerExecAsync(replaced.ID, new ContainerExecCreateParameters { Cmd = ["cat", "/data/first"], AttachStdout = true }, CancellationToken.None);

        using (var stream = await client.Exec.StartContainerExecAsync(exec.ID, new ContainerExecStartParameters(), CancellationToken.None))
        {
            var (stdout, _) = await stream.ReadOutputToEndAsync(CancellationToken.None);

            Assert.Equal("hello", stdout.Trim());
        }

        // and the old container is gone
        var all = await client.Containers.ListContainersAsync(new ContainersListParameters { All = true });

        Assert.DoesNotContain(all, c => c.ID == old.ID);
        Assert.Contains(messages, m => m.StartsWith("Stopping", StringComparison.Ordinal));
    }

    [DockerFact]
    public async Task AReplacedContainerKeepsOnlyItsOwnSettingsAndItsPrivileges()
    {
        var client = _hosts.GetClient(_host.Id);

        await _containers.CreateAsync(_host.Id, new ContainerDefinition
        {
            Name = _name,
            Image = Image,
            Environment = [new EnvironmentVariable { Name = "GREETING", Value = "hello" }],
            Capabilities = ["NET_ADMIN"],
        });

        var spec = await _containers.DefinitionOfAsync(_host.Id, await client.Containers.InspectContainerAsync(_name));

        // busybox's PATH and command are its own, not the container's
        Assert.Equal(["GREETING=hello"], spec.Environment.Select(v => $"{v.Name}={v.Value}"));
        Assert.Equal("", spec.Command);
        Assert.Contains(spec.Defaults!.Environment, v => v.Name == "PATH");

        await _containers.RecreateAsync(_host.Id, _name, spec);

        var replaced = await client.Containers.InspectContainerAsync(_name);

        Assert.Contains("GREETING=hello", replaced.Config.Env);
        Assert.Contains(replaced.Config.Env, v => v.StartsWith("PATH=", StringComparison.Ordinal));
        Assert.Equal(["NET_ADMIN"], replaced.HostConfig.CapAdd);
    }

    [DockerFact]
    public async Task GameFilesAreUpdatedWithEveryServerSharingThemStopped()
    {
        var client = _hosts.GetClient(_host.Id);
        var gameFiles = _name + "-base";

        // Two servers sharing game files; busybox has no install step, so the one-off container is
        // told what to do with its command, which writes a file to show it ran with the files mounted
        foreach (var server in new[] { "-ctf", "-dm" })
        {
            await _containers.CreateAsync(_host.Id, new ContainerDefinition
            {
                Name = _name + server,
                Image = Image,
                Command = "sh -c '[ \"$START_EXE\" = true ] && date > /config/Server/updated || sleep 600'",
                Mounts = [new VolumeMount { Source = gameFiles, Destination = "/config/Server" }],
            });
        }

        await client.Containers.StartContainerAsync(_name + "-ctf", new ContainerStartParameters());

        var messages = new List<string>();
        var update = await _containers.UpdateGameFilesAsync(_host.Id, _name + "-dm", new SynchronousProgress(messages));

        Assert.Equal([_name + "-ctf", _name + "-dm"], update.Sharing);
        Assert.Equal([_name + "-ctf"], update.Restarted);
        Assert.Contains(messages, m => m.StartsWith("Stopping", StringComparison.Ordinal));

        // The one that was running is again, and the update reached the shared files
        Assert.True((await client.Containers.InspectContainerAsync(_name + "-ctf")).State.Running);
        Assert.False((await client.Containers.InspectContainerAsync(_name + "-dm")).State.Running);

        var exec = await client.Exec.CreateContainerExecAsync(_name + "-ctf", new ContainerExecCreateParameters { Cmd = ["cat", "/config/Server/updated"], AttachStdout = true }, CancellationToken.None);

        using (var stream = await client.Exec.StartContainerExecAsync(exec.ID, new ContainerExecStartParameters(), CancellationToken.None))
        {
            var (stdout, _) = await stream.ReadOutputToEndAsync(CancellationToken.None);

            Assert.False(string.IsNullOrWhiteSpace(stdout));
        }

        // and the one-off container is gone
        var all = await client.Containers.ListContainersAsync(new ContainersListParameters { All = true });

        Assert.DoesNotContain(all, c => c.Names.Any(n => n.Contains("-update-", StringComparison.Ordinal)));

        // A named volume outlives its containers
        foreach (var server in new[] { "-ctf", "-dm" })
            await client.Containers.RemoveContainerAsync(_name + server, new ContainerRemoveParameters { Force = true });

        await client.Volumes.RemoveAsync(gameFiles, force: true);
    }

    [DockerFact]
    public async Task AZipOfGameFilesIsCopiedInWithTheServersSharingThemStopped()
    {
        var client = _hosts.GetClient(_host.Id);
        var gameFiles = _name + "-base";

        foreach (var server in new[] { "-ctf", "-dm" })
        {
            await _containers.CreateAsync(_host.Id, new ContainerDefinition
            {
                Name = _name + server,
                Image = Image,
                Command = "sleep 600",
                Mounts = [new VolumeMount { Source = gameFiles, Destination = "/config/Server" }],
            });
        }

        await client.Containers.StartContainerAsync(_name + "-ctf", new ContainerStartParameters());

        // A game zipped with its folder, as it usually is
        using var scratch = new ScratchDirectory();
        Directory.CreateDirectory(scratch.Path);
        var zipPath = Path.Combine(scratch.Path, "deusex.zip");

        using (var zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("DeusEx/System/DeusEx.exe").Open());
            writer.Write("MZ game");
        }

        var upload = await _containers.UploadGameFilesAsync(_host.Id, _name + "-dm", zipPath, stripTopFolder: true);

        Assert.Equal(1, upload.Files);
        Assert.Equal("DeusEx", upload.StrippedFolder);
        Assert.Equal([_name + "-ctf"], upload.Restarted);

        // The one that was running is again, and sees the game where the images look for it
        Assert.True((await client.Containers.InspectContainerAsync(_name + "-ctf")).State.Running);

        var exec = await client.Exec.CreateContainerExecAsync(_name + "-ctf", new ContainerExecCreateParameters { Cmd = ["cat", "/config/Server/System/DeusEx.exe"], AttachStdout = true }, CancellationToken.None);

        using (var stream = await client.Exec.StartContainerExecAsync(exec.ID, new ContainerExecStartParameters(), CancellationToken.None))
        {
            var (stdout, _) = await stream.ReadOutputToEndAsync(CancellationToken.None);

            Assert.Equal("MZ game", stdout);
        }

        var all = await client.Containers.ListContainersAsync(new ContainersListParameters { All = true });

        Assert.DoesNotContain(all, c => c.Names.Any(n => n.Contains("-upload-", StringComparison.Ordinal)));

        foreach (var server in new[] { "-ctf", "-dm" })
            await client.Containers.RemoveContainerAsync(_name + server, new ContainerRemoveParameters { Force = true });

        await client.Volumes.RemoveAsync(gameFiles, force: true);
    }

    [DockerFact]
    public async Task AContainerThatCannotBeReplacedIsPutBack()
    {
        var server = await AddServerAsync("sleep 600");
        var client = _hosts.GetClient(_host.Id);

        await client.Containers.StartContainerAsync(_name, new ContainerStartParameters());

        var old = await client.Containers.InspectContainerAsync(_name);
        var spec = ContainerDefinition.From(old);

        // Docker refuses a volume mounted twice at the same place
        spec.Mounts.Add(new VolumeMount { Source = "one", Destination = "/same" });
        spec.Mounts.Add(new VolumeMount { Source = "two", Destination = "/same" });

        await Assert.ThrowsAnyAsync<DockerApiException>(() => _containers.RecreateAsync(_host.Id, _name, spec));

        var after = await client.Containers.InspectContainerAsync(_name);

        Assert.Equal(old.ID, after.ID);
        Assert.True(after.State.Running);
        Assert.Equal(ServerEngineStatus.Running, await _engine.GetStatusAsync(server, CancellationToken.None));
    }

    [DockerFact]
    public async Task AServersFilesAreWrittenListedReadAndDeletedInItsContainer()
    {
        var server = await AddServerAsync("sleep 600");

        await _hosts.GetClient(_host.Id).Containers.StartContainerAsync(_name, new ContainerStartParameters());

        server = server with { Settings = ServerEngineSettings.Write(new DockerServerSettings { HostId = _host.Id, ContainerId = _name, FilesPath = "/srv/game" }) };

        // Folders it is in are made
        await _engine.WriteFileAsync(server, "cfg/server.cfg", new MemoryStream("hostname \"LAN\"\n"u8.ToArray()), CancellationToken.None);
        await _engine.CreateDirectoryAsync(server, "maps", CancellationToken.None);

        var top = await _engine.ListFilesAsync(server, "", CancellationToken.None);

        Assert.Equal(["cfg", "maps"], top.Select(f => f.Name).Order());
        Assert.All(top, f => Assert.True(f.IsDirectory));

        var config = Assert.Single(await _engine.ListFilesAsync(server, "cfg", CancellationToken.None));

        Assert.Equal(new ServerEngineFile("server.cfg", false, 15, config.LastModified), config);
        Assert.InRange(config.LastModified!.Value, DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddMinutes(5));

        await using (var stream = await _engine.OpenFileAsync(server, "cfg/server.cfg", CancellationToken.None))
            Assert.Equal("hostname \"LAN\"\n", await new StreamReader(stream).ReadToEndAsync());

        // Replacing a file writes it in place
        await _engine.WriteFileAsync(server, "cfg/server.cfg", new MemoryStream("hostname Arena\n"u8.ToArray()), CancellationToken.None);

        await using (var stream = await _engine.OpenFileAsync(server, "cfg/server.cfg", CancellationToken.None))
            Assert.Equal("hostname Arena\n", await new StreamReader(stream).ReadToEndAsync());

        await _engine.DeleteFileAsync(server, "cfg", CancellationToken.None);

        Assert.Equal("maps", Assert.Single(await _engine.ListFilesAsync(server, "", CancellationToken.None)).Name);

        await Assert.ThrowsAsync<FileNotFoundException>(() => _engine.OpenFileAsync(server, "cfg/server.cfg", CancellationToken.None));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => _engine.ListFilesAsync(server, "cfg", CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(() => _engine.OpenFileAsync(server, "maps", CancellationToken.None));
    }

    [DockerFact]
    public async Task AStoppedContainersFilesCantBeManaged()
    {
        var server = await AddServerAsync("sleep 600");

        var ex = await Assert.ThrowsAsync<DockerHostException>(() => _engine.ListFilesAsync(server, "", CancellationToken.None));

        Assert.Contains("isn't running", ex.Message);
    }

    [DockerFact]
    public async Task ABinaryFileIsReadBackByteForByte()
    {
        var server = await AddServerAsync("sleep 600");

        await _hosts.GetClient(_host.Id).Containers.StartContainerAsync(_name, new ContainerStartParameters());

        var bytes = new byte[300_000];
        new Random(7).NextBytes(bytes);

        await _engine.WriteFileAsync(server, "tmp/map.bsp", new MemoryStream(bytes), CancellationToken.None);

        using var read = new MemoryStream();

        await using (var stream = await _engine.OpenFileAsync(server, "tmp/map.bsp", CancellationToken.None))
            await stream.CopyToAsync(read);

        Assert.Equal(bytes, read.ToArray());
    }

    private sealed class SynchronousProgress(List<string> messages) : IProgress<string>
    {
        public void Report(string value)
        {
            lock (messages)
                messages.Add(value);
        }
    }
}
