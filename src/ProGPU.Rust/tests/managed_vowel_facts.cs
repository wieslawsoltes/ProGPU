// Original public property inventory and actual private matcher, not source arrays.
using System.Reflection;
using ProGPU.Text;
using var input=new BinaryReader(new BufferedStream(Console.OpenStandardInput(),65536));
using var output=new BinaryWriter(new BufferedStream(Console.OpenStandardOutput(),65536));
output.Write((uint)UnicodeShapingProperties.VowelConstraintCount);
for(int i=0;i<UnicodeShapingProperties.VowelConstraintCount;i++) {
    var c=UnicodeShapingProperties.GetVowelConstraint(i);
    output.Write(c.ScriptTag);output.Write(c.First);output.Write(c.Second);output.Write(c.Third);
}
var type=typeof(TtfFont).Assembly.GetType("ProGPU.Text.VowelConstraintData",true)!;
var match=type.GetMethod("MatchLength",BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic)!
    .CreateDelegate<Func<string,uint,uint,uint,int>>();
for(uint groups=input.ReadUInt32();groups!=0;groups--) {
    var chars=new char[input.ReadInt32()];
    for(int i=0;i<chars.Length;i++)chars[i]=checked((char)input.ReadUInt32());
    var script=new string(chars);
    for(uint count=input.ReadUInt32();count!=0;count--)
        output.Write((uint)match(script,input.ReadUInt32(),input.ReadUInt32(),input.ReadUInt32()));
}
