// Execute actual original script actions; reflection only transports complete records/state.
#nullable enable
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using ProGPU.Text;
using ProGPU.Text.Shaping;
static class Control
{
    const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static Func<uint,uint> JoiningQuery<T>(MethodInfo method) where T : struct, Enum {
        var query=method.CreateDelegate<Func<uint,T>>();
        return point=>Convert.ToUInt32(query(point));
    }
    delegate B Create<B>(string text, TtfFont font, string script, ShapingClusterLevel level,
                         ShapingBufferFlags flags, ReadOnlyMemory<char> pre, ReadOnlyMemory<char> post);
    delegate bool Select(TtfFont font, string script, out string layout);
    static void Main()
    {
        var shaper = typeof(TtfFont).Assembly.GetType("ProGPU.Text.OpenTypeTextShaper", true)!;
        typeof(Control).GetMethod(nameof(Run), F)!.MakeGenericMethod(
            shaper.GetNestedType("GlyphSubstitutionBuffer", F)!, shaper.GetNestedType("GlyphRecord", F)!)
            .Invoke(null, new object[] { shaper });
    }
    static void Run<B, G>(Type shaper) where B : class where G : struct
    {
        var create = typeof(B).GetMethod("Create", F)!.CreateDelegate<Create<B>>();
        var select = shaper.GetMethod("ResolveLayoutScript", F)!.CreateDelegate<Select>();
        var records = typeof(B).GetField("_glyphs", F)!;
        var beginning=typeof(B).GetMethod("InsertBeginningDottedCircle",F)!.CreateDelegate<Action<B>>();
        var directional=typeof(B).GetMethod("ApplyDirectionalCodePointFallback",F)!.CreateDelegate<Action<B,TtfFont,ShapingDirection,bool>>();
        var hangul=typeof(B).GetMethod("PrepareHangulShaping",F)!.CreateDelegate<Action<B,bool,TtfFont>>();
        var combining=typeof(B).GetMethod("ReorderModifiedCombiningMarks",F)!.CreateDelegate<Action<B,string>>();
        var hebrew=typeof(B).GetMethod("ComposeHebrewPresentationForms",F)!.CreateDelegate<Action<B,bool>>();
        var use=typeof(B).GetMethod("NormalizeUseDiacritics",F)!.CreateDelegate<Action<B,bool>>();
        var thai=typeof(B).GetMethod("PrepareThaiLao",F)!.CreateDelegate<Action<B,string>>();
        var fractions=typeof(B).GetMethod("AssignFractionActions",F)!.CreateDelegate<Action<B>>();
        var clearSub=typeof(B).GetMethod("ClearSubstitutionFlags",F)!.CreateDelegate<Action<B>>();
        var clearSyllable=typeof(B).GetMethod("ClearSyllables",F)!.CreateDelegate<Action<B>>();
        var stretch=typeof(B).GetMethod("RecordArabicStretch",F)!.CreateDelegate<Action<B>>();
        var reverse=typeof(B).GetMethod("Reverse",F)!.CreateDelegate<Action<B>>();
        var vowels=typeof(B).GetMethod("ApplyVowelConstraints",F)!.CreateDelegate<Action<B,string>>();
        var khmer=typeof(B).GetMethod("PrepareKhmerShaping",F)!.CreateDelegate<Action<B,string>>();
        var joining=typeof(B).GetMethod("AssignArabicJoiningActions",F)!.CreateDelegate<Action<B,string>>();
        var fallback=typeof(B).GetMethod("ApplyArabicFallback",F)!.CreateDelegate<Action<B,bool,TextShapingOptions>>();
        var manual=typeof(B).GetMethod("SetManualJoiners",F)!.CreateDelegate<Action<B,bool>>();
        var syllable=typeof(B).GetMethod("SetLookupSyllable",F)!.CreateDelegate<Action<B,int,bool>>();
        var clearLookup=typeof(B).GetMethod("ClearLookupSyllable",F)!.CreateDelegate<Action<B>>();
        var fallbackOptions=new TextShapingOptions[64];
        for(int code=22;code<64;code++) {
            var settings=new List<OpenTypeFeatureSetting>();
            int index=0;
            foreach(string tag in new[]{"init","medi","fina","isol","rlig"}) {
                void Add(string name,int value)=>settings.Add(new OpenTypeFeatureSetting(name,value));
                switch(code) {
                    case 22:Add(tag,1);break;
                    case 23:break;
                    case 24:Add(tag,1);Add(tag,0);break;
                    case 25:Add(tag,0);Add(tag,int.MinValue);break;
                    case 26:Add(tag.ToUpperInvariant(),1);break;
                    case 27:Add(tag[..3]+"\ud800",1);break;
                    case 28:Add(tag,-1);Add(tag,1);break;
                    default:Add(tag,((code-32)>>index)&1);break;
                }
                index++;
            }
            fallbackOptions[code]=new TextShapingOptions{Features=settings};
        }
        var joiningMethod=typeof(TtfFont).Assembly.GetType("ProGPU.Text.ArabicJoiningData",true)!.GetMethod("GetJoiningType",F)!;
        var joiningType=(Func<uint,uint>)typeof(Control).GetMethod(nameof(JoiningQuery),F)!.MakeGenericMethod(joiningMethod.ReturnType).Invoke(null,new object[]{joiningMethod})!;
        var modified=typeof(B).GetMethod("GetModifiedCombiningClass",F)!.CreateDelegate<Func<uint,int>>();
        var digit=typeof(B).GetMethod("IsDecimalDigit",F)!.CreateDelegate<Func<uint,bool>>();
        var fields = new[] { "GlyphIndex", "Cluster", "GraphemeCluster", "CharacterCluster", "CodePoint", "Flags",
            "AdvanceX", "AdvanceY", "OffsetX", "OffsetY", "ArabicAction", "SpaceFallback", "FractionAction",
            "ScriptFeatureMask", "ScriptShaper", "UseCategory", "UseSyllable", "IndicCategory", "IndicPosition",
            "IndicOriginalOrder", "UseRphfEligible", "Substituted", "Ligated", "MultipleSubstitutionComponent",
            "Multiplied", "LigatureComponentCount", "LigatureComponent", "HasCharacterCluster", "Positioned",
            "AttachmentKind", "AttachmentTarget" }.Select(name => typeof(G).GetField(name, F)!).ToArray();
        var state = new[] { "_contextMatchEnd", "_randomState", "_markFilteringCoverage", "_nestedLookupDepth",
            "_manualJoiners", "_restrictLookupToSyllable", "_lookupSyllable", "_hasFractionActions" }
            .Select(name => typeof(B).GetField(name, F)!).ToArray();
        using var input = new BinaryReader(new BufferedStream(Console.OpenStandardInput(), 65536));
        using var output = new BinaryWriter(new BufferedStream(Console.OpenStandardOutput(), 65536));
        Console.Error.WriteLine(RuntimeInformation.FrameworkDescription + " " + RuntimeInformation.OSDescription);
        void Units(ReadOnlySpan<char> units) { output.Write((uint)units.Length); foreach (char unit in units) output.Write((uint)unit); }
        // These observations select a candidate repertoire; complete case comparison follows.
        foreach (string text in new[] { "\U000105c9", "\U000105d2\u0307", "a\u0897\u0334",
                     "a\u1acf\u0334", "a\u0301\u1acf", "\ufffe" })
            foreach (var form in new[] { NormalizationForm.FormD, NormalizationForm.FormC })
                try { Units(text.Normalize(form)); } catch (ArgumentException) { output.Write(uint.MaxValue); }
        if(Environment.GetEnvironmentVariable("PROGPU_RUST_ACTION_FACTS") == "1") {
            for(uint point=0;point<=0x110000;point++) {
                output.Write((uint)modified(point));
                try {output.Write(digit(point)?1u:0u);}catch(ArgumentException){output.Write(uint.MaxValue);}
                output.Write(joiningType(point));
            }
            return;
        }
        var fonts = new TtfFont?[input.ReadInt32()];
        for (int i = 0; i < fonts.Length; i++)
        {
            byte[] data = input.ReadBytes(input.ReadInt32());
            try { fonts[i] = new TtfFont(data); } catch (Exception) { fonts[i] = null; }
        }
        while (true)
        {
            uint selected;
            try { selected = input.ReadUInt32(); } catch (EndOfStreamException) { break; }
            var request = new uint[input.ReadInt32()];
            for (int i = 0; i < request.Length; i++) request[i] = input.ReadUInt32();
            int cursor = 0;
            char[] Text() { var value = new char[request[cursor++]]; for (int i = 0; i < value.Length; i++) value[i] = (char)request[cursor++]; return value; }
            string script = new(Text()); var level = (ShapingClusterLevel)request[cursor++];
            var flags = (ShapingBufferFlags)request[cursor++]; string text = new(Text()); char[] pre = Text(), post = Text();
            var actions=new uint[request[cursor++]];
            for(int i=0;i<actions.Length;i++)actions[i]=request[cursor++];
            uint rawCount=request[cursor++];uint[]? raw=null;
            if(rawCount!=uint.MaxValue){raw=new uint[checked((int)rawCount*31)];for(int i=0;i<raw.Length;i++)raw[i]=request[cursor++];}
            if (cursor != request.Length) throw new InvalidDataException("request tail");
            if (fonts[selected] is not { } font) { output.Write(1u); continue; }
            output.Write(0u); output.Write(select(font, script, out string layout) ? 1u : 0u); Units(layout);
                G[]? rawGlyphs=null;
            if(raw is not null) {
                rawGlyphs=new G[rawCount];

                    for(int i=0;i<rawGlyphs.Length;i++) {
                        object value=default(G);
                        for(int j=0;j<31;j++) {uint word=raw[i*31+j];var type=fields[j].FieldType;
                            object converted=type.IsEnum?Enum.ToObject(type,word):type==typeof(ushort)?(object)unchecked((ushort)word):type==typeof(short)?(object)unchecked((short)word):type==typeof(byte)?(object)unchecked((byte)word):type==typeof(int)?(object)unchecked((int)word):(object)word;
                            fields[j].SetValue(value,converted);
                        }
                        rawGlyphs[i]=(G)value;
                    }
                }
            void Apply(B buffer,ref uint attempted) {
                    foreach(uint action in actions) {
                        attempted++;
                        switch(action) {
                            case 0:beginning(buffer);break;
                            case >=1 and <=8:directional(buffer,font,(ShapingDirection)(1+(action-1)/2),(action-1)%2!=0);break;
                            case 9:hangul(buffer,script=="hang",font);break;
                            case 10:combining(buffer,script);break;
                            case 11:case 12:hebrew(buffer,script=="hebr"&&action==11);break;
                            case 13:case 14:use(buffer,action==14);break;
                            case 15:thai(buffer,script);break;
                            case 16:fractions(buffer);break;
                            case 17:clearSub(buffer);break;
                            case 18:clearSyllable(buffer);break;
                            case 19:stretch(buffer);break;
                            case 20:reverse(buffer);break;
                            case 21:joining(buffer,script);break;
                            case >=22 and <=28:case >=32 and <=63:fallback(buffer,action!=22,fallbackOptions[action]);break;
                            case 64:case 65:manual(buffer,action==64);break;
                            case 66:syllable(buffer,0,true);break;
                            case 67:clearLookup(buffer);break;
                            case 68:vowels(buffer,script);break;
                            case 69:khmer(buffer,script);break;
                            default:throw new InvalidDataException("action");
                        }
                    }
            }
            if(Environment.GetEnvironmentVariable("PROGPU_RUST_ACTIONS_BENCHMARK")=="1") {
                int rounds=Environment.GetEnvironmentVariable("PROGPU_RUST_ACTIONS_PROFILE")=="1"?100000:45;
                var times=new List<double>();var allocations=new List<long>();double first=0;
                for(int round=0;round<rounds;round++) {
                    B candidate=create(text,font,script,level,flags,pre,post);
                    if(rawGlyphs is not null)rawGlyphs.AsSpan().CopyTo(CollectionsMarshal.AsSpan((List<G>)records.GetValue(candidate)!));
                    uint attempted=0;
                    long allocated=GC.GetAllocatedBytesForCurrentThread();long started=System.Diagnostics.Stopwatch.GetTimestamp();
                    Apply(candidate,ref attempted);
                    double elapsed=(System.Diagnostics.Stopwatch.GetTimestamp()-started)*1e6/System.Diagnostics.Stopwatch.Frequency;
                    allocated=GC.GetAllocatedBytesForCurrentThread()-allocated;GC.KeepAlive(candidate);
                    if(round==0)first=elapsed;
                    if(round>=5){times.Add(elapsed);allocations.Add(allocated);}
                    ((IDisposable)candidate).Dispose();
                }
                times.Sort();allocations.Sort();int n=times.Count;
                Console.Error.WriteLine(FormattableString.Invariant($"first_us={first:F3} p50_us={times[(n-1)/2]:F3} p95_us={times[(n-1)*95/100]:F3} p99_us={times[(n*99+99)/100-1]:F3} allocated_p50_bytes={allocations[(n-1)/2]}"));
            }
            B? buffer = null;
            uint error = 0;
            try { buffer = create(text, font, script, level, flags, pre, post); }
            catch (Exception e) { error = e switch { ArgumentException => 2u, InvalidOperationException => 3u,
                IndexOutOfRangeException => 4u, _ => throw new InvalidOperationException("Unexpected original preparation failure", e) }; }
            output.Write(error);
            if (buffer is null) continue;
            try
            {
                var glyphs = (List<G>)records.GetValue(buffer)!;
                if(rawGlyphs is not null) {
                    if(rawGlyphs.Length!=glyphs.Count)throw new InvalidDataException("raw record count differs from actual prepared count");
                    rawGlyphs.AsSpan().CopyTo(CollectionsMarshal.AsSpan(glyphs));
                }
                uint attempted=0,actionError=0;
                try { Apply(buffer,ref attempted);

                }catch(Exception e){actionError=e switch {
                    ArgumentOutOfRangeException range when range.ParamName=="index"=>4u,
                    ArgumentException=>2u,IndexOutOfRangeException=>4u,
                    _=>throw new InvalidOperationException("Unexpected original action failure",e)};}
                output.Write(attempted);output.Write(actionError);
                output.Write((uint)glyphs.Count);
                output.Write(ReferenceEquals(typeof(B).GetField("_font", F)!.GetValue(buffer), font) ? 1u : 0u);
                output.Write((uint)Convert.ToByte(typeof(B).GetField("_clusterLevel", F)!.GetValue(buffer)));
                output.Write((uint)Convert.ToByte(typeof(B).GetField("_bufferFlags", F)!.GetValue(buffer)));
                Units(((ReadOnlyMemory<char>)typeof(B).GetField("_preContext", F)!.GetValue(buffer)!).Span);
                Units(((ReadOnlyMemory<char>)typeof(B).GetField("_postContext", F)!.GetValue(buffer)!).Span);
                foreach (var field in state) output.Write(unchecked((uint)Convert.ToInt64(field.GetValue(buffer)!)));
                foreach (G glyph in glyphs) foreach (var field in fields)
                    output.Write(unchecked((uint)Convert.ToInt64(field.GetValue(glyph)!)));
            }
            finally { ((IDisposable)buffer).Dispose(); }
        }
    }
}
