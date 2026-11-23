// Original managed resolver; decoding/allocation remain outside the measured loop.
using System.Diagnostics;
using System.Text;
using ProGPU.Text;
if (args.Length != 2) throw new ArgumentException("usage: ascii|mixed|emoji REPEATS");
string pattern = args[0] switch { "ascii" => "ProGPU sample ($123.45) - end ", "mixed" => "A\u0301 العربية 日本語\r\n क्क ", "emoji" => "🇵🇱👩\u0301\u200d👧👍🏽🇦 \u1100\u1161\u11a8 ", _ => throw new ArgumentException() };
int repeats=int.Parse(args[1]);if(repeats<1||repeats>4096)throw new ArgumentException();
string text=string.Concat(Enumerable.Repeat(pattern,repeats));
var source=new List<UnicodeLineBreakScalar>();uint offset=0;
foreach(Rune rune in text.EnumerateRunes()){source.Add(new((uint)rune.Value,offset,(ushort)rune.Utf16SequenceLength));offset+=(uint)rune.Utf16SequenceLength;}
var scalars=source.ToArray();var classes=new UnicodeLineBreakClass[scalars.Length];var breaks=new UnicodeLineBreakKind[scalars.Length];
ulong Execute(){if(!UnicodeLineBreakResolver.TryResolve(scalars,classes,breaks,out _))throw new InvalidOperationException();ulong hash=14695981039346656037UL;for(int i=0;i<scalars.Length;i++){hash=unchecked((hash^(byte)classes[i])*1099511628211UL);hash=unchecked((hash^(byte)breaks[i])*1099511628211UL);}return hash;}
ulong checksum=Execute();for(int i=0;i<5;i++)if(Execute()!=checksum)throw new InvalidOperationException();
var times=new double[40];for(int i=0;i<times.Length;i++){long start=Stopwatch.GetTimestamp();ulong actual=Execute();times[i]=Stopwatch.GetElapsedTime(start).TotalMicroseconds;if(actual!=checksum)throw new InvalidOperationException();}
Array.Sort(times);Console.WriteLine(FormattableString.Invariant($"units={text.Length} scalars={scalars.Length} scratch_bytes={classes.Length} checksum={checksum} round_us_p50={times[19]:F3} p95={times[37]:F3} p99={times[39]:F3}"));
