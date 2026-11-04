using System.IO.Compression;
using System.IO.Pipelines;
using System.Net;
using Docker.DotNet;
using Docker.DotNet.Models;
using LANCommander.Plugins.Docker.Hosts;
using Microsoft.Extensions.Logging;

namespace LANCommander.Plugins.Docker.Containers;

/// <summary>A container as listed on a host.</summary>
public sealed record ContainerSummary(string Id, string Name, string Image, string State, string Status);

/// <summary>A network on a host, and the driver that says what joining it means.</summary>
public sealed record NetworkSummary(string Name, string Driver);

/// <summary>What updating a game's shared files did.</summary>
/// <param name="Sharing">The containers that share the files, the updated one among them.</param>
/// <param name="Restarted">Those that were running, stopped for the update and started again.</param>
/// <param name="Log">
/// The end of the update's log. The images' install steps report a failure there rather than by
/// failing, so it's the only word of one.
/// </param>
public sealed record GameFilesUpdate(IReadOnlyList<string> Sharing, IReadOnlyList<string> Restarted, IReadOnlyList<string> Log);

/// <summary>What uploading a ZIP of game files did.</summary>
/// <param name="Sharing">The containers that share the files, the one uploaded to among them.</param>
/// <param name="Restarted">Those that were running, stopped for the upload and started again.</param>
/// <param name="Files">How many files were copied.</param>
/// <param name="StrippedFolder">The folder the ZIP had everything in, whose contents were copied rather than it; null if none.</param>
public sealed record GameFilesUpload(IReadOnlyList<string> Sharing, IReadOnlyList<string> Restarted, int Files, string? StrippedFolder);

/// <summary>What updating a container's image did.</summary>
/// <param name="Replaced">Whether a newer image was pulled and the container replaced to use it.</param>
public sealed record ImageUpdate(bool Replaced, string Container);

/// <summary>Finds, creates and changes containers on the plugin's Docker hosts.</summary>
public sealed class ContainerService(DockerHosts hosts, ILogger<ContainerService> logger)
{
    public async Task<IReadOnlyList<ContainerSummary>> ListAsync(Guid hostId, CancellationToken cancellationToken = default)
    {
        var containers = await hosts.GetClient(hostId).Containers.ListContainersAsync(new ContainersListParameters { All = true }, cancellationToken);

        return containers
            .Select(c => new ContainerSummary(
                c.ID,
                c.Names?.FirstOrDefault()?.TrimStart('/') ?? c.ID[..Math.Min(12, c.ID.Length)],
                c.Image,
                c.State,
                c.Status))
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>The container, or null when the host has no such container.</summary>
    public async Task<ContainerInspectResponse?> InspectAsync(Guid hostId, string container, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(container))
            return null;

        try
        {
            return await hosts.GetClient(hostId).Containers.InspectContainerAsync(container, cancellationToken);
        }
        catch (DockerContainerNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// The container's definition, read with the image it was created from so the image's defaults
    /// are told apart from the container's own settings. Without that image, since removed from the
    /// host, everything the container has is taken as its own.
    /// </summary>
    public async Task<ContainerDefinition> DefinitionOfAsync(Guid hostId, ContainerInspectResponse container, CancellationToken cancellationToken = default) =>
        ContainerDefinition.From(container, await InspectImageAsync(hosts.GetClient(hostId), container.Image, cancellationToken));

    public async Task<IReadOnlyList<NetworkSummary>> ListNetworksAsync(Guid hostId, CancellationToken cancellationToken = default)
    {
        var networks = await hosts.GetClient(hostId).Networks.ListNetworksAsync(new NetworksListParameters(), cancellationToken);

        return networks
            .Select(n => new NetworkSummary(n.Name, n.Driver ?? ""))
            .OrderBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The host ports and the folders or volumes the host's containers take, running or not, besides
    /// <paramref name="except"/>'s (by name or id), the container they're being compared with.
    /// </summary>
    public async Task<HostUsage> ReadHostUsageAsync(Guid hostId, IReadOnlyList<NetworkSummary> networks, string? except = null, CancellationToken cancellationToken = default)
    {
        var client = hosts.GetClient(hostId);
        var drivers = networks.GroupBy(n => n.Name).ToDictionary(g => g.Key, g => g.First().Driver);
        var containers = await client.Containers.ListContainersAsync(new ContainersListParameters { All = true }, cancellationToken);

        // A stopped container's ports are only in its configuration
        var inspected = await Task.WhenAll(containers
            .Where(c => !IsContainer(c, except))
            .Select(async c =>
            {
                try
                {
                    return await client.Containers.InspectContainerAsync(c.ID, cancellationToken);
                }
                catch (DockerContainerNotFoundException)
                {
                    return null;
                }
            }));

        var others = inspected
            .OfType<ContainerInspectResponse>()
            .Select(c => (Name: c.Name.TrimStart('/'), Running: c.State?.Running == true, Network: c.HostConfig?.NetworkMode, Definition: ContainerDefinition.From(c)))
            .ToList();

        return new HostUsage(
            others
                .SelectMany(c => PortConflicts.UsesOf(c.Name, c.Running, c.Definition.Ports, PortConflicts.KindOf(c.Network, drivers)))
                .ToList(),
            others
                .SelectMany(c => c.Definition.Mounts.Select(m => new MountUse(c.Name, c.Running, m.Source, m.Destination, m.ReadOnly)))
                .ToList());
    }

    private static bool IsContainer(ContainerListResponse container, string? nameOrId) =>
        !string.IsNullOrWhiteSpace(nameOrId)
        && (container.ID.StartsWith(nameOrId, StringComparison.OrdinalIgnoreCase)
            || (container.Names ?? []).Any(name => name.TrimStart('/') == nameOrId));

    /// <summary>
    /// A definition for a new container from the image, pulling it first if the host doesn't have
    /// it, so its ports, volumes and defaults can be filled in.
    /// </summary>
    public async Task<ContainerDefinition> DefineFromImageAsync(Guid hostId, string name, string image, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(image))
            throw new ArgumentException("The container needs an image.");

        var client = hosts.GetClient(hostId);

        image = image.Trim();

        await PullIfMissingAsync(client, image, progress, cancellationToken);

        var inspected = await InspectImageAsync(client, image, cancellationToken)
            ?? throw new DockerHostException($"The host has no image \"{image}\" after pulling it.");

        return ContainerDefinition.ForImage(name.Trim(), image, inspected, hosts.Find(hostId)?.DataFolder);
    }

    /// <summary>
    /// Pulls the container's image again, and if that brings a newer one, replaces the container as
    /// <paramref name="spec"/> describes to use it. A container already on the newest is left alone.
    /// </summary>
    public async Task<ImageUpdate> UpdateImageAsync(Guid hostId, string container, ContainerDefinition spec, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var client = hosts.GetClient(hostId);
        var current = await client.Containers.InspectContainerAsync(container, cancellationToken);
        var image = spec.Image.Trim();

        await PullAsync(client, image, progress, cancellationToken);

        var pulled = await InspectImageAsync(client, image, cancellationToken);

        if (pulled?.ID == current.Image)
            return new ImageUpdate(false, current.Name.TrimStart('/'));

        return new ImageUpdate(true, await RecreateAsync(hostId, container, spec, progress, cancellationToken));
    }

    /// <summary>
    /// Installs or updates the game files a server shares with the game's other servers, while none of
    /// them is running: OverlayFS doesn't allow the files under a mounted overlay to change. Every
    /// container mounting the files is stopped, a one-off container like the server runs the image's
    /// install and update step on them, and those that were running are started again, whether or not
    /// the update succeeded.
    /// </summary>
    /// <exception cref="DockerHostException">The server has no shared game files, or the update failed.</exception>
    public async Task<GameFilesUpdate> UpdateGameFilesAsync(Guid hostId, string container, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var client = hosts.GetClient(hostId);
        var server = await client.Containers.InspectContainerAsync(container, cancellationToken);
        var spec = ContainerDefinition.From(server, await InspectImageAsync(client, server.Image, cancellationToken));
        var name = server.Name.TrimStart('/');

        var gameFiles = spec.GameFiles
            ?? throw new DockerHostException($"{name} has no game files of its own to update: nothing is mounted at {ContainerDefinition.BaseFolder}.");

        var log = new Queue<string>();

        var (sharing, running) = await WithFilesFreeAsync(client, hostId, gameFiles, progress, async () =>
        {
            var parameters = spec.ForUpdatingGameFiles($"{name}-update-{DateTime.UtcNow:yyyyMMddHHmmss}").ToCreateParameters(server);

            // The image the server runs, even if its tag has moved on since; and none of its labels, so
            // whatever manages the server, such as a compose project, doesn't take this for its own
            parameters.Image = server.Image;
            parameters.Labels = null;

            progress?.Report($"Updating {gameFiles.Source}");

            var created = await client.Containers.CreateContainerAsync(parameters, cancellationToken);

            try
            {
                await client.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), cancellationToken);

                // Read until it exits, when Docker ends its log
                await FollowLogAsync(client, created.ID, line =>
                {
                    if (string.IsNullOrWhiteSpace(line))
                        return;

                    progress?.Report(line);

                    log.Enqueue(line);

                    if (log.Count > LogLines)
                        log.Dequeue();
                }, cancellationToken);

                var exited = await WaitForExitAsync(client, created.ID, cancellationToken);

                if (exited != 0)
                    throw new DockerHostException($"Updating the game files failed (exit code {exited}):{System.Environment.NewLine}{string.Join(System.Environment.NewLine, log.TakeLast(10))}");
            }
            finally
            {
                await client.Containers.RemoveContainerAsync(created.ID, new ContainerRemoveParameters { Force = true }, CancellationToken.None);
            }
        }, cancellationToken);

        logger.LogInformation("Updated the game files {GameFiles} shared by {Containers}", gameFiles.Source, string.Join(", ", sharing));

        return new GameFilesUpdate(sharing, running, log.ToList());
    }

    /// <summary>
    /// Puts the game files in a ZIP into a server's game files folder, while none of the servers sharing
    /// it is running, as <see cref="UpdateGameFilesAsync"/> updates them: they're stopped, the files
    /// are copied in through Docker's archive API, into a one-off container of the server's that's never
    /// started, and those that were running are started again. Files already there are replaced; others
    /// are left.
    /// </summary>
    /// <param name="zipPath">The ZIP, on this server's disk: reading one needs to seek, which an upload can't.</param>
    /// <param name="stripTopFolder">Whether a ZIP whose files are all in one folder has that folder's contents put there, rather than the folder.</param>
    /// <exception cref="DockerHostException">The server isn't one of LANCommander's server images.</exception>
    /// <exception cref="InvalidDataException">The file isn't a ZIP.</exception>
    public async Task<GameFilesUpload> UploadGameFilesAsync(Guid hostId, string container, string zipPath, bool stripTopFolder, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var client = hosts.GetClient(hostId);
        var server = await client.Containers.InspectContainerAsync(container, cancellationToken);
        var spec = ContainerDefinition.From(server);
        var name = server.Name.TrimStart('/');

        // Into the game files folder if it's mounted on its own; otherwise into the Server folder in the
        // server's own files, where the images keep the game when it isn't shared
        var (target, into, mount) = spec.GameFiles is { } gameFiles
            ? (ContainerDefinition.BaseFolder, (string?)null, gameFiles)
            : spec.Mounts.FirstOrDefault(m => m.Destination.Trim().TrimEnd('/') == ContainerDefinition.ConfigFolder) is { } config && ContainerDefinition.IsServerImage(spec.Image)
                ? (ContainerDefinition.ConfigFolder, "Server", config)
                : throw new DockerHostException($"{name} has no folder for game files: nothing is mounted at {ContainerDefinition.BaseFolder} or {ContainerDefinition.ConfigFolder}.");

        using var zip = ZipFile.OpenRead(zipPath);
        var strip = stripTopFolder ? GameFilesArchive.SingleTopFolder(zip) : null;
        var files = 0;

        var (sharing, running) = await WithFilesFreeAsync(client, hostId, mount, progress, async () =>
        {
            // Never started, so nothing in it runs: it's only a way to the server's folders
            var created = await client.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Name = $"{name}-upload-{DateTime.UtcNow:yyyyMMddHHmmss}",
                Image = server.Image,
                HostConfig = new HostConfig
                {
                    Binds = spec.Mounts
                        .Where(m => !string.IsNullOrWhiteSpace(m.Source) && !string.IsNullOrWhiteSpace(m.Destination))
                        .Select(m => $"{m.Source.Trim()}:{m.Destination.Trim()}")
                        .ToList(),
                },
            }, cancellationToken);

            try
            {
                progress?.Report($"Copying the game files into {mount.Source}");

                // The tar is made as Docker reads it, rather than kept whole
                var pipe = new Pipe();
                var copied = 0;

                var writing = Task.Run(() =>
                {
                    try
                    {
                        using var output = pipe.Writer.AsStream();

                        files = GameFilesArchive.WriteTar(zip, output, strip, new SynchronousProgress<string>(file =>
                        {
                            if (++copied % 100 == 1)
                                progress?.Report($"Copying {copied} files: {file}");
                        }), into);
                    }
                    catch (Exception ex)
                    {
                        pipe.Writer.Complete(ex);

                        throw;
                    }
                }, cancellationToken);

                Exception? refused = null;

                try
                {
                    await client.Containers.ExtractArchiveToContainerAsync(created.ID, new CopyToContainerParameters { Path = target }, pipe.Reader.AsStream(), cancellationToken);
                }
                catch (Exception ex)
                {
                    refused = ex;
                }
                finally
                {
                    // So a tar Docker stopped reading doesn't wait to be read
                    await pipe.Reader.CompleteAsync();
                }

                // Done with the ZIP before it's closed; Docker's refusal says more than the tar's end
                try
                {
                    await writing;
                }
                catch (Exception) when (refused != null)
                {
                }

                if (refused != null)
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(refused);
            }
            finally
            {
                await client.Containers.RemoveContainerAsync(created.ID, new ContainerRemoveParameters { Force = true }, CancellationToken.None);
            }
        }, cancellationToken);

        logger.LogInformation("Copied {Files} game files from a ZIP into {GameFiles}, shared by {Containers}", files, mount.Source, string.Join(", ", sharing));

        return new GameFilesUpload(sharing, running, files, strip);
    }

    /// <summary>
    /// Does <paramref name="work"/> on a folder of game files with none of the containers mounting it
    /// running: OverlayFS doesn't allow the files under a mounted overlay to change. Those that were
    /// running are started again, whether or not it succeeded.
    /// </summary>
    private async Task<(IReadOnlyList<string> Sharing, IReadOnlyList<string> Restarted)> WithFilesFreeAsync(DockerClient client, Guid hostId, VolumeMount files, IProgress<string>? progress, Func<Task> work, CancellationToken cancellationToken)
    {
        // Networks only matter to ports, which aren't looked at here
        var sharers = (await ReadHostUsageAsync(hostId, [], cancellationToken: cancellationToken)).SharersOf(files);
        var sharing = sharers.Select(s => s.Container).Distinct().Order(StringComparer.CurrentCultureIgnoreCase).ToList();
        var running = sharers.Where(s => s.Running).Select(s => s.Container).Distinct().ToList();

        try
        {
            if (running.Count > 0)
            {
                progress?.Report($"Stopping {string.Join(", ", running)}");

                await Task.WhenAll(running.Select(c => client.Containers.StopContainerAsync(c, new ContainerStopParameters(), cancellationToken)));
            }

            await work();
        }
        finally
        {
            foreach (var stopped in running)
            {
                try
                {
                    progress?.Report($"Starting {stopped}");

                    await client.Containers.StartContainerAsync(stopped, new ContainerStartParameters(), CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Could not start {Container} again after changing its game files", stopped);
                }
            }
        }

        return (sharing, running);
    }

    // How much of a game files update's log is kept to show
    private const int LogLines = 40;

    private static async Task FollowLogAsync(DockerClient client, string container, Action<string> line, CancellationToken cancellationToken)
    {
        using var stream = await client.Containers.GetContainerLogsAsync(container, new ContainerLogsParameters { ShowStdout = true, ShowStderr = true, Follow = true }, cancellationToken);

        var buffer = new byte[16 * 1024];
        var readers = new Dictionary<MultiplexedStream.TargetStream, Engine.LineReader>();

        while (true)
        {
            var read = await stream.ReadOutputAsync(buffer, 0, buffer.Length, cancellationToken);

            if (read.EOF)
                break;

            if (!readers.TryGetValue(read.Target, out var reader))
                readers[read.Target] = reader = new Engine.LineReader(line);

            reader.Append(buffer, read.Count);
        }

        foreach (var reader in readers.Values)
            reader.Flush();
    }

    // Polled rather than waited on: a wait is one request, which an update longer than the client's
    // timeout would outlast
    private static async Task<long> WaitForExitAsync(DockerClient client, string container, CancellationToken cancellationToken)
    {
        while (true)
        {
            var state = (await client.Containers.InspectContainerAsync(container, cancellationToken)).State;

            if (state is not { Running: true } and not { Restarting: true })
                return state?.ExitCode ?? -1;

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    /// <summary>Creates a container, pulling its image first if the host doesn't have it. Returns its name.</summary>
    public async Task<string> CreateAsync(Guid hostId, ContainerDefinition spec, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var client = hosts.GetClient(hostId);
        var parameters = spec.ToCreateParameters();

        // So it can be told from containers made otherwise, should no server use it any more
        parameters.Labels ??= new Dictionary<string, string>();
        parameters.Labels[ManagedLabel] = "true";

        await PullIfMissingAsync(client, parameters.Image, progress, cancellationToken);

        progress?.Report($"Creating {parameters.Name}");

        await client.Containers.CreateContainerAsync(parameters, cancellationToken);

        logger.LogInformation("Created container {Container} from {Image}", parameters.Name, parameters.Image);

        return parameters.Name;
    }

    /// <summary>On the containers the plugin creates, and kept when they're replaced.</summary>
    public const string ManagedLabel = "com.lancommander.docker.managed";

    /// <summary>What removing the container would do: which of its files could go with it.</summary>
    public async Task<RemovalPlan> PlanRemovalAsync(Guid hostId, string container, CancellationToken cancellationToken = default)
    {
        var client = hosts.GetClient(hostId);
        var inspected = await client.Containers.InspectContainerAsync(container, cancellationToken);

        return await PlanAsync(hostId, inspected, cancellationToken);
    }

    private async Task<RemovalPlan> PlanAsync(Guid hostId, ContainerInspectResponse container, CancellationToken cancellationToken)
    {
        // The others: this one's own mounts aren't shared with itself
        var others = await ReadHostUsageAsync(hostId, [], container.ID, cancellationToken);

        return new RemovalPlan(
            container.Name.TrimStart('/'),
            container.State?.Running == true,
            ContainerRemoval.Plan(ContainerDefinition.From(container).Mounts, others, hosts.Find(hostId)?.DataFolder));
    }

    /// <summary>
    /// Stops and removes the container, then the files of its mounts named in <paramref name="files"/>
    /// (by their source, as <see cref="RemovalPlan"/> lists them), each only if it still may be. A
    /// volume is removed by Docker; a folder on the host, by a one-off container of the removed one's
    /// image that mounts the folder it's in. Files that can't be removed are reported, not thrown for.
    /// </summary>
    /// <exception cref="InvalidOperationException">A file named can't be removed; nothing has been.</exception>
    public async Task<ContainerRemoved> RemoveAsync(Guid hostId, string container, IReadOnlyCollection<string> files, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var client = hosts.GetClient(hostId);
        var inspected = await client.Containers.InspectContainerAsync(container, cancellationToken);
        var plan = await PlanAsync(hostId, inspected, cancellationToken);
        var name = plan.Container;

        // Checked again now, before anything is removed: another server may have taken it since
        var chosen = new List<MountRemoval>();

        foreach (var source in files.Distinct(StringComparer.Ordinal))
        {
            var mount = plan.Mounts.FirstOrDefault(m => m.Mount.Source == source)
                ?? throw new InvalidOperationException($"{name} doesn't mount {source}.");

            if (!mount.CanRemove)
                throw new InvalidOperationException($"{source} can't be removed: {mount.CantRemove}");

            chosen.Add(mount);
        }

        progress?.Report($"Removing {name}");

        await client.Containers.RemoveContainerAsync(inspected.ID, new ContainerRemoveParameters { Force = true, RemoveVolumes = false }, cancellationToken);

        logger.LogInformation("Removed container {Container}", name);

        var removed = new List<string>();
        var failed = new List<string>();

        foreach (var mount in chosen)
        {
            var source = mount.Mount.Source.Trim();

            try
            {
                progress?.Report($"Removing {source}");

                if (mount.Kind == MountKind.Volume)
                    await client.Volumes.RemoveAsync(source, force: false, cancellationToken);
                else
                    await DeleteFolderAsync(client, inspected.Image, source, cancellationToken);

                removed.Add(source);

                logger.LogInformation("Removed {Files}, which {Container} mounted", source, name);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not remove {Files}, which {Container} mounted", source, name);

                failed.Add($"{source}: {ex.Message}");
            }
        }

        return new ContainerRemoved(name, removed, failed);
    }

    /// <summary>
    /// Deletes a folder on the host, which Docker has no call for: a one-off container of the image
    /// mounts the folder it's in and removes it, as root, since the server's files may be root's.
    /// </summary>
    private static async Task DeleteFolderAsync(DockerClient client, string image, string folder, CancellationToken cancellationToken)
    {
        var (parent, folderName) = ContainerRemoval.Split(folder);

        var created = await client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Name = $"lancommander-remove-{Guid.NewGuid():N}",
            Image = image,
            User = "0",
            // Instead of the image's own, which would start its server; its command is dropped with it
            Entrypoint = ["rm", "-rf", "--", $"/remove/{folderName}"],
            HostConfig = new HostConfig { Binds = [$"{parent}:/remove"] },
        }, cancellationToken);

        try
        {
            await client.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), cancellationToken);

            var exited = await WaitForExitAsync(client, created.ID, cancellationToken);

            if (exited != 0)
                throw new IOException($"rm exited with code {exited}.");
        }
        finally
        {
            await client.Containers.RemoveContainerAsync(created.ID, new ContainerRemoveParameters { Force = true }, CancellationToken.None);
        }
    }

    /// <summary>
    /// The containers the plugin created on the host that no server uses: none of
    /// <paramref name="inUse"/>, the containers servers name, by name or id.
    /// </summary>
    public async Task<IReadOnlyList<ContainerSummary>> ListUnusedAsync(Guid hostId, IReadOnlyCollection<string> inUse, CancellationToken cancellationToken = default)
    {
        var containers = await hosts.GetClient(hostId).Containers.ListContainersAsync(new ContainersListParameters { All = true }, cancellationToken);

        return containers
            .Where(c => c.Labels?.ContainsKey(ManagedLabel) == true)
            .Where(c => !inUse.Any(used => IsContainer(c, used)))
            .Select(c => new ContainerSummary(
                c.ID,
                c.Names?.FirstOrDefault()?.TrimStart('/') ?? c.ID[..Math.Min(12, c.ID.Length)],
                c.Image,
                c.State,
                c.Status))
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Recreates a container as the spec describes, keeping whatever the spec doesn't cover. The old
    /// container is kept until the new one has been created, and restored if it can't be. A container
    /// that was running is started again. Returns the new container's name.
    /// </summary>
    public async Task<string> RecreateAsync(Guid hostId, string container, ContainerDefinition spec, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var client = hosts.GetClient(hostId);
        var old = await client.Containers.InspectContainerAsync(container, cancellationToken);
        var oldName = old.Name.TrimStart('/');
        var parameters = spec.ToCreateParameters(old, await InspectImageAsync(client, old.Image, cancellationToken));
        var wasRunning = old.State?.Running == true;

        await PullIfMissingAsync(client, parameters.Image, progress, cancellationToken);

        if (wasRunning)
        {
            progress?.Report($"Stopping {oldName}");

            await client.Containers.StopContainerAsync(old.ID, new ContainerStopParameters(), cancellationToken);
        }

        // Out of the way of the new container's name, which is usually the same
        var backupName = $"{oldName}-replaced-{DateTime.UtcNow:yyyyMMddHHmmss}";

        await client.Containers.RenameContainerAsync(old.ID, new ContainerRenameParameters { NewName = backupName }, cancellationToken);

        CreateContainerResponse created;

        try
        {
            progress?.Report($"Creating {parameters.Name}");

            created = await client.Containers.CreateContainerAsync(parameters, cancellationToken);
        }
        catch
        {
            // Put the old container back as it was
            await client.Containers.RenameContainerAsync(old.ID, new ContainerRenameParameters { NewName = oldName }, CancellationToken.None);

            if (wasRunning)
                await client.Containers.StartContainerAsync(old.ID, new ContainerStartParameters(), CancellationToken.None);

            throw;
        }

        progress?.Report($"Removing the old {oldName}");

        await client.Containers.RemoveContainerAsync(old.ID, new ContainerRemoveParameters { RemoveVolumes = false }, cancellationToken);

        if (wasRunning)
        {
            progress?.Report($"Starting {parameters.Name}");

            await client.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), cancellationToken);
        }

        logger.LogInformation("Recreated container {OldName} as {Name} from {Image}", oldName, parameters.Name, parameters.Image);

        return parameters.Name;
    }

    /// <summary>The image, by name or id; null if the host doesn't have it.</summary>
    private static async Task<ImageInspectResponse?> InspectImageAsync(DockerClient client, string? image, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(image))
            return null;

        try
        {
            return await client.Images.InspectImageAsync(image, cancellationToken);
        }
        catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private async Task PullIfMissingAsync(DockerClient client, string image, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (await InspectImageAsync(client, image, cancellationToken) != null)
            return;

        await PullAsync(client, image, progress, cancellationToken);
    }

    private static async Task PullAsync(DockerClient client, string image, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        progress?.Report($"Pulling {image}");

        var (name, tag) = SplitImage(image);
        string? error = null;

        // Docker reports a pull that fails part way in its progress, not as a failed request
        await client.Images.CreateImageAsync(
            new ImagesCreateParameters { FromImage = name, Tag = tag },
            null,
            new SynchronousProgress<JSONMessage>(message =>
            {
                if (!string.IsNullOrWhiteSpace(message.ErrorMessage) || !string.IsNullOrWhiteSpace(message.Error?.Message))
                    error = message.Error?.Message ?? message.ErrorMessage;
                else if (!string.IsNullOrWhiteSpace(message.Status))
                    progress?.Report($"Pulling {image}: {message.Status}");
            }),
            cancellationToken);

        if (error != null)
            throw new DockerHostException($"{image} could not be pulled: {error}");
    }

    /// <summary>Reports on the caller's thread, so what's reported is all seen once the call returns.</summary>
    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    /// <summary>An image reference's repository and tag; a digest stays with the repository.</summary>
    public static (string Name, string? Tag) SplitImage(string image)
    {
        if (image.Contains('@'))
            return (image, null);

        var colon = image.LastIndexOf(':');

        return colon > image.LastIndexOf('/') ? (image[..colon], image[(colon + 1)..]) : (image, "latest");
    }
}
