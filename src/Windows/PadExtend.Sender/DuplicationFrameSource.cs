using System;
using System.Runtime.InteropServices;

namespace PadExtend.Sender;

// Desktop Duplication API 帧来源。
// 从任意 DXGI 显示输出（包括已签名虚拟显示驱动创建的虚拟屏）采集 BGRA 帧，
// 不依赖任何自研驱动的私有共享内存/文件契约。
//
//   --source=duplication [--output=N]
//     N：DXGI 输出序号（0 基）。-1/省略 时自动取最后一个输出（虚拟屏通常最后接入）。
//     启动时会打印所有输出，便于对号入座。
internal sealed partial class DuplicationFrameSource : IFrameSource
{
    private readonly int _requestedOutput;
    private IntPtr _device, _context, _dupl, _output1;
    private IntPtr _staging;      // CPU 可读 staging 纹理
    private int _stagingW, _stagingH;
    private byte[][] _bufs = { Array.Empty<byte>(), Array.Empty<byte>() };
    private int _bufIdx;
    private bool _initialized;

    // 鼠标光标状态：形状缓存 + 位置 + 可见性。Desktop Duplication 不把光标画进桁，需自行叠加。
    private byte[] _curBuf = Array.Empty<byte>();
    private DXGI_OUTDUPL_POINTER_SHAPE_INFO _curShape;
    private bool _curHasShape;
    private int _curX, _curY;
    private bool _curVisible;

    public DuplicationFrameSource(int requestedOutput) { _requestedOutput = requestedOutput; }

    public string Name => $"DESKTOP DUPLICATION (output={_requestedOutput})";
    public bool SelfPaced => true;

    public (byte[] bgra, int w, int h)? WaitFrame(int timeoutMs)
    {
        if (!_initialized)
        {
            if (!TryInit()) { System.Threading.Thread.Sleep(Math.Min(timeoutMs, 200)); return null; }
        }
        try { return Acquire(timeoutMs); }
        catch (AccessLostException)
        {
            Console.WriteLine("[dup] access lost, reinitializing duplication...");
            Cleanup();
            return null;
        }
    }

    private sealed class AccessLostException : Exception { }

    // ---- 初始化：D3D11 device -> DXGI adapter -> 选 output -> DuplicateOutput ----
    private unsafe bool TryInit()
    {
        try
        {
            int hr = D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero,
                0, IntPtr.Zero, 0, D3D11_SDK_VERSION,
                out _device, out _, out _context);
            if (hr < 0 || _device == IntPtr.Zero)
            {
                hr = D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_WARP, IntPtr.Zero,
                    0, IntPtr.Zero, 0, D3D11_SDK_VERSION,
                    out _device, out _, out _context);
                if (hr < 0 || _device == IntPtr.Zero) { Console.WriteLine($"[dup] D3D11CreateDevice failed hr=0x{hr:X8}"); return false; }
            }

            IntPtr dxgiDevice = QueryInterface(_device, IID_IDXGIDevice);
            if (dxgiDevice == IntPtr.Zero) { Console.WriteLine("[dup] QI IDXGIDevice failed"); return false; }
            IntPtr adapter = DxgiDevice_GetAdapter(dxgiDevice);
            Marshal.Release(dxgiDevice);
            if (adapter == IntPtr.Zero) { Console.WriteLine("[dup] GetAdapter failed"); return false; }

            // 枚举输出
            var outputs = new System.Collections.Generic.List<IntPtr>();
            for (uint i = 0; ; i++)
            {
                IntPtr outp = Adapter_EnumOutputs(adapter, i);
                if (outp == IntPtr.Zero) break;
                outputs.Add(outp);
            }
            Marshal.Release(adapter);
            if (outputs.Count == 0) { Console.WriteLine("[dup] no DXGI outputs"); return false; }

            for (int i = 0; i < outputs.Count; i++)
            {
                var d = Output_GetDesc(outputs[i]);
                Console.WriteLine($"[dup] output #{i}: '{d.DeviceName}' {d.Width}x{d.Height} attached={d.AttachedToDesktop}");
            }

            int pick = _requestedOutput;
            if (pick < 0 || pick >= outputs.Count) pick = outputs.Count - 1; // 默认最后一个
            Console.WriteLine($"[dup] using output #{pick}");

            IntPtr chosen = outputs[pick];
            _output1 = QueryInterface(chosen, IID_IDXGIOutput1);
            foreach (var o in outputs) Marshal.Release(o);
            if (_output1 == IntPtr.Zero) { Console.WriteLine("[dup] QI IDXGIOutput1 failed"); return false; }

            hr = Output1_DuplicateOutput(_output1, _device, out _dupl);
            if (hr < 0 || _dupl == IntPtr.Zero) { Console.WriteLine($"[dup] DuplicateOutput failed hr=0x{hr:X8}"); return false; }

            _initialized = true;
            return true;
        }
        catch (Exception ex) { Console.WriteLine($"[dup] init error: {ex.Message}"); Cleanup(); return false; }
    }

    private unsafe (byte[] bgra, int w, int h)? Acquire(int timeoutMs)
    {
        IntPtr desktopResource;
        DXGI_OUTDUPL_FRAME_INFO info;
        int hr = Dupl_AcquireNextFrame(_dupl, (uint)Math.Max(1, timeoutMs), out info, out desktopResource);
        if (hr == DXGI_ERROR_WAIT_TIMEOUT) return null;
        if (hr == DXGI_ERROR_ACCESS_LOST) throw new AccessLostException();
        if (hr < 0 || desktopResource == IntPtr.Zero) return null;

        UpdateCursor(ref info);

        try
        {
            IntPtr tex = QueryInterface(desktopResource, IID_ID3D11Texture2D);
            if (tex == IntPtr.Zero) return null;
            try
            {
                D3D11_TEXTURE2D_DESC desc = Texture2D_GetDesc(tex);
                int w = (int)desc.Width, h = (int)desc.Height;

                EnsureStaging(w, h);
                if (_staging == IntPtr.Zero) return null;

                Context_CopyResource(_context, _staging, tex);

                D3D11_MAPPED_SUBRESOURCE map;
                int mhr = Context_Map(_context, _staging, 0, D3D11_MAP_READ, 0, out map);
                if (mhr < 0 || map.pData == IntPtr.Zero) return null;
                try
                {
                    int tight = w * 4;
                    long need = (long)tight * h;
                    byte[] dst = _bufs[_bufIdx];
                    if (dst.Length < need) { dst = new byte[need]; _bufs[_bufIdx] = dst; }

                    int pitch = (int)map.RowPitch;
                    if (pitch == tight)
                    {
                        Marshal.Copy(map.pData, dst, 0, (int)need);
                    }
                    else
                    {
                        for (int y = 0; y < h; y++)
                            Marshal.Copy(IntPtr.Add(map.pData, y * pitch), dst, y * tight, tight);
                    }
                    BlendCursor(dst, w, h);
                    _bufIdx ^= 1;
                    return (dst, w, h);
                }
                finally { Context_Unmap(_context, _staging, 0); }
            }
            finally { Marshal.Release(tex); }
        }
        finally
        {
            Marshal.Release(desktopResource);
            Dupl_ReleaseFrame(_dupl);
        }
    }

    // 根据 frame info 更新光标位置/可见性；有新形状时取回并缓存。
    private void UpdateCursor(ref DXGI_OUTDUPL_FRAME_INFO info)
    {
        // LastMouseUpdateTime != 0 表示本帧有鼠标更新（位置或可见性）。
        if (info.LastMouseUpdateTime != 0)
        {
            _curVisible = info.PointerPosition.Visible != 0;
            _curX = info.PointerPosition.X;
            _curY = info.PointerPosition.Y;
        }

        // PointerShapeBufferSize > 0 表示形状有更新，取回新位图。
        if (info.PointerShapeBufferSize > 0)
        {
            uint need = info.PointerShapeBufferSize;
            if (_curBuf.Length < need) _curBuf = new byte[need];
            int hr = Dupl_GetFramePointerShape(_dupl, (uint)_curBuf.Length, _curBuf, out _, out var shapeInfo);
            if (hr >= 0) { _curShape = shapeInfo; _curHasShape = true; }
        }
    }

    // 将缓存的光标按当前位置 alpha 混合到 top-down BGRA 帧缓冲。
    private void BlendCursor(byte[] dst, int w, int h)
    {
        if (!_curVisible || !_curHasShape) return;

        int cw = (int)_curShape.Width;
        int ch0 = (int)_curShape.Height;
        int pitch = (int)_curShape.Pitch;
        int px = _curX, py = _curY;
        if (cw <= 0 || pitch <= 0) return;

        if (_curShape.Type == DXGI_OUTDUPL_POINTER_SHAPE_TYPE_COLOR ||
            _curShape.Type == DXGI_OUTDUPL_POINTER_SHAPE_TYPE_MASKED_COLOR)
        {
            // 32bpp BGRA；MASKED_COLOR 与 COLOR 都当作带 alpha 近似处理。
            int ch = ch0;
            for (int y = 0; y < ch; y++)
            {
                int dy = py + y;
                if (dy < 0 || dy >= h) continue;
                int srow = y * pitch;
                int drow = dy * w * 4;
                for (int x = 0; x < cw; x++)
                {
                    int dx = px + x;
                    if (dx < 0 || dx >= w) continue;
                    int si = srow + x * 4;
                    if (si + 3 >= _curBuf.Length) continue;
                    byte b = _curBuf[si + 0], g = _curBuf[si + 1], r = _curBuf[si + 2], a = _curBuf[si + 3];
                    if (a == 0) continue;
                    int di = drow + dx * 4;
                    if (a == 255)
                    {
                        dst[di + 0] = b; dst[di + 1] = g; dst[di + 2] = r;
                    }
                    else
                    {
                        dst[di + 0] = (byte)((b * a + dst[di + 0] * (255 - a)) / 255);
                        dst[di + 1] = (byte)((g * a + dst[di + 1] * (255 - a)) / 255);
                        dst[di + 2] = (byte)((r * a + dst[di + 2] * (255 - a)) / 255);
                    }
                }
            }
        }
        else if (_curShape.Type == DXGI_OUTDUPL_POINTER_SHAPE_TYPE_MONOCHROME)
        {
            // 单色光标：高度为 Height/2，前半 AND 掩码、后半 XOR 掩码，每像素 1 bit。
            int ch = ch0 / 2;
            for (int y = 0; y < ch; y++)
            {
                int dy = py + y;
                if (dy < 0 || dy >= h) continue;
                int andRow = y * pitch;
                int xorRow = (y + ch) * pitch;
                int drow = dy * w * 4;
                for (int x = 0; x < cw; x++)
                {
                    int dx = px + x;
                    if (dx < 0 || dx >= w) continue;
                    int byteOff = x / 8;
                    int bit = 7 - (x % 8);
                    if (andRow + byteOff >= _curBuf.Length || xorRow + byteOff >= _curBuf.Length) continue;
                    int andBit = (_curBuf[andRow + byteOff] >> bit) & 1;
                    int xorBit = (_curBuf[xorRow + byteOff] >> bit) & 1;
                    int di = drow + dx * 4;
                    if (andBit == 0 && xorBit == 0) { dst[di + 0] = 0; dst[di + 1] = 0; dst[di + 2] = 0; }           // 黑
                    else if (andBit == 0 && xorBit == 1) { dst[di + 0] = 255; dst[di + 1] = 255; dst[di + 2] = 255; } // 白
                    else if (andBit == 1 && xorBit == 1)                                                              // 反色
                    {
                        dst[di + 0] = (byte)(255 - dst[di + 0]);
                        dst[di + 1] = (byte)(255 - dst[di + 1]);
                        dst[di + 2] = (byte)(255 - dst[di + 2]);
                    }
                    // andBit==1 && xorBit==0 ：透明，保留背景。
                }
            }
        }
    }

    private void EnsureStaging(int w, int h)
    {
        if (_staging != IntPtr.Zero && _stagingW == w && _stagingH == h) return;
        if (_staging != IntPtr.Zero) { Marshal.Release(_staging); _staging = IntPtr.Zero; }

        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
            Format = DXGI_FORMAT_B8G8R8A8_UNORM, SampleCount = 1, SampleQuality = 0,
            Usage = D3D11_USAGE_STAGING, BindFlags = 0,
            CPUAccessFlags = D3D11_CPU_ACCESS_READ, MiscFlags = 0
        };
        int hr = Device_CreateTexture2D(_device, ref desc, IntPtr.Zero, out _staging);
        if (hr < 0 || _staging == IntPtr.Zero) { Console.WriteLine($"[dup] create staging failed hr=0x{hr:X8}"); _staging = IntPtr.Zero; return; }
        _stagingW = w; _stagingH = h;
    }

    private void Cleanup()
    {
        if (_staging != IntPtr.Zero) { Marshal.Release(_staging); _staging = IntPtr.Zero; }
        if (_dupl != IntPtr.Zero) { Marshal.Release(_dupl); _dupl = IntPtr.Zero; }
        if (_output1 != IntPtr.Zero) { Marshal.Release(_output1); _output1 = IntPtr.Zero; }
        if (_context != IntPtr.Zero) { Marshal.Release(_context); _context = IntPtr.Zero; }
        if (_device != IntPtr.Zero) { Marshal.Release(_device); _device = IntPtr.Zero; }
        _stagingW = _stagingH = 0;
        _initialized = false;
    }

    public void Dispose() => Cleanup();
}
