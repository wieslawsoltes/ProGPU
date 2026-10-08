internal sealed record ShaderAffineMathInput(string Name,double[] Values);
internal static class ShaderAffineMathOracle
{
    internal static ShaderAffineMathInput[] Cases()
    {
        double[] Input(double[] local,double[]? parent=null,double dx=1,double dy=1) =>
            [..local,..(parent??[1d,0,0,1,0,0]),2.25,3.5,dx,dy,12.75,14.25,43.75,30,8,10,64,32];
        return [new("quarter",Input([0,1,-1,0,64,0])),new("mirror",Input([-1,0,0,1,64,0])),
            new("swap",Input([0,1,1,0,0,0])),new("shear",Input([1,0,.75,1,0,0])),
            new("oblique",Input([.6,.8,-.8,.6,64,0])),
            new("mixed-dpi",Input([1,.25,.5,1,0,0],dx:1.25,dy:1.5)),
            new("noncommuting",Input([0,1,-1,0,0,0],[2,0,0,.5,3,4])),
            new("canceled-history",Input([0,1,-1,0,0,0],[0,-1,1,0,0,0]))];
    }

    // Independent row-vector algebra with explicit float publication. This
    // reference never imports product code, a descriptor, or rendered output.
    private readonly record struct Matrix(float A,float B,float C,float D,float Z,float W,float X,float Y)
    {
        internal static Matrix Identity => new(1,0,0,1,1,1,0,0);
        internal float[] Values => [A,B,0,0,C,D,0,0,0,0,Z,0,X,Y,0,W];
    }
    private static float P(float a,float b)=>ShaderAxisOracle.Product(a,b);
    private static float S(float a,float b)=>ShaderAxisOracle.Sum(a,b);
    private static Matrix Multiply(Matrix a,Matrix b)=>new(
        S(P(a.A,b.A),P(a.B,b.C)),S(P(a.A,b.B),P(a.B,b.D)),
        S(P(a.C,b.A),P(a.D,b.C)),S(P(a.C,b.B),P(a.D,b.D)),P(a.Z,b.Z),P(a.W,b.W),
        S(S(P(a.X,b.A),P(a.Y,b.C)),P(a.W,b.X)),S(S(P(a.X,b.B),P(a.Y,b.D)),P(a.W,b.Y)));
    private static Matrix Inverse(Matrix m,bool neon)
    {
        float d=S(P(m.A,m.D),-P(m.B,m.C)),zw=P(m.Z,m.W),q=ShaderAxisOracle.Divide(1,P(d,zw));
        // The signed translation cofactors are CY-DX and BX-AY. The NEON
        // reference retains fused negative-multiply-subtract / multiply-add
        // publication; absence of the x86 FMA3 define says nothing about NEON.
        // All other publications remain separate. Original SDK captures and
        // independent arithmetic controls must both match this bounded model.
        float x=neon ? MathF.FusedMultiplyAdd(-m.D,m.X,P(m.C,m.Y)) : S(P(m.C,m.Y),-P(m.D,m.X));
        float y=neon ? MathF.FusedMultiplyAdd(m.B,m.X,-P(m.A,m.Y)) : S(P(m.B,m.X),-P(m.A,m.Y));
        return new(P(P(m.D,zw),q),P(-P(m.B,zw),q),P(-P(m.C,zw),q),P(P(m.A,zw),q),
            P(P(d,m.W),q),P(P(d,m.Z),q),P(P(x,m.Z),q),P(P(y,m.Z),q));
    }
    private static bool UsesNeon(uint[] traits)
    {
        if(traits.Length!=5 || traits[0]==0 || traits[1]==0 || traits[4]!=0 ||
            traits[2] is not (0x8664 or 0xAA64) || traits[3]!=(traits[2]==0x8664?1U:2U))
            throw new InvalidOperationException("Unknown original affine SDK contract.");
        return traits[3]==2;
    }
    internal static void Check(ShaderAffineMathInput input,float[] actual,uint[] traits)
    {
        bool neon=UsesNeon(traits);
        if(actual.Length!=156) throw new InvalidOperationException("Incomplete original affine SDK matrix inventory.");
        var v=input.Values;
        Matrix Local(int offset)=>new((float)v[offset],(float)v[offset+1],(float)v[offset+2],(float)v[offset+3],1,1,(float)v[offset+4],(float)v[offset+5]);
        float dx=(float)((96*v[14])*(1.0/96)),dy=(float)((96*v[15])*(1.0/96));
        var world=Multiply(Local(0),Multiply(Matrix.Identity with{X=(float)v[12],Y=(float)v[13]},
            Multiply(Local(6),Matrix.Identity with{A=dx,D=dy})));
        float sx=MathF.Sqrt(S(P(world.A,world.A),P(world.B,world.B))),sy=MathF.Sqrt(S(P(world.C,world.C),P(world.D,world.D)));
        var scale=Matrix.Identity with{A=sx,D=sy};var inverse=Inverse(scale,neon);var rest=Multiply(inverse,world);
        float ax=MathF.Floor(P((float)v[16],sx)),ay=MathF.Floor(P((float)v[17],sy));
        float ex=MathF.Ceiling(P((float)v[18],sx))-ax,ey=MathF.Ceiling(P((float)v[19],sy))-ay;
        var final=Multiply(Matrix.Identity with{X=ax,Y=ay},rest);
        var unit=Multiply(Matrix.Identity with{A=ex,D=ey},final);
        float rx=ShaderAxisOracle.Divide(1,(float)v[22]),ry=ShaderAxisOracle.Divide(1,(float)v[23]);
        var projection=Matrix.Identity with{A=P(2,rx),D=P(-2,ry),X=-S(1,rx),Y=S(1,ry)};
        var projected=Multiply(Multiply(unit,Matrix.Identity with{X=-(float)v[20],Y=-(float)v[21]}),projection);
        Matrix[] matrices=[world,scale,inverse,rest,final,unit,Inverse(unit,neon),projection,projected];
        var expected=matrices.SelectMany(m=>m.Values).Concat(new[]{ax,ay,ex,ey,(float)v[16],(float)v[17],(float)v[18],(float)v[19],dx,dy,sx,sy}).ToArray();
        for(int i=0;i<expected.Length;++i)
        {
            // Preserve every original bit in the receipt; both signed zeros
            // denote the same sparse structural zero, never a numeric tolerance.
            if(actual[i]==0 && expected[i]==0) continue;
            if(BitConverter.SingleToUInt32Bits(actual[i])!=BitConverter.SingleToUInt32Bits(expected[i]))
                throw new InvalidOperationException($"{input.Name} SDK float[{i}]={BitConverter.SingleToUInt32Bits(actual[i]):X8}, expected {BitConverter.SingleToUInt32Bits(expected[i]):X8}.");
        }
    }

    internal static double[] ArithmeticInputs()
    {
        // Exact binary operands: the first lane's rounded product is one but
        // its fused residual is -2^-46. Both signs distinguish multiply-add;
        // the last two lanes distinguish negative-multiply-subtract.
        double e=Math.ScaleB(1,-23);
        return [1+e,-(1+e),1+e,-(1+e), 1-e,1-e,1-e,1-e, -1,1,1,-1];
    }

    internal static void CheckArithmetic(double[] input,float[] actual,uint[] traits)
    {
        bool neon=UsesNeon(traits);
        if(input.Length!=12 || actual.Length!=8) throw new InvalidOperationException("Incomplete SDK arithmetic controls.");
        for(int i=0;i<4;i++)
        {
            float a=(float)input[i],b=(float)input[i+4],c=(float)input[i+8];
            float add=neon ? MathF.FusedMultiplyAdd(a,b,c) : S(P(a,b),c);
            float subtract=neon ? MathF.FusedMultiplyAdd(-a,b,c) : S(c,-P(a,b));
            if(BitConverter.SingleToUInt32Bits(actual[i])!=BitConverter.SingleToUInt32Bits(add) ||
                BitConverter.SingleToUInt32Bits(actual[i+4])!=BitConverter.SingleToUInt32Bits(subtract))
                throw new InvalidOperationException($"SDK arithmetic lane {i} does not match its declared intrinsic backend.");
        }
    }

    internal static int VerifyCorruptionControls(ShaderAffineMathInput input,float[] actual,uint[] traits)
    {
        Check(input,actual,traits);
        var changed=(float[])actual.Clone();
        for(int i=0;i<changed.Length;i++)
        {
            changed[i]=BitConverter.UInt32BitsToSingle(BitConverter.SingleToUInt32Bits(actual[i]) ^ 1u);
            bool rejected=false;
            try { Check(input,changed,traits); }
            catch(InvalidOperationException) { rejected=true; }
            if(!rejected) throw new InvalidOperationException($"SDK matrix corruption at {i} escaped exact validation.");
            changed[i]=actual[i];
        }
        return changed.Length;
    }
}
