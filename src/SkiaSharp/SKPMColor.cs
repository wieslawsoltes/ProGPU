using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

#nullable disable

namespace SkiaSharp;

/// <summary>
/// Represents a packed, premultiplied 32-bit color in the platform's native
/// 32-bit color layout.
/// </summary>
public readonly struct SKPMColor : IEquatable<SKPMColor>
{
    // The official native packages use RGBA N32 storage on Apple targets and
    // BGRA N32 storage on Windows/Linux. Keep this decision process-wide so
    // the scalar and array hot paths pay only one predictable branch.
    private static readonly bool UsesRgbaLayout =
        OperatingSystem.IsMacOS() ||
        OperatingSystem.IsIOS() ||
        OperatingSystem.IsMacCatalyst() ||
        OperatingSystem.IsTvOS() ||
        OperatingSystem.IsBrowser();

    private readonly uint _color;

    public SKPMColor(uint value)
    {
        _color = value;
    }

    public byte Alpha => (byte)(_color >> 24);

    public byte Red => UsesRgbaLayout ? (byte)_color : (byte)(_color >> 16);

    public byte Green => (byte)(_color >> 8);

    public byte Blue => UsesRgbaLayout ? (byte)(_color >> 16) : (byte)_color;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SKPMColor PreMultiply(SKColor color) =>
        new(PremultiplyPacked((uint)color));

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static SKPMColor[] PreMultiply(SKColor[] colors)
    {
        ArgumentNullException.ThrowIfNull(colors);

        var result = new SKPMColor[colors.Length];
        ref var source = ref MemoryMarshal.GetArrayDataReference(colors);
        ref var destination = ref MemoryMarshal.GetArrayDataReference(result);
        for (var index = 0; index < colors.Length; index++)
        {
            var color = Unsafe.Add(ref source, index);
            Unsafe.Add(ref destination, index) =
                new SKPMColor(PremultiplyPacked((uint)color));
        }
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SKColor UnPreMultiply(SKPMColor pmcolor)
    {
        var alpha = pmcolor.Alpha;
        if (alpha == 0)
            return SKColor.Empty;

        var scale = UnpremultiplyScales[alpha];
        return PackUnpremultiplied(pmcolor, alpha, scale);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static SKColor[] UnPreMultiply(SKPMColor[] pmcolors)
    {
        ArgumentNullException.ThrowIfNull(pmcolors);

        var result = new SKColor[pmcolors.Length];
        ref var source = ref MemoryMarshal.GetArrayDataReference(pmcolors);
        ref var destination = ref MemoryMarshal.GetArrayDataReference(result);
        for (var index = 0; index < pmcolors.Length; index++)
        {
            var pmcolor = Unsafe.Add(ref source, index);
            var alpha = pmcolor.Alpha;
            if (alpha == 0)
            {
                Unsafe.Add(ref destination, index) = SKColor.Empty;
                continue;
            }

            var scale = UnpremultiplyScales[alpha];
            Unsafe.Add(ref destination, index) =
                PackUnpremultiplied(pmcolor, alpha, scale);
        }
        return result;
    }

    public bool Equals(SKPMColor obj) => _color == obj._color;

    public override bool Equals(object other) =>
        other is SKPMColor color && Equals(color);

    public override int GetHashCode() => _color.GetHashCode();

    public override string ToString() =>
        $"#{Alpha:x2}{Red:x2}{Green:x2}{Blue:x2}";

    public static bool operator ==(SKPMColor left, SKPMColor right) =>
        left.Equals(right);

    public static bool operator !=(SKPMColor left, SKPMColor right) =>
        !left.Equals(right);

    public static implicit operator SKPMColor(uint color) => new(color);

    public static explicit operator uint(SKPMColor color) => color._color;

    public static explicit operator SKPMColor(SKColor color) =>
        PreMultiply(color);

    public static explicit operator SKColor(SKPMColor color) =>
        UnPreMultiply(color);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint PremultiplyPacked(uint color)
    {
        var alpha = color >> 24;
        if (alpha == 255)
            return UsesRgbaLayout ? SwapRedBlue(color) : color;

        var redBlue = (color & 0x00ff00ffu) * alpha + 0x00800080u;
        redBlue = (redBlue + ((redBlue >> 8) & 0x00ff00ffu)) >> 8;

        var green = (color & 0x0000ff00u) * alpha + 0x00008000u;
        green = (green + ((green >> 8) & 0x0000ff00u)) >> 8;

        var logical =
            (alpha << 24) |
            (redBlue & 0x00ff00ffu) |
            (green & 0x0000ff00u);
        return UsesRgbaLayout ? SwapRedBlue(logical) : logical;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte UnpremultiplyComponent(byte component, uint scale) =>
        (byte)(((ulong)component * scale + (1u << 23)) >> 24);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static SKColor PackUnpremultiplied(
        SKPMColor color,
        byte alpha,
        uint scale) =>
        new(
            ((uint)alpha << 24) |
            ((uint)UnpremultiplyComponent(color.Red, scale) << 16) |
            ((uint)UnpremultiplyComponent(color.Green, scale) << 8) |
            UnpremultiplyComponent(color.Blue, scale));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint SwapRedBlue(uint color) =>
        (color & 0xff00ff00u) |
        ((color & 0x00ff0000u) >> 16) |
        ((color & 0x000000ffu) << 16);

    // Generated as round((255 << 24) / alpha). One lookup and three fixed
    // multiplies replace three integer divisions while retaining exact 8-bit
    // behavior. The compiler stores this span as read-only assembly data.
    private static ReadOnlySpan<uint> UnpremultiplyScales =>
    [
        0u, 4278190080u, 2139095040u, 1426063360u, 1069547520u, 855638016u, 713031680u, 611170011u,
        534773760u, 475354453u, 427819008u, 388926371u, 356515840u, 329091545u, 305585006u, 285212672u,
        267386880u, 251658240u, 237677227u, 225167899u, 213909504u, 203723337u, 194463185u, 186008264u,
        178257920u, 171127603u, 164545772u, 158451484u, 152792503u, 147523796u, 142606336u, 138006132u,
        133693440u, 129642124u, 125829120u, 122234002u, 118838613u, 115626759u, 112583949u, 109697182u,
        106954752u, 104346100u, 101861669u, 99492793u, 97231593u, 95070891u, 93004132u, 91025321u,
        89128960u, 87310002u, 85563802u, 83886080u, 82272886u, 80720568u, 79225742u, 77785274u,
        76396251u, 75055966u, 73761898u, 72511696u, 71303168u, 70134264u, 69003066u, 67907779u,
        66846720u, 65818309u, 64821062u, 63853583u, 62914560u, 62002755u, 61117001u, 60256198u,
        59419307u, 58605344u, 57813379u, 57042534u, 56291975u, 55560910u, 54848591u, 54154305u,
        53477376u, 52817161u, 52173050u, 51544459u, 50930834u, 50331648u, 49746396u, 49174599u,
        48615796u, 48069551u, 47535445u, 47013078u, 46502066u, 46002044u, 45512660u, 45033580u,
        44564480u, 44105052u, 43655001u, 43214041u, 42781901u, 42358318u, 41943040u, 41535826u,
        41136443u, 40744667u, 40360284u, 39983085u, 39612871u, 39249450u, 38892637u, 38542253u,
        38198126u, 37860089u, 37527983u, 37201653u, 36880949u, 36565727u, 36255848u, 35951177u,
        35651584u, 35356943u, 35067132u, 34782033u, 34501533u, 34225521u, 33953890u, 33686536u,
        33423360u, 33164264u, 32909154u, 32657940u, 32410531u, 32166843u, 31926792u, 31690297u,
        31457280u, 31227665u, 31001377u, 30778346u, 30558501u, 30341774u, 30128099u, 29917413u,
        29709653u, 29504759u, 29302672u, 29103334u, 28906690u, 28712685u, 28521267u, 28332385u,
        28145987u, 27962027u, 27780455u, 27601226u, 27424295u, 27249618u, 27077152u, 26906856u,
        26738688u, 26572609u, 26408581u, 26246565u, 26086525u, 25928425u, 25772229u, 25617905u,
        25465417u, 25314734u, 25165824u, 25018655u, 24873198u, 24729422u, 24587299u, 24446800u,
        24307898u, 24170565u, 24034776u, 23900503u, 23767723u, 23636409u, 23506539u, 23378088u,
        23251033u, 23125352u, 23001022u, 22878022u, 22756330u, 22635926u, 22516790u, 22398901u,
        22282240u, 22166788u, 22052526u, 21939436u, 21827500u, 21716701u, 21607021u, 21498443u,
        21390950u, 21284528u, 21179159u, 21074828u, 20971520u, 20869220u, 20767913u, 20667585u,
        20568222u, 20469809u, 20372334u, 20275782u, 20180142u, 20085399u, 19991542u, 19898559u,
        19806436u, 19715162u, 19624725u, 19535115u, 19446319u, 19358326u, 19271126u, 19184709u,
        19099063u, 19014178u, 18930045u, 18846652u, 18763992u, 18682053u, 18600826u, 18520303u,
        18440474u, 18361331u, 18282864u, 18205064u, 18127924u, 18051435u, 17975589u, 17900377u,
        17825792u, 17751826u, 17678471u, 17605720u, 17533566u, 17462000u, 17391017u, 17320608u,
        17250766u, 17181486u, 17112760u, 17044582u, 16976945u, 16909842u, 16843268u, 16777216u,
    ];
}
