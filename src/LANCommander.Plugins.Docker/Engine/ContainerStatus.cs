using Docker.DotNet.Models;
using LANCommander.Server.Plugins;

namespace LANCommander.Plugins.Docker.Engine;

internal static class ContainerStatus
{
    // What a container exits with when it is stopped: cleanly, or by SIGINT, SIGKILL or SIGTERM
    private static readonly int[] StoppedExitCodes = [0, 130, 137, 143];

    public static ServerEngineStatus Of(State? state)
    {
        if (state == null)
            return ServerEngineStatus.Stopped;

        if (state.Restarting)
            return ServerEngineStatus.Starting;

        if (state.Running && !state.Paused)
            return ServerEngineStatus.Running;

        return Failure(state) != null ? ServerEngineStatus.Error : ServerEngineStatus.Stopped;
    }

    /// <summary>Why a container that isn't running failed, or null if it was stopped or never ran.</summary>
    public static string? Failure(State state)
    {
        if (state.OOMKilled)
            return "The container ran out of memory";

        if (!string.IsNullOrWhiteSpace(state.Error))
            return state.Error;

        if (state.Dead)
            return "The container is dead";

        if (!state.Running && !StoppedExitCodes.Contains((int)state.ExitCode))
            return $"The container exited with code {state.ExitCode}";

        return null;
    }
}
