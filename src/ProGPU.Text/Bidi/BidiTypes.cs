namespace ProGPU.Text.Bidi;

// Values are private to ProGPU's independently implemented UAX #9 resolver.
// Their order is also the compact encoding used by UnicodeBidiData.Generated.cs.
internal enum BidiClass : byte
{
    L,
    R,
    AL,
    EN,
    ES,
    ET,
    AN,
    CS,
    NSM,
    BN,
    B,
    S,
    WS,
    ON,
    LRE,
    LRO,
    RLE,
    RLO,
    PDF,
    LRI,
    RLI,
    FSI,
    PDI
}

internal enum BracketKind : byte
{
    None,
    Open,
    Close
}
