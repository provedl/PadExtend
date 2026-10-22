using System;

namespace PadExtend.Sender;

// 帧来源模式。Auto 保持旧默认行为（优先 shm，回退 file）。
internal enum FrameSourceMode { Auto, Duplication, Shm, File }

// 统一的帧来源接口：WaitFrame 返回紧凑 top-down BGRA 缓冲与宽高。
// SelfPaced=true 表示来源内部已按帧节流（shm/duplication 阻塞等待新帧），调用方无需额外 sleep。
internal interface IFrameSource : IDisposable
{
    string Name { get; }
    bool SelfPaced { get; }
    (byte[] bgra, int w, int h)? WaitFrame(int timeoutMs);
}

internal static class FrameSource
{
    public static IFrameSource Create(FrameSourceMode mode, string framePath, int outputIndex, int intervalMs)
    {
        switch (mode)
        {
            case FrameSourceMode.Duplication:
                return new DuplicationFrameSource(outputIndex);
            case FrameSourceMode.Shm:
            {
                var shm = new ShmFrameSource();
                shm.TryOpen();
                return shm;
            }
            case FrameSourceMode.File:
                return new FileFrameSource(framePath);
            case FrameSourceMode.Auto:
            default:
            {
                var shm = new ShmFrameSource();
                if (shm.TryOpen()) return shm;
                shm.Dispose();
                return new FileFrameSource(framePath);
            }
        }
    }
}

// 共享内存适配器（包装 SharedFrameReader，自研 IDD 驱动写入）。
internal sealed class ShmFrameSource : IFrameSource
{
    private readonly SharedFrameReader _r = new();
    public string Name => "SHARED MEMORY (IDD driver)";
    public bool SelfPaced => true;
    public bool TryOpen() => _r.TryOpen();
    public (byte[] bgra, int w, int h)? WaitFrame(int timeoutMs) => _r.WaitFrame(timeoutMs);
    public void Dispose() => _r.Dispose();
}

// 磁盘 BMP 轮询适配器。
internal sealed class FileFrameSource : IFrameSource
{
    private readonly string _path;
    public FileFrameSource(string path) { _path = path; }
    public string Name => $"BMP file ({_path})";
    public bool SelfPaced => false;
    public (byte[] bgra, int w, int h)? WaitFrame(int timeoutMs) => Program.TryReadBgra(_path);
    public void Dispose() { }
}
