using System.Runtime.InteropServices;
using System.Threading;

namespace PadExtend.Sender;

// 共享内存帧读取：对应 src/Shared/padextend_shared.h 的契约。
// 打开驱动创建的 Local\PadExtendFrame 映射与 Local\PadExtendFrameReady 事件，
// 等事件→按 seqBefore/seqAfter 做 torn-read 校验→拷出 top-down 紧凑 BGRA。
internal sealed class SharedFrameReader : IDisposable
{
    private const string MapName = "Global\\PadExtendFrame";
    private const string EventName = "Global\\PadExtendFrameReady";
    private const uint Magic = 0x50455853; // 'PEXS'
    // 头布局（4 字节对齐）：magic,version,width,height,rowPitch,format,seqBefore,seqAfter (8*4=32)
    //  + payloadBytes(uint64, 偏移 32) + reserved[6] (偏移 40..63)。头大小 = 64。
    private const int HeaderSize = 64;
    private const int OffMagic = 0, OffWidth = 8, OffHeight = 12, OffRowPitch = 16,
                      OffFormat = 20, OffSeqBefore = 24, OffSeqAfter = 28;

    private MemoryMappedFileHandleWrapper? _mmf;
    private EventWaitHandle? _ready;
    private readonly byte[][] _bufs = { Array.Empty<byte>(), Array.Empty<byte>() };
    private int _bufIdx = 0;
    private uint _lastSeq = 0;            // 上一次成功读取的 seqAfter
    private int _staleCount = 0;          // 连续读到相同 seq 的次数（检测驱动停止）
    private const int MaxStaleBeforeReopen = 30; // 连续 30 次（~6 秒）无新帧则重新打开映射

    public bool Available => _mmf != null;

    public bool TryOpen()
    {
        // 先关闭旧的映射（如果有），确保拿到最新的 handle
        Close();
        try
        {
            _mmf = MemoryMappedFileHandleWrapper.OpenExisting(MapName);
            _ready = EventWaitHandle.OpenExisting(EventName);
            _lastSeq = 0;
            _staleCount = 0;
            return true;
        }
        catch
        {
            Close();
            return false;
        }
    }

    // 关闭映射但不 Dispose 整个对象，允许重新 TryOpen
    private void Close()
    {
        _mmf?.Dispose(); _mmf = null;
        _ready?.Dispose(); _ready = null;
    }

    // 等待下一帧并拷出。timeoutMs 内无新帧返回 null。
    // 如果长时间没有新帧（驱动停止/重启），自动尝试重新打开映射。
    public (byte[] bgra, int w, int h)? WaitFrame(int timeoutMs)
    {
        if (_mmf == null)
        {
            // 映射不可用，尝试重新打开
            if (!TryOpen()) return null;
        }
        _ready?.WaitOne(timeoutMs);

        var view = _mmf!.View;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            uint magic = ReadU32(view, OffMagic);
            if (magic != Magic)
            {
                // magic 不对，映射可能已失效，尝试重新打开
                _staleCount++;
                if (_staleCount >= MaxStaleBeforeReopen)
                {
                    Console.WriteLine("[shm] magic mismatch, reopening mapping...");
                    if (TryOpen() && _mmf != null)
                    {
                        view = _mmf.View;
                        continue; // 重新打开后重试
                    }
                }
                return null;
            }
            int w = (int)ReadU32(view, OffWidth);
            int h = (int)ReadU32(view, OffHeight);
            int pitch = (int)ReadU32(view, OffRowPitch);
            if (w <= 0 || h <= 0 || pitch <= 0) return null;
            int tight = w * 4;
            long need = (long)tight * h;

            // 双缓冲复用，避免每帧 14MB 堆分配；驱动按 tight pitch 紧凑写入，直接单次拷贝。
            byte[] dst = _bufs[_bufIdx];
            if (dst.Length < need) { dst = new byte[need]; _bufs[_bufIdx] = dst; }

            uint seqBefore = ReadU32(view, OffSeqBefore);
            if (pitch == tight)
            {
                Marshal.Copy(IntPtr.Add(view, HeaderSize), dst, 0, (int)need);
            }
            else
            {
                for (int y = 0; y < h; y++)
                    Marshal.Copy(IntPtr.Add(view, HeaderSize + y * pitch), dst, y * tight, tight);
            }
            uint seqAfter = ReadU32(view, OffSeqAfter);

            if (seqBefore == seqAfter)
            {
                // 检测是否有新帧：seq 没变说明驱动没有产新帧
                if (seqAfter == _lastSeq)
                {
                    _staleCount++;
                    if (_staleCount >= MaxStaleBeforeReopen)
                    {
                        // 长时间没有新帧，驱动可能重启了，尝试重新打开映射
                        Console.WriteLine($"[shm] no new frames for {_staleCount} reads, reopening mapping...");
                        if (TryOpen() && _mmf != null)
                        {
                            view = _mmf.View;
                            continue; // 重新打开后重试
                        }
                    }
                    // 返回上一帧数据（复用），但不切换缓冲
                    return (dst, w, h);
                }
                _lastSeq = seqAfter;
                _staleCount = 0;
                _bufIdx ^= 1; // 下帧用另一块，调用方可安全持有本帧直到下次 WaitFrame
                return (dst, w, h);
            }
            // torn read: 重试
        }
        return null;
    }

    private static unsafe uint ReadU32(IntPtr basePtr, int off)
    {
        return *(uint*)((byte*)basePtr + off);
    }

    public void Dispose()
    {
        _mmf?.Dispose(); _mmf = null;
        _ready?.Dispose(); _ready = null;
    }
}

// 用 Win32 OpenFileMapping + MapViewOfFile，避免 MemoryMappedFile 对 Local\ 命名空间的限制。
internal sealed class MemoryMappedFileHandleWrapper : IDisposable
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenFileMappingW(uint dwDesiredAccess, bool bInheritHandle, string lpName);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr MapViewOfFile(IntPtr hFileMappingObject, uint dwDesiredAccess, uint offHigh, uint offLow, UIntPtr dwNumberOfBytesToMap);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UnmapViewOfFile(IntPtr lpBaseAddress);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint FILE_MAP_READ = 0x0004;

    private IntPtr _handle;
    public IntPtr View { get; private set; }

    public static MemoryMappedFileHandleWrapper OpenExisting(string name)
    {
        IntPtr h = OpenFileMappingW(FILE_MAP_READ, false, name);
        if (h == IntPtr.Zero) throw new InvalidOperationException($"OpenFileMapping failed {Marshal.GetLastWin32Error()}");
        IntPtr view = MapViewOfFile(h, FILE_MAP_READ, 0, 0, UIntPtr.Zero);
        if (view == IntPtr.Zero) { CloseHandle(h); throw new InvalidOperationException($"MapViewOfFile failed {Marshal.GetLastWin32Error()}"); }
        return new MemoryMappedFileHandleWrapper { _handle = h, View = view };
    }

    public void Dispose()
    {
        if (View != IntPtr.Zero) { UnmapViewOfFile(View); View = IntPtr.Zero; }
        if (_handle != IntPtr.Zero) { CloseHandle(_handle); _handle = IntPtr.Zero; }
    }
}


