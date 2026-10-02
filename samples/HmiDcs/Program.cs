using Microsoft.UI.Xaml;
using ProGPU.Hmi;

namespace ProGPU.Hmi.Dcs.Sample;

public static class Program
{
    public static void Main(string[] args) => AppBuilder<DcsApplication>.Configure()
        .WithTitle("ProGPU DCS / Operator and Engineering Workplaces")
        .WithSize(1600, 1000).Build().Run(args);
}

public sealed class DcsApplication : Application
{
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var studio = DcsStartup.CreateStudio();
        studio.ConnectionFactory = profile => profile.Protocol switch
        {
            HmiConnectionProtocol.ModbusTcp => new Modbus.HmiModbusConnection(profile),
            HmiConnectionProtocol.Mqtt => new Mqtt.HmiMqttConnection(profile),
            HmiConnectionProtocol.OpcUa => new OpcUa.HmiOpcUaConnection(profile),
            _ => throw new NotSupportedException("Protocol not registered.")
        };
        // No endpoint or permissive WriteAuthorizer is installed. Startup is offline.
        var window = new Window { Title = "ProGPU DCS / Northwater", Width = 1600, Height = 1000, Content = studio };
        window.Closed += (_, _) => studio.Dispose();
        window.Activate();
    }
}
