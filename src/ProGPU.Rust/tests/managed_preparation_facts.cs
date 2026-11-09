// Observe the original preparation predicate, without reading its source tables.
using System.Reflection;
using ProGPU.Text;
const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
var shaper = typeof(TtfFont).Assembly.GetType("ProGPU.Text.OpenTypeTextShaper", true)!;
var buffer = shaper.GetNestedType("GlyphSubstitutionBuffer", BindingFlags.NonPublic)!;
var query = buffer.GetMethod("IsKhmerBaseCategory", Flags)!.CreateDelegate<Func<uint, bool>>();
using var output = new BinaryWriter(new BufferedStream(Console.OpenStandardOutput()));
for (uint point = 0; point <= 0x10ffff; point++)
    output.Write(point is < 0xd800 or > 0xdfff && query(point) ? (byte)1 : (byte)0);
