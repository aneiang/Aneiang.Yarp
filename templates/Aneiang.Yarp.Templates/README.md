# Aneiang.Yarp.Templates

`dotnet new` templates for [Aneiang.Yarp](https://github.com/aneiang/Aneiang.Yarp): one for a
gateway, one for a plugin that plugs into a gateway.

```bash
# Preview: the version is explicit; drop the "::<version>" suffix once a stable build exists
dotnet new install Aneiang.Yarp.Templates::2.3.0.40-preview.1
```

## 网关：`aneiang-yarp`

```bash
dotnet new aneiang-yarp -n MyGateway
cd MyGateway
dotnet run
```

That produces a gateway with routing, a SQLite config store and the management dashboard already
wired together — then `http://localhost:5200/aneiang/login`.

| Option | Values | Default |
|:--|:--|:--|
| `--Framework` | `net8.0`, `net9.0`, `net10.0` | `net8.0` |

The generated `Dockerfile` pins its base images through `ARG DOTNET_VERSION`, which keeps the
template default (`8.0`). `--Framework` rewrites the csproj but **not** that ARG — the template
engine has no reliable way to derive `net10.0` → `10.0` here — so if you generated a `net9.0` or
`net10.0` project, build the image with the matching tag:

```bash
docker build --build-arg DOTNET_VERSION=10.0 .
```

An SDK cannot restore a project that targets a newer framework than itself, so a mismatch fails at
`docker build` rather than at `docker run`.

The package assets themselves are multi-targeted `net8.0;net9.0;net10.0`, so a gateway on any of
those three consumes the same package. `--Framework` decides only what the *generated project*
targets.

The project references the `Aneiang.Yarp`, `Aneiang.Yarp.Dashboard` and `Aneiang.Yarp.Storage.Sqlite`
packages. The dashboard package brings the built-in plugins with it (it has to know their schemas to
configure them); a gateway without the dashboard can reference just the plugins it runs.

## 插件：`aneiang-yarp-plugin`

```bash
dotnet new aneiang-yarp-plugin -n MyPlugin --PluginId my-plugin
dotnet publish MyPlugin -c Release -o out
# copy out/ into the host's plugins/my-plugin/  (or install that path from the Plugin Center)
```

| Option | Values | Default |
|:--|:--|:--|
| `--Framework` | `net8.0`, `net9.0`, `net10.0` | `net8.0` |
| `--PluginId` | any slug | `my-plugin` |

`plugin.json` is **generated at publish time** from `plugin.manifest.json` plus the csproj properties
`PluginId` / `PluginDisplayName` / `PluginEntryType` / `Version`. Edit the manifest for scopes,
capabilities, `order` and the config schema; edit the csproj for identity. The generated project has
its own README covering what the host validates at load time, and the compatibility matrix lives in
`docs/plugins/plugin-compatibility.md` in the repository.

The plugin is configurable out of the box: the manifest ships a three-field schema and the middleware
reads it per request from the gateway snapshot, so a binding edit shows up in the next response
without reloading anything. That is the shortest loop that proves both halves — the plugin is in the
pipeline, and the value it writes came from the dashboard.

The template references `Aneiang.Yarp.Plugin.Abstractions` and `Aneiang.Yarp`, both with
`ExcludeAssets="runtime"`: the host always uses its own copies, and shipping a second one would create
two `IGatewayPlugin` types that do not recognise each other.
