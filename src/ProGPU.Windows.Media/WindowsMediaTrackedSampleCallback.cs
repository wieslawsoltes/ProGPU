using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ProGPU.Windows.Media;

/// <summary>
/// AOT-safe IMFAsyncCallback used to return one bounded DXGI encoder target
/// after the Media Foundation sink writer releases its tracked sample.
/// </summary>
/// <remarks>
/// Construction allocates one native COM header and one GC handle. Invoke is
/// O(1), allocation-free, and never lets a managed exception cross the COM
/// ABI. The callback may outlive its managed root while Media Foundation owns
/// a COM reference.
/// </remarks>
internal sealed unsafe class
    WindowsMediaTrackedSampleCallback :
    IDisposable
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
    private static readonly Guid s_asyncCallback =
        new("a27003cf-2354-4f2a-8d6a-ab7cff15437e");
    private static readonly nint* s_vtable = CreateVTable();
    private nint _native;

    internal WindowsMediaTrackedSampleCallback(
        Action released)
    {
        ArgumentNullException.ThrowIfNull(released);
        GCHandle state = GCHandle.Alloc(released);
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
                (nuint)(5 * sizeof(nint)));
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
                uint*,
                uint*,
                int>)&GetParameters;
        table[4] =
            (nint)(delegate* unmanaged[Stdcall]<
                NativeObject*,
                nint,
                int>)&Invoke;
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
            *interfaceId != s_asyncCallback)
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
    private static uint AddRef(
        NativeObject* value) =>
        AddRefCore(value);

    private static uint AddRefCore(
        NativeObject* value) =>
        unchecked((uint)Interlocked.Increment(
            ref value->References));

    [UnmanagedCallersOnly(
        CallConvs = [typeof(CallConvStdcall)])]
    private static uint Release(
        NativeObject* value) =>
        ReleaseCore(value);

    private static uint ReleaseCore(
        NativeObject* value)
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
    private static int GetParameters(
        NativeObject* _,
        uint* flags,
        uint* queue)
    {
        if (flags != null)
        {
            *flags = 0;
        }
        if (queue != null)
        {
            *queue = 0;
        }
        return unchecked((int)0x8000_4001);
    }

    [UnmanagedCallersOnly(
        CallConvs = [typeof(CallConvStdcall)])]
    private static int Invoke(
        NativeObject* value,
        nint _)
    {
        try
        {
            var released =
                (Action)GCHandle.FromIntPtr(
                    value->StateHandle).Target!;
            released();
            return 0;
        }
        catch
        {
            return unchecked((int)0x8000_4005);
        }
    }
}
