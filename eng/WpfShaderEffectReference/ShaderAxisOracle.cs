using System.Runtime.CompilerServices;

internal sealed record AxisInput(string Name,double DpiX,double DpiY,double ScaleX = 1,double ScaleY = 1,bool Padded = false)
{
    internal double[] Values => [DpiX,DpiY,ScaleX,ScaleY,2,3,16.75,16.25,15.5,7.5,
        Padded ? .25 : 0,Padded ? 1.25 : 0,Padded ? .5 : 0,Padded ? 1.5 : 0];
}

internal static class ShaderAxisOracle
{
    internal static AxisInput[] Cases() =>
    [
        new("axis-root-125",1.25,1.25),
        new("axis-root-150",1.5,1.5),
        new("axis-root-mixed-125-150",1.25,1.5),
        new("axis-source-nonuniform-125",1.25,1.25,1.5,.75,true),
        new("axis-source-nonuniform-150",1.5,1.5,1.25,.75,true),
        new("axis-root-125-next",MathF.BitIncrement(1.25f),1.25),
        new("axis-root-150-previous",MathF.BitDecrement(1.5f),1.5),
        new("axis-source-next",1.25,1.25,MathF.BitIncrement(1f),1),
        new("axis-source-previous",1.25,1.25,MathF.BitDecrement(1f),1),
        new("axis-source-double-midpoint",1.25,1.25,1 + Math.ScaleB(1,-24),1,true)
    ];

    // Independent diagonal cofactor reduction, not a copied matrix algorithm.
    // Separate float publication prohibits an optimizer from fusing operations.
    [MethodImpl(MethodImplOptions.NoInlining)] internal static float Product(float a,float b) => a*b;
    [MethodImpl(MethodImplOptions.NoInlining)] internal static float Sum(float a,float b) => a+b;
    [MethodImpl(MethodImplOptions.NoInlining)] internal static float Divide(float a,float b) => a/b;
    internal static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);
    internal static float[] DiagonalInverse(float x,float y)
    {
        float determinant = Product(x,y), reciprocal = Divide(1,determinant);
        return [Product(y,reciprocal),Product(x,reciprocal),Product(determinant,reciprocal),determinant];
    }

    internal static void CheckSdk(AxisInput input,float[] values,uint[] traits)
    {
        void Same(float actual,float expected,string name)
        {
            if (Bits(actual) != Bits(expected)) throw new InvalidOperationException(
                $"{input.Name}: SDK {name} bits {Bits(actual):X8} != independent {Bits(expected):X8}.");
        }
        if (values.Length != 128 || traits.Length != 5 || traits[0] == 0 || traits[1] == 0 ||
            traits[2] is not (0x8664 or 0xAA64) || traits[3] != (traits[2] == 0x8664 ? 1U : 2U) || traits[4] != 0)
            throw new InvalidOperationException("Unknown original SDK architecture/compiler/intrinsic contract.");
        float dx = (float)((96*input.DpiX)*(1.0/96)),dy = (float)((96*input.DpiY)*(1.0/96));
        float mx = Product((float)input.ScaleX,dx),my = Product((float)input.ScaleY,dy);
        float sx = MathF.Sqrt(Product(mx,mx)),sy = MathF.Sqrt(Product(my,my));
        Same(values[0],mx,"worldX"); Same(values[5],my,"worldY");
        Same(values[12],Product(2,dx),"worldTx"); Same(values[13],Product(3,dy),"worldTy");
        Same(values[120],sx,"scaleX"); Same(values[121],sy,"scaleY");
        float[] inverse = DiagonalInverse(sx,sy);
        Same(values[32],inverse[0],"inverseX"); Same(values[37],inverse[1],"inverseY");
        Same(values[42],inverse[2],"inverseZ"); Same(values[47],inverse[2],"inverseW");
        Same(values[122],inverse[3],"determinant");
        Same(values[48],Product(inverse[0],mx),"residualX");
        Same(values[53],Product(inverse[1],my),"residualY");
        Same(values[58],inverse[2],"residualZ"); Same(values[63],inverse[2],"residualW");
        Same(values[60],Product(inverse[2],values[12]),"residualTx");
        Same(values[61],Product(inverse[2],values[13]),"residualTy");
    }

    internal static PaddingFrame OutputClip(AxisInput input)
    {
        double[] v = input.Values;
        float dx=(float)((96*input.DpiX)*(1.0/96)),dy=(float)((96*input.DpiY)*(1.0/96));
        int Edge(float local,float scale,float dpi,float offset) => ShaderLocalCaptureOracle.AliasedOutputEdge(
            Sum(Product(Product(local,scale),dpi),Product(offset,dpi)));
        int l = Math.Clamp(Edge(Sum((float)v[6],-(float)v[12]),(float)input.ScaleX,dx,2),0,96);
        int t = Math.Clamp(Edge(Sum((float)v[7],-(float)v[10]),(float)input.ScaleY,dy,3),0,64);
        int r = Math.Clamp(Edge(Sum((float)(v[6]+v[8]),(float)v[13]),(float)input.ScaleX,dx,2),0,96);
        int b = Math.Clamp(Edge(Sum((float)(v[7]+v[9]),(float)v[11]),(float)input.ScaleY,dy,3),0,64);
        return new(l,t,r-l,b-t);
    }

    internal static int VerifyArithmeticControls()
    {
        int count=0;
        void Require(bool value) { if (!value) throw new InvalidOperationException("Axis oracle control failed."); ++count; }
        var cases=Cases();
        Require(cases.Length==10 && cases.Select(x=>x.Name).Distinct().Count()==10);
        foreach(var c in cases) { Require(c.Values.Length==14); Require(OutputClip(c).Width>0 && OutputClip(c).Height>0); }
        var a=DiagonalInverse(1.25f,1.25f);
        Require(Bits(a[0])==0x3f4ccccc && Bits(Divide(1,1.25f))==0x3f4ccccd);
        Require(Bits(Product(a[0],1.25f))==0x3f7fffff && Bits(a[2])==0x3f800000);
        var b=DiagonalInverse(1.25f,1.5f);
        Require(Bits(b[0])==0x3f4cccce && Bits(Product(b[0],1.25f))==0x3f800001);
        var c1=DiagonalInverse(MathF.BitIncrement(1.25f),1.25f);
        Require(Bits(c1[2])==0x3f7fffff && Product(c1[2],3.75f)!=3.75f);
        var d=DiagonalInverse(1.5f,1.5f);
        Require(Product(d[0],1.5f)==1 && Product(d[1],1.5f)==1);
        Require((float)cases[9].ScaleX==1 && cases[9].ScaleX!=1);
        Require(Product((float)cases[9].ScaleX,1.25f)==1.25f && (float)(cases[9].ScaleX*1.25)!=1.25f);
        Require(OutputClip(cases[0])==new PaddingFrame(23,24,20,9));
        return count;
    }
}
