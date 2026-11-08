// Original plan reader and the same .NET canonical forms used by its generator
// and managed shaper; shaping-specific grapheme/script admission is independent.
using ProGPU.Text;
using System.Text;
using var input = new BinaryReader(Console.OpenStandardInput());
using var output = new BinaryWriter(Console.OpenStandardOutput());
var records=UnicodeNormalizationPlan.DecompositionRecords.Span;
var scalars=UnicodeNormalizationPlan.DecompositionScalars.Span;
var pairs=UnicodeNormalizationPlan.CompositionRecords.Span;
output.Write((uint)records.Length);foreach(uint v in records)output.Write(v);
output.Write((uint)scalars.Length);foreach(uint v in scalars)output.Write(v);
output.Write((uint)pairs.Length);foreach(uint v in pairs)output.Write(v);
while(true){uint count;try{count=input.ReadUInt32();}catch(EndOfStreamException){break;}
 var source=new char[count];for(int i=0;i<source.Length;i++)source[i]=(char)input.ReadUInt16();
 byte form=input.ReadByte();if(form>1)throw new InvalidDataException();
 try{string normalized=new string(source).Normalize(form==0?NormalizationForm.FormD:NormalizationForm.FormC);output.Write((uint)normalized.Length);foreach(char c in normalized)output.Write((ushort)c);}
 catch(ArgumentException){output.Write(uint.MaxValue);}
}
