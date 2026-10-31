# LANCommander.Plugins.Docker

Runs [LANCommander](https://github.com/LANCommander/LANCommander) game servers as Docker containers,
on any number of Docker hosts.

The server's own engines run a game server as a process on the LANCommander server's machine. With
this plugin, Docker is offered as an engine on a server's General page as well. A server using it is
a container: start, stop, autostart, the Monitor's output and commands, CPU and memory, consoles,
actions and scripts all work as they do for any other server.

Earlier versions of LANCommander had a Docker engine built in. That engine kept only which container
a server was, and everything else about the server was configured on top of it. Here the container is
the configuration: its image, command, environment, ports, volumes and restart policy are read from
Docker and changed on the server's Container page.

## Using it

1. Install the plugin from **Settings → Plugins**, and restart the server.
2. Add the Docker daemons servers run on under **Settings → Docker Hosts**: the local one
   (`unix:///var/run/docker.sock`, or `npipe://./pipe/docker_engine` on Windows), or another machine's
   (`tcp://docker-host:2375`). **Test Connection** asks the daemon for its version.
3. On a server's General page, choose **Docker** as the engine, then a host and one of its
   containers, or create a container from an image there. Choose one of its published ports to make it
   the port players connect to.
4. Save. The server's **Container** page now shows the container's settings.

### Changing a container

Docker can't change a container's image, environment, ports or volumes once it exists, so applying
changes on the Container page replaces it:

1. The image is pulled if the host doesn't have it.
2. A running container is stopped.
3. It is renamed out of the way, and a new container is created with the same name, keeping
   everything the page doesn't show: entry point, user, network, limits and so on.
4. The old container is removed, and the new one started if the old one was running.

Every volume the old container had, including the ones Docker created without a name for an image's
`VOLUME`s, is mounted in the new one, so its data stays. If the new container can't be created, the old
one is put back as it was.

### Consoles

Under a server's **Monitor**, beside its process output, the container has three consoles of its own:

- **Container log**: the container's log as `docker logs` shows it, its latest lines and then new ones
  as they are written, with standard error in red. Commands typed under it go to the server as they do
  from the process output.
- **Attach**: the server's own console, as `docker attach` joins it. In a container with a terminal
  (`tty`) it is that terminal, keys and all; otherwise lines are edited in the browser and sent on
  Enter, and a container whose input isn't open (`stdin_open`) is shown but can't be typed into.
  Ctrl+C reaches the server like any other key, so it may stop it.
- **Shell**: `bash`, or `sh` where there is no `bash`, in the running container, as `docker exec -it`
  opens one. Leaving the page ends it.

Both terminals need the container to be running. The server logs who opened each.

### Files

A server's **Files** page shows the files in its container under the container's working directory,
or under the **Files folder** set on its General page, e.g. a data volume like `/data`. Text files such
as the server's configuration are edited there, and maps, mods and other files uploaded, replacing any
of the same name; folders can be made and deleted.

Each change is made by a short `docker exec` of `sh` in the container, as the container's own user, so
files are written with the owner the server expects, and a file that is replaced keeps its owner and
mode. The container needs `sh` and the usual commands (`stat`, `cat`, `mkdir`, `rm`), as any image based
on a distribution or busybox has.

### Servers from the built-in engine

Servers that used the built-in Docker engine are moved to this plugin's engine when the server's
database is upgraded, and the plugin takes over the Docker hosts from the server's settings the first
time it starts, keeping their ids so those servers still find them. Install the plugin when upgrading
the server, or those servers can't be started until it is.

## Limitations

- Connections are plain: TLS-protected daemons (`tcp://…:2376` with client certificates) aren't
  supported yet.
- The page edits one network, the one the container already has. Containers on several networks
  keep only the first when replaced.
- A server's log file consoles read files on the LANCommander server's disk, not inside the container.
- A server's files can only be managed while its container is running, since Docker runs commands only
  in running containers. A server that won't start because of its configuration can't be fixed from
  its Files page; use a volume the host can reach, or start the container with another command.
- Files and folders whose names hold a line break, and links that lead nowhere, aren't listed.
- A shell keeps the size the browser's terminal had when it was opened: the Docker client has no call
  to resize a running `exec`. Reopen it after resizing the window. Attach follows the window.
- A shell left running a program when its page is closed is sent an end of input (Ctrl+D), which ends
  a shell at its prompt but not every program; Docker itself leaves an `exec` running when its
  connection goes.

## Building

The plugin builds against the LANCommander server's `LANCommander.SDK`, `LANCommander.Server.Plugins`
and `LANCommander.Server.UI` packages, of the version in `Directory.Packages.props`. To build against an
unreleased server, pack those projects into `artifacts/packages` (used as a package source when it
exists), and pass the version:

```
dotnet pack ../LANCommander/LANCommander.Server.Plugins -c Release -p:Version=2.2.0-local.1 -o artifacts/packages
# and the same for LANCommander.SDK and LANCommander.Server.UI
dotnet build -p:LANCommanderVersion=2.2.0-local.1
```

`dotnet build -t:Deploy` copies the plugin into the Plugins folder of a server run from a LANCommander
checkout beside this repository; `dotnet pack` makes the package the Plugins page installs.

The tests that need Docker run against the local daemon and the `busybox` image, and are skipped
without them (`docker pull busybox`). They create containers named `lancommander-test-…` and remove
them afterwards.
