namespace System.Drawing.Imaging;

public sealed class Encoder
{
    public static readonly Encoder Compression = new(new Guid("e09d739d-ccd4-44ee-8eba-3fbf8be4fc58"));
    public static readonly Encoder ColorDepth = new(new Guid("66087055-ad66-4c7c-9a18-38a2310b8337"));
    public static readonly Encoder ScanMethod = new(new Guid("3a4e2661-3109-4e56-8536-42c156e7dcfa"));
    public static readonly Encoder Version = new(new Guid("24d18c76-814a-41a4-bf53-1c219cccf797"));
    public static readonly Encoder RenderMethod = new(new Guid("6d42c53a-229a-4825-8bb7-5c99e2b9a8b8"));
    public static readonly Encoder Quality = new(new Guid("1d5be4b5-fa4a-452d-9cdd-5db35105e7eb"));
    public static readonly Encoder Transformation = new(new Guid("8d0eb2d1-a58e-4ea8-aa14-108074b7b6f9"));
    public static readonly Encoder LuminanceTable = new(new Guid("edb33bce-0266-4a77-b904-27216099e717"));
    public static readonly Encoder ChrominanceTable = new(new Guid("f2e455dc-09b3-4316-8260-676ada32481c"));
    public static readonly Encoder SaveFlag = new(new Guid("292266fc-ac40-47bf-8cfc-a85b89a655de"));
    public static readonly Encoder ColorSpace = new(new Guid("ae7a62a0-ee2c-49d8-9d07-1ba8a927596e"));
    public static readonly Encoder ImageItems = new(new Guid("63875e13-1f1d-45ab-9195-a29b6066a650"));
    public static readonly Encoder SaveAsCmyk = new(new Guid("a219bbc9-0a9d-4005-a3ee-3a421b8bb06c"));

    public Encoder(Guid guid) => Guid = guid;

    public Guid Guid { get; }
}
