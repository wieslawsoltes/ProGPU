// Invoke original public and raw property APIs; never inspect generated arrays.
using System.Reflection;
using System.Runtime.InteropServices;
using ProGPU.Text;
var flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static;
var assembly=typeof(TtfFont).Assembly;
var indic=assembly.GetType("ProGPU.Text.IndicShapingData",true)!;
var use=assembly.GetType("ProGPU.Text.UseShapingData",true)!;
var rawIndic=indic.GetMethod("GetProperties",flags)!.CreateDelegate<Func<uint,ushort>>();
var rawUse=use.GetMethod("GetCategory",flags)!.CreateDelegate<Func<uint,byte>>();
var category=indic.GetMethod("GetCategory",flags)!.CreateDelegate<Func<ushort,byte>>();
var position=indic.GetMethod("GetPosition",flags)!.CreateDelegate<Func<ushort,byte>>();
Console.Error.WriteLine(RuntimeInformation.FrameworkDescription+" "+RuntimeInformation.OSDescription);
using var input=new BinaryReader(new BufferedStream(Console.OpenStandardInput(),65536));
using var output=new BinaryWriter(new BufferedStream(Console.OpenStandardOutput(),65536));
for(int raw=0;raw<=ushort.MaxValue;raw++){output.Write(category((ushort)raw));output.Write(position((ushort)raw));}
while(true){
 uint cp;try{cp=input.ReadUInt32();}catch(EndOfStreamException){break;}
 output.Write(rawIndic(cp));output.Write(rawUse(cp));
 output.Write(UnicodeShapingProperties.GetIndicProperties(cp));output.Write(UnicodeShapingProperties.GetUseCategory(cp));
}
