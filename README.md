# NetCraft.DevTools

Development tools for [NetCraft](https://github.com/NetCraft-Dev/NetCraft) mods.

| Project | Contents |
|---|---|
| `NetCraft.ModBuild.Tools` | `ncm` — the mod development CLI, packaged as a dotnet tool |
| `NetCraft.ModsProjectType` | The `dotnet new ncm` project template |
| `reference/` | Kernel reference assemblies handed to newly created projects |

## Install the CLI

```powershell
dotnet pack NetCraft.ModBuild.Tools -c Release -o ./nupkg
dotnet tool install -g netcraft.modbuild.tools --add-source ./nupkg
```

Then, from a mod project:

```powershell
ncm init                        # create a new mod project
ncm template example <api>      # write an example for an API type
ncm build                       # diagnose, then build
ncm asm <assembly>              # inspect any .NET assembly
```

## Install the project template

```powershell
dotnet pack NetCraft.ModsProjectType -c Release -o ./nupkg
dotnet new install ./nupkg/NetCraft.ModsProjectType.0.1.0.nupkg
dotnet new ncm -n MyMod
```

## Keeping `reference/` current

`reference/` holds the kernel assemblies that ship inside the project template, so new projects can compile without the kernel source. Refresh it from a kernel build output plus a ModApi build:

```powershell
./tools/sync-reference.ps1 -KernelOutput <NetCraft.Server.Exe bin/Release/net10.0> -ModApiDll <NetCraft.ModApi.dll>
```

## License

Apache-2.0 — see [LICENSE](./LICENSE).
