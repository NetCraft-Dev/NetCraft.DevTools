# NetCraft.DevTools

Development tools for [NetCraft](https://github.com/NetCraft-Dev/NetCraft) mods.

| Project | Contents |
|---|---|
| `NetCraft.ModBuild.Tools` | `ncm` — the mod development CLI, packaged as a dotnet tool |
| `NetCraft.ModsProjectType` | The `dotnet new ncm` project template |
| `reference/` | Kernel reference assemblies handed to newly created projects |

## Install the toolchain

The install scripts set up everything a mod author needs — the .NET 10 SDK, the `dotnet new ncm` project template and the `ncm` CLI — downloading whatever is missing and refreshing what is already installed. They are idempotent, so rerunning one is the update path.

| Platform | Script |
|---|---|
| Windows | `tools/install.ps1` |
| Linux | `tools/install-linux.sh` |
| macOS | `tools/install-macos.sh` |

```powershell
./tools/install.ps1
```

```shell
./tools/install-linux.sh
```

Packages come from nuget.org. Then, from a mod project:

```powershell
ncm init                        # create a mod project with prompts
ncm init <name> <id> <description> <authors> <homepage> <sources> <license>
                                # same fields in the same order, no prompts
                                # trailing ones may be omitted, empty string skips one
ncm template example <api>      # write an example for an API type
ncm template tui                # terminal panel: grade the api usage of the current project
ncm template gui                # the same catalog and grading in a window
ncm build                       # diagnose, then build
ncm runserver                   # build the mod and run the server
ncm asm <assembly>              # inspect any .NET assembly
ncm icon                        # write a white background mod icon
ncm update                      # check nuget.org, then update ncm itself

dotnet new ncm -n MyMod         # or create from the template directly
```

## Command reference

`ncm help` lists every tool, `ncm help <tool>` prints the parameters below.

### `init`

Create a new NetCraft mod project in a subdirectory.

| Parameter | Description |
|---|---|
| `<name>` | Mod name, required |
| `[id]` | Mod id, derived from the name when omitted |
| `[description]` | Mod description |
| `[authors]` | Comma separated author names |
| `[homepage]` | Homepage url |
| `[sources]` | Source repository url |
| `[license]` | License name |

### `template`

Browse and pull mod templates.

| Parameter | Description |
|---|---|
| `view [pattern]` | List template entries, `?` and `*` work as wildcards |
| `example <api id>` | Pull the example file of an entry into the current directory |
| `gui` | Open the template panel in a window |
| `tui` | Open the terminal panel, grading the api usage of the project |
| `--refresh` | Clear the local cache first and pull everything again |

### `build`

Diagnose and build the mod project in the current directory.

| Parameter | Description |
|---|---|
| `-c`, `--configuration <name>` | Build configuration, defaults to `Release` |
| `--no-check` | Skip the api and syntax checks and run `dotnet build` directly |
| `--check-only` | Run the api and syntax checks only, build nothing |
| `--no-manifest` | Skip the `ncmod.json` lookup, treat the current directory as a plain C# project (must contain a csproj) |

### `runserver`

Build the current mod and run the NetCraft server. On the first run of a project it downloads the Minecraft client jar from the remote index and passes it to the server as `--jar-path`, so the terrain and asset data land in the run directory.

| Parameter | Description |
|---|---|
| `--refresh` | Refresh the server cache against the remote index, no `ncmod.json` and no build needed |
| `[server args]` | Everything after `runserver` is passed to the server unchanged |

### `asm`

Inspect a .NET assembly: type list, type details, decompiled source, dependencies.

| Parameter | Description |
|---|---|
| `<assembly>` | Path to a .NET assembly |
| `-t`, `--type <name>` | Show type details |
| `-d`, `--decompile <name>` | Decompile a type to C# source |
| `-dep`, `--dependencies` | List assembly dependencies |
| `-r`, `--reference <dir>` | Extra directory to look for dependencies, repeatable |
| `-o`, `--output <file>` | Write the result to a file instead of the console |

### `icon`

Generate a white background mod icon with the mod name.

| Parameter | Description |
|---|---|
| `-f`, `--force` | Overwrite an existing icon without asking |

### `update`

Check nuget.org and update ncm to the latest version. Takes no parameters.

## License

Apache-2.0 — see [LICENSE](./LICENSE).
