using System.Net;
using Docker.DotNet;
using Docker.DotNet.Models;
using LANCommander.Plugins.Docker.Hosts;
using Microsoft.Extensions.Logging;

namespace LANCommander.Plugins.Docker.Containers;

/// <summary>A container as listed on a host.</summary>
public sealed record ContainerSummary(string Id, string Name, string Image, string State, string Status);

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

    /// <summary>Creates a container, pulling its image first if the host doesn't have it. Returns its name.</summary>
    public async Task<string> CreateAsync(Guid hostId, ContainerDefinition spec, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var client = hosts.GetClient(hostId);
        var parameters = spec.ToCreateParameters();

        await PullIfMissingAsync(client, parameters.Image, progress, cancellationToken);

        progress?.Report($"Creating {parameters.Name}");

        await client.Containers.CreateContainerAsync(parameters, cancellationToken);

        logger.LogInformation("Created container {Container} from {Image}", parameters.Name, parameters.Image);

        return parameters.Name;
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

        progress?.Report($"Pulling {image}");

        var (name, tag) = SplitImage(image);

        await client.Images.CreateImageAsync(
            new ImagesCreateParameters { FromImage = name, Tag = tag },
            null,
            new Progress<JSONMessage>(message =>
            {
                if (!string.IsNullOrWhiteSpace(message.Status))
                    progress?.Report($"Pulling {image}: {message.Status}");
            }),
            cancellationToken);
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
