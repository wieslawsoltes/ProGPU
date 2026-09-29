using Microsoft.UI.Xaml;
using ProGPU.WinUI.Hmi.Designer;

namespace ProGPU.Hmi.Sample;

public static class Program
{
    public static void Main(string[] args)
    {
        AppBuilder<HmiApplication>.Configure()
            .WithTitle("ProGPU HMI Designer")
            .WithSize(1600, 1000)
            .Build()
            .Run(args);
    }
}

public sealed class HmiApplication : Application
{
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var designer = new HmiDesignerHost { ConnectionFactory = profile => profile.Protocol switch
            {
                ProGPU.Hmi.HmiConnectionProtocol.ModbusTcp => new ProGPU.Hmi.Modbus.HmiModbusConnection(profile),
                ProGPU.Hmi.HmiConnectionProtocol.Mqtt => new ProGPU.Hmi.Mqtt.HmiMqttConnection(profile),
                ProGPU.Hmi.HmiConnectionProtocol.OpcUa => new ProGPU.Hmi.OpcUa.HmiOpcUaConnection(profile),
                _ => throw new NotSupportedException("Protocol not registered.")
            } };
        var window = new Window { Title = "ProGPU HMI Designer · Local simulation", Width = 1600, Height = 1000, Content = designer };
        window.Activate();
        UIThread.Post(designer.Fit);
    }
}
