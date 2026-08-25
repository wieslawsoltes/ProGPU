namespace ProGPU.Wpf.Interop;

public enum PortableBitmapScalingMode : byte
{
    Unspecified,
    Linear,
    Fant,
    NearestNeighbor
}

public enum PortableEdgeMode : byte
{
    Unspecified,
    Aliased
}

public enum PortableClearTypeHint : byte
{
    Auto,
    Enabled
}
