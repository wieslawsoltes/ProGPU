// Observe the actual original API; no generated source-array extraction.
using System.Reflection;
using ProGPU.Text;
static class Control
{
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    static void Main()
    {
        var type = typeof(TtfFont).Assembly.GetType("ProGPU.Text.ArabicJoiningData", true)!;
        var method = type.GetMethod("GetJoiningType", Flags)!;
        typeof(Control).GetMethod(nameof(Capture), Flags)!.MakeGenericMethod(method.ReturnType)
            .Invoke(null, new object[] { method });
    }
    static void Capture<T>(MethodInfo method) where T : struct, Enum
    {
        var query = method.CreateDelegate<Func<uint, T>>();
        using var output = new BufferedStream(Console.OpenStandardOutput(), 65536);
        for (uint point = 0; point <= 0x110000; point++)
            output.WriteByte(Convert.ToByte(query(point)));
        output.WriteByte(Convert.ToByte(query(uint.MaxValue)));
    }
}
