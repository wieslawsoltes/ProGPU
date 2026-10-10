using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

internal static partial class Program
{
    private static void CaptureShaderAffineMath(string directory,string commit,Stopwatch timer)
    {
        using var sdk=new OriginalAxisSdk(commit);
        var cases=ShaderAffineMathOracle.Cases();
        int atomic=sdk.VerifyAffineAtomicControls(cases[0].Values);
        var observations=new List<object>(); var failures=new List<string>();
        var arithmeticInputs=ShaderAffineMathOracle.ArithmeticInputs();
        var arithmetic=sdk.CaptureArithmetic(arithmeticInputs);
        int arithmeticAtomic=sdk.VerifyArithmeticAtomicControls(),corruptions=0;
        try { ShaderAffineMathOracle.CheckArithmetic(arithmeticInputs,arithmetic.Values,arithmetic.Traits); }
        catch(InvalidOperationException error) { failures.Add(error.Message); }
        foreach(var input in cases)
        {
            CheckPaddingDeadline(timer);
            var actual=sdk.CaptureAffine(input.Values);
            try
            {
                if(!actual.Traits.SequenceEqual(arithmetic.Traits)) throw new InvalidOperationException("SDK arithmetic and matrix backends differ.");
                corruptions+=ShaderAffineMathOracle.VerifyCorruptionControls(input,actual.Values,actual.Traits);
            }
            catch(InvalidOperationException error) { failures.Add(error.Message); }
            observations.Add(new {input.Name,OriginalDoubleBits=input.Values.Select(x=>BitConverter.DoubleToInt64Bits(x).ToString("X16")).ToArray(),
                actual.Values,actual.Traits,FloatBits=actual.Values.Select(x=>BitConverter.SingleToUInt32Bits(x).ToString("X8")).ToArray()});
        }
        if(observations.Count!=8 || atomic!=4) throw new InvalidOperationException("Incomplete original affine arithmetic inventory.");
        var receipt=new {Schema=1,SourceCommit=commit,Architecture=RuntimeInformation.ProcessArchitecture.ToString(),
            SdkCompanion=sdk.Identity,CaseCount=8,Cases=observations,AtomicControls=atomic,Failures=failures,
            ArithmeticControls=new {OriginalDoubleBits=arithmeticInputs.Select(x=>BitConverter.DoubleToInt64Bits(x).ToString("X16")).ToArray(),
                arithmetic.Values,arithmetic.Traits,FloatBits=arithmetic.Values.Select(x=>BitConverter.SingleToUInt32Bits(x).ToString("X8")).ToArray(),
                AtomicControls=arithmeticAtomic,RejectedMatrixBitMutations=corruptions},
            QualifiedSdkCases=failures.Count==0?8:0,QualifiedNativeCases=0,QualifiedHardwareCases=0,
            FloatLayout="world,scale,inverseScale,residual,final,unit,inverseUnit,projection,unitProjection: nine row-major16-float matrices; allocation[144:148],localLTRB[148:152],rootDPI[152:154],scaleXY[154:156]",
            Qualification="Original installed SDK full matrices only; no wpfgfx build-provenance, original hardware pixel or native-renderer claim."};
        using(var file=new FileStream(Path.Combine(directory,failures.Count==0?"shader-affine-math.json":"shader-affine-math.failed.json"),FileMode.CreateNew))
            JsonSerializer.Serialize(file,receipt,new JsonSerializerOptions{WriteIndented=true});
        if(failures.Count!=0) throw new InvalidOperationException(string.Join(Environment.NewLine,failures));
    }
}
