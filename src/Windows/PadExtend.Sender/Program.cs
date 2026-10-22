using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace PadExtend.Sender;

// PadExtend 实时发送端（阶段四：H.264 over 协议 v1.1）
// 读取驱动写出的虚拟屏帧 (frame.bmp, 32bpp BGRA) → 调用本机 C++ 编码器 DLL
// (Media Foundation H.264) → 按协议 v1.1 发送 type=6(meta)/3(SPS+PPS)/4(IDR)/5(P)。
// 通道：TCP 127.0.0.1:<port>，经 adb reverse 走 USB 到 Android。
internal static class Program
{
    private static readonly byte[] Magic = "PEX1"u8.ToArray();
    private const uint TypeText = 1;
    private const uint TypeConfig = 3;    // SPS+PPS
    private const uint TypeKeyframe = 4;  // IDR
    private const uint TypeDelta = 5;     // P frame
    private const uint TypeMeta = 6;      // JSON {w,h,fps}

    private const string EncDll = "PadExtend.Encoder.dll";

    [DllImport(EncDll)] private static extern IntPtr PexEncoderCreate(int w, int h, int fps, int bitrate, int keyIntervalFrames);
    [DllImport(EncDll)] private static extern int PexEncoderEncode(IntPtr e, byte[] bgra, int bgraLen, out IntPtr outPtr, out int outLen, out int isKeyframe);
    [DllImport(EncDll)] private static extern int PexEncoderGetConfig(IntPtr e, out IntPtr outPtr, out int outLen);
    [DllImport(EncDll)] private static extern void PexEncoderForceKeyframe(IntPtr e);
    [DllImport(EncDll)] private static extern void PexEncoderDestroy(IntPtr e);

    private static async Task<int> Main(string[] args)
    {
        int port = 27555;
        int fps = 30;
        int bitrate = 8_000_000;
        int keyIntervalSec = 2;
        string framePath = @"C:\ProgramData\PadExtend\frame.bmp";
        // 帧来源：auto=优先 shm 再回退 file（旧行为）；duplication=Desktop Duplication 采集虚拟屏；
        //          shm=只用共享内存；file=只用磁盘 BMP。
        FrameSourceMode sourceMode = FrameSourceMode.Auto;
        int outputIndex = -1; // duplication 模式下选第几个显示输出；-1=自动挑最后一个（通常是最新接入的虚拟屏）。

        foreach (var a in args)
        {
            if (a.StartsWith("--port=", StringComparison.OrdinalIgnoreCase)) _ = int.TryParse(a["--port=".Length..], out port);
            else if (a.StartsWith("--fps=", StringComparison.OrdinalIgnoreCase)) _ = int.TryParse(a["--fps=".Length..], out fps);
            else if (a.StartsWith("--bitrate=", StringComparison.OrdinalIgnoreCase)) _ = int.TryParse(a["--bitrate=".Length..], out bitrate);
            else if (a.StartsWith("--frame=", StringComparison.OrdinalIgnoreCase)) framePath = a["--frame=".Length..];
            else if (a.StartsWith("--output=", StringComparison.OrdinalIgnoreCase)) _ = int.TryParse(a["--output=".Length..], out outputIndex);
            else if (a.StartsWith("--source=", StringComparison.OrdinalIgnoreCase))
            {
                string v = a["--source=".Length..];
                if (v.Equals("duplication", StringComparison.OrdinalIgnoreCase) || v.Equals("dup", StringComparison.OrdinalIgnoreCase)) sourceMode = FrameSourceMode.Duplication;
                else if (v.Equals("shm", StringComparison.OrdinalIgnoreCase)) sourceMode = FrameSourceMode.Shm;
                else if (v.Equals("file", StringComparison.OrdinalIgnoreCase) || v.Equals("bmp", StringComparison.OrdinalIgnoreCase)) sourceMode = FrameSourceMode.File;
                else sourceMode = FrameSourceMode.Auto;
            }
        }
        fps = Math.Clamp(fps, 1, 60);
        int frameIntervalMs = 1000 / fps;
        int keyIntervalFrames = Math.Max(1, keyIntervalSec * fps);

        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        Console.WriteLine($"[sender] listening 127.0.0.1:{port} fps={fps} bitrate={bitrate} source={framePath}");
        Console.WriteLine($"[sender] run on PC: adb reverse tcp:{port} tcp:{port}");

        // 单活动连接模型：新连接接管，旧连接立即让路（取消 + 关闭）。
        // 避免串行 serve 导致“新客户端连上却收不到任何字节”的连接错位。
        TcpClient? activeClient = null;
        CancellationTokenSource? activeCts = null;
        Task activeServe = Task.CompletedTask;
        while (true)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(); }
            catch (Exception ex) { Console.WriteLine($"[sender] accept error: {ex.Message}"); continue; }

            Console.WriteLine($"[sender] client connected: {client.Client.RemoteEndPoint}");

            // 让上一个连接退出：取消其 token 并关闭旧 socket，唤醒阻塞中的收发。
            var prevCts = activeCts;
            var prevClient = activeClient;
            var prevServe = activeServe;
            if (prevCts != null)
            {
                Console.WriteLine("[sender] new client takes over, dropping previous connection");
                try { prevCts.Cancel(); } catch { }
                try { prevClient?.Close(); } catch { }
                try { await prevServe; } catch { }
                prevCts.Dispose();
                prevClient?.Dispose();
            }

            var cts = new CancellationTokenSource();
            activeCts = cts;
            activeClient = client;
            activeServe = ServeClientAsync(client, framePath, sourceMode, outputIndex, frameIntervalMs, fps, bitrate, keyIntervalFrames, cts.Token);
        }
    }

    // 包装 ServeAsync：统一异常与资源清理，便于 accept 循环以 fire-and-forget 方式托管。
    private static async Task ServeClientAsync(TcpClient client, string framePath, FrameSourceMode sourceMode, int outputIndex, int intervalMs, int fps, int bitrate, int keyIntervalFrames, CancellationToken ct)
    {
        try { await ServeAsync(client, framePath, sourceMode, outputIndex, intervalMs, fps, bitrate, keyIntervalFrames, ct); }
        catch (OperationCanceledException) { Console.WriteLine("[sender] client dropped (superseded)"); }
        catch (Exception ex) { Console.WriteLine($"[sender] client disconnected: {ex.Message}"); }
        finally { client.Dispose(); }
    }

    private static async Task ServeAsync(TcpClient client, string framePath, FrameSourceMode sourceMode, int outputIndex, int intervalMs, int fps, int bitrate, int keyIntervalFrames, CancellationToken ct)
    {
        client.NoDelay = true;
        using var stream = client.GetStream();
        uint seq = 0;

        // 根据 sourceMode 选择帧来源，统一包装成 IFrameSource：
        //   Duplication -> Desktop Duplication 采集虚拟屏（配合已签名的虚拟显示驱动）
        //   Shm         -> 共享内存（自研 IDD 驱动写入）
        //   File        -> 磁盘 BMP 轮询
        //   Auto        -> 优先 shm，不可用则回退 file（旧默认行为）
        IFrameSource source = FrameSource.Create(sourceMode, framePath, outputIndex, intervalMs);
        bool pacedSource = source.SelfPaced; // duplication/shm 自带阻塞节流，file 需要外部 sleep
        Console.WriteLine($"[sender] frame source: {source.Name}");

        // 等待第一帧可读，拿到真实分辨率再建编码器。
        (byte[] bgra, int w, int h)? first = null;
        for (int tries = 0; tries < 200 && first == null && !ct.IsCancellationRequested; tries++)
        {
            first = source.WaitFrame(100);
            if (first == null && pacedSource == false) await Task.Delay(50, ct);
        }
        ct.ThrowIfCancellationRequested();
        if (first == null) throw new IOException("cannot obtain first frame");

        int W = first.Value.w & ~1, H = first.Value.h & ~1;
        // 复用进程级编码器：分辨率不变就不重建，避免每次拔插重连都付 MF 编码器初始化（~2 秒）的代价。
        IntPtr enc = GetOrCreateEncoder(W, H, fps, bitrate, keyIntervalFrames);
        if (enc == IntPtr.Zero) throw new InvalidOperationException("encoder create failed");
        // 新连接（含拔插重连）立即产 IDR，客户端拿到 config 后无需等下一个关键帧周期即可出画面。
        PexEncoderForceKeyframe(enc);

        var sendChannel = System.Threading.Channels.Channel.CreateBounded<(uint seq, uint type, byte[] payload)>(
            new System.Threading.Channels.BoundedChannelOptions(3) {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait,
                SingleReader = true, SingleWriter = true
            });

        try
        {
            // 发送任务（消费者）：独立把队列里的包推到 TCP，与编码重叠。
            var sendTask = Task.Run(async () =>
            {
                await foreach (var item in sendChannel.Reader.ReadAllAsync(ct))
                    await SendAsync(stream, item.seq, item.type, item.payload, ct);
            }, ct);

            await sendChannel.Writer.WriteAsync((seq++, TypeText, Encoding.UTF8.GetBytes($"PadExtend H264 up @ {DateTime.Now:HH:mm:ss}")), ct);
            await sendChannel.Writer.WriteAsync((seq++, TypeMeta, Encoding.UTF8.GetBytes($"{{\"w\":{W},\"h\":{H},\"fps\":{fps}}}")), ct);

            (byte[] bgra, int w, int h)? lastFrame = first;
            bool configSent = false;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long lastStats = 0; int frames = 0; long bytes = 0;

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    long tick = sw.ElapsedMilliseconds;
                    var f = source.WaitFrame(200);
                    if (f == null && pacedSource && lastFrame != null) f = lastFrame;
                    else if (f != null && pacedSource) lastFrame = f;
                    if (f != null && f.Value.w == W && f.Value.h == H)
                    {
                        byte[] buf = f.Value.bgra;
                        // 确保编码器有效（可能被 RecordEncodeResult 重建）
                        enc = GetOrCreateEncoder(W, H, fps, bitrate, keyIntervalFrames);
                        if (enc == IntPtr.Zero)
                        {
                            await Task.Delay(100, ct);
                            continue;
                        }
                        
                        int r = PexEncoderEncode(enc, buf, buf.Length, out IntPtr outPtr, out int outLen, out int isKey);
                        bool encodeSuccess = (r == 0 && outLen > 0);
                        
                        // 记录编码结果；如果连续失败过多会重建编码器
                        bool wasReset = RecordEncodeResult(encodeSuccess, W, H, fps, bitrate, keyIntervalFrames);
                        if (wasReset)
                        {
                            // 编码器已重建，重新获取并强制 IDR
                            enc = GetOrCreateEncoder(W, H, fps, bitrate, keyIntervalFrames);
                            if (enc != IntPtr.Zero)
                            {
                                PexEncoderForceKeyframe(enc);
                                configSent = false; // 需要重新发送 config
                                Console.WriteLine("[sender] encoder reset, will resend config on next keyframe");
                            }
                            continue;
                        }
                        
                        // 新连接的首个视频包必须是 IDR，且前面紧跟 config(SPS/PPS)。
                        // 复用进程级编码器时，ForceKeyframe 后硬件异步 MFT 会先吐出若干已排队的 P 帧，
                        // 这些 P 帧对刚建好的客户端解码器无参考意义——若放它们先过去，客户端只能一直丢帧等 IDR，
                        // 期间任何丢包/时序错位都会卡成永久黑屏。因此在首个 IDR 到来前，丢弃所有 P 帧。
                        if (encodeSuccess && (configSent || isKey != 0))
                        {
                            if (!configSent)
                            {
                                // 拿到首个 IDR：先发 config，紧接着发这个 IDR，保证客户端能干净起解。
                                if (PexEncoderGetConfig(enc, out IntPtr cfgPtr, out int cfgLen) == 0 && cfgLen > 0)
                                {
                                    var cfg = new byte[cfgLen];
                                    Marshal.Copy(cfgPtr, cfg, 0, cfgLen);
                                    await sendChannel.Writer.WriteAsync((seq++, TypeConfig, cfg), ct);
                                }
                                configSent = true;
                            }
                            var au = new byte[outLen];
                            Marshal.Copy(outPtr, au, 0, outLen);
                            await sendChannel.Writer.WriteAsync((seq++, isKey != 0 ? TypeKeyframe : TypeDelta, au), ct);
                            frames++; bytes += outLen;
                        }
                    }

                    long now = sw.ElapsedMilliseconds;
                    if (now - lastStats >= 2000)
                    {
                        double secs = (now - lastStats) / 1000.0;
                        Console.WriteLine($"[sender] {frames} AUs, {bytes / 1024} KB in {secs:F1}s ({frames / secs:F1} fps, {bytes / 1024.0 / secs:F0} KB/s)");
                        frames = 0; bytes = 0; lastStats = now;
                    }

                    if (!pacedSource)
                    {
                        int wait = (int)(intervalMs - (sw.ElapsedMilliseconds - tick));
                        if (wait > 0) await Task.Delay(wait, ct);
                    }
                }
                ct.ThrowIfCancellationRequested();
            }
            finally
            {
                sendChannel.Writer.TryComplete();
                try { await sendTask; } catch { }
            }
        }
        finally { source.Dispose(); } // 编码器为进程级复用，不在单连接结束时销毁。
    }

    // ---- 进程级编码器复用 ----
    // 跨连接保持一个编码器实例；分辨率变化才销毁重建。消除拔插重连时的编码器初始化延迟。
    private static readonly object s_encLock = new();
    private static IntPtr s_enc = IntPtr.Zero;
    private static int s_encW, s_encH;
    private static int s_encFailCount = 0;  // 连续编码失败次数
    private const int MaxEncFailBeforeReset = 60; // 连续 60 次编码失败则重建编码器

    private static IntPtr GetOrCreateEncoder(int w, int h, int fps, int bitrate, int keyIntervalFrames)
    {
        lock (s_encLock)
        {
            if (s_enc != IntPtr.Zero && s_encW == w && s_encH == h)
                return s_enc; // 复用

            if (s_enc != IntPtr.Zero)
            {
                PexEncoderDestroy(s_enc);
                s_enc = IntPtr.Zero;
                Console.WriteLine($"[sender] encoder recreated for {w}x{h} (was {s_encW}x{s_encH})");
            }

            IntPtr e = PexEncoderCreate(w, h, fps, bitrate, keyIntervalFrames);
            if (e != IntPtr.Zero)
            {
                s_enc = e; s_encW = w; s_encH = h;
                s_encFailCount = 0;
                Console.WriteLine($"[sender] encoder created {w}x{h}");
            }
            return e;
        }
    }

    // 强制重建编码器（长时间无输出时调用）
    private static void ResetEncoder(int w, int h, int fps, int bitrate, int keyIntervalFrames)
    {
        lock (s_encLock)
        {
            if (s_enc != IntPtr.Zero)
            {
                Console.WriteLine($"[sender] force resetting encoder after {s_encFailCount} failed encodes");
                PexEncoderDestroy(s_enc);
                s_enc = IntPtr.Zero;
            }
            s_encFailCount = 0;
            GetOrCreateEncoder(w, h, fps, bitrate, keyIntervalFrames);
        }
    }

    // 记录编码结果，连续失败过多则重建
    private static bool RecordEncodeResult(bool success, int w, int h, int fps, int bitrate, int keyIntervalFrames)
    {
        lock (s_encLock)
        {
            if (success)
            {
                s_encFailCount = 0;
                return false;
            }
            s_encFailCount++;
            if (s_encFailCount >= MaxEncFailBeforeReset)
            {
                ResetEncoder(w, h, fps, bitrate, keyIntervalFrames);
                return true; // 编码器已重建
            }
            return false;
        }
    }

    // ---- \u5e27\u6765\u6e90\u62bd\u8c61 ----

    // 读 32bpp BMP → 返回 top-down BGRA 缓冲 + 宽高。文件正写/读不全返回 null。
    internal static (byte[] bgra, int w, int h)? TryReadBgra(string path)
    {
        try
        {
            byte[] raw;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                raw = new byte[fs.Length];
                int off = 0;
                while (off < raw.Length) { int n = fs.Read(raw, off, raw.Length - off); if (n <= 0) break; off += n; }
                if (off != raw.Length) return null;
            }
            if (raw.Length < 54) return null;
            // BITMAPFILEHEADER(14) + BITMAPINFOHEADER(40)
            int bitCount = BitConverter.ToUInt16(raw, 28);
            if (bitCount != 32) return null;
            int w = BitConverter.ToInt32(raw, 18);
            int hRaw = BitConverter.ToInt32(raw, 22);
            int h = Math.Abs(hRaw);
            bool bottomUp = hRaw > 0;
            int offBits = BitConverter.ToInt32(raw, 10);
            long need = (long)offBits + (long)w * h * 4;
            if (raw.Length < need) return null;

            var outBuf = new byte[(long)w * h * 4];
            int rowBytes = w * 4;
            for (int row = 0; row < h; row++)
            {
                int src = bottomUp ? (h - 1 - row) : row;
                Buffer.BlockCopy(raw, offBits + src * rowBytes, outBuf, row * rowBytes, rowBytes);
            }
            return (outBuf, w, h);
        }
        catch { return null; }
    }

    private static async Task SendAsync(NetworkStream stream, uint seq, uint type, byte[] payload, CancellationToken ct)
    {
        var header = new byte[16];
        Array.Copy(Magic, 0, header, 0, 4);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), seq);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8, 4), type);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12, 4), (uint)payload.Length);
        await stream.WriteAsync(header, ct);
        if (payload.Length > 0) await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);
    }
}


