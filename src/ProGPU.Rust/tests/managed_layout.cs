// Invoke the actual compiled original managed shaper through span delegates.
// Admission remains the stronger original native validation in the Rust driver.
using System.Reflection;
using ProGPU.Text;
using ProGPU.Text.Shaping;
using var input=new BinaryReader(Console.OpenStandardInput());
using var output=new BinaryWriter(Console.OpenStandardOutput());
var type=typeof(TtfFont).Assembly.GetType("ProGPU.Text.OpenTypeTextShaper",throwOnError:true)!;
var flags=BindingFlags.Static|BindingFlags.NonPublic;
var coverage=type.GetMethod("FindCoverage",flags)!.CreateDelegate<Query>();
var classes=type.GetMethod("GetGlyphClass",flags)!.CreateDelegate<Query>();
while(true){uint kind;try{kind=input.ReadUInt32();}catch(EndOfStreamException){break;}
 if(kind==4){int a=input.ReadInt32(),b=input.ReadInt32(),c=input.ReadInt32();output.Write(OpenTypeGdefPolicy.IsBlocklisted(a,b,c)?1u:0u);continue;}
 ulong offset=input.ReadUInt64();byte[] data=input.ReadBytes(checked((int)input.ReadUInt32()));uint count=input.ReadUInt32();
 if(kind>1)throw new InvalidDataException();output.Write(0u);
 for(uint i=0;i<count;i++){ushort glyph=(ushort)input.ReadUInt32();output.Write(unchecked((uint)(kind==0?coverage:classes)(data,checked((int)offset),glyph)));}
}
delegate int Query(ReadOnlySpan<byte> data,int offset,ushort glyph);
