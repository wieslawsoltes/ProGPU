# ProGPU HMI Designer sample

A complete HMI-design workbench hosted by ProGPU's cross-platform WinUI-compatible application layer. This is **not** a Microsoft Windows App SDK application; it uses the same `AppBuilder` and GPU-backed `Microsoft.UI.Xaml` implementation as the repository's other standalone WinUI samples.

```sh
dotnet run --project samples/HmiDesigner/HmiDesigner.csproj -c Release
```

The original water-treatment project opens in design mode. Choose **Components**, drag a symbol or press **+**, and edit its tags and actions in the **HMI** inspector. Use **Run / Stop** for a separate local simulation, **Pause / Resume** to pause time, and **Step 100 ms** to inspect transitions.

The lower tabs edit tags, alarms, recipes and versioned project JSON. The **Help** tab documents keyboard shortcuts and the main workflows. Enter a desktop file path in the top bar to open or save `.hmi.json` projects.

No equipment, network driver or PLC endpoint is connected. This is an authoring and simulation sample, not a safety system.

See [HMI architecture, component APIs and validation](../../docs/hmi-designer.md).
