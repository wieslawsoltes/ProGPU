using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Hmi;

namespace ProGPU.WinUI.Hmi.Designer;

public sealed partial class HmiDesignerHost
{
    private TextBox _browseNamespace = null!;
    private TextBox _browseIdentifier = null!;
    private TextBox _browseIndex = null!;
    private DataGrid _browseTable = null!;
    private HmiBrowseResult? _browseResult;
    private string? _browseProfileId;
    private bool _browseBusy;

    private FrameworkElement BuildNodeBrowserPane()
    {
        _browseNamespace = Input("Namespace URI (empty = namespace zero)", 290);
        _browseIdentifier = Input("Namespace-local node identifier", 230); _browseIdentifier.Text = "i=85";
        _browseIndex = Input("Result row", 90); _browseIndex.Text = "0";
        var tools = Toolbar();
        tools.AddChild(Text("OPC UA NODE BROWSER", 11));
        tools.AddChild(_browseNamespace); tools.AddChild(_browseIdentifier);
        tools.AddChild(Command("Browse children", () => _ = BrowseHardwareNodesAsync()));
        tools.AddChild(_browseIndex);
        tools.AddChild(Command("Use as browse root", () =>
        {
            var node = SelectedBrowseNode();
            _browseNamespace.Text = node.NamespaceUri; _browseIdentifier.Text = node.Identifier;
        }));
        tools.AddChild(Command("Map variable", MapBrowsedNode));
        _browseTable = Table(("Row", "45", "Index"), ("Display name", "220", "Name"), ("Class", "80", "Class"),
            ("Namespace URI", "260", "Namespace"), ("Identifier", "*", "Identifier"));
        _browseTable.Height = 180;
        var pane = new StackPanel();
        pane.AddChild(tools); pane.AddChild(_browseTable);
        pane.AddChild(Text("Browse requires an explicit OPC UA connection; zero mappings are allowed. Disconnect before mapping. Enter an existing tag above. New mappings are read-only; select the exact server scalar type in the mapping table.", 10));
        return pane;
    }
    private HmiBrowseNode SelectedBrowseNode()
    {
        int index = Integer(_browseIndex.Text);
        if (_browseResult == null || index < 0 || index >= _browseResult.Nodes.Count) throw new InvalidOperationException("Choose a valid node-browser result row.");
        return _browseResult.Nodes[index];
    }
    private async Task BrowseHardwareNodesAsync()
    {
        int generation = _connectionGeneration;
        if (_browseBusy) { Status("A node browse is already in progress."); return; }
        try
        {
            if (_connectedTransport is not IHmiNodeBrowser browser || _connectionLifetime == null || _connectedProfileId == null)
                throw new InvalidOperationException("Connect an OPC UA profile explicitly before browsing.");
            string profileId = _connectedProfileId;
            string namespaceUri = _browseNamespace.Text, identifier = _browseIdentifier.Text;
            var token = _connectionLifetime.Token;
            _browseBusy = true;
            var result = await browser.BrowseAsync(namespaceUri, identifier, 256, token).ConfigureAwait(false);
            await DispatchRuntimeAsync(() =>
            {
                if (_disposed || generation != _connectionGeneration) return;
                _browseResult = result; _browseProfileId = profileId;
                _browseTable.ClearItems();
                for (int index = 0; index < result.Nodes.Count; index++)
                {
                    var node = result.Nodes[index];
                    _browseTable.AddItem(new HmiEditorRow(new()
                    {
                        ["Index"] = index.ToString(Invariant), ["Name"] = node.DisplayName, ["Class"] = node.NodeClass,
                        ["Namespace"] = node.NamespaceUri, ["Identifier"] = node.Identifier
                    }));
                }
                Status($"Browsed {result.Nodes.Count} nodes{(result.Truncated ? " (bounded result truncated; browse a more specific node)" : "")}. Browsing does not change the project.");
            }, token).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            UIThread.Post(() => { if (!_disposed && generation == _connectionGeneration) Status("Browse failed: " + error.Message, true); });
        }
        finally { UIThread.Post(() => _browseBusy = false); }
    }
    private void MapBrowsedNode() => DesignCommand(() =>
    {
        var node = SelectedBrowseNode();
        if (node.NodeClass != "Variable") throw new InvalidOperationException("Only variable nodes can be mapped to process tags.");
        Session.Edit("Map browsed OPC UA variable", project =>
        {
            var profile = FindProfile(project);
            if (profile.Id != _browseProfileId || profile.Protocol != HmiConnectionProtocol.OpcUa)
                throw new InvalidOperationException("Select the OPC UA profile that produced these browse results.");
            var tag = project.Tags.SingleOrDefault(t => t.Name == _mappingTag.Text) ?? throw new InvalidOperationException("Enter an existing tag name in the mapping toolbar.");
            if (profile.Mappings.Any(m => m.Tag == tag.Name)) throw new InvalidOperationException("This tag already has a mapping. Edit or remove it first.");
            profile.Mappings.Add(new HmiIoMapping
            {
                Tag = tag.Name, Type = tag.Type, Writable = false,
                OpcUa = new HmiOpcUaAddress
                {
                    NamespaceUri = node.NamespaceUri, Identifier = node.Identifier,
                    DataType = tag.Type == HmiTagType.Boolean ? HmiOpcUaDataType.Boolean : tag.Type == HmiTagType.Text ? HmiOpcUaDataType.String : HmiOpcUaDataType.Double
                }
            });
        });
        Status("Read-only node mapping added. Verify the server scalar type before acquiring; writable permission remains explicit.");
    });
}
