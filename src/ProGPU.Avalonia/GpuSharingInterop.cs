using System;
using System.Runtime.InteropServices;

namespace ProGPU.Avalonia;

public static unsafe class GpuSharingInterop
{
    // --- macOS IOSurface Interop ---
    
    private const string ObjcLib = "/usr/lib/libobjc.A.dylib";
    private const string IOSurfaceLib = "/System/Library/Frameworks/IOSurface.framework/IOSurface";

    [DllImport(ObjcLib, EntryPoint = "objc_getClass")]
    public static extern IntPtr GetClass(string name);

    [DllImport(ObjcLib, EntryPoint = "sel_registerName")]
    public static extern IntPtr RegisterSelector(string name);

    [DllImport(ObjcLib, EntryPoint = "objc_msgSend")]
    public static extern IntPtr MsgSend(IntPtr self, IntPtr op);

    [DllImport(ObjcLib, EntryPoint = "objc_msgSend")]
    public static extern IntPtr MsgSend(IntPtr self, IntPtr op, IntPtr arg1);

    [DllImport(ObjcLib, EntryPoint = "objc_msgSend")]
    public static extern IntPtr MsgSend(IntPtr self, IntPtr op, IntPtr arg1, IntPtr arg2);

    [DllImport(ObjcLib, EntryPoint = "objc_msgSend")]
    public static extern IntPtr MsgSend(IntPtr self, IntPtr op, int arg1);

    [DllImport(IOSurfaceLib)]
    public static extern IntPtr IOSurfaceCreate(IntPtr properties);

    [DllImport(IOSurfaceLib)]
    public static extern void IOSurfaceIncrementUseCount(IntPtr buffer);

    [DllImport(IOSurfaceLib)]
    public static extern void IOSurfaceDecrementUseCount(IntPtr buffer);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    public static extern void CFRelease(IntPtr cf);

    [DllImport(IOSurfaceLib)]
    public static extern int IOSurfaceLock(IntPtr buffer, uint options, uint* seed);

    [DllImport(IOSurfaceLib)]
    public static extern int IOSurfaceUnlock(IntPtr buffer, uint options, uint* seed);

    [DllImport(IOSurfaceLib)]
    public static extern void* IOSurfaceGetBaseAddress(IntPtr buffer);

    [DllImport(IOSurfaceLib)]
    public static extern nuint IOSurfaceGetBytesPerRow(IntPtr buffer);
    
    public static IntPtr CreateMacSharedSurface(uint width, uint height)
    {
        try
        {
            // 1. Get classes
            IntPtr nsMutableDictionaryClass = GetClass("NSMutableDictionary");
            IntPtr nsNumberClass = GetClass("NSNumber");
            IntPtr nsStringClass = GetClass("NSString");

            if (nsMutableDictionaryClass == IntPtr.Zero || nsNumberClass == IntPtr.Zero || nsStringClass == IntPtr.Zero)
                return IntPtr.Zero;

            // 2. Selectors
            IntPtr dictionarySel = RegisterSelector("dictionary");
            IntPtr setObjectSel = RegisterSelector("setObject:forKey:");
            IntPtr numberWithIntSel = RegisterSelector("numberWithInt:");
            IntPtr stringWithUtf8Sel = RegisterSelector("stringWithUTF8String:");

            // 3. Create dictionary
            IntPtr dict = MsgSend(nsMutableDictionaryClass, dictionarySel);
            if (dict == IntPtr.Zero) return IntPtr.Zero;

            // 4. Create keys & values
            void AddIntValue(string key, int value)
            {
                IntPtr utf8Key = Marshal.StringToHGlobalAnsi(key);
                try
                {
                    IntPtr nsKey = MsgSend(nsStringClass, stringWithUtf8Sel, utf8Key);
                    IntPtr nsVal = MsgSend(nsNumberClass, numberWithIntSel, value);
                    MsgSend(dict, setObjectSel, nsVal, nsKey);
                }
                finally
                {
                    Marshal.FreeHGlobal(utf8Key);
                }
            }

            AddIntValue("IOSurfaceWidth", (int)width);
            AddIntValue("IOSurfaceHeight", (int)height);
            AddIntValue("IOSurfaceBytesPerElement", 4);
            
            // PixelFormat 'BGRA' = 1111970369 (0x42475241)
            int bgraFormat = 1111970369;
            AddIntValue("IOSurfacePixelFormat", bgraFormat);
            
            uint bytesPerRow = (width * 4 + 255) & ~255u;
            AddIntValue("IOSurfaceBytesPerRow", (int)bytesPerRow);
            AddIntValue("IOSurfaceAllocSize", (int)(bytesPerRow * height));

            // 5. Create IOSurface
            IntPtr surface = IOSurfaceCreate(dict);
            if (surface != IntPtr.Zero)
            {
                IOSurfaceIncrementUseCount(surface);
            }
            return surface;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }
    
    public static void ReleaseMacSharedSurface(IntPtr surface)
    {
        if (surface != IntPtr.Zero)
        {
            try
            {
                IOSurfaceDecrementUseCount(surface);
                CFRelease(surface);
            }
            catch
            {
                // Graceful error handling for non-supported platforms
            }
        }
    }

    // --- Windows D3D11 / DXGI Interop ---
    
    [StructLayout(LayoutKind.Sequential)]
    public struct D3D11_TEXTURE2D_DESC
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public int Format; // DXGI_FORMAT_B8G8R8A8_UNORM = 87
        public DXGI_SAMPLE_DESC SampleDesc;
        public int Usage; // D3D11_USAGE_DEFAULT = 0
        public uint BindFlags; // D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE = 40
        public uint CPUAccessFlags;
        public uint MiscFlags; // D3D11_RESOURCE_MISC_SHARED = 2
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DXGI_SAMPLE_DESC
    {
        public uint Count;
        public uint Quality;
    }

    [DllImport("d3d11.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern int D3D11CreateDevice(
        IntPtr pAdapter,
        int driverType,
        IntPtr Software,
        uint flags,
        int[]? pFeatureLevels,
        uint featureLevels,
        uint sdkVersion,
        out IntPtr ppDevice,
        out int pFeatureLevel,
        out IntPtr ppImmediateContext);

    public static class COMHelper
    {
        public static int CallCreateTexture2D(
            IntPtr device,
            ref D3D11_TEXTURE2D_DESC desc,
            IntPtr initialData,
            out IntPtr texture)
        {
            IntPtr* vtbl = *(IntPtr**)device;
            IntPtr funcPtr = vtbl[5]; // CreateTexture2D is index 5
            
            delegate* unmanaged[Stdcall]<IntPtr, ref D3D11_TEXTURE2D_DESC, IntPtr, out IntPtr, int> createTexture2D = 
                (delegate* unmanaged[Stdcall]<IntPtr, ref D3D11_TEXTURE2D_DESC, IntPtr, out IntPtr, int>)funcPtr;
                
            return createTexture2D(device, ref desc, initialData, out texture);
        }
        
        public static int CallQueryInterface(
            IntPtr obj,
            ref Guid riid,
            out IntPtr ppvObject)
        {
            IntPtr* vtbl = *(IntPtr**)obj;
            IntPtr funcPtr = vtbl[0]; // QueryInterface is index 0
            
            delegate* unmanaged[Stdcall]<IntPtr, ref Guid, out IntPtr, int> queryInterface = 
                (delegate* unmanaged[Stdcall]<IntPtr, ref Guid, out IntPtr, int>)funcPtr;
                
            return queryInterface(obj, ref riid, out ppvObject);
        }
        
        public static int CallGetSharedHandle(
            IntPtr resource,
            out IntPtr sharedHandle)
        {
            IntPtr* vtbl = *(IntPtr**)resource;
            IntPtr funcPtr = vtbl[8]; // GetSharedHandle is index 8
            
            delegate* unmanaged[Stdcall]<IntPtr, out IntPtr, int> getSharedHandle = 
                (delegate* unmanaged[Stdcall]<IntPtr, out IntPtr, int>)funcPtr;
                
            return getSharedHandle(resource, out sharedHandle);
        }
        
        public static void CallGetImmediateContext(IntPtr device, out IntPtr context)
        {
            IntPtr* vtbl = *(IntPtr**)device;
            IntPtr funcPtr = vtbl[40]; // GetImmediateContext is index 40
            
            delegate* unmanaged[Stdcall]<IntPtr, out IntPtr, void> getImmediateContext = 
                (delegate* unmanaged[Stdcall]<IntPtr, out IntPtr, void>)funcPtr;
                
            getImmediateContext(device, out context);
        }

        public static void CallUpdateSubresource(
            IntPtr context,
            IntPtr resource,
            uint dstSubresource,
            IntPtr dstBox,
            void* srcData,
            uint srcRowPitch,
            uint srcDepthPitch)
        {
            IntPtr* vtbl = *(IntPtr**)context;
            IntPtr funcPtr = vtbl[49]; // UpdateSubresource is index 49
            
            delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, IntPtr, void*, uint, uint, void> updateSubresource = 
                (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, IntPtr, void*, uint, uint, void>)funcPtr;
                
            updateSubresource(context, resource, dstSubresource, dstBox, srcData, srcRowPitch, srcDepthPitch);
        }

        public static uint CallRelease(IntPtr obj)
        {
            if (obj == IntPtr.Zero) return 0;
            IntPtr* vtbl = *(IntPtr**)obj;
            IntPtr funcPtr = vtbl[2]; // Release is index 2
            
            delegate* unmanaged[Stdcall]<IntPtr, uint> release = 
                (delegate* unmanaged[Stdcall]<IntPtr, uint>)funcPtr;
                
            return release(obj);
        }
    }

    public static IntPtr CreateWindowsSharedTexture(uint width, uint height, out IntPtr outDevice, out IntPtr outTexture2D)
    {
        outDevice = IntPtr.Zero;
        outTexture2D = IntPtr.Zero;

        try
        {
            // D3D_DRIVER_TYPE_HARDWARE = 1
            // SDK_VERSION = 7
            int hr = D3D11CreateDevice(
                IntPtr.Zero,
                1, // Hardware
                IntPtr.Zero,
                0, // flags
                null,
                0,
                7, // SDK_VERSION
                out IntPtr device,
                out int featureLevel,
                out IntPtr context);

            if (hr < 0) return IntPtr.Zero;
            outDevice = device;
            
            if (context != IntPtr.Zero)
            {
                COMHelper.CallRelease(context);
            }

            var desc = new D3D11_TEXTURE2D_DESC
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = 87, // DXGI_FORMAT_B8G8R8A8_UNORM
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
                Usage = 0, // D3D11_USAGE_DEFAULT
                BindFlags = 40, // D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE
                CPUAccessFlags = 0,
                MiscFlags = 2 // D3D11_RESOURCE_MISC_SHARED
            };

            hr = COMHelper.CallCreateTexture2D(device, ref desc, IntPtr.Zero, out IntPtr texture);
            if (hr < 0)
            {
                COMHelper.CallRelease(device);
                outDevice = IntPtr.Zero;
                return IntPtr.Zero;
            }
            outTexture2D = texture;

            var dxgiResourceGuid = new Guid("035f3e44-85d6-4e52-a0e2-55db242c2378");
            hr = COMHelper.CallQueryInterface(texture, ref dxgiResourceGuid, out IntPtr dxgiResource);
            if (hr < 0)
            {
                COMHelper.CallRelease(texture);
                COMHelper.CallRelease(device);
                outDevice = IntPtr.Zero;
                outTexture2D = IntPtr.Zero;
                return IntPtr.Zero;
            }

            hr = COMHelper.CallGetSharedHandle(dxgiResource, out IntPtr sharedHandle);
            COMHelper.CallRelease(dxgiResource);

            if (hr < 0)
            {
                COMHelper.CallRelease(texture);
                COMHelper.CallRelease(device);
                outDevice = IntPtr.Zero;
                outTexture2D = IntPtr.Zero;
                return IntPtr.Zero;
            }

            return sharedHandle;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }
    
    public static void ReleaseWindowsSharedTexture(IntPtr device, IntPtr texture)
    {
        try
        {
            if (texture != IntPtr.Zero)
            {
                COMHelper.CallRelease(texture);
            }
            if (device != IntPtr.Zero)
            {
                COMHelper.CallRelease(device);
            }
        }
        catch
        {
            // Graceful error handling
        }
    }
}
