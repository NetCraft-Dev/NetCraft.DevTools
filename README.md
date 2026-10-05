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
ncm asm <assembly>              # inspect any .NET assembly
ncm update                      # check nuget.org, then update ncm itself

dotnet new ncm -n MyMod         # or create from the template directly
```

### Build the packages from source

To try local changes instead of the published packages:

```powershell
dotnet pack NetCraft.ModBuild.Tools -c Release -o ./nupkg
dotnet pack NetCraft.ModsProjectType -c Release -o ./nupkg
dotnet tool install -g netcraft.modbuild.tools --add-source ./nupkg
dotnet new install ./nupkg/NetCraft.ModsProjectType.0.1.0.nupkg
```

## Keeping `reference/` current

`reference/` holds the kernel assemblies that ship inside the project template, so new projects can compile without the kernel source. Refresh it from a kernel build output plus a ModApi build:

```powershell
./tools/sync-reference.ps1 -KernelOutput <NetCraft.ServerExe bin/Release/net10.0> -ModApiDll <NetCraft.ModApi.dll>
```

## License

Apache-2.0 — see [LICENSE](./LICENSE).
