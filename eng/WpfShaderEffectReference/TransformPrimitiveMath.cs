using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

internal static partial class Program
{
    private static void CaptureTransformPrimitiveMath(string directory,string commit,Stopwatch timer)
    {
        using var sdk=new OriginalAxisSdk(commit);
        (string Name,double[] Values)[] inputs=[
            ("translate-quarter",[0,.25,-.5,0,0]),("scale-centered",[1,1.25,-.5,3.25,-4.5]),
            ("rotate-zero",[2,-0.0,0,3.25,-4.5]),("rotate-quarter",[2,90,0,3.25,-4.5]),
            ("rotate-negative",[2,-90,0,-3.25,4.5]),("rotate-large",[2,360000000.25,0,3.25,-4.5]),
            ("rotate-angle-ulp",[2,double.BitIncrement(17),0,double.BitIncrement(3.25),-4.5]),
            ("skew-centered",[3,17,-31,3.25,-4.5]),("skew-large",[3,360000000.25,-720.125,3.25,-4.5]),
            ("skew-signed-zero",[3,-0.0,0,-0.0,0])];
        int atomic=sdk.VerifyPrimitiveAtomicControls(inputs[0].Values);
        var observations=new List<object>(); var failures=new List<string>();
        foreach(var input in inputs)
        {
            CheckPaddingDeadline(timer);
            var actual=sdk.CapturePrimitive(input.Values);
            try { CheckPrimitiveCapture(input.Values,actual.Values,actual.Traits); }
            catch(InvalidOperationException error) { failures.Add(input.Name+": "+error.Message); }
            observations.Add(new {input.Name,OriginalDoubleBits=input.Values.Select(x=>BitConverter.DoubleToInt64Bits(x).ToString("X16")).ToArray(),
                actual.Values,actual.Traits,FloatBits=actual.Values.Select(x=>BitConverter.SingleToUInt32Bits(x).ToString("X8")).ToArray()});
        }
        var receipt=new {Schema=1,SourceCommit=commit,Architecture=RuntimeInformation.ProcessArchitecture.ToString(),
            SdkCompanion=sdk.Identity,CaseCount=10,Cases=observations,AtomicControls=atomic,Failures=failures,
            QualifiedNativeCases=0,QualifiedHardwareCases=0,TrigonometricOracle="ObservationOnly",
            FloatLayout="original narrowed parameters[0:4], modulo then narrow[4:6], SDK radians[6:8], core16[8:24], centered16[24:40]",
            Qualification="Original installed SDK primitive calls and exact independent transport/composition controls; no host-libm equality, native leaf or original hardware qualification."};
        using(var file=new FileStream(Path.Combine(directory,failures.Count==0?"transform-primitives.json":"transform-primitives.failed.json"),FileMode.CreateNew))
            JsonSerializer.Serialize(file,receipt,new JsonSerializerOptions{WriteIndented=true});
        if(failures.Count!=0) throw new InvalidOperationException(string.Join(Environment.NewLine,failures));
    }

    private static void CheckPrimitiveCapture(double[] input,float[] values,uint[] traits)
    {
        if(values.Length!=40 || values.Any(x=>!float.IsFinite(x)) || traits.Length!=5 || traits[0]==0 || traits[1]==0 ||
            traits[2] is not (0x8664 or 0xAA64) || traits[3]!=(traits[2]==0x8664?1U:2U) || traits[4]!=0)
            throw new InvalidOperationException("Unknown original primitive SDK contract.");
        void Equal(float expected,int index)
        {
            if(expected==0 && values[index]==0) return; // every observed sign remains in the receipt
            if(BitConverter.SingleToUInt32Bits(expected)!=BitConverter.SingleToUInt32Bits(values[index]))
                throw new InvalidOperationException($"Original primitive float[{index}] differs from the independent transport/composition expression.");
        }
        for(int i=0;i<4;++i) Equal((float)input[1+i],i);
        if(input[0]>=2)
        {
            for(int i=0;i<2;++i)
            {
                float reduced=(float)(input[1+i]%360.0); Equal(reduced,4+i);
                // Public XMConvertToRadians contract; no foreign polynomial.
                Equal(ShaderAxisOracle.Product(reduced,MathF.PI/180.0f),6+i);
            }
        }
        else { Equal((float)input[1],4); Equal((float)input[2],5); Equal(0,6); Equal(0,7); }
        // Compare centered composition independently, using the SDK primitive
        // core as an observation, NOT as evidence for any product trig result.
        for(int i=0;i<16;++i) if(i is not (12 or 13)) Equal(values[8+i],24+i);
        float P(float a,float b)=>ShaderAxisOracle.Product(a,b);
        float S(float a,float b)=>ShaderAxisOracle.Sum(a,b);
        float cx=(float)input[3],cy=(float)input[4];
        Equal(S(S(S(P(-cx,values[8]),P(-cy,values[12])),values[20]),cx),36);
        Equal(S(S(S(P(-cx,values[9]),P(-cy,values[13])),values[21]),cy),37);
    }
}
