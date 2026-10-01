# ProGPU DCS

Standalone operator and engineering workplaces using ProGPU's WinUI-compatible UI and retained HMI graphics. The original Northwater training project opens offline.

```sh
dotnet run --project samples/HmiDcs/HmiDcs.csproj -c Release
```

Use the display tabs or Plant Explorer, then select equipment to open its faceplate. Alarms, events, trends and system diagnostics share the object context. **Simulate** explicitly starts local data; Review and Confirm local are separate actions. No equipment is connected by this workflow.

**Engineering Workplace** opens the full existing designer on demand. Edits, undo and unsaved state are retained when returning to Operator; the updated runtime snapshot starts offline. The engineering connections pane retains explicit Modbus TCP, MQTT and OPC UA commissioning. No permissive external-write authorizer is installed.

The visual/workflow reference is ABB System 800xA, implemented as an original ProGPU application—not an ABB product, proprietary project-format clone or certified industrial control system. This is ProGPU's `Microsoft.UI.Xaml` implementation, not Microsoft Windows App SDK.

See [workplace architecture, embedding, safeguards, tests and coverage](../../docs/hmi-dcs-workplace.md).
