# Serial-port plugins: the host provides System.IO.Ports

**Context.** The Elecraft KPA500 amplifier plugin (issue #1351) is the first
plugin that talks RS-232. It was originally proposed as a code drop in this
repo (PR #1354); the plugin itself lives in
`openhpsdr-zeus-plugins/amplifiers/Kpa500/` (id `org.openhpsdr.kpa500`). This
lesson records what the audit of that PR established about how serial I/O
reaches a plugin at runtime — it is not obvious from a successful build.

## The load-bearing facts

1. **`PluginLoadContext` delegates every `System.*` assembly to the default
   ALC** (`Zeus.Plugins.Host/PluginLoader.cs`). That delegation exists so
   contract/runtime types keep one identity across host and plugin. The
   consequence for dependencies: a plugin can NEVER load its own copy of a
   `System.*` package — whatever it ships under that prefix in its zip is
   ignored, and resolution falls through to the host's dependency graph.

2. **`System.IO.Ports` is host-provided.** `Zeus.Server.Hosting` has
   referenced `System.IO.Ports` (10.0.0) since the G2-Ultra front-panel /
   CAT serial work (#861), so the host's deps graph resolves it — including
   the RID-specific implementation (`runtimes/unix|win`) — on every platform
   Zeus publishes for. Plugins get it "for free" through the delegation above.

3. **A `dotnet build` of a serial plugin proves nothing about runtime
   loading.** If the host ever dropped its `System.IO.Ports` reference, every
   serial plugin would still compile and then throw `FileNotFoundException`
   at first `SerialPort` touch. Treat the host reference as a plugin-facing
   contract: do not remove it from `Zeus.Server.Hosting.csproj` without
   checking the plugin registry for serial-control plugins (KPA500 at
   minimum).

## Packaging rules for serial plugins

- The plugin csproj keeps `<PackageReference Include="System.IO.Ports">`
  **pinned to the same version the host references** (compile-time surface
  only).
- The release zip must NOT include `System.IO.Ports.dll`, a `runtimes/`
  tree, or `<plugin>.deps.json` for it — the loader ignores them (fact 1)
  and they bloat/confuse the package. Zip = `plugin.json` (root) + plugin
  dll + README + `ui/*.es.js`, same as every other plugin.

## Cross-platform notes

- `SerialPort.GetPortNames()` on Linux does not enumerate udev symlinks
  (`/dev/KPA500`). Serial-plugin UIs must accept a free-typed device path in
  addition to the scanned list (the KPA500 settings drawer uses an input +
  `datalist` for exactly this).
- On macOS/Linux, unplugging a USB-serial adapter leaves
  `SerialPort.IsOpen == true` while every read throws. Reconnect logic must
  close and recreate the `SerialPort` after consecutive failures — polling
  the same object "until it recovers" never recovers.
