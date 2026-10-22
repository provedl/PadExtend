using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PadExtend.TestSender;

// PadExtend 传输协议 v1 测试发送端（阶段二原型）
// 作用：监听 TCP 端口，向接入的接收端周期性发送测试文本和一张测试 JPEG，
//       用于验证 USB(ADB reverse) 通道连通性、数据完整性与序号连续性。
internal static class Program
{
    private static readonly byte[] Magic = "PEX1"u8.ToArray();
    private const uint TypeText = 1;
    private const uint TypeJpeg = 2;

    private static async Task<int> Main(string[] args)
    {
        int port = 27555;
        int intervalMs = 1000;
        foreach (var a in args)
        {
            if (a.StartsWith("--port=", StringComparison.OrdinalIgnoreCase))
                _ = int.TryParse(a["--port=".Length..], out port);
            else if (a.StartsWith("--interval=", StringComparison.OrdinalIgnoreCase))
                _ = int.TryParse(a["--interval=".Length..], out intervalMs);
        }

        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        Console.WriteLine($"[sender] listening on 127.0.0.1:{port}, interval={intervalMs}ms");
        Console.WriteLine("[sender] waiting for receiver (run: adb reverse tcp:{0} tcp:{0})", port);

        while (true)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[sender] accept error: {ex.Message}");
                continue;
            }

            Console.WriteLine($"[sender] client connected: {client.Client.RemoteEndPoint}");
            try
            {
                await ServeClientAsync(client, intervalMs);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[sender] client disconnected: {ex.Message}");
            }
            finally
            {
                client.Dispose();
            }
        }
    }

    private static async Task ServeClientAsync(TcpClient client, int intervalMs)
    {
        using var stream = client.GetStream();
        uint seq = 0;
        var jpeg = BuildTestJpeg();

        while (true)
        {
            string text = $"hello seq={seq} time={DateTime.Now:HH:mm:ss.fff}";
            await SendFrameAsync(stream, seq++, TypeText, Encoding.UTF8.GetBytes(text));

            await SendFrameAsync(stream, seq++, TypeJpeg, jpeg);

            Console.WriteLine($"[sender] sent text+jpeg, next seq={seq}");
            await Task.Delay(intervalMs);
        }
    }

    private static async Task SendFrameAsync(NetworkStream stream, uint seq, uint type, byte[] payload)
    {
        var header = new byte[16];
        Array.Copy(Magic, 0, header, 0, 4);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), seq);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8, 4), type);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12, 4), (uint)payload.Length);
        await stream.WriteAsync(header);
        if (payload.Length > 0)
            await stream.WriteAsync(payload);
        await stream.FlushAsync();
    }

    // 生成一张最小的合法 JPEG（纯色图），避免引入图像库依赖。
    // 这是真实可解码的 JPEG 字节流，接收端用 BitmapFactory 可解。
    private static byte[] BuildTestJpeg()
    {
        // 一张 2x2 像素的基线 JPEG（手工构造的最小合法文件）。
        // 内容不重要，关键是接收端能解出一张 Bitmap 验证“图像类数据”通道。
        return Convert.FromBase64String(
            "/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRof" +
            "Hh0aHBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/wAALCAACAAIBAREA/8QAFAAB" +
            "AAAAAAAAAAAAAAAAAAAAAP/EABQQAQAAAAAAAAAAAAAAAAAAAAD/2gAIAQEAAD8AfwD/2Q==");
    }
}
