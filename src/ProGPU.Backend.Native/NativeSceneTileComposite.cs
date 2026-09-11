using System.Numerics;
using System.Runtime.CompilerServices;

namespace ProGPU.Backend.Native;

public partial struct NativeSceneTileComposite
{
    public NativeSceneTileComposite(NativeImageRect output, Matrix3x2 outputToTile,
        uint addressU, uint addressV)
    {
        this = default;
        StructSize = (uint)Unsafe.SizeOf<NativeSceneTileComposite>();
        AddressU = addressU;
        AddressV = addressV;
        OutputX = output.X;
        OutputY = output.Y;
        OutputWidth = output.Width;
        OutputHeight = output.Height;
        M11 = outputToTile.M11;
        M12 = outputToTile.M12;
        M21 = outputToTile.M21;
        M22 = outputToTile.M22;
        M31 = outputToTile.M31;
        M32 = outputToTile.M32;
    }

    internal readonly bool IsValid => StructSize == Unsafe.SizeOf<NativeSceneTileComposite>() &&
        AddressU <= 2 && AddressV <= 2 && Reserved == 0 && Reserved0 == 0 && Reserved1 == 0 &&
        float.IsFinite(OutputX) && float.IsFinite(OutputY) &&
        float.IsFinite(OutputWidth) && float.IsFinite(OutputHeight) &&
        OutputWidth > 0 && OutputHeight > 0 &&
        float.IsFinite(OutputX + OutputWidth) && float.IsFinite(OutputY + OutputHeight) &&
        float.IsFinite(M11) && float.IsFinite(M12) && float.IsFinite(M21) &&
        float.IsFinite(M22) && float.IsFinite(M31) && float.IsFinite(M32);
}
