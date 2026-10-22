using System;
using System.Runtime.InteropServices;

namespace PadExtend.Sender;

internal sealed partial class DuplicationFrameSource
{
    // ---- 常量 ----
    private const uint D3D_DRIVER_TYPE_HARDWARE = 1;
    private const uint D3D_DRIVER_TYPE_WARP = 5;
    private const uint D3D11_SDK_VERSION = 7;
    private const uint DXGI_FORMAT_B8G8R8A8_UNORM = 87;
    private const uint D3D11_USAGE_STAGING = 3;
    private const uint D3D11_CPU_ACCESS_READ = 0x20000;
    private const uint D3D11_MAP_READ = 1;
    private const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);
    private const int DXGI_ERROR_ACCESS_LOST = unchecked((int)0x887A0026);

    private static readonly Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    private static readonly Guid IID_IDXGIOutput1 = new("00cddea8-939b-4b83-a340-a685226666cc");
    private static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    // ---- 结构 ----
    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_TEXTURE2D_DESC
    {
        public uint Width, Height, MipLevels, ArraySize, Format;
        public uint SampleCount, SampleQuality;
        public uint Usage, BindFlags, CPUAccessFlags, MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_MAPPED_SUBRESOURCE { public IntPtr pData; public uint RowPitch; public uint DepthPitch; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_OUTDUPL_FRAME_INFO
    {
        public long LastPresentTime;
        public long LastMouseUpdateTime;
        public uint AccumulatedFrames;
        public int RectsCoalesced;
        public int ProtectedContentMaskedOut;
        public DXGI_OUTDUPL_POINTER_POSITION PointerPosition;
        public uint TotalMetadataBufferSize;
        public uint PointerShapeBufferSize;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_OUTDUPL_POINTER_POSITION { public int X; public int Y; public int Visible; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_OUTPUT_DESC_RAW
    {
        // WCHAR DeviceName[32] = 64 \u5b57\u8282\uff1b\u7528 byte[] \u663e\u5f0f\u5360\u4f4d\u907f\u514d char \u6309 ANSI \u5bbd\u5ea6\u9020\u6210\u540e\u7eed\u5b57\u6bb5\u9519\u4f4d\u3002
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public byte[] DeviceNameBytes;
        public int Left, Top, Right, Bottom;   // DesktopCoordinates RECT
        public int AttachedToDesktop;
        public uint Rotation;
        public IntPtr Monitor;
    }

    private struct OutputDesc { public string DeviceName; public int Width, Height; public bool AttachedToDesktop; }

    // ---- P/Invoke ----
    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(
        IntPtr pAdapter, uint driverType, IntPtr software, uint flags,
        IntPtr pFeatureLevels, uint featureLevels, uint sdkVersion,
        out IntPtr ppDevice, out uint pFeatureLevel, out IntPtr ppContext);

    // ---- COM vtable 调用辅助 ----
    private static IntPtr Vtbl(IntPtr obj, int slot)
    {
        IntPtr vtbl = Marshal.ReadIntPtr(obj);
        return Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size);
    }

    private static IntPtr QueryInterface(IntPtr obj, Guid iid)
    {
        var fn = (QueryInterfaceDel)Marshal.GetDelegateForFunctionPointer(Vtbl(obj, 0), typeof(QueryInterfaceDel));
        IntPtr pGuid = Marshal.AllocHGlobal(Marshal.SizeOf<Guid>());
        try
        {
            Marshal.StructureToPtr(iid, pGuid, false);
            int hr = fn(obj, pGuid, out IntPtr ppv);
            return hr >= 0 ? ppv : IntPtr.Zero;
        }
        finally { Marshal.FreeHGlobal(pGuid); }
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryInterfaceDel(IntPtr self, IntPtr iid, out IntPtr ppv);

    // IDXGIDevice::GetAdapter (slot 7)
    private static IntPtr DxgiDevice_GetAdapter(IntPtr dev)
    {
        var fn = (GetAdapterDel)Marshal.GetDelegateForFunctionPointer(Vtbl(dev, 7), typeof(GetAdapterDel));
        return fn(dev, out IntPtr a) >= 0 ? a : IntPtr.Zero;
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetAdapterDel(IntPtr self, out IntPtr adapter);

    // IDXGIAdapter::EnumOutputs (slot 7)
    private static IntPtr Adapter_EnumOutputs(IntPtr adapter, uint index)
    {
        var fn = (EnumOutputsDel)Marshal.GetDelegateForFunctionPointer(Vtbl(adapter, 7), typeof(EnumOutputsDel));
        int hr = fn(adapter, index, out IntPtr o);
        return hr >= 0 ? o : IntPtr.Zero;
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EnumOutputsDel(IntPtr self, uint index, out IntPtr output);

    // IDXGIOutput::GetDesc (slot 7)
    private static OutputDesc Output_GetDesc(IntPtr output)
    {
        var fn = (GetOutputDescDel)Marshal.GetDelegateForFunctionPointer(Vtbl(output, 7), typeof(GetOutputDescDel));
        IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf<DXGI_OUTPUT_DESC_RAW>());
        try
        {
            if (fn(output, p) < 0) return new OutputDesc { DeviceName = "?", Width = 0, Height = 0 };
            var raw = Marshal.PtrToStructure<DXGI_OUTPUT_DESC_RAW>(p);
            string name = raw.DeviceNameBytes != null
                ? System.Text.Encoding.Unicode.GetString(raw.DeviceNameBytes).TrimEnd('\0')
                : "?";
            return new OutputDesc
            {
                DeviceName = name,
                Width = raw.Right - raw.Left,
                Height = raw.Bottom - raw.Top,
                AttachedToDesktop = raw.AttachedToDesktop != 0
            };
        }
        finally { Marshal.FreeHGlobal(p); }
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetOutputDescDel(IntPtr self, IntPtr desc);

    // IDXGIOutput1::DuplicateOutput (slot 22)
    private static int Output1_DuplicateOutput(IntPtr output1, IntPtr device, out IntPtr dupl)
    {
        var fn = (DuplicateOutputDel)Marshal.GetDelegateForFunctionPointer(Vtbl(output1, 22), typeof(DuplicateOutputDel));
        return fn(output1, device, out dupl);
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int DuplicateOutputDel(IntPtr self, IntPtr device, out IntPtr dupl);

    // IDXGIOutputDuplication::AcquireNextFrame (slot 8)
    private static int Dupl_AcquireNextFrame(IntPtr dupl, uint timeoutMs, out DXGI_OUTDUPL_FRAME_INFO info, out IntPtr res)
    {
        var fn = (AcquireNextFrameDel)Marshal.GetDelegateForFunctionPointer(Vtbl(dupl, 8), typeof(AcquireNextFrameDel));
        return fn(dupl, timeoutMs, out info, out res);
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int AcquireNextFrameDel(IntPtr self, uint timeout, out DXGI_OUTDUPL_FRAME_INFO info, out IntPtr res);

    // IDXGIOutputDuplication::ReleaseFrame (slot 14)
    private static void Dupl_ReleaseFrame(IntPtr dupl)
    {
        var fn = (ReleaseFrameDel)Marshal.GetDelegateForFunctionPointer(Vtbl(dupl, 14), typeof(ReleaseFrameDel));
        fn(dupl);
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ReleaseFrameDel(IntPtr self);

    // ID3D11Device::CreateTexture2D (slot 5)
    private static int Device_CreateTexture2D(IntPtr device, ref D3D11_TEXTURE2D_DESC desc, IntPtr initData, out IntPtr tex)
    {
        var fn = (CreateTexture2DDel)Marshal.GetDelegateForFunctionPointer(Vtbl(device, 5), typeof(CreateTexture2DDel));
        return fn(device, ref desc, initData, out tex);
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateTexture2DDel(IntPtr self, ref D3D11_TEXTURE2D_DESC desc, IntPtr initData, out IntPtr tex);

    // ID3D11Texture2D::GetDesc (slot 10)
    private static D3D11_TEXTURE2D_DESC Texture2D_GetDesc(IntPtr tex)
    {
        var fn = (GetTexDescDel)Marshal.GetDelegateForFunctionPointer(Vtbl(tex, 10), typeof(GetTexDescDel));
        fn(tex, out D3D11_TEXTURE2D_DESC d);
        return d;
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void GetTexDescDel(IntPtr self, out D3D11_TEXTURE2D_DESC desc);

    // ID3D11DeviceContext::CopyResource (slot 47)
    private static void Context_CopyResource(IntPtr ctx, IntPtr dst, IntPtr src)
    {
        var fn = (CopyResourceDel)Marshal.GetDelegateForFunctionPointer(Vtbl(ctx, 47), typeof(CopyResourceDel));
        fn(ctx, dst, src);
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void CopyResourceDel(IntPtr self, IntPtr dst, IntPtr src);

    // ID3D11DeviceContext::Map (slot 14)
    private static int Context_Map(IntPtr ctx, IntPtr res, uint sub, uint mapType, uint flags, out D3D11_MAPPED_SUBRESOURCE mapped)
    {
        var fn = (MapDel)Marshal.GetDelegateForFunctionPointer(Vtbl(ctx, 14), typeof(MapDel));
        return fn(ctx, res, sub, mapType, flags, out mapped);
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int MapDel(IntPtr self, IntPtr res, uint sub, uint mapType, uint flags, out D3D11_MAPPED_SUBRESOURCE mapped);

    // ID3D11DeviceContext::Unmap (slot 15)
    private static void Context_Unmap(IntPtr ctx, IntPtr res, uint sub)
    {
        var fn = (UnmapDel)Marshal.GetDelegateForFunctionPointer(Vtbl(ctx, 15), typeof(UnmapDel));
        fn(ctx, res, sub);
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void UnmapDel(IntPtr self, IntPtr res, uint sub);

    // ---- 鼠标光标 ----
    internal const uint DXGI_OUTDUPL_POINTER_SHAPE_TYPE_MONOCHROME = 1;
    internal const uint DXGI_OUTDUPL_POINTER_SHAPE_TYPE_COLOR = 2;
    internal const uint DXGI_OUTDUPL_POINTER_SHAPE_TYPE_MASKED_COLOR = 4;

    [StructLayout(LayoutKind.Sequential)]
    internal struct DXGI_OUTDUPL_POINTER_SHAPE_INFO
    {
        public uint Type;
        public uint Width;
        public uint Height;
        public uint Pitch;
        public int HotSpotX;
        public int HotSpotY;
    }

    // IDXGIOutputDuplication::GetFramePointerShape (slot 11)
    private static int Dupl_GetFramePointerShape(IntPtr dupl, uint bufSize, byte[] buf, out uint required, out DXGI_OUTDUPL_POINTER_SHAPE_INFO info)
    {
        var fn = (GetPointerShapeDel)Marshal.GetDelegateForFunctionPointer(Vtbl(dupl, 11), typeof(GetPointerShapeDel));
        return fn(dupl, bufSize, buf, out required, out info);
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetPointerShapeDel(IntPtr self, uint bufSize, byte[] buf, out uint required, out DXGI_OUTDUPL_POINTER_SHAPE_INFO info);

}
