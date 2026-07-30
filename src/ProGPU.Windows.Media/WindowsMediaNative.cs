using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ProGPU.Windows.Media;

internal static unsafe partial class WindowsMediaNative
{
    internal const int SFalse = 1;
    internal const uint DxgiFormatB8G8R8A8Unorm = 87;
    internal const uint D3D11BindShaderResource = 0x8;
    internal const uint D3D11BindRenderTarget = 0x20;
    internal const uint D3D11CpuAccessRead = 0x20_000;
    internal const uint D3D11UsageStaging = 3;
    internal const uint D3D11ResourceMiscSharedKeyedMutex = 0x100;
    internal const uint D3D11ResourceMiscSharedNtHandle = 0x800;
    internal const uint DxgiSharedResourceRead = 0x8000_0000;
    internal const uint DxgiSharedResourceWrite = 0x1;

    private const uint D3D11SdkVersion = 7;
    private const uint D3D11CreateDeviceBgraSupport = 0x20;
    private const uint D3D11CreateDeviceVideoSupport = 0x800;
    private const uint CoinitMultithreaded = 0;
    private const uint ClsctxInprocServer = 1;
    private const uint MfVersion = 0x0002_0070;
    private const ushort VariantTypeWideString = 31;
    private const ushort VariantTypeClassId = 72;

    internal static readonly Guid MediaEngineCallback =
        new("c60381b8-83a4-41f8-a3d0-de05076849a9");
    internal static readonly Guid MediaEngineDxgiManager =
        new("065702da-1094-486d-8617-ee7cc4ee4648");
    internal static readonly Guid MediaEngineVideoOutputFormat =
        new("5066893c-8cf9-42bc-8b8a-472212e52726");
    internal static readonly Guid MediaEngineAudioCategory =
        new("c8d4c51d-350e-41f2-ba46-faebbb0857f6");
    internal static readonly Guid MediaEngineAudioEndpointRole =
        new("d2cb93d1-116a-44f2-9385-f7d0fda2fb46");

    private static readonly Guid s_mediaEngineFactoryClass =
        new("b44392da-499b-446b-a4cb-005fead0e6d5");
    private static readonly Guid s_mediaEngineFactoryInterface =
        new("4d645ace-26aa-4688-9be1-df3516990b93");
    private static readonly Guid s_dxgiResource1 =
        new("30961379-4609-4a41-998e-54fe567ee0c1");
    private static readonly Guid s_dxgiKeyedMutex =
        new("9d8e1289-d7b3-465f-8126-250e349af85d");
    private static readonly Guid s_mediaEngineEx =
        new("83015ead-b1e6-40d0-a98a-37145ffe1ad1");
    private static readonly Guid s_mediaEngineTimedText =
        new("805ea411-92e0-4e59-9b6e-5c7d7915e64f");
    private static readonly Guid s_timedText =
        new("1f2a94c9-a3df-430d-9d0f-acd85ddc29af");
    private static readonly Guid s_mediaTypeMajorType =
        new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid s_mediaTypeSubtype =
        new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid s_streamLanguage =
        new("00af2180-bdc2-423c-abca-f503593bc121");
    private static readonly Guid s_streamName =
        new("4f1b099d-d314-41e5-a781-7fefaa4c501f");
    internal static readonly Guid MediaTypeAudio =
        new("73647561-0000-0010-8000-00aa00389b71");
    internal static readonly Guid MediaTypeVideo =
        new("73646976-0000-0010-8000-00aa00389b71");

    [StructLayout(LayoutKind.Sequential)]
    internal struct SampleDescription
    {
        internal uint Count;
        internal uint Quality;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Texture2DDescription
    {
        internal uint Width;
        internal uint Height;
        internal uint MipLevels;
        internal uint ArraySize;
        internal uint Format;
        internal SampleDescription SampleDescription;
        internal uint Usage;
        internal uint BindFlags;
        internal uint CpuAccessFlags;
        internal uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MappedSubresource
    {
        internal byte* Data;
        internal uint RowPitch;
        internal uint DepthPitch;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    internal readonly record struct MediaEngineStreamInfo(
        uint NativeIndex,
        Guid MajorType,
        Guid Subtype,
        string Name,
        string Language,
        bool Selected);

    internal readonly record struct MediaEngineTimedTextTrackInfo(
        uint NativeId,
        int Kind,
        string Label,
        string Language,
        string DispatchType,
        Guid DataFormat,
        bool Active);

    internal readonly record struct MediaEngineTimedTextCueInfo(
        uint NativeId,
        uint TrackId,
        int Kind,
        double StartTime,
        double Duration,
        string Text);

    internal static void InitializeCom() =>
        ThrowIfFailed(
            CoInitializeEx(0, CoinitMultithreaded),
            "initialize COM");

    internal static void UninitializeCom() => CoUninitialize();

    internal static void StartupMediaFoundation() =>
        ThrowIfFailed(
            MFStartup(MfVersion, 0),
            "start Media Foundation");

    internal static void ShutdownMediaFoundation() =>
        _ = MFShutdown();

    internal static nint CreateAttributes(uint capacity)
    {
        nint attributes = 0;
        ThrowIfFailed(
            MFCreateAttributes(&attributes, capacity),
            "create Media Foundation attributes");
        return attributes;
    }

    internal static nint CreateD3D11Device(out nint immediateContext)
    {
        nint device = 0;
        nint context = 0;
        uint selectedLevel = 0;
        uint[] levels =
        [
            0xb100,
            0xb000,
            0xa100,
            0xa000
        ];
        fixed (uint* featureLevels = levels)
        {
            ThrowIfFailed(
                D3D11CreateDevice(
                    0,
                    driverType: 1,
                    software: 0,
                    flags:
                        D3D11CreateDeviceBgraSupport |
                        D3D11CreateDeviceVideoSupport,
                    featureLevels,
                    (uint)levels.Length,
                    D3D11SdkVersion,
                    &device,
                    &selectedLevel,
                    &context),
                "create the D3D11 media device");
        }

        immediateContext = context;
        return device;
    }

    internal static nint CreateDxgiDeviceManager(
        nint d3d11Device)
    {
        uint resetToken = 0;
        nint manager = 0;
        ThrowIfFailed(
            MFCreateDXGIDeviceManager(
                &resetToken,
                &manager),
            "create the Media Foundation DXGI device manager");
        try
        {
            delegate* unmanaged[Stdcall]<
                nint,
                nint,
                uint,
                int> reset =
                (delegate* unmanaged[Stdcall]<
                    nint,
                    nint,
                    uint,
                    int>)VTable(manager)[7];
            ThrowIfFailed(
                reset(manager, d3d11Device, resetToken),
                "bind the D3D11 device to Media Foundation");
            return manager;
        }
        catch
        {
            Release(manager);
            throw;
        }
    }

    internal static nint CreateMediaEngineFactory()
    {
        nint factory = 0;
        fixed (Guid* classId = &s_mediaEngineFactoryClass)
        fixed (Guid* interfaceId = &s_mediaEngineFactoryInterface)
        {
            ThrowIfFailed(
                CoCreateInstance(
                    classId,
                    0,
                    ClsctxInprocServer,
                    interfaceId,
                    &factory),
                "create the Media Foundation media-engine factory");
        }
        return factory;
    }

    internal static nint CreateMediaEngine(
        nint factory,
        nint attributes,
        bool realTimePlayback)
    {
        nint engine = 0;
        delegate* unmanaged[Stdcall]<
            nint,
            uint,
            nint,
            nint*,
            int> create =
            (delegate* unmanaged[Stdcall]<
                nint,
                uint,
                nint,
                nint*,
                int>)VTable(factory)[3];
        ThrowIfFailed(
            create(
                factory,
                realTimePlayback ? 0x8u : 0u,
                attributes,
                &engine),
            "create the Media Foundation media engine");
        return engine;
    }

    internal static void SetAttributeUnknown(
        nint attributes,
        in Guid key,
        nint value)
    {
        fixed (Guid* keyPointer = &key)
        {
            delegate* unmanaged[Stdcall]<
                nint,
                Guid*,
                nint,
                int> set =
                (delegate* unmanaged[Stdcall]<
                    nint,
                    Guid*,
                    nint,
                    int>)VTable(attributes)[27];
            ThrowIfFailed(
                set(attributes, keyPointer, value),
                "set a Media Foundation object attribute");
        }
    }

    internal static void SetAttributeUInt32(
        nint attributes,
        in Guid key,
        uint value)
    {
        fixed (Guid* keyPointer = &key)
        {
            delegate* unmanaged[Stdcall]<
                nint,
                Guid*,
                uint,
                int> set =
                (delegate* unmanaged[Stdcall]<
                    nint,
                    Guid*,
                    uint,
                    int>)VTable(attributes)[21];
            ThrowIfFailed(
                set(attributes, keyPointer, value),
                "set a Media Foundation integer attribute");
        }
    }

    internal static void SetSource(nint engine, string source)
    {
        nint bstr = Marshal.StringToBSTR(source);
        try
        {
            delegate* unmanaged[Stdcall]<nint, nint, int> set =
                (delegate* unmanaged[Stdcall]<
                    nint,
                    nint,
                    int>)VTable(engine)[6];
            ThrowIfFailed(
                set(engine, bstr),
                "set the media source");
        }
        finally
        {
            Marshal.FreeBSTR(bstr);
        }
    }

    internal static void Load(nint engine) =>
        CallResult(engine, 12, "load the media source");

    internal static ushort GetReadyState(nint engine)
    {
        delegate* unmanaged[Stdcall]<nint, ushort> call =
            (delegate* unmanaged[Stdcall]<
                nint,
                ushort>)VTable(engine)[14];
        return call(engine);
    }

    internal static bool IsPaused(nint engine) =>
        CallBool(engine, 20);

    internal static bool IsEnded(nint engine) =>
        CallBool(engine, 27);

    internal static double GetCurrentTime(nint engine) =>
        CallDouble(engine, 16);

    internal static double GetDuration(nint engine) =>
        CallDouble(engine, 19);

    internal static double GetPlaybackRate(nint engine) =>
        CallDouble(engine, 23);

    internal static bool HasVideo(nint engine) =>
        CallBool(engine, 38);

    internal static bool HasAudio(nint engine) =>
        CallBool(engine, 39);

    internal static void GetNativeVideoSize(
        nint engine,
        out uint width,
        out uint height)
    {
        uint nativeWidth = 0;
        uint nativeHeight = 0;
        delegate* unmanaged[Stdcall]<
            nint,
            uint*,
            uint*,
            int> call =
            (delegate* unmanaged[Stdcall]<
                nint,
                uint*,
                uint*,
                int>)VTable(engine)[40];
        ThrowIfFailed(
            call(engine, &nativeWidth, &nativeHeight),
            "query the native video size");
        width = nativeWidth;
        height = nativeHeight;
    }

    internal static void Play(nint engine) =>
        CallResult(engine, 32, "play media");

    internal static void Pause(nint engine) =>
        CallResult(engine, 33, "pause media");

    internal static void SetCurrentTime(
        nint engine,
        double seconds)
    {
        delegate* unmanaged[Stdcall]<nint, double, int> call =
            (delegate* unmanaged[Stdcall]<
                nint,
                double,
                int>)VTable(engine)[17];
        ThrowIfFailed(
            call(engine, seconds),
            "seek media");
    }

    internal static void SetPlaybackRate(
        nint engine,
        double rate)
    {
        delegate* unmanaged[Stdcall]<nint, double, int> call =
            (delegate* unmanaged[Stdcall]<
                nint,
                double,
                int>)VTable(engine)[24];
        ThrowIfFailed(
            call(engine, rate),
            "set the media playback rate");
    }

    internal static void SetLoop(nint engine, bool loop) =>
        CallBooleanResult(engine, 31, loop, "set media looping");

    internal static void SetMuted(nint engine, bool muted) =>
        CallBooleanResult(engine, 35, muted, "set media mute");

    internal static void SetVolume(
        nint engine,
        double volume)
    {
        delegate* unmanaged[Stdcall]<nint, double, int> call =
            (delegate* unmanaged[Stdcall]<
                nint,
                double,
                int>)VTable(engine)[37];
        ThrowIfFailed(
            call(engine, volume),
            "set media volume");
    }

    internal static void SetBalance(
        nint engine,
        double balance)
    {
        nint extended = QueryInterface(
            engine,
            in s_mediaEngineEx);
        try
        {
            delegate* unmanaged[Stdcall]<
                nint,
                double,
                int> call =
                (delegate* unmanaged[Stdcall]<
                    nint,
                    double,
                    int>)VTable(extended)[49];
            ThrowIfFailed(
                call(extended, balance),
                "set media audio balance");
        }
        finally
        {
            Release(extended);
        }
    }

    internal static void FrameStep(
        nint engine,
        bool forward)
    {
        nint extended = QueryInterface(
            engine,
            in s_mediaEngineEx);
        try
        {
            delegate* unmanaged[Stdcall]<
                nint,
                int,
                int> call =
                (delegate* unmanaged[Stdcall]<
                    nint,
                    int,
                    int>)VTable(extended)[51];
            ThrowIfFailed(
                call(
                    extended,
                    forward ? 1 : 0),
                "step the media engine frame");
        }
        finally
        {
            Release(extended);
        }
    }

    internal static MediaEngineStreamInfo[] GetStreams(
        nint engine)
    {
        nint extended = QueryInterface(
            engine,
            in s_mediaEngineEx);
        try
        {
            uint streamCount = 0;
            delegate* unmanaged[Stdcall]<
                nint,
                uint*,
                int> getStreamCount =
                (delegate* unmanaged[Stdcall]<
                    nint,
                    uint*,
                    int>)VTable(extended)[54];
            ThrowIfFailed(
                getStreamCount(
                    extended,
                    &streamCount),
                "enumerate Media Engine streams");

            var streams =
                new List<MediaEngineStreamInfo>(
                    checked((int)streamCount));
            for (uint nativeIndex = 0;
                 nativeIndex < streamCount;
                 nativeIndex++)
            {
                if (!TryGetStreamGuidAttribute(
                        extended,
                        nativeIndex,
                        in s_mediaTypeMajorType,
                        out Guid majorType) ||
                    (majorType != MediaTypeAudio &&
                     majorType != MediaTypeVideo))
                {
                    continue;
                }

                _ = TryGetStreamGuidAttribute(
                    extended,
                    nativeIndex,
                    in s_mediaTypeSubtype,
                    out Guid subtype);
                _ = TryGetStreamStringAttribute(
                    extended,
                    nativeIndex,
                    in s_streamName,
                    out string name);
                _ = TryGetStreamStringAttribute(
                    extended,
                    nativeIndex,
                    in s_streamLanguage,
                    out string language);
                streams.Add(
                    new MediaEngineStreamInfo(
                        nativeIndex,
                        majorType,
                        subtype,
                        name,
                        language,
                        GetStreamSelection(
                            extended,
                            nativeIndex)));
            }
            return streams.ToArray();
        }
        finally
        {
            Release(extended);
        }
    }

    internal static void SetExclusiveStreamSelection(
        nint engine,
        ReadOnlySpan<uint> nativeIndices,
        uint selectedNativeIndex)
    {
        nint extended = QueryInterface(
            engine,
            in s_mediaEngineEx);
        try
        {
            delegate* unmanaged[Stdcall]<
                nint,
                uint,
                int,
                int> setStreamSelection =
                (delegate* unmanaged[Stdcall]<
                    nint,
                    uint,
                    int,
                    int>)VTable(extended)[57];
            for (int index = 0;
                 index < nativeIndices.Length;
                 index++)
            {
                ThrowIfFailed(
                    setStreamSelection(
                        extended,
                        nativeIndices[index],
                        0),
                    "deselect a Media Engine stream");
            }
            if (selectedNativeIndex != uint.MaxValue)
            {
                ThrowIfFailed(
                    setStreamSelection(
                        extended,
                        selectedNativeIndex,
                        1),
                    "select a Media Engine stream");
            }

            CallResult(
                extended,
                58,
                "apply Media Engine stream selections");
        }
        finally
        {
            Release(extended);
        }
    }

    internal static bool TryGetTimedTextService(
        nint engine,
        out nint timedText)
    {
        nint result = 0;
        fixed (Guid* service = &s_mediaEngineTimedText)
        fixed (Guid* interfaceId = &s_timedText)
        {
            int status = MFGetService(
                engine,
                service,
                interfaceId,
                &result);
            if (status < 0 || result == 0)
            {
                Release(result);
                timedText = 0;
                return false;
            }
        }

        timedText = result;
        return true;
    }

    internal static void RegisterTimedTextNotifications(
        nint timedText,
        nint notification)
    {
        delegate* unmanaged[Stdcall]<
            nint,
            nint,
            int> register =
                (delegate* unmanaged[Stdcall]<
                    nint,
                    nint,
                    int>)VTable(timedText)[3];
        ThrowIfFailed(
            register(timedText, notification),
            "register Media Foundation timed-text notifications");
    }

    internal static void ClearTimedTextNotifications(
        nint timedText)
    {
        if (timedText == 0)
        {
            return;
        }

        delegate* unmanaged[Stdcall]<
            nint,
            nint,
            int> register =
                (delegate* unmanaged[Stdcall]<
                    nint,
                    nint,
                    int>)VTable(timedText)[3];
        _ = register(timedText, 0);
    }

    internal static void SelectTimedTextTrack(
        nint timedText,
        uint trackId,
        bool selected)
    {
        delegate* unmanaged[Stdcall]<
            nint,
            uint,
            int,
            int> select =
                (delegate* unmanaged[Stdcall]<
                    nint,
                    uint,
                    int,
                    int>)VTable(timedText)[4];
        ThrowIfFailed(
            select(
                timedText,
                trackId,
                selected ? 1 : 0),
            selected
                ? "select a Media Foundation timed-text track"
                : "deselect a Media Foundation timed-text track");
    }

    internal static MediaEngineTimedTextTrackInfo[]
        GetTimedTextTracks(nint timedText)
    {
        nint trackList = 0;
        delegate* unmanaged[Stdcall]<
            nint,
            nint*,
            int> getTracks =
                (delegate* unmanaged[Stdcall]<
                    nint,
                    nint*,
                    int>)VTable(timedText)[11];
        ThrowIfFailed(
            getTracks(timedText, &trackList),
            "enumerate Media Foundation timed-text tracks");
        if (trackList == 0)
        {
            return [];
        }
        try
        {
            delegate* unmanaged[Stdcall]<
                nint,
                uint> getLength =
                    (delegate* unmanaged[Stdcall]<
                        nint,
                        uint>)VTable(trackList)[3];
            uint count = getLength(trackList);
            var result =
                new MediaEngineTimedTextTrackInfo[
                    checked((int)count)];
            int written = 0;
            for (uint index = 0; index < count; index++)
            {
                nint track = 0;
                delegate* unmanaged[Stdcall]<
                    nint,
                    uint,
                    nint*,
                    int> getTrack =
                        (delegate* unmanaged[Stdcall]<
                            nint,
                            uint,
                            nint*,
                            int>)VTable(trackList)[4];
                if (getTrack(
                        trackList,
                        index,
                        &track) < 0 ||
                    track == 0)
                {
                    Release(track);
                    continue;
                }

                try
                {
                    uint id = CallUInt32(track, 3);
                    int kind =
                        unchecked((int)CallUInt32(track, 7));
                    _ = TryGetAllocatedString(
                        track,
                        4,
                        out string label);
                    _ = TryGetAllocatedString(
                        track,
                        6,
                        out string language);
                    string dispatchType = string.Empty;
                    if (kind == 3)
                    {
                        _ = TryGetAllocatedString(
                            track,
                            9,
                            out dispatchType);
                    }
                    Guid dataFormat = Guid.Empty;
                    Guid* format = &dataFormat;
                    delegate* unmanaged[Stdcall]<
                        nint,
                        Guid*,
                        int> getDataFormat =
                            (delegate* unmanaged[Stdcall]<
                                nint,
                                Guid*,
                                int>)VTable(track)[13];
                    if (getDataFormat(
                            track,
                            format) < 0)
                    {
                        dataFormat = Guid.Empty;
                    }

                    result[written++] =
                        new MediaEngineTimedTextTrackInfo(
                            id,
                            kind,
                            label,
                            language,
                            dispatchType,
                            dataFormat,
                            CallBool(track, 10));
                }
                finally
                {
                    Release(track);
                }
            }

            if (written == result.Length)
            {
                return result;
            }

            Array.Resize(ref result, written);
            return result;
        }
        finally
        {
            Release(trackList);
        }
    }

    internal static MediaEngineTimedTextCueInfo
        ReadTimedTextCue(nint cue)
    {
        uint id = CallUInt32(cue, 3);
        int kind = unchecked((int)CallUInt32(cue, 5));
        double startTime = CallDouble(cue, 6);
        double duration = CallDouble(cue, 7);
        uint trackId = CallUInt32(cue, 8);
        uint lineCount = CallUInt32(cue, 12);
        var lines = new string[checked((int)lineCount)];
        int written = 0;
        for (uint index = 0; index < lineCount; index++)
        {
            nint line = 0;
            delegate* unmanaged[Stdcall]<
                nint,
                uint,
                nint*,
                int> getLine =
                    (delegate* unmanaged[Stdcall]<
                        nint,
                        uint,
                        nint*,
                        int>)VTable(cue)[13];
            if (getLine(cue, index, &line) < 0 ||
                line == 0)
            {
                Release(line);
                continue;
            }

            try
            {
                if (TryGetAllocatedString(
                        line,
                        3,
                        out string text))
                {
                    lines[written++] = text;
                }
            }
            finally
            {
                Release(line);
            }
        }

        string combined = written switch
        {
            0 => string.Empty,
            1 => lines[0],
            _ => string.Join(
                "\n",
                lines,
                0,
                written)
        };
        return new MediaEngineTimedTextCueInfo(
            id,
            trackId,
            kind,
            startTime,
            duration,
            combined);
    }

    private static bool TryGetAllocatedString(
        nint value,
        int methodIndex,
        out string result)
    {
        nint text = 0;
        delegate* unmanaged[Stdcall]<
            nint,
            nint*,
            int> get =
                (delegate* unmanaged[Stdcall]<
                    nint,
                    nint*,
                    int>)VTable(value)[methodIndex];
        int status = get(value, &text);
        try
        {
            if (status < 0 || text == 0)
            {
                result = string.Empty;
                return false;
            }

            result =
                Marshal.PtrToStringUni(text) ??
                string.Empty;
            return true;
        }
        finally
        {
            CoTaskMemFree(text);
        }
    }

    private static bool TryGetStreamGuidAttribute(
        nint extended,
        uint nativeIndex,
        in Guid attribute,
        out Guid value)
    {
        var variant = new PropVariant();
        try
        {
            fixed (Guid* attributePointer = &attribute)
            {
                delegate* unmanaged[Stdcall]<
                    nint,
                    uint,
                    Guid*,
                    PropVariant*,
                    int> getStreamAttribute =
                    (delegate* unmanaged[Stdcall]<
                        nint,
                        uint,
                        Guid*,
                        PropVariant*,
                        int>)VTable(extended)[55];
                int result = getStreamAttribute(
                    extended,
                    nativeIndex,
                    attributePointer,
                    &variant);
                if (result < 0 ||
                    variant.VariantType !=
                        VariantTypeClassId ||
                    variant.Pointer == 0)
                {
                    value = Guid.Empty;
                    return false;
                }
                value = *(Guid*)variant.Pointer;
                return true;
            }
        }
        finally
        {
            _ = PropVariantClear(&variant);
        }
    }

    private static bool GetStreamSelection(
        nint extended,
        uint nativeIndex)
    {
        int selected = 0;
        delegate* unmanaged[Stdcall]<
            nint,
            uint,
            int*,
            int> getStreamSelection =
                (delegate* unmanaged[Stdcall]<
                    nint,
                    uint,
                    int*,
                    int>)VTable(extended)[56];
        ThrowIfFailed(
            getStreamSelection(
                extended,
                nativeIndex,
                &selected),
            "query a Media Engine stream selection");
        return selected != 0;
    }

    private static bool TryGetStreamStringAttribute(
        nint extended,
        uint nativeIndex,
        in Guid attribute,
        out string value)
    {
        var variant = new PropVariant();
        try
        {
            fixed (Guid* attributePointer = &attribute)
            {
                delegate* unmanaged[Stdcall]<
                    nint,
                    uint,
                    Guid*,
                    PropVariant*,
                    int> getStreamAttribute =
                    (delegate* unmanaged[Stdcall]<
                        nint,
                        uint,
                        Guid*,
                        PropVariant*,
                        int>)VTable(extended)[55];
                int result = getStreamAttribute(
                    extended,
                    nativeIndex,
                    attributePointer,
                    &variant);
                if (result < 0 ||
                    variant.VariantType !=
                        VariantTypeWideString ||
                    variant.Pointer == 0)
                {
                    value = string.Empty;
                    return false;
                }
                value =
                    Marshal.PtrToStringUni(
                        variant.Pointer) ??
                    string.Empty;
                return true;
            }
        }
        finally
        {
            _ = PropVariantClear(&variant);
        }
    }

    internal static bool TryGetVideoTick(
        nint engine,
        out long presentationTime)
    {
        long value = 0;
        delegate* unmanaged[Stdcall]<nint, long*, int> call =
            (delegate* unmanaged[Stdcall]<
                nint,
                long*,
                int>)VTable(engine)[44];
        int result = call(engine, &value);
        if (result == SFalse)
        {
            presentationTime = 0;
            return false;
        }
        ThrowIfFailed(result, "query a Media Foundation video tick");
        presentationTime = value;
        return true;
    }

    internal static void TransferVideoFrame(
        nint engine,
        nint texture,
        uint width,
        uint height)
    {
        var destination = new Rect
        {
            Right = checked((int)width),
            Bottom = checked((int)height)
        };
        delegate* unmanaged[Stdcall]<
            nint,
            nint,
            void*,
            Rect*,
            void*,
            int> call =
            (delegate* unmanaged[Stdcall]<
                nint,
                nint,
                void*,
                Rect*,
                void*,
                int>)VTable(engine)[43];
        ThrowIfFailed(
            call(
                engine,
                texture,
                null,
                &destination,
                null),
            "transfer the Media Foundation video frame");
    }

    internal static void ShutdownMediaEngine(nint engine)
    {
        if (engine != 0)
        {
            try
            {
                CallResult(
                    engine,
                    42,
                    "shut down the media engine");
            }
            catch
            {
            }
        }
    }

    internal static nint CreateSharedVideoTexture(
        nint device,
        uint width,
        uint height,
        out nint sharedHandle,
        out nint keyedMutex)
    {
        var description = new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DxgiFormatB8G8R8A8Unorm,
            SampleDescription = new SampleDescription
            {
                Count = 1
            },
            BindFlags =
                D3D11BindShaderResource |
                D3D11BindRenderTarget,
            MiscFlags =
                D3D11ResourceMiscSharedKeyedMutex |
                D3D11ResourceMiscSharedNtHandle
        };

        nint texture = 0;
        delegate* unmanaged[Stdcall]<
            nint,
            Texture2DDescription*,
            void*,
            nint*,
            int> createTexture =
            (delegate* unmanaged[Stdcall]<
                nint,
                Texture2DDescription*,
                void*,
                nint*,
                int>)VTable(device)[5];
        ThrowIfFailed(
            createTexture(
                device,
                &description,
                null,
                &texture),
            "create a shared D3D11 video texture");
        try
        {
            nint resource = QueryInterface(
                texture,
                in s_dxgiResource1);
            try
            {
                nint handle = 0;
                delegate* unmanaged[Stdcall]<
                    nint,
                    void*,
                    uint,
                    char*,
                    nint*,
                    int> createHandle =
                    (delegate* unmanaged[Stdcall]<
                        nint,
                        void*,
                        uint,
                        char*,
                        nint*,
                        int>)VTable(resource)[13];
                ThrowIfFailed(
                    createHandle(
                        resource,
                        null,
                        DxgiSharedResourceRead |
                        DxgiSharedResourceWrite,
                        null,
                        &handle),
                    "create the DXGI shared video handle");
                sharedHandle = handle;
            }
            finally
            {
                Release(resource);
            }

            keyedMutex = QueryInterface(
                texture,
                in s_dxgiKeyedMutex);
            return texture;
        }
        catch
        {
            Release(texture);
            throw;
        }
    }

    internal static nint CreateBgraReadbackTexture(
        nint device,
        uint width,
        uint height)
    {
        var description =
            new Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = DxgiFormatB8G8R8A8Unorm,
                SampleDescription =
                    new SampleDescription
                    {
                        Count = 1
                    },
                Usage = D3D11UsageStaging,
                CpuAccessFlags = D3D11CpuAccessRead
            };
        nint texture = 0;
        delegate* unmanaged[Stdcall]<
            nint,
            Texture2DDescription*,
            void*,
            nint*,
            int> createTexture =
            (delegate* unmanaged[Stdcall]<
                nint,
                Texture2DDescription*,
                void*,
                nint*,
                int>)VTable(device)[5];
        ThrowIfFailed(
            createTexture(
                device,
                &description,
                null,
                &texture),
            "create a D3D11 thumbnail readback texture");
        return texture;
    }

    internal static void ReadBgraTexture(
        nint immediateContext,
        nint source,
        nint staging,
        uint width,
        uint height,
        Span<byte> destination)
    {
        int rowBytes =
            checked((int)width * 4);
        int required =
            checked(rowBytes * (int)height);
        if (destination.Length < required)
        {
            throw new ArgumentException(
                "The thumbnail readback destination is too small.",
                nameof(destination));
        }

        CopyD3D11Texture(
            immediateContext,
            staging,
            source);
        var mapped =
            new MappedSubresource();
        delegate* unmanaged[Stdcall]<
            nint,
            nint,
            uint,
            uint,
            uint,
            MappedSubresource*,
            int> map =
            (delegate* unmanaged[Stdcall]<
                nint,
                nint,
                uint,
                uint,
                uint,
                MappedSubresource*,
                int>)VTable(immediateContext)[14];
        delegate* unmanaged[Stdcall]<
            nint,
            nint,
            uint,
            void> unmap =
            (delegate* unmanaged[Stdcall]<
                nint,
                nint,
                uint,
                void>)VTable(immediateContext)[15];
        bool isMapped = false;
        try
        {
            ThrowIfFailed(
                map(
                    immediateContext,
                    staging,
                    0,
                    1,
                    0,
                    &mapped),
                "map the D3D11 thumbnail readback texture");
            isMapped = true;
            if (mapped.Data is null ||
                mapped.RowPitch < rowBytes)
            {
                throw new InvalidDataException(
                    "D3D11 returned an invalid thumbnail row layout.");
            }
            for (uint y = 0;
                 y < height;
                 y++)
            {
                new ReadOnlySpan<byte>(
                    mapped.Data +
                    checked((nint)(y * mapped.RowPitch)),
                    rowBytes).CopyTo(
                        destination.Slice(
                            checked((int)y * rowBytes),
                            rowBytes));
            }
        }
        finally
        {
            if (isMapped)
            {
                unmap(
                    immediateContext,
                    staging,
                    0);
            }
        }
    }

    internal static bool TryAcquireKeyedMutex(
        nint keyedMutex,
        uint timeoutMilliseconds)
    {
        delegate* unmanaged[Stdcall]<
            nint,
            ulong,
            uint,
            int> acquire =
            (delegate* unmanaged[Stdcall]<
                nint,
                ulong,
                uint,
                int>)VTable(keyedMutex)[8];
        return acquire(
            keyedMutex,
            0,
            timeoutMilliseconds) >= 0;
    }

    internal static void ReleaseKeyedMutex(nint keyedMutex)
    {
        delegate* unmanaged[Stdcall]<
            nint,
            ulong,
            int> release =
            (delegate* unmanaged[Stdcall]<
                nint,
                ulong,
                int>)VTable(keyedMutex)[9];
        ThrowIfFailed(
            release(keyedMutex, 0),
            "release the DXGI keyed mutex");
    }

    internal static nint QueryInterface(
        nint value,
        in Guid interfaceId)
    {
        nint result = 0;
        fixed (Guid* id = &interfaceId)
        {
            delegate* unmanaged[Stdcall]<
                nint,
                Guid*,
                nint*,
                int> query =
                (delegate* unmanaged[Stdcall]<
                    nint,
                    Guid*,
                    nint*,
                    int>)VTable(value)[0];
            ThrowIfFailed(
                query(value, id, &result),
                $"query COM interface {interfaceId}");
        }
        return result;
    }

    internal static uint AddRef(nint value)
    {
        if (value == 0)
        {
            return 0;
        }
        delegate* unmanaged[Stdcall]<nint, uint> addRef =
            (delegate* unmanaged[Stdcall]<
                nint,
                uint>)VTable(value)[1];
        return addRef(value);
    }

    internal static uint Release(nint value)
    {
        if (value == 0)
        {
            return 0;
        }
        delegate* unmanaged[Stdcall]<nint, uint> release =
            (delegate* unmanaged[Stdcall]<
                nint,
                uint>)VTable(value)[2];
        return release(value);
    }

    internal static void CloseSharedHandle(nint handle)
    {
        if (handle != 0)
        {
            _ = CloseHandle(handle);
        }
    }

    internal static void ThrowIfFailed(
        int result,
        string operation)
    {
        if (result < 0)
        {
            Marshal.ThrowExceptionForHR(
                result,
                new IntPtr(-1));
        }
    }

    private static void CallResult(
        nint value,
        int methodIndex,
        string operation)
    {
        delegate* unmanaged[Stdcall]<nint, int> call =
            (delegate* unmanaged[Stdcall]<
                nint,
                int>)VTable(value)[methodIndex];
        ThrowIfFailed(call(value), operation);
    }

    private static void CallBooleanResult(
        nint value,
        int methodIndex,
        bool argument,
        string operation)
    {
        delegate* unmanaged[Stdcall]<nint, int, int> call =
            (delegate* unmanaged[Stdcall]<
                nint,
                int,
                int>)VTable(value)[methodIndex];
        ThrowIfFailed(
            call(value, argument ? 1 : 0),
            operation);
    }

    private static bool CallBool(
        nint value,
        int methodIndex)
    {
        delegate* unmanaged[Stdcall]<nint, int> call =
            (delegate* unmanaged[Stdcall]<
                nint,
                int>)VTable(value)[methodIndex];
        return call(value) != 0;
    }

    private static double CallDouble(
        nint value,
        int methodIndex)
    {
        delegate* unmanaged[Stdcall]<nint, double> call =
            (delegate* unmanaged[Stdcall]<
                nint,
                double>)VTable(value)[methodIndex];
        return call(value);
    }

    private static uint CallUInt32(
        nint value,
        int methodIndex)
    {
        delegate* unmanaged[Stdcall]<nint, uint> call =
            (delegate* unmanaged[Stdcall]<
                nint,
                uint>)VTable(value)[methodIndex];
        return call(value);
    }

    private static nint* VTable(nint value) =>
        *(nint**)value;

    [LibraryImport("ole32.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int CoInitializeEx(
        nint reserved,
        uint concurrencyModel);

    [LibraryImport("ole32.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int CoCreateInstance(
        Guid* classId,
        nint outer,
        uint context,
        Guid* interfaceId,
        nint* result);

    [LibraryImport("ole32.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial void CoTaskMemFree(nint value);

    [LibraryImport("ole32.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int PropVariantClear(
        PropVariant* value);

    [LibraryImport("mf.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int MFGetService(
        nint value,
        Guid* service,
        Guid* interfaceId,
        nint* result);

    [LibraryImport("mfplat.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int MFStartup(
        uint version,
        uint flags);

    [LibraryImport("mfplat.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int MFShutdown();

    [LibraryImport("mfplat.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int MFCreateAttributes(
        nint* attributes,
        uint initialSize);

    [LibraryImport("mfplat.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int MFCreateDXGIDeviceManager(
        uint* resetToken,
        nint* manager);

    [LibraryImport("d3d11.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int D3D11CreateDevice(
        nint adapter,
        uint driverType,
        nint software,
        uint flags,
        uint* featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        nint* device,
        uint* selectedFeatureLevel,
        nint* immediateContext);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial bool CloseHandle(nint handle);
}

internal sealed unsafe class MediaEngineNotification : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeObject
    {
        internal nint* VTable;
        internal nint StateHandle;
        internal int References;
    }

    private static readonly Guid s_unknown =
        new("00000000-0000-0000-c000-000000000046");
    private static readonly Guid s_notify =
        new("fee7c112-e776-42b5-9bbf-0048524e2bd5");
    private static readonly nint* s_vtable = CreateVTable();
    private nint _native;

    internal MediaEngineNotification(
        Action<uint, nuint, uint> onEvent)
    {
        ArgumentNullException.ThrowIfNull(onEvent);
        GCHandle state = GCHandle.Alloc(onEvent);
        NativeObject* value =
            (NativeObject*)NativeMemory.AllocZeroed(
                (nuint)sizeof(NativeObject));
        value->VTable = s_vtable;
        value->StateHandle =
            GCHandle.ToIntPtr(state);
        value->References = 1;
        _native = (nint)value;
    }

    internal nint NativePointer =>
        Volatile.Read(ref _native);

    public void Dispose()
    {
        nint value =
            Interlocked.Exchange(ref _native, 0);
        if (value != 0)
        {
            _ = ReleaseCore((NativeObject*)value);
        }
    }

    private static nint* CreateVTable()
    {
        nint* table =
            (nint*)NativeMemory.Alloc(
                (nuint)(4 * sizeof(nint)));
        table[0] =
            (nint)(delegate* unmanaged[Stdcall]<
                NativeObject*,
                Guid*,
                void**,
                int>)&QueryInterface;
        table[1] =
            (nint)(delegate* unmanaged[Stdcall]<
                NativeObject*,
                uint>)&AddRef;
        table[2] =
            (nint)(delegate* unmanaged[Stdcall]<
                NativeObject*,
                uint>)&Release;
        table[3] =
            (nint)(delegate* unmanaged[Stdcall]<
                NativeObject*,
                uint,
                nuint,
                uint,
                int>)&EventNotify;
        return table;
    }

    [UnmanagedCallersOnly(
        CallConvs = [typeof(CallConvStdcall)])]
    private static int QueryInterface(
        NativeObject* value,
        Guid* interfaceId,
        void** result)
    {
        if (result == null || interfaceId == null)
        {
            return unchecked((int)0x8000_4003);
        }
        if (*interfaceId != s_unknown &&
            *interfaceId != s_notify)
        {
            *result = null;
            return unchecked((int)0x8000_4002);
        }

        _ = AddRefCore(value);
        *result = value;
        return 0;
    }

    [UnmanagedCallersOnly(
        CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddRef(NativeObject* value) =>
        AddRefCore(value);

    private static uint AddRefCore(NativeObject* value) =>
        unchecked((uint)Interlocked.Increment(
            ref value->References));

    [UnmanagedCallersOnly(
        CallConvs = [typeof(CallConvStdcall)])]
    private static uint Release(NativeObject* value)
        => ReleaseCore(value);

    private static uint ReleaseCore(NativeObject* value)
    {
        int remaining =
            Interlocked.Decrement(
                ref value->References);
        if (remaining == 0)
        {
            GCHandle state =
                GCHandle.FromIntPtr(
                    value->StateHandle);
            state.Free();
            NativeMemory.Free(value);
        }
        return unchecked((uint)remaining);
    }

    [UnmanagedCallersOnly(
        CallConvs = [typeof(CallConvStdcall)])]
    private static int EventNotify(
        NativeObject* value,
        uint eventCode,
        nuint parameter1,
        uint parameter2)
    {
        try
        {
            var callback =
                (Action<uint, nuint, uint>)
                GCHandle.FromIntPtr(
                    value->StateHandle).Target!;
            callback(
                eventCode,
                parameter1,
                parameter2);
            return 0;
        }
        catch
        {
            return unchecked((int)0x8000_4005);
        }
    }
}

internal sealed unsafe class MediaEngineTimedTextNotification :
    IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeObject
    {
        internal nint* VTable;
        internal nint StateHandle;
        internal int References;
    }

    private sealed record Callbacks(
        Action<uint> TrackChanged,
        Action<int, int, uint> Error,
        Action<int, double, nint> Cue,
        Action Reset);

    private static readonly Guid s_unknown =
        new("00000000-0000-0000-c000-000000000046");
    private static readonly Guid s_notify =
        new("df6b87b6-ce12-45db-aba7-432fe054e57d");
    private static readonly nint* s_vtable = CreateVTable();
    private nint _native;

    internal MediaEngineTimedTextNotification(
        Action<uint> onTrackChanged,
        Action<int, int, uint> onError,
        Action<int, double, nint> onCue,
        Action onReset)
    {
        ArgumentNullException.ThrowIfNull(onTrackChanged);
        ArgumentNullException.ThrowIfNull(onError);
        ArgumentNullException.ThrowIfNull(onCue);
        ArgumentNullException.ThrowIfNull(onReset);
        GCHandle state = GCHandle.Alloc(
            new Callbacks(
                onTrackChanged,
                onError,
                onCue,
                onReset));
        NativeObject* value =
            (NativeObject*)NativeMemory.AllocZeroed(
                (nuint)sizeof(NativeObject));
        value->VTable = s_vtable;
        value->StateHandle =
            GCHandle.ToIntPtr(state);
        value->References = 1;
        _native = (nint)value;
    }

    internal nint NativePointer =>
        Volatile.Read(ref _native);

    public void Dispose()
    {
        nint value =
            Interlocked.Exchange(ref _native, 0);
        if (value != 0)
        {
            _ = ReleaseCore((NativeObject*)value);
        }
    }

    private static nint* CreateVTable()
    {
        nint* table =
            (nint*)NativeMemory.Alloc(
                (nuint)(10 * sizeof(nint)));
        table[0] =
            (nint)(delegate* unmanaged[Stdcall]<
                NativeObject*,
                Guid*,
                void**,
                int>)&QueryInterface;
        table[1] =
            (nint)(delegate* unmanaged[Stdcall]<
                NativeObject*,
                uint>)&AddRef;
        table[2] =
            (nint)(delegate* unmanaged[Stdcall]<
                NativeObject*,
                uint>)&Release;
        table[3] =
            (nint)(delegate* unmanaged[Stdcall]<
                NativeObject*,
                uint,
                void>)&TrackAdded;
        table[4] =
            (nint)(delegate* unmanaged[Stdcall]<
                NativeObject*,
                uint,
                void>)&TrackRemoved;
        table[5] =
            (nint)(delegate* unmanaged[Stdcall]<
                NativeObject*,
                uint,
                int,
                void>)&TrackSelected;
        table[6] =
            (nint)(delegate* unmanaged[Stdcall]<
                NativeObject*,
                uint,
                void>)&TrackReadyStateChanged;
        table[7] =
            (nint)(delegate* unmanaged[Stdcall]<
                NativeObject*,
                int,
                int,
                uint,
                void>)&Error;
        table[8] =
            (nint)(delegate* unmanaged[Stdcall]<
                NativeObject*,
                int,
                double,
                nint,
                void>)&Cue;
        table[9] =
            (nint)(delegate* unmanaged[Stdcall]<
                NativeObject*,
                void>)&Reset;
        return table;
    }

    [UnmanagedCallersOnly(
        CallConvs = [typeof(CallConvStdcall)])]
    private static int QueryInterface(
        NativeObject* value,
        Guid* interfaceId,
        void** result)
    {
        if (result == null || interfaceId == null)
        {
            return unchecked((int)0x8000_4003);
        }
        if (*interfaceId != s_unknown &&
            *interfaceId != s_notify)
        {
            *result = null;
            return unchecked((int)0x8000_4002);
        }

        _ = AddRefCore(value);
        *result = value;
        return 0;
    }

    [UnmanagedCallersOnly(
        CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddRef(NativeObject* value) =>
        AddRefCore(value);

    private static uint AddRefCore(NativeObject* value) =>
        unchecked((uint)Interlocked.Increment(
            ref value->References));

    [UnmanagedCallersOnly(
        CallConvs = [typeof(CallConvStdcall)])]
    private static uint Release(NativeObject* value) =>
        ReleaseCore(value);

    private static uint ReleaseCore(NativeObject* value)
    {
        int remaining =
            Interlocked.Decrement(
                ref value->References);
        if (remaining == 0)
        {
            GCHandle state =
                GCHandle.FromIntPtr(
                    value->StateHandle);
            state.Free();
            NativeMemory.Free(value);
        }
        return unchecked((uint)remaining);
    }

    [UnmanagedCallersOnly(
        CallConvs = [typeof(CallConvStdcall)])]
    private static void TrackAdded(
        NativeObject* value,
        uint trackId) =>
        DispatchTrackChanged(value, trackId);

    [UnmanagedCallersOnly(
        CallConvs = [typeof(CallConvStdcall)])]
    private static void TrackRemoved(
        NativeObject* value,
        uint trackId) =>
        DispatchTrackChanged(value, trackId);

    [UnmanagedCallersOnly(
        CallConvs = [typeof(CallConvStdcall)])]
    private static void TrackSelected(
        NativeObject* value,
        uint trackId,
        int selected) =>
        DispatchTrackChanged(value, trackId);

    [UnmanagedCallersOnly(
        CallConvs = [typeof(CallConvStdcall)])]
    private static void TrackReadyStateChanged(
        NativeObject* value,
        uint trackId) =>
        DispatchTrackChanged(value, trackId);

    private static void DispatchTrackChanged(
        NativeObject* value,
        uint trackId)
    {
        try
        {
            GetCallbacks(value).TrackChanged(trackId);
        }
        catch
        {
        }
    }

    [UnmanagedCallersOnly(
        CallConvs = [typeof(CallConvStdcall)])]
    private static void Error(
        NativeObject* value,
        int errorCode,
        int extendedErrorCode,
        uint sourceTrackId)
    {
        try
        {
            GetCallbacks(value).Error(
                errorCode,
                extendedErrorCode,
                sourceTrackId);
        }
        catch
        {
        }
    }

    [UnmanagedCallersOnly(
        CallConvs = [typeof(CallConvStdcall)])]
    private static void Cue(
        NativeObject* value,
        int cueEvent,
        double currentTime,
        nint cue)
    {
        try
        {
            GetCallbacks(value).Cue(
                cueEvent,
                currentTime,
                cue);
        }
        catch
        {
        }
    }

    [UnmanagedCallersOnly(
        CallConvs = [typeof(CallConvStdcall)])]
    private static void Reset(NativeObject* value)
    {
        try
        {
            GetCallbacks(value).Reset();
        }
        catch
        {
        }
    }

    private static Callbacks GetCallbacks(
        NativeObject* value) =>
        (Callbacks)GCHandle.FromIntPtr(
            value->StateHandle).Target!;
}
