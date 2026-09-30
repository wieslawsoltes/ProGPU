using System.Text.Json;
using ProGPU.Samples;

if (JsonSerializer.IsReflectionEnabledByDefault)
    throw new InvalidOperationException("The gallery regression probe must disable reflection serialization.");

// This is the exact page factory called by NavigationViewItem.GetOrCreatePage
// in the reported desktop crash. No replacement test-only designer factory is used.
var page = VisualDesignerPage.Create();
if (page == null) throw new InvalidOperationException("The gallery did not create its Visual Designer / HMI page.");
GC.KeepAlive(page);
Console.WriteLine("PASS: actual VisualDesignerPage.Create with reflection JSON disabled; shared visual designer and standalone HMI/DCS sample entry constructed.");
// This test does not create a native window, GPU device or physical input source.
