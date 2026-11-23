// Measure the same .NET canonical operation used by the original plan/shaper.
// Runtime string allocations are included; caller source creation is excluded.
using System.Diagnostics;
using System.Text;
if(args.Length!=3)throw new ArgumentException();
string pattern=args[0] switch{"ascii"=>"ProGPU sample 123 ","composed"=>"Åé각𝅗𝅥 日本語 ","reordered"=>"A\u0315\u0301\u0327 क\u093c \u1100\u1161\u11a8 ",_=>throw new ArgumentException()};
NormalizationForm form=args[1] switch{"nfd"=>NormalizationForm.FormD,"nfc"=>NormalizationForm.FormC,_=>throw new ArgumentException()};
int repeats=int.Parse(args[2]);if(repeats<1||repeats>4096)throw new ArgumentException();string source=string.Concat(Enumerable.Repeat(pattern,repeats));int scalars=source.EnumerateRunes().Count();
(ulong,int) Execute(){string normalized=source.Normalize(form);ulong hash=14695981039346656037UL;int count=0;foreach(Rune rune in normalized.EnumerateRunes()){hash=unchecked((hash^(uint)rune.Value)*1099511628211UL);count++;}return(hash,count);}
var identity=Execute();for(int i=0;i<5;i++)if(Execute()!=identity)throw new InvalidOperationException();
var times=new double[40];var allocations=new long[40];for(int i=0;i<40;i++){long before=GC.GetAllocatedBytesForCurrentThread();long start=Stopwatch.GetTimestamp();var actual=Execute();times[i]=Stopwatch.GetElapsedTime(start).TotalMicroseconds;allocations[i]=GC.GetAllocatedBytesForCurrentThread()-before;if(actual!=identity)throw new InvalidOperationException();}Array.Sort(times);Array.Sort(allocations);
Console.WriteLine(FormattableString.Invariant($"units={source.Length} scalars={scalars} output={identity.Item2} checksum={identity.Item1} round_us_p50={times[19]:F3} p95={times[37]:F3} p99={times[39]:F3} managed_allocated_bytes_p50={allocations[19]}"));
