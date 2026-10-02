# ProGPU DCS

Standalone operator and engineering workplaces using ProGPU's WinUI-compatible UI and retained HMI graphics. The original Northwater training project opens offline.

```sh
dotnet run --project samples/HmiDcs/HmiDcs.csproj -c Release
```

Startup loads the bundled Inter Regular face before constructing the workplace;
no installed Arial font, working-directory font file or prior sample launch is
required. An explicitly supplied host default font is preserved. The same font
is passed to the operator workplace and its lazily created engineering view.

Use the display tabs or Plant Explorer, then select equipment to open its faceplate. Alarms, events, trends and system diagnostics share the object context. **Simulate** explicitly starts local data; Review and Confirm local are separate actions. No equipment is connected by this workflow.

**Engineering Workplace** opens the full existing designer on demand. Edits, undo and unsaved state are retained when returning to Operator; the updated runtime snapshot starts offline. The engineering connections pane retains explicit Modbus TCP, MQTT and OPC UA commissioning. No permissive external-write authorizer is installed.

## Set up a project

Open **Engineering Workplace**, then use its wrapping **PROJECT SETUP** row:

1. **1. Tags** opens the existing Tags table, even if the data panel was hidden. Choose **Add tag**, then edit the row's name and type (`Number`, `Boolean` or `Text`), initial value and engineering unit. Changing type resets the initial value and simulation mode; set those after choosing the type. Components and I/O mappings refer to the exact tag name.
2. **2. PLC / connections** opens Connections. Add the actual protocol profile with **+ Modbus TCP**, **+ MQTT / TLS** or **+ OPC UA**; edit its host, port and security settings. Select its connection ID, enter an existing project tag under **Tag to map / remove**, and choose **+ Mapping**. Configure the real zero-based Modbus area/address/encoding, MQTT topic, or OPC UA namespace/identifier/type. A profile or a writable mapping does not connect equipment or authorize a command.
3. **3. Components** opens the existing component library. Search and drag, draw or insert a symbol on the design screen.
4. Select that component and choose **4. Bind selected**. In its **HMI** properties, set **Value tag** to the existing tag name. **Graphic convention** chooses Process, HighPerformance or Schematic presentation; it does not change the tag or protocol. Visibility and Enabled tags require Boolean values.

**Setup guide** opens the existing Help page. These shortcuts retain the current
selection, project, undo history and any running preview; stop simulation before
editing. On compact layouts, opening one side pane dismisses the other overlay.
The data shortcuts expand a small panel without shrinking an already larger one.

Use **File → Open / save location…** and **Save project** to save the project.
Simulation is local and explicit; it never automatically connects a PLC. For
equipment commissioning, review the profile, mappings and transport permissions,
then choose **Connect read-only** separately. This sample supplies Modbus TCP,
MQTT and OPC UA adapters, but starts offline and does not connect until that
explicit action. Embedders supply their own connection factory.
External writes still require a host-supplied authorization policy and separate
review/confirmation. Desktop visual validation of this setup workflow remains
pending under issue [#260](https://github.com/wieslawsoltes/ProGPU/issues/260).

The visual/workflow reference is ABB System 800xA, implemented as an original ProGPU application—not an ABB product, proprietary project-format clone or certified industrial control system. This is ProGPU's `Microsoft.UI.Xaml` implementation, not Microsoft Windows App SDK.

See [workplace architecture, embedding, safeguards, tests and coverage](../../docs/hmi-dcs-workplace.md).
