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
ncm add nuget <id> [version]    # declare a package dependency in the .ncproj
ncm add file <path>             # declare a managed dll as a compile reference
ncm add project <path>          # reference another project, built first, used as a compile reference
ncm restore                     # resolve the declared packages into Build/packages/
ncm build                       # diagnose, then build
ncm clean [--all]               # drop the build cache, --all drops the downloads as well
ncm runserver                   # build the mod and run the server
ncm asm <assembly>              # inspect any .NET assembly
ncm icon                        # write a white background mod icon
ncm upgrade                     # convert a csproj project to a .ncproj one
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

### `add`

Declare a dependency or a reference in the project config. The change is written back to the `.ncproj`.

| Parameter | Description |
|---|---|
| `nuget <id> [version]` | Add a nuget package, the version takes the same syntax as `dotnet add package` |
| `file <path>` | Add a managed dll as a compile reference, written as a `<File>` under `<References>` |
| `project <path>` | Add another project as a compile reference, written as a `<Project>` under `<References>` |
| `mod <id> [version]` | Add a mod dependency, not implemented yet |

`file` and `project` take a path relative to the current directory and store it relative to the project root.

### `restore`

Resolve the `<Packages>` declared in the project config and copy their assemblies and build files into the project, under `Build/packages` and `Build/targets`. `build` runs the same step on its own, so this is only needed when the restored files are wanted without a build.

| Parameter | Description |
|---|---|
| `[directory]` | Where to restore, relative to the project root, defaults to `Build/packages` |

### `build`

Diagnose and build the mod project in the current directory. The kernel reference assemblies under `Build/kernel` and the declared packages are laid down first, whatever is already there is kept, and only the missing files are fetched. Referenced projects are built too and their output joins the compile references. Then the project is compiled, the build targets the packages ship are run, and the result is deployed.

| Parameter | Description |
|---|---|
| `-c`, `--configuration <name>` | Build configuration, defaults to `Release` |
| `--no-check` | Skip the api and syntax checks and run `dotnet build` directly |
| `--check-only` | Run the api and syntax checks only, build nothing |
| `--no-manifest` | Skip the `ncmod.json` lookup, treat the current directory as a plain C# project (must contain a csproj). Its assembly, project and package references are resolved through msbuild, restoring it first when needed |

### `clean`

In a project this removes its build cache, `Build`, and leaves the downloads alone: the next build lays it down again. Outside a project the only thing there is to remove is the shared download cache, so ncm says where it is and how big it is and asks first.

Everything ncm downloads lives under the user directory — `%LOCALAPPDATA%\NetCraft` on Windows, `~/.local/share/NetCraft` on Linux, `~/Library/Application Support/NetCraft` on macOS. The shared cache sits in `ncm/`, the self-update package and its script in `Update/`. Updating or reinstalling ncm leaves both alone, and every project on the machine reuses the same copy.

| Parameter | Description |
|---|---|
| `--all` | Remove both without asking: the client jar, the server runtime, the template files, the packages and the update package. In a project the build cache goes as well |

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

### `upgrade`

Convert a csproj based mod project to a `.ncproj` one. What ncm does not understand is listed so it can be handled by hand; the original csproj is renamed to `.bak`.

| Parameter | Description |
|---|---|
| `[file]` | The csproj to convert, defaults to the only csproj in the current directory |

### `update`

Check nuget.org and update ncm to the latest version. A newer package is downloaded into `Update/` first, then a script there runs `dotnet tool update` against that local copy, so the install itself does not depend on nuget.org being reachable. Takes no parameters.

## Project config

A mod project can carry a `.ncproj` file next to `ncmod.json`. It holds development-time settings only — how the project is built, run and packaged — and is never embedded into the mod, unlike `ncmod.json`, which the loader reads at runtime.

With a `.ncproj` present ncm compiles the project with Roslyn directly; without one it falls back to `dotnet build`. Tasks declared here run as `ncm <task>`.

Everything the build needs at build time lives under `Build`: `Build/kernel` holds the NC reference assemblies synced from the kernel cache, `Build/packages` the packages pulled by the restore, `Build/targets` the props and targets those packages ship, `Build/obj` the intermediate directory the build targets run in, and `Build/out` the build output.

```xml
<ncproj version="1">
  <Check LangVersion="latest" />
  <Build AssemblyName="Demo" Configuration="Release" Output="Build/out" OutputType="Library"
         RootNamespace="Demo" Nullable="true" ImplicitUsings="true" DefineConstants="DEBUG" ExtraArgs="" />
  <Packages>
    <Package Id="Newtonsoft.Json" Version="13.0.3" />
  </Packages>
  <References>
    <File Include="libs/**.dll" />
    <Project Include="../SharedLib" />
  </References>
  <Server Cache="cache" Args="--nogui" Debug="false" />
  <Client Version="26.2" Jar="https://example.com/client.jar" Args="--username dev" />
  <Sources Url="https://example.com/kernel/" Index="index.txt" Format="sha256-lines" />
  <Template Url="https://example.com/NetCraftTemplate.yaml" Base="https://example.com/NetCraftTemplate" />
  <InternalsVisibleTo>
    <Assembly Name="NetCraft.Test" />
  </InternalsVisibleTo>
  <AvaloniaResources>
    <Resource Include="Gui/Assets/**" />
  </AvaloniaResources>
  <EmbeddedResources>
    <Resource Include="assets/**" />
    <Resource Include="Fonts/icon.ttf" LogicalName="icon.ttf" />
  </EmbeddedResources>
  <Deploy To="run/mods;../host/mods" />
  <Tasks>
    <Task Name="release" Description="build and pack" Depends="check">
      <Exec>ncm build</Exec>
      <Copy From="Build/*.dll" To="dist/" />
      <Zip From="dist" To="dist/$(ModId)-$(ModVersion).zip" />
    </Task>
  </Tasks>
</ncproj>
```

| Element | Attributes |
|---|---|
| `Check` | `LangVersion` — the C# version used for the api and syntax checks |
| `Build` | `AssemblyName`, `Configuration`, `Output`, `OutputType` — `Library` (the default) or `Exe`, a plain compilation choice, ncm writes neither an apphost nor a runtimeconfig, `RootNamespace` — used as the default prefix of embedded resource names, `Nullable`, `ImplicitUsings`, `DefineConstants`, `ExtraArgs`, `DependsOnModApi` — whether the mod api is restored as a compile reference and staged next to the mod, `true` by default; set it to `false` for a pure mod that does not use the api |
| `Packages` | one `Package` per dependency with `Id` and an optional `Version`, the version range syntax matches NuGet |
| `References` | `File` — a dll path or pattern, `*`, `?` and `**` work as wildcards; `Project` — the directory or the project file of another project, ncproj or csproj. Both are resolved relative to the project root and only used as compile references, never embedded into the mod nor deployed. A referenced project is built first, so its output is up to date |
| `Server` | `Cache` — where the runtime files are cached, the shared download cache by default, `Args` — extra server arguments, `Debug` — always run in debug mode |
| `Client` | `Version` — the client jar to fetch, `Jar` — a direct download url instead of the version manifest, `Args` — extra client arguments |
| `Sources` | `Url` — where the kernel is fetched from, used as it stands so a mirror prefix can be glued in front, `Index` — the manifest file name, `Format` — `sha256-lines`, `plain` or `regex`, `Pattern` — the two capture groups, hash then path, of the regex form |
| `Template` | `Url` — where the template catalog is fetched from, `Base` — the base the example files are pulled from, replacing the one written in the catalog; either may be left out, and both take a mirror prefix the same way `Sources` does |
| `InternalsVisibleTo` | one `Assembly` per friend assembly with `Name`, emitted as `[assembly: InternalsVisibleTo]` |
| `AvaloniaResources` | one `Resource` per pattern with `Include`, packed into the `!AvaloniaResources` resource the Avalonia asset loader reads; every `*.axaml` of the project is picked up as well, so only plain assets have to be listed |
| `EmbeddedResources` | one `Resource` per pattern with `Include` and an optional `LogicalName`; every matched file is embedded into the built assembly. Without `LogicalName` the resource name is the root namespace plus the file path relative to the project root, slashes turned into dots, so files outside the project root need an explicit one |
| `Deploy` | `To` — semicolon separated directories the built dll is copied into right after a successful build |
| `Tasks` | one `Task` per task with `Name`, `Description`, `Depends` and `Override`, holding `Exec` steps that run a command line, `Copy` steps that take `From` and `To`, and `Zip` steps that pack the `From` directory into the `To` file. `$(Configuration)`, `$(ProjectDir)`, `$(ModId)`, `$(ModName)`, `$(ModVersion)` and `$(env:NAME)` are substituted in every attribute |
| root | `Override` — the default for tasks that do not carry their own |

## License

Apache-2.0 — see [LICENSE](./LICENSE).
