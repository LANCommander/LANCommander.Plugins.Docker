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
   (`tcp://docker-host:2375`). **Test Connection** asks the daemon for its version. Give a host a
   **Data folder**, such as `/data/Servers`, to keep new servers' files in folders on it; without one
   they're kept in Docker volumes.
3. On a server's General page, choose **Docker** as the engine, then a host and one of its
   containers, or create a container from an image there (below). Choose one of its ports to make it
   the port players connect to.
4. Save. The server's **Container** page now shows the container's settings.

### Creating a container

Give the container a name and an image, and choose **Next**: the image is pulled and read, and the
container's settings are filled in from it, to check before choosing **Create**:

- Every port the image exposes, published on the same host port.
- A volume for each of the image's `VOLUME`s, in a folder of the container's name under the host's
  data folder (`/data/Servers/<name>`, or `/data/Servers/<name>/<folder>` for several), or a named
  volume (`<name>-<folder>`).
- The image's environment variables, listed as the image's to change any of.

LANCommander's own server images (`lancommander/*`) are set up as their compose examples are: run as
root with `SYS_ADMIN` and AppArmor unconfined so they can mount their overlay, restarted unless stopped,
and the server's Files page showing `/config/Merged`, the game as the server sees it.

Their volumes are laid out so servers of the same game share its files. The images run the game from
an overlay: `/config/Server` holds the game's files, `/config/Overlay` the server's own changes and
configuration, and `/config/Merged` the two together. So each game's servers mount one folder of the
game's files as `/config/Server`, and a folder of their own as `/config`:

```
/data/Servers/quake3/
├── Base/        → /config/Server in every Quake 3 server: the game, installed once
├── quake3-ctf/  → /config in the CTF server: its overlay, scripts and settings
└── quake3-dm/   → /config in the deathmatch server
```

Without a data folder they're the volumes `quake3-base` and `quake3-ctf-config`. The game is the image's
name, so every tag of an image shares it. The images games are built on (`lancommander/base`,
`lancommander/steamcmd`, `lancommander/wine`) can run any game, so their servers keep the game in their
own `/config` instead.

The form says which other servers share a folder. A server that updates the game when it starts would
change the files under the others' overlays, which OverlayFS doesn't allow, so a new server of an image
with `STEAM_APP_UPDATE` has it set to `false`, and the game is updated with **Update Game Files** (below)
instead. For the first server of a game, **Install the game files now** on the form installs them once
it's created. Two servers given the same `/config` can't run at the same time, and the form warns of it.

`STEAM_APP_UPDATE` is only honoured by images whose install step reads it. Those built on the SteamCMD
image's own install step update the game on every start whatever it's set to, so start one of their
servers while the others sharing its files are stopped.

### Updating a game's files

**Update Game Files**, on the Container page of a server with shared game files, updates them with every
server sharing them stopped:

1. Every container that mounts the game's files is stopped, if it's running.
2. A one-off container is run from the server's image with its volumes and privileges, set to install or
   update the game and exit rather than start the server (`START_EXE=true`, `STEAM_APP_UPDATE=true`). It
   publishes no ports and joins Docker's default network. Its log is shown as it runs.
3. It's removed, and the servers that were running are started again, whether or not the update
   succeeded. The end of its log is shown afterwards: the images report an install that failed there,
   such as game files they couldn't find or download, rather than by failing, so check it.

It runs whatever the image does when it starts: SteamCMD images update the game, and images of retail
games install it from their `*_ARCHIVE_URL` if it isn't there yet.

Once created, its first UDP port becomes the port players connect to, if the server has none yet.

### Networks and ports

The **Network** a container joins decides how players reach it:

- **Docker's default bridge**, or another bridge network: behind Docker's NAT, on the host ports it
  publishes. Games that find servers by LAN broadcast won't see it.
- **host**: the host's own network. The server listens on the host's ports itself, and is seen by LAN
  browsers.
- A **macvlan** or **ipvlan** network: an address of its own on the LAN, fixed with **Address** or
  assigned by the network. It's seen by LAN browsers, and players connect to its address rather than
  the Docker host's. Create the network on the host first (`docker network create -d macvlan ...`).

On a bridge or host network, the form warns of ports another container on the host takes too, running
or stopped, since only one of them can run at a time. On a bridge it offers to move every published host
port up together to the first free ones, keeping them the same distance apart, as games that work out
their query port from their game port expect. With host networking the ports are the server's own, so
change them in its settings instead. Programs on the host itself aren't seen, only containers. A
container with an address of its own can't collide, and isn't checked.

### Changing a container

Docker can't change a container's image, environment, ports or volumes once it exists, so applying
changes on the Container page replaces it:

1. The image is pulled if the host doesn't have it.
2. A running container is stopped.
3. It is renamed out of the way, and a new container is created with the same name, keeping
   everything the page doesn't show: limits and so on.
4. The old container is removed, and the new one started if the old one was running.

Every volume the old container had, including the ones Docker created without a name for an image's
`VOLUME`s, is mounted in the new one, so its data stays. If the new container can't be created, the old
one is put back as it was.

The page shows the container's own settings apart from its image's. Environment variables, command and
user the container only has from its image are listed as the image's and left to the image, as are its
entry point, working directory and labels; so a container replaced after its image is updated, or with
another image, takes that image's defaults rather than keeping the old one's. Choose **Change** beside
one of the image's variables to set it for the container. If the image the container was created from
is no longer on the host, they can't be told apart, and everything is kept as the container has it.

**Privileges** allows the container to mount file systems (`SYS_ADMIN`, AppArmor unconfined), which
LANCommander's server images need; any other capabilities and security options the container has are
kept. **Run as** sets the user the entry point starts as.

**Update Image** pulls the container's image again. If that brings a newer one, the container is
replaced as above to use it, with the page's settings; if not, it's left alone.

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
- A container joins one network. Containers on several networks keep only their main one when
  replaced.
- Port collisions are only checked against other containers on the same Docker host: not programs on
  the host itself, and not other hosts.
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
