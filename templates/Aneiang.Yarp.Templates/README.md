# Aneiang.Yarp.Templates

`dotnet new` templates for [Aneiang.Yarp](https://github.com/aneiangsoft/Aneiang.Yarp): one for a
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
| `--Framework` | `net8.0`, `net9.0` | `net8.0` |

The project references the `Aneiang.Yarp`, `Aneiang.Yarp.Dashboard` and `Aneiang.Yarp.Storage.Sqlite`
packages. The dashboard package brings the built-in plugins with it (it has to know their schemas to
configure them); a gateway without the dashboard can reference just the plugins it runs.

## 插件：`aneiang-yarp-plugin`

```bash
dotnet new aneiang-yarp-plugin -n MyPlugin --PluginId my-plugin
dotnet publish MyPlugin -c Release -o out
# copy out/ into the host's plugins/my-plugin/
```

| Option | Values | Default |
|:--|:--|:--|
| `--Framework` | `net8.0`, `net9.0` | `net8.0` |
| `--PluginId` | any slug | `my-plugin` |

`plugin.json` is **generated at publish time** from `plugin.manifest.json` plus the csproj properties
`PluginId` / `PluginDisplayName` / `PluginEntryType` / `Version`. Edit the manifest for scopes,
capabilities, `order` and the config schema; edit the csproj for identity. The generated project has
its own README covering what the host validates at load time, and the compatibility matrix lives in
`docs/plugins/plugin-compatibility.md` in the repository.

The template references `Aneiang.Yarp.Plugin.Abstractions` with `ExcludeAssets="runtime"`: the host
always uses its own copy of the contract assembly, and shipping a second one would create two
`IGatewayPlugin` types that do not recognise each other.
