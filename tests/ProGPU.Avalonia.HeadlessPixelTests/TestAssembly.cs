global using Avalonia.Headless.XUnit;
global using Xunit;

using Avalonia.Headless;
using ProGPU.Avalonia.HeadlessPixelTests;

[assembly: AvaloniaTestApplication(typeof(TestApplication))]
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerTest)]
