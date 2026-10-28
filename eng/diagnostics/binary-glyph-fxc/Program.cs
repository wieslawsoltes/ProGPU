using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static unsafe class Program
{
    [DllImport("d3dcompiler_47.dll", CallingConvention=CallingConvention.StdCall, CharSet=CharSet.Ansi, ExactSpelling=true)]
    static extern int D3DCompile(byte[] source, nuint size, string name, nint defines, nint include,
        string entry, string target, uint flags, uint flags2, out nint code, out nint errors);
    static byte[] CopyBlob(nint blob)
    {
        if (blob == 0) return [];
        var table=*(nint**)blob;
        var ptr=((delegate* unmanaged[Stdcall]<nint,nint>)table[3])(blob);
        var length=((delegate* unmanaged[Stdcall]<nint,nuint>)table[4])(blob);
        if (length > int.MaxValue) throw new InvalidOperationException("Oversized compiler blob");
        return new ReadOnlySpan<byte>((void*)ptr,(int)length).ToArray();
    }
    static void Release(nint blob)
    {
        if (blob != 0) ((delegate* unmanaged[Stdcall]<nint,uint>)(*(nint**)blob)[2])(blob);
    }
    static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows() || args.Length!=2) return 2;
        var input=Path.GetFullPath(args[0]); var output=Path.GetFullPath(args[1]);
        Directory.CreateDirectory(output);
        var compiler=Path.Combine(Environment.SystemDirectory,"d3dcompiler_47.dll");
        nint module=NativeLibrary.Load(compiler);
        NativeLibrary.SetDllImportResolver(typeof(Program).Assembly,(name,_,_)=>name=="d3dcompiler_47.dll"?module:0);
        File.WriteAllText(Path.Combine(output,"compiler.json"),JsonSerializer.Serialize(new {
            path=compiler,sha256=Hash(File.ReadAllBytes(compiler)),version=FileVersionInfo.GetVersionInfo(compiler).FileVersion,
            processArchitecture=RuntimeInformation.ProcessArchitecture.ToString(),runtime=RuntimeInformation.FrameworkDescription,
            sourceName="ProGPU binary glyph scan shader",target="cs_5_1",flags1=0x800,flags2=0,
            gpuDeviceCreated=false,inputActions=0
        },new JsonSerializerOptions{WriteIndented=true}));
        bool accepted=true;
        foreach (string variant in new[]{"original","candidate"})
        foreach (string entry in new[]{"cs_binary_horizontal","cs_binary_vertical"})
        {
            var source=File.ReadAllBytes(Path.Combine(input,variant+".hlsl"));
            nint code=0,error=0; string stem=variant+"-"+entry;
            File.WriteAllText(Path.Combine(output,stem+"-started.json"),JsonSerializer.Serialize(new {variant,entry,sourceSha256=Hash(source),utc=DateTimeOffset.UtcNow}));
            var clock=Stopwatch.StartNew();
            try {
                int hr=D3DCompile(source,(nuint)source.Length,"ProGPU binary glyph scan shader",0,0,entry,"cs_5_1",0x800,0,out code,out error);
                var bytecode=CopyBlob(code); var errors=CopyBlob(error);
                var message=Encoding.UTF8.GetString(errors);
                File.WriteAllBytes(Path.Combine(output,stem+"-errors.bin"),errors);
                File.WriteAllText(Path.Combine(output,stem+"-errors.txt"),message.Replace("\0","\\0"));
                File.WriteAllBytes(Path.Combine(output,stem+".dxbc"),bytecode);
                bool expected=variant=="original" ? hr<0 && message.Contains("X3550") && message.Contains("X3511") : hr==0 && bytecode.Length>0;
                accepted &= expected;
                File.WriteAllText(Path.Combine(output,stem+".json"),JsonSerializer.Serialize(new {
                    variant,entry,sourceSha256=Hash(source),hresult=$"0x{unchecked((uint)hr):x8}",elapsedSeconds=clock.Elapsed.TotalSeconds,
                    bytecodeBytes=bytecode.Length,bytecodeSha256=Hash(bytecode),errorBytes=errors.Length,errorSha256=Hash(errors),expected
                },new JsonSerializerOptions{WriteIndented=true}));
                Console.WriteLine($"{stem}: HRESULT=0x{unchecked((uint)hr):x8}, bytecode={bytecode.Length}, expected={expected}");
            } finally { Release(error);Release(code); }
        }
        // Retain the module until all P/Invoke/delegate use ends at process exit.
        return accepted?0:1;
    }
}
