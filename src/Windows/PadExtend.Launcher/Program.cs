using System.Diagnostics;
using System.Windows.Forms;

namespace PadExtend.Launcher;

// PadExtend 一体化启动器（托盘后台版，以管理员运行）。
// 启动后无黑框：编排在后台线程完成，推流在后台进行，仅任务栏托盘有图标。
// 托盘右键菜单可查看状态、重启推流、退出（退出会停止推流）。
internal static class Program
{
    private const int Port = 27555;
    private const int VddW = 1920, VddH = 1080, VddHz = 60;
    private const string VddHwid = "Root\\MttVDD";
    private const string AndroidPkg = "com.padextend.client";

    private static readonly string BaseDir = AppContext.BaseDirectory;
    private static string P(params string[] parts) => Path.Combine(new[] { BaseDir }.Concat(parts).ToArray());

    private static NotifyIcon _tray = null!;
    private static Process? _sender;
    private static StreamWriter? _log;
    private static volatile string _status = "启动中...";

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        TryOpenLog();

        _tray = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Visible = true,
            Text = "PadExtend：启动中..."
        };
        var menu = new ContextMenuStrip();
        var miStatus = new ToolStripMenuItem("状态：启动中...") { Enabled = false };
        var miRestart = new ToolStripMenuItem("重启推流", null, (_, _) => RestartPipeline());
        var miExit = new ToolStripMenuItem("退出", null, (_, _) => ExitApp());
        menu.Items.Add(miStatus);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(miRestart);
        menu.Items.Add(miExit);
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => _tray.ShowBalloonTip(2000, "PadExtend", _status, ToolTipIcon.Info);

        // 定时把状态同步到菜单/提示
        var timer = new System.Windows.Forms.Timer { Interval = 800 };
        timer.Tick += (_, _) =>
        {
            miStatus.Text = "状态：" + _status;
            _tray.Text = ("PadExtend：" + _status).Length > 63
                ? ("PadExtend：" + _status).Substring(0, 63)
                : "PadExtend：" + _status;
        };
        timer.Start();

        // 后台编排，避免阻塞 UI
        Task.Run(RunPipeline);

        Application.Run();
    }

    private static void RunPipeline()
    {
        try
        {
            SetStatus("检查虚拟显示驱动...");
            EnsureVddDriver();

            SetStatus("准备虚拟扩展屏...");
            string vddDevice = EnsureVirtualDisplay();

            SetStatus("连接平板...");
            string adb = P("payload", "adb", "adb.exe");
            if (!File.Exists(adb)) throw new FileNotFoundException("缺少 adb：" + adb);
            EnsureAdbDevice(adb);
            EnsureApk(adb);
            Run(adb, $"reverse tcp:{Port} tcp:{Port}");
            Run(adb, "shell input keyevent KEYCODE_WAKEUP");
            Run(adb, $"shell am start -n {AndroidPkg}/.MainActivity");

            SetStatus("启动推流...");
            StartSender(vddDevice);

            SetStatus($"推流中（虚拟屏 {vddDevice}）");
            _tray.ShowBalloonTip(3000, "PadExtend", "扩展屏已就绪，把窗口拖到最右侧那块屏即可。", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            SetStatus("失败：" + ex.Message);
            Log("[错误] " + ex.Message);
            MessageBox.Show(ex.Message, "PadExtend 启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static void StartSender(string vddDevice)
    {
        StopSender();
        string sender = P("payload", "sender", "PadExtend.Sender.exe");
        if (!File.Exists(sender)) throw new FileNotFoundException("缺少 Sender：" + sender);
        var psi = new ProcessStartInfo(sender, "--source=duplication --output=-1 --fps=30 --bitrate=8000000")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        _sender = Process.Start(psi);
        if (_sender != null)
        {
            _sender.OutputDataReceived += (_, e) => { if (e.Data != null) Log("[sender] " + e.Data); };
            _sender.ErrorDataReceived += (_, e) => { if (e.Data != null) Log("[sender-err] " + e.Data); };
            _sender.BeginOutputReadLine();
            _sender.BeginErrorReadLine();
        }
    }

    private static void StopSender()
    {
        try { if (_sender != null && !_sender.HasExited) _sender.Kill(true); } catch { }
        _sender = null;
    }

    private static void RestartPipeline()
    {
        Task.Run(() =>
        {
            try { StopSender(); RunPipeline(); }
            catch (Exception ex) { SetStatus("重启失败：" + ex.Message); }
        });
    }

    private static void ExitApp()
    {
        StopSender();
        _tray.Visible = false;
        _log?.Flush();
        Application.Exit();
    }

    // ---------- VDD 驱动 ----------
    private static void EnsureVddDriver()
    {
        if (DriverStoreHasMttVdd()) { Log("驱动已在仓库，跳过安装。"); return; }
        string inf = P("payload", "vdd", "MttVDD.inf");
        if (!File.Exists(inf)) throw new FileNotFoundException("缺少 VDD 驱动：" + inf);
        string cfgDir = @"C:\VirtualDisplayDriver";
        Directory.CreateDirectory(cfgDir);
        string srcCfg = P("payload", "vdd", "vdd_settings.xml");
        if (File.Exists(srcCfg)) File.Copy(srcCfg, Path.Combine(cfgDir, "vdd_settings.xml"), true);
        Log("安装驱动 (pnputil)...");
        int rc = Run("pnputil", $"/add-driver \"{inf}\" /install", out string outp);
        Log(outp);
        if (!DriverStoreHasMttVdd()) throw new Exception("VDD 驱动安装失败（pnputil rc=" + rc + "）。");
        Log("驱动安装成功。");
    }

    private static bool DriverStoreHasMttVdd()
    {
        Run("pnputil", "/enum-drivers", out string outp);
        return outp.Contains("MttVDD", StringComparison.OrdinalIgnoreCase);
    }

    // ---------- 虚拟屏 ----------
    private static string EnsureVirtualDisplay()
    {
        string? dev = Display.FindVirtualScreenDeviceName();
        if (dev == null)
        {
            Log("未发现虚拟屏，创建设备节点...");
            string nef = P("payload", "vdd", "nefconc.exe");
            if (!File.Exists(nef)) throw new Exception("缺少 nefconc.exe，无法自动创建虚拟屏。");
            Run(nef, $"--create-device-node --hardware-id {VddHwid} --class-name Display --class-guid 4d36e968-e325-11ce-bfc1-08002be10318 --no-duplicates", out string no);
            Log(no);
            System.Threading.Thread.Sleep(2500);
        }
        string? screen = null;
        for (int i = 0; i < 20 && screen == null; i++)
        {
            screen = Display.FindVirtualScreenDeviceName();
            if (screen == null) System.Threading.Thread.Sleep(500);
        }
        if (screen == null) throw new Exception("虚拟屏未出现在显示列表中。");
        Log("虚拟屏已就绪：" + screen);
        int r = Display.SetResolution(screen, VddW, VddH, VddHz);
        Log(r == 0 ? $"分辨率已设为 {VddW}x{VddH}@{VddHz}。" : $"分辨率设置返回码 {r}。");
        return screen;
    }

    // ---------- adb ----------
    private static void EnsureAdbDevice(string adb)
    {
        Run(adb, "start-server");
        for (int i = 0; i < 20; i++)
        {
            Run(adb, "devices", out string outp);
            var lines = outp.Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0 && !s.StartsWith("List")).ToArray();
            if (lines.Any(l => l.EndsWith("device")))
            {
                Log("平板已连接。");
                return;
            }
            if (i == 0) SetStatus("等待平板 USB 连接并授权调试...");
            System.Threading.Thread.Sleep(1500);
        }
        throw new Exception("未检测到已授权的平板。请插 USB 并在平板上允许调试。");
    }

    private static void EnsureApk(string adb)
    {
        Run(adb, $"shell pm list packages {AndroidPkg}", out string outp);
        if (outp.Contains(AndroidPkg, StringComparison.OrdinalIgnoreCase)) { Log("客户端已安装。"); return; }
        string apk = P("payload", "app", "PadExtend.Client-debug.apk");
        if (!File.Exists(apk)) throw new FileNotFoundException("缺少 APK：" + apk);
        Log("安装客户端 APK...");
        Run(adb, $"install -r \"{apk}\"", out string ins);
        Log(ins);
    }

    // ---------- 工具 ----------
    private static int Run(string file, string args) => Run(file, args, out _);
    private static int Run(string file, string args, out string output)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }

    private static void SetStatus(string s) { _status = s; Log(">> " + s); }

    private static void TryOpenLog()
    {
        try { _log = new StreamWriter(Path.Combine(BaseDir, "launcher.log"), append: false) { AutoFlush = true }; }
        catch { _log = null; }
    }
    private static void Log(string s)
    {
        try { _log?.WriteLine($"{DateTime.Now:HH:mm:ss} {s}"); } catch { }
    }
}
