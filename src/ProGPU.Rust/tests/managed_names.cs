// Calls original selected-face GetNames/TryGetName and real TtfFont variation names.
using System.Reflection;
using ProGPU.Text;
static class Control {
 const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
 delegate bool GetName<T>(T face,ushort id,out string value);
 static void Bench(Action action){var times=new List<double>();var allocations=new List<long>();for(int n=0;n<45;n++){long bytes=GC.GetAllocatedBytesForCurrentThread();long start=System.Diagnostics.Stopwatch.GetTimestamp();action();double elapsed=(System.Diagnostics.Stopwatch.GetTimestamp()-start)*1e6/System.Diagnostics.Stopwatch.Frequency;bytes=GC.GetAllocatedBytesForCurrentThread()-bytes;if(n>=5){times.Add(elapsed);allocations.Add(bytes);}}times.Sort();allocations.Sort();Console.Error.WriteLine(FormattableString.Invariant($"execute_p50_us={times[19]:F3} execute_p95_us={times[37]:F3} execute_p99_us={times[39]:F3} allocated_p50_bytes={allocations[19]}"));}
 static void Main(){typeof(Control).GetMethod(nameof(Run),F)!.MakeGenericMethod(typeof(TtfFont).Assembly.GetType("ProGPU.Text.SfntFontFace",true)!).Invoke(null,null);}
 static void Run<T>() {
  var load=typeof(T).GetMethod("Load",F,null,new[]{typeof(byte[]),typeof(int)},null)!.CreateDelegate<Func<byte[],int,T>>();
  var names=typeof(T).GetMethod("GetNames",F)!.CreateDelegate<Func<T,ushort,IReadOnlyList<string>>>();
  var first=typeof(T).GetMethod("TryGetName",F)!.CreateDelegate<GetName<T>>();
  using var input=new BinaryReader(Console.OpenStandardInput());using var output=new BinaryWriter(Console.OpenStandardOutput());
  void Text(string value){output.Write((uint)value.Length);foreach(char c in value)output.Write((uint)c);}
  while(true){int length;try{length=input.ReadInt32();}catch(EndOfStreamException){break;}byte[] bytes=input.ReadBytes(length);bool axes=input.ReadUInt32()!=0;int count=input.ReadInt32();var ids=new ushort[count];for(int i=0;i<count;i++)ids[i]=(ushort)input.ReadUInt32();T face=load(bytes,0);
   foreach(ushort id in ids){var values=names(face,id);bool found=first(face,id,out string selected);string mode=Environment.GetEnvironmentVariable("PROGPU_RUST_NAME_BENCHMARK");if(mode=="all")Bench(()=>values=names(face,id));else if(mode=="first")Bench(()=>found=first(face,id,out selected));output.Write((uint)values.Count);foreach(string value in values)Text(value);output.Write(found?1u:0u);Text(selected);}
   if(axes){var font=new TtfFont(bytes);var values=font.VariationAxes;output.Write((uint)values.Count);foreach(var axis in values)Text(axis.Name);}
  }
 }
}
