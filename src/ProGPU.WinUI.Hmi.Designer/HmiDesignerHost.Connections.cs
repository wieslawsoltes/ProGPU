using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Hmi;

namespace ProGPU.WinUI.Hmi.Designer;

public sealed partial class HmiDesignerHost
{
    private DataGrid _connectionTable = null!;
    private DataGrid _mappingTable = null!;
    private TextBox _connectionId = null!;
    private TextBox _mappingTag = null!;
    private TextBox _operator = null!;
    private TextBox _reason = null!;
    private TextBlock _pendingCommand = null!;

    /// <summary>Optional transport plug-in factory. Constructing/opening a project never invokes this factory.</summary>
    public Func<HmiConnectionProfile, IHmiConnection>? ConnectionFactory { get; set; }
    /// <summary>Required for external writes. The embedding host must verify authenticated identity and endpoint/tag ACLs.</summary>
    public HmiWriteAuthorizer? WriteAuthorizer { get; set; }

    private FrameworkElement BuildConnectionsPane()
    {
        _connectionId = Input("Selected connection ID", 235);
        _mappingTag = Input("Tag to map / remove", 200);
        _operator = Input("Operator audit identity", 180);
        _reason = Input("Reason for write", 240);
        _pendingCommand = Text("External commands are disabled without a host-supplied authorization policy.", 11);
        var tools = Toolbar();
        tools.AddChild(Command("+ Modbus TCP", () => AddConnection(HmiConnectionProtocol.ModbusTcp)));
        tools.AddChild(Command("+ MQTT / TLS", () => AddConnection(HmiConnectionProtocol.Mqtt)));
        tools.AddChild(Command("+ OPC UA", () => AddConnection(HmiConnectionProtocol.OpcUa)));
        tools.AddChild(_connectionId);
        tools.AddChild(Command("Select", RefreshConnectionTables));
        tools.AddChild(Command("Connect read-only", () => _ = ConnectHardwareAsync()));
        tools.AddChild(Command("Disconnect", StopPreview));
        tools.AddChild(Command("Refresh", () => { RefreshConnectionTables(); RefreshMonitor(); }));
        tools.AddChild(Command("Delete connection", () => DesignCommand(() => Session.Edit("Delete connection", p => p.Connections.RemoveAll(c => c.Id == _connectionId.Text)))));
        var mappingTools = Toolbar();
        mappingTools.AddChild(_mappingTag);
        mappingTools.AddChild(Command("+ Mapping", AddMapping));
        mappingTools.AddChild(Command("Remove mapping", () => DesignCommand(() => Session.Edit("Remove mapping", p => FindProfile(p).Mappings.RemoveAll(m => m.Tag == _mappingTag.Text)))));
        mappingTools.AddChild(Text("Modbus addresses are zero-based. Plain TCP requires explicit permission. Writable mappings alone do not authorize commands.", 10));
        var commandTools = Toolbar();
        commandTools.AddChild(_operator); commandTools.AddChild(_reason);
        commandTools.AddChild(Command("Confirm pending write", () => _ = ConfirmHardwareWriteAsync()));
        commandTools.AddChild(Command("Discard pending write", DiscardHardwareWrite));
        _connectionTable = Table(("ID", "155", "Id"), ("Name", "150", "Name"), ("Protocol", "95", "Protocol"), ("Host", "180", "Host"), ("Port", "65", "Port"), ("TLS", "55", "Tls"), ("Allow plain", "85", "Plain"), ("Unit", "55", "Unit"), ("Poll ms", "70", "Poll"), ("Timeout ms", "90", "Timeout"), ("UA path", "160", "UaPath"), ("UA security", "125", "UaSecurity"), ("UA policy", "180", "UaPolicy"));
        _connectionTable.Height = 100;
        _mappingTable = Table(("Tag", "150", "Tag"), ("Area", "130", "Area"), ("Address", "70", "Address"), ("Encoding", "95", "Encoding"), ("Order", "135", "Order"), ("Scale", "70", "Scale"), ("Offset", "70", "Offset"), ("Writable", "65", "Writable"), ("Permissive tag", "160", "Interlock"), ("Topic", "200", "Topic"), ("Command topic", "200", "Command"), ("UA namespace", "240", "UaNamespace"), ("UA identifier", "200", "UaIdentifier"), ("UA scalar", "95", "UaType"));
        _mappingTable.Height = 170;
        var panel = new StackPanel();
        panel.AddChild(tools); panel.AddChild(_connectionTable); panel.AddChild(mappingTools); panel.AddChild(_mappingTable);
        panel.AddChild(commandTools); panel.AddChild(_pendingCommand);
        panel.AddChild(BuildNodeBrowserPane());
        return new ScrollViewer { Content = panel, HorizontalScrollMode = ScrollMode.Enabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
    private HmiConnectionProfile FindProfile(HmiProject project) =>
        project.Connections.SingleOrDefault(c => c.Id == _connectionId.Text) ?? throw new InvalidOperationException("Select an existing connection ID.");
    private void AddConnection(HmiConnectionProtocol protocol) => DesignCommand(() =>
    {
        string id = "controller-" + (Session.Document.Connections.Count + 1);
        while (Session.Document.Connections.Any(c => c.Id == id)) id += "-new";
        _connectionId.Text = id;
        Session.Edit("Add connection", p => p.Connections.Add(new HmiConnectionProfile
        {
            Id = id, Name = protocol == HmiConnectionProtocol.OpcUa ? "OPC UA server" : protocol == HmiConnectionProtocol.Mqtt ? "MQTT broker" : "Modbus controller", Protocol = protocol,
            Host = "localhost", UseTls = protocol == HmiConnectionProtocol.Mqtt, Port = protocol == HmiConnectionProtocol.OpcUa ? 4840 : protocol == HmiConnectionProtocol.Mqtt ? 8883 : 502
        }));
    });
    private void AddMapping() => DesignCommand(() => Session.Edit("Add I/O mapping", p =>
    {
        var profile = FindProfile(p);
        var tag = p.Tags.SingleOrDefault(t => t.Name == _mappingTag.Text) ?? throw new InvalidOperationException("Enter an existing project tag name.");
        if (profile.Protocol == HmiConnectionProtocol.ModbusTcp && tag.Type == HmiTagType.Text) throw new InvalidOperationException("This Modbus adapter supports numeric registers and Boolean bit areas, not strings.");
        profile.Mappings.Add(new HmiIoMapping
        {
            Tag = tag.Name, Type = tag.Type, Area = tag.Type == HmiTagType.Boolean ? HmiModbusArea.Coil : HmiModbusArea.HoldingRegister,
            Encoding = tag.Type == HmiTagType.Boolean ? HmiRegisterEncoding.Boolean : HmiRegisterEncoding.UInt16,
            Topic = "plant/" + tag.Name, CommandTopic = "plant/" + tag.Name + "/set",
            OpcUa = new HmiOpcUaAddress { Identifier = "s=" + tag.Name, DataType = tag.Type == HmiTagType.Boolean ? HmiOpcUaDataType.Boolean : tag.Type == HmiTagType.Text ? HmiOpcUaDataType.String : HmiOpcUaDataType.Double }
        });
    }));
    private void RefreshConnectionTables()
    {
        if (_connectionTable == null) return;
        _connectionTable.ClearItems(); _mappingTable.ClearItems();
        if (Session.Document.Connections.Count > 0 && !Session.Document.Connections.Any(c => c.Id == _connectionId.Text)) _connectionId.Text = Session.Document.Connections[0].Id;
        foreach (var profile in Session.Document.Connections)
        {
            string id = profile.Id;
            _connectionTable.AddItem(new HmiEditorRow(new()
            {
                ["Id"] = id, ["Name"] = profile.Name, ["Protocol"] = profile.Protocol.ToString(), ["Host"] = profile.Host, ["Port"] = profile.Port.ToString(Invariant),
                ["Tls"] = profile.UseTls.ToString(), ["Plain"] = profile.AllowUnsecuredTransport.ToString(), ["Unit"] = profile.UnitId.ToString(Invariant),
                ["Poll"] = profile.PollMilliseconds.ToString(Invariant), ["Timeout"] = profile.TimeoutMilliseconds.ToString(Invariant), ["UaPath"] = profile.OpcUa.EndpointPath, ["UaSecurity"] = profile.OpcUa.SecurityMode.ToString(), ["UaPolicy"] = profile.OpcUa.SecurityPolicy.ToString()
            }, (property, value) => DesignCommand(() => Session.Edit("Edit connection", p =>
            {
                var target = p.Connections.Single(c => c.Id == id);
                switch (property)
                {
                    case "Name": target.Name = value; break;
                    case "Host": target.Host = value.Trim(); break;
                    case "Port": target.Port = Integer(value); break;
                    case "Tls": target.UseTls = Boolean(value); break;
                    case "Plain": target.AllowUnsecuredTransport = Boolean(value); break;
                    case "Unit": target.UnitId = checked((byte)Integer(value)); break;
                    case "Poll": target.PollMilliseconds = Integer(value); break;
                    case "Timeout": target.TimeoutMilliseconds = Integer(value); break;
                    case "UaPath": target.OpcUa.EndpointPath = value; break;
                    case "UaSecurity": target.OpcUa.SecurityMode = Choice<HmiOpcUaSecurityMode>(value); break;
                    case "UaPolicy": target.OpcUa.SecurityPolicy = Choice<HmiOpcUaSecurityPolicy>(value); break;
                    default: throw new InvalidOperationException("Connection identity and protocol are read-only; add a new profile to change protocol.");
                }
            })), error => Status(error, true)));
        }
        var selected = Session.Document.Connections.SingleOrDefault(c => c.Id == _connectionId.Text);
        if (selected == null) return;
        foreach (var mapping in selected.Mappings)
        {
            string tag = mapping.Tag, connection = selected.Id;
            _mappingTable.AddItem(new HmiEditorRow(new()
            {
                ["Tag"] = tag, ["Area"] = mapping.Area.ToString(), ["Address"] = mapping.Address.ToString(Invariant), ["Encoding"] = mapping.Encoding.ToString(),
                ["Order"] = mapping.Order.ToString(), ["Scale"] = Format(mapping.Scale), ["Offset"] = Format(mapping.Offset), ["Writable"] = mapping.Writable.ToString(),
                ["Interlock"] = mapping.InterlockTag, ["Topic"] = mapping.Topic, ["Command"] = mapping.CommandTopic, ["UaNamespace"] = mapping.OpcUa.NamespaceUri, ["UaIdentifier"] = mapping.OpcUa.Identifier, ["UaType"] = mapping.OpcUa.DataType.ToString()
            }, (property, value) => DesignCommand(() => Session.Edit("Edit I/O mapping", p =>
            {
                var target = p.Connections.Single(c => c.Id == connection).Mappings.Single(m => m.Tag == tag);
                switch (property)
                {
                    case "Area": target.Area = Choice<HmiModbusArea>(value); break;
                    case "Address": target.Address = Integer(value); break;
                    case "Encoding": target.Encoding = Choice<HmiRegisterEncoding>(value); break;
                    case "Order": target.Order = Choice<HmiRegisterOrder>(value); break;
                    case "Scale": target.Scale = Number(value); break;
                    case "Offset": target.Offset = Number(value); break;
                    case "Writable": target.Writable = Boolean(value); break;
                    case "Interlock": target.InterlockTag = value.Trim(); break;
                    case "Topic": target.Topic = value; break;
                    case "Command": target.CommandTopic = value; break;
                    case "UaNamespace": target.OpcUa.NamespaceUri = value; break;
                    case "UaIdentifier": target.OpcUa.Identifier = value; break;
                    case "UaType": target.OpcUa.DataType = Choice<HmiOpcUaDataType>(value); break;
                    default: throw new InvalidOperationException("Use project tag rename to rename mapped tags consistently.");
                }
            })), error => Status(error, true)));
        }
        if (_acquisition != null)
        {
            var diagnostic = _acquisition.Diagnostics;
            Status($"{diagnostic.State}: {diagnostic.Message} · polls {diagnostic.Polls} · failures {diagnostic.Failures} · last poll {diagnostic.LastPollMilliseconds:F1} ms");
        }
    }
}
