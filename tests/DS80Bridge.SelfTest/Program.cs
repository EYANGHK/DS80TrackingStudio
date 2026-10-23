using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Windows.Forms;
using DS80Bridge.Core;

namespace DS80Bridge.SelfTest;

/// <summary>
/// 不依赖硬件的数据链路自检：
/// 1. Free-D 报文编解码 / 校验和
/// 2. 设备坐标系 → Free-D 坐标系的姿态与位置换算
/// 3. 原点标定
/// 4. 安装偏移
/// 5. UDP 实际收发（回环）
/// </summary>
internal static class Program
{
    private static int _failures;

    [STAThread]
    private static void Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (args.Contains("--settings"))
        {
            var s = AppSettings.Load();
            Console.WriteLine("SETTINGS_PATH=" + AppSettings.SettingsPath);
            Console.WriteLine($"VERSION={s.SettingsVersion} FRAME={s.SourceFrame} SERIAL={s.DeviceSerialNumber} " +
                              $"FREED={s.FreedPort} JSON={s.JsonPort} SMOOTH={s.SmoothingEnabled}");
            return;
        }

        if (args.Length == 3 && args[0].Equals("--aximmetry", StringComparison.OrdinalIgnoreCase))
        {
            var document = AximmetryLensDocument.Load(args[1]);
            string json = document.ToUlensJson(3840, 2160, "", "");
            File.WriteAllText(args[2], json, new System.Text.UTF8Encoding(false));
            var first = document.Points[0];
            Console.WriteLine($"AXI name={document.Name} sensor={document.SensorWidthMm}x{document.SensorHeightMm:F4}mm " +
                              $"focal={first.FocalLengthMm:F4}mm fx={first.Fx:F6} fy={first.Fy:F6} " +
                              $"center=({first.PrincipalX:F7},{first.PrincipalY:F7}) " +
                              $"K=({first.K1:F6},{first.K2:F6},{first.K3:F6}) points={document.Points.Count}");
            Console.WriteLine("ULENS=" + Path.GetFullPath(args[2]));
            return;
        }

        if (args.Contains("--native"))
        {
            var fixture = XvisioPoseSource.ReadNativeFixture();
            Check("C++ / C# ABI 120 字节", fixture.IsUsable && fixture.Size == 120);
            Check("SDK 米制位置未取反/缩放", fixture.X == 1.25 && fixture.Y == -2.5 && fixture.Z == 3.75);
            Check("SDK XYZW 四元数", Near(fixture.Qz, Math.Sqrt(0.5), 1e-9) && Near(fixture.Qw, Math.Sqrt(0.5), 1e-9) && fixture.Qx == 0 && fixture.Qy == 0);
            Check("SDK 时间戳", fixture.EdgeTimestampUs == 123456 && fixture.HostTimestamp == 12.5);
            Console.WriteLine($"SDK Euler radians: {fixture.Pitch:F6}, {fixture.Yaw:F6}, {fixture.Roll:F6}");
            Environment.ExitCode = _failures == 0 ? 0 : 1;
            return;
        }
        if (args.Contains("--list-devices"))
        {
            foreach (string serial in XvisioPoseSource.EnumerateDevices()) Console.WriteLine("DEVICE_SN=" + serial);
            return;
        }

        if (args.Length == 2 && args[0] == "--ui-preview")
        {
            System.Windows.Forms.Application.EnableVisualStyles();
            Directory.CreateDirectory(args[1]);
            var settings = new AppSettings();
            using var engine = new TrackerEngine(settings);
            using var form = new CalibrationForm(engine, settings);
            form.Show();
            var tabs = form.Controls.OfType<System.Windows.Forms.TabControl>().Single();
            for (int i = 0; i < tabs.TabCount; i++)
            {
                tabs.SelectedIndex = i;
                System.Windows.Forms.Application.DoEvents();
                using var bitmap = new System.Drawing.Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, form.Width, form.Height));
                bitmap.Save(Path.Combine(args[1], $"calibration-{i}.png"));
            }
            form.Close();

            using var main = new MainForm();
            main.Show();
            main.StartSimulator();
            var warmup = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < warmup)
            {
                System.Windows.Forms.Application.DoEvents();
                Thread.Sleep(20);
            }
            System.Windows.Forms.Application.DoEvents();
            using (var bitmap = new System.Drawing.Bitmap(main.Width, main.Height))
            {
                main.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, main.Width, main.Height));
                bitmap.Save(Path.Combine(args[1], "main.png"));
            }
            if (FindTabControl(main) is { } mainTabs)
            {
                for (int i = 0; i < mainTabs.TabCount; i++)
                {
                    mainTabs.SelectedIndex = i;
                    System.Windows.Forms.Application.DoEvents();
                    using var bitmap = new System.Drawing.Bitmap(main.Width, main.Height);
                    main.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, main.Width, main.Height));
                    bitmap.Save(Path.Combine(args[1], $"main-tab-{i}.png"));
                }
            }
            main.Close();
            return;
        }

        if (args.Length > 0 && args[0].Equals("--device", StringComparison.OrdinalIgnoreCase))
        {
            ProbeDevice(args.Length > 1 ? args[1] : "");
            return;
        }

        TestFreeDRoundTrip();
        TestFreeDChecksum();
        TestFrameConversion();
        TestOriginReset();
        TestMountOffset();
        TestUdpLoopback();
        TestNoPoseJson();
        TestStaleAndRestart();
        TestOriginAndLensProjects();
        TestHighRateSmoothing();
        TestDocumentedXvisioFrame();
        TestSettingsMigration();
        TestAximmetryUlens();
        if (args.Contains("--timing")) Test500HzLoopback();

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "全部自检通过 OK" : $"自检失败 {_failures} 项");
        Environment.ExitCode = _failures == 0 ? 0 : 1;
    }

    /// <summary>探测本机是否已安装 SDK、是否检测到 DS80（不会进入跟踪循环）。</summary>
    private static void ProbeDevice(string serial = "")
    {
        Console.WriteLine("== DS80 设备探测 ==");

        string? dir = SdkEnvironment.LocateBinDirectory();
        if (dir == null)
        {
            Console.WriteLine("SDK 未找到：请安装 XVSDK 或设置 XVSDK_BIN。");
            Environment.ExitCode = 2;
            return;
        }

        Console.WriteLine($"SDK 目录: {dir}");
        if (!SdkEnvironment.EnsureLoaded(out string load))
        {
            Console.WriteLine(load);
            Environment.ExitCode = 3;
            return;
        }
        Console.WriteLine(load);

        using var source = new XvisioPoseSource(serial);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool ok = source.Connect(out string message);
        sw.Stop();
        Console.WriteLine($"连接结果: {ok}（耗时 {sw.ElapsedMilliseconds} ms）");
        Console.WriteLine(message);

        if (!ok)
        {
            Environment.ExitCode = 1;
            return;
        }

        using var engine = new TrackerEngine(new AppSettings { SendFreeD = false, SendJson = false });
        engine.Start(source, out _);
        for (int i = 0; i < 5; i++)
        {
            Thread.Sleep(1100);
            var frame = engine.Latest;
            Console.WriteLine($"目标 {TrackerEngine.TargetPoseRateHz} Hz / 读取 {engine.ReadRateHz:F1} Hz / " +
                              $"新 Pose {engine.PoseRateHz:F1} Hz / conf={frame.Confidence:F3} / age={frame.DataAgeMs:F1} ms");
            var raw = source.RawPose;
            Console.WriteLine($"SDK_RAW SN={source.SerialNumber} seq={raw.Sequence} p_m=({raw.X:F6},{raw.Y:F6},{raw.Z:F6}) " +
                $"pitch/yaw/roll_deg=({raw.Pitch * PoseMath.Rad2Deg:F3},{raw.Yaw * PoseMath.Rad2Deg:F3},{raw.Roll * PoseMath.Rad2Deg:F3}) " +
                $"q_xyzw=({raw.Qx:F6},{raw.Qy:F6},{raw.Qz:F6},{raw.Qw:F6})");
            Console.WriteLine($"FREED  preset={engine.Settings.SourceFrame} " +
                $"pan/tilt/roll_deg=({frame.Pan:F3},{frame.Tilt:F3},{frame.Roll:F3}) " +
                $"pos_m=({frame.X:F4},{frame.Y:F4},{frame.Z:F4})");
        }
        if (engine.Latest.Sequence == 0) Environment.ExitCode = 4;
        engine.Stop();
        Console.WriteLine("探测完成。");
    }

    private static void Check(string name, bool condition, string detail = "")
    {
        if (condition)
        {
            Console.WriteLine($"  PASS  {name} {detail}");
        }
        else
        {
            _failures++;
            Console.WriteLine($"  FAIL  {name} {detail}");
        }
    }

    private static bool Near(double a, double b, double tol) => Math.Abs(a - b) <= tol;

    private static TabControl? FindTabControl(Control parent)
    {
        foreach (Control control in parent.Controls)
        {
            if (control is TabControl tabs) return tabs;
            if (FindTabControl(control) is { } nested) return nested;
        }
        return null;
    }

    /// <summary>系统临时目录在受限环境下可能不可写，退化到程序目录下的临时文件夹。</summary>
    private static string NewTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "ds80-selftest-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(path);
            return path;
        }
        catch (UnauthorizedAccessException)
        {
            path = Path.Combine(AppContext.BaseDirectory, "self-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }
    }

    /// <summary>独立实现的 Free-D 解码，避免“自己编自己解”掩盖错误。</summary>
    private static (double Pan, double Tilt, double Roll, double X, double Y, double Z, bool ChecksumOk)
        Decode(byte[] p)
    {
        int S24(int off)
        {
            int v = (p[off] << 16) | (p[off + 1] << 8) | p[off + 2];
            if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
            return v;
        }

        int sum = 0x40;
        for (int i = 0; i < 28; i++) sum -= p[i];
        bool ok = (byte)(((sum % 256) + 256) % 256) == p[28];

        return (S24(2) / 32768.0, S24(5) / 32768.0, S24(8) / 32768.0,
                S24(11) / 64.0 / 1000.0, S24(14) / 64.0 / 1000.0, S24(17) / 64.0 / 1000.0, ok);
    }

    private static void TestFreeDRoundTrip()
    {
        Console.WriteLine("[1] Free-D 编解码");
        var packet = FreeDEncoder.Encode(1234.5, -678.9, 2500.0, 12.5, -7.25, 3.125, cameraId: 3);

        Check("报文长度 29 字节", packet.Length == 29);
        Check("报文头 0xD1", packet[0] == 0xD1);
        Check("Camera ID", packet[1] == 3);

        var d = Decode(packet);
        Check("校验和", d.ChecksumOk);
        Check("Pan", Near(d.Pan, 12.5, 1e-3), $"({d.Pan:F4})");
        Check("Tilt", Near(d.Tilt, -7.25, 1e-3), $"({d.Tilt:F4})");
        Check("Roll", Near(d.Roll, 3.125, 1e-3), $"({d.Roll:F4})");
        Check("X", Near(d.X, 1.2345, 1e-5), $"({d.X:F5} m)");
        Check("Y", Near(d.Y, -0.6789, 1e-5), $"({d.Y:F5} m)");
        Check("Z", Near(d.Z, 2.5, 1e-5), $"({d.Z:F5} m)");
    }

    private static void TestFreeDChecksum()
    {
        Console.WriteLine("[2] Free-D 校验和");
        var packet = FreeDEncoder.Encode(0, 0, 0, 0, 0, 0);
        Check("静止报文通过校验", FreeDEncoder.Verify(packet));

        packet[10] ^= 0x01;
        Check("篡改后校验失败", !FreeDEncoder.Verify(packet));
    }

    /// <summary>把 Free-D 世界坐标下的位姿换算成 XVSDK 设备坐标（X 右 / Y 下 / Z 前）。</summary>
    private static PoseSample MakeDeviceSample(double x, double y, double z, double pan, double tilt, double roll)
    {
        var qFrame = TrackerPipeline.RotationToFreed(SourceFramePreset.XvisioXRightYDownZForward);
        var qFreed = PoseMath.FromFreedAngles(pan, tilt, roll);
        var qDevice = PoseMath.Multiply(PoseMath.Conjugate(qFrame), PoseMath.Multiply(qFreed, qFrame));
        var pDevice = PoseMath.Rotate(PoseMath.Conjugate(qFrame), x, y, z);
        return PoseSample.FromPose(pDevice.X, pDevice.Y, pDevice.Z,
            qDevice.X, qDevice.Y, qDevice.Z, qDevice.W, 0.95, 1.0);
    }

    private static void TestFrameConversion()
    {
        Console.WriteLine("[3] 设备坐标 -> Free-D 坐标");
        var pipeline = new TrackerPipeline { Settings = new AppSettings() };
        var sample = MakeDeviceSample(0.5, 1.2, 1.6, 30, -10, 5);
        var frame = pipeline.Process(sample, 1, 1.0, 0.0);

        Check("位置 X", Near(frame.X, 0.5, 1e-4), $"({frame.X:F4})");
        Check("位置 Y", Near(frame.Y, 1.2, 1e-4), $"({frame.Y:F4})");
        Check("位置 Z", Near(frame.Z, 1.6, 1e-4), $"({frame.Z:F4})");
        Check("Pan", Near(frame.Pan, 30, 1e-3), $"({frame.Pan:F4})");
        Check("Tilt", Near(frame.Tilt, -10, 1e-3), $"({frame.Tilt:F4})");
        Check("Roll", Near(frame.Roll, 5, 1e-3), $"({frame.Roll:F4})");
    }

    private static void TestOriginReset()
    {
        Console.WriteLine("[4] 原点标定");
        var pipeline = new TrackerPipeline { Settings = new AppSettings() };
        var start = MakeDeviceSample(2.0, 3.0, 1.5, 45, 0, 0);

        pipeline.SetOrigin(start);
        var zero = pipeline.Process(start, 1, 1.0, 0.0);

        Check("原点位置归零", Near(zero.X, 0, 1e-4) && Near(zero.Y, 0, 1e-4) && Near(zero.Z, 0, 1e-4),
            $"({zero.X:F4},{zero.Y:F4},{zero.Z:F4})");
        Check("原点航向归零", Near(zero.Pan, 0, 1e-3), $"({zero.Pan:F4})");

        // 从原点沿“镜头右方”移动 1 米。Pan=45° 时右方向为 (cos45, -sin45)。
        double rad = 45 * Math.PI / 180.0;
        var moved = MakeDeviceSample(2.0 + Math.Cos(rad), 3.0 - Math.Sin(rad), 1.5, 45, 0, 0);
        var f2 = pipeline.Process(moved, 2, 1.1, 0.0);
        Check("航向归零后向右移动 -> 局部 +X", Near(f2.X, 1.0, 1e-3), $"({f2.X:F4})");
    }

    private static void TestMountOffset()
    {
        Console.WriteLine("[5] 安装偏移");
        var settings = new AppSettings { MountYmm = 200 }; // 摄像机光心在 DS80 前方 20cm
        var pipeline = new TrackerPipeline { Settings = settings };
        var sample = MakeDeviceSample(0, 0, 0, 0, 0, 0);
        var frame = pipeline.Process(sample, 1, 1.0, 0.0);

        Check("零姿态下偏移作用于 +Y（前）", Near(frame.Y, 0.2, 1e-4), $"({frame.Y:F4})");

        // 转 90° 后，偏移应指向世界 +X
        var turned = MakeDeviceSample(0, 0, 0, 90, 0, 0);
        var f2 = pipeline.Process(turned, 2, 1.1, 0.0);
        Check("Pan 90 度后偏移指向 +X", Near(f2.X, 0.2, 1e-4), $"({f2.X:F4})");
    }

    private static void TestUdpLoopback()
    {
        Console.WriteLine("[6] UDP 回环");
        int freedPort = 47100, jsonPort = 47101;

        using var freedRx = new UdpClient(new IPEndPoint(IPAddress.Loopback, freedPort));
        using var jsonRx = new UdpClient(new IPEndPoint(IPAddress.Loopback, jsonPort));
        freedRx.Client.ReceiveTimeout = 2000;
        jsonRx.Client.ReceiveTimeout = 2000;

        using var output = new UdpOutput();
        var settings = new AppSettings
        {
            TargetIp = "127.0.0.1",
            FreedPort = freedPort,
            JsonPort = jsonPort,
            SendFreeD = true,
            SendJson = true,
        };
        output.Configure(settings.TargetIp, settings.FreedPort, settings.JsonPort);

        var frame = new TrackedFrame
        {
            Valid = true, Sequence = 42, X = 0.5, Y = 1.25, Z = 1.6,
            Pan = 12.0, Tilt = -3.0, Roll = 1.5, Confidence = 0.9,
            Qw = 1, Timestamp = 123.456, DataAgeMs = 5,
        };
        output.Send(frame, settings);

        IPEndPoint remote = new(IPAddress.Any, 0);
        var freed = freedRx.Receive(ref remote);
        var json = jsonRx.Receive(ref remote);

        var d = Decode(freed);
        Check("收到 Free-D", freed.Length == 29 && d.ChecksumOk);
        Check("Free-D 位置", Near(d.Y, 1.25, 1e-4), $"({d.Y:F4})");
        Check("Free-D Pan", Near(d.Pan, 12.0, 1e-3), $"({d.Pan:F4})");

        string text = System.Text.Encoding.UTF8.GetString(json);
        Check("收到 JSON", text.Contains("\"seq\":42") && text.Contains("\"pan\":12.0"), text);
    }

    private static void TestNoPoseJson()
    {
        Console.WriteLine("[7] 无 Pose 的 JSON 仍可解析");
        var frame = new TrackerPipeline().Process(PoseSample.Invalid, 0, 0, double.PositiveInfinity);
        using var json = System.Text.Json.JsonDocument.Parse(JsonProtocol.Encode(frame));
        Check("无数据年龄为 null", json.RootElement.GetProperty("age").ValueKind == System.Text.Json.JsonValueKind.Null);
        Check("无数据时 valid=false", !json.RootElement.GetProperty("valid").GetBoolean());
    }

    private sealed class OnePoseSource : IPoseSource
    {
        public bool Emit { get; set; } = true;
        public string Name => "single frame";
        public string Status => "test";
        public bool IsConnected { get; private set; }
        public bool Connect(out string message) { IsConnected = true; message = "test"; return true; }
        public bool TryRead(out PoseSample sample)
        {
            sample = Emit ? MakeDeviceSample(1, 2, 3, 0, 0, 0) : PoseSample.Invalid;
            bool result = Emit;
            Emit = false;
            return result;
        }
        public void Disconnect() => IsConnected = false;
        public void Dispose() => Disconnect();
    }

    private static void TestStaleAndRestart()
    {
        Console.WriteLine("[8] 数据中断与重连");
        using var engine = new TrackerEngine(new AppSettings { SendFreeD = false, SendJson = false });
        using var source = new OnePoseSource();
        engine.Start(source, out _);
        Check("首次有效 Pose", SpinWait.SpinUntil(() => engine.Latest.Valid, 2000));
        Thread.Sleep(1200);
        Check("保持帧不增加序号", engine.Latest.Sequence == 1);
        Check("数据过期后失效", !engine.Latest.Valid && engine.Latest.DataAgeMs >= 1000);
        Check("新帧频率不被读取频率抬高", engine.PoseRateHz < 2);
        engine.Stop();
        Check("停止清空显示和频率", !engine.Latest.Valid && engine.ReadRateHz == 0 && engine.PoseRateHz == 0);
        engine.Start(source, out _);
        Thread.Sleep(150);
        Check("重连不沿用旧帧", !engine.Latest.Valid && engine.Latest.Sequence == 0);
        engine.Stop();
    }

    private static void Test500HzLoopback()
    {
        Console.WriteLine("[9] 500 Hz 模拟器 / Free-D / JSON 五秒实测");
        using var freedRx = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var jsonRx = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        freedRx.Client.ReceiveBufferSize = jsonRx.Client.ReceiveBufferSize = 1 << 20;
        using var cancel = new CancellationTokenSource();
        long freedCount = 0, jsonCount = 0, bad = 0, lastSequence = 0;
        var freedTask = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var packet = await freedRx.ReceiveAsync(cancel.Token);
                    if (!FreeDEncoder.Verify(packet.Buffer)) Interlocked.Increment(ref bad);
                    Interlocked.Increment(ref freedCount);
                }
            }
            catch (OperationCanceledException) { }
        });
        var jsonTask = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var packet = await jsonRx.ReceiveAsync(cancel.Token);
                    using var json = System.Text.Json.JsonDocument.Parse(packet.Buffer);
                    long sequence = json.RootElement.GetProperty("seq").GetInt64();
                    if (sequence <= lastSequence || !json.RootElement.GetProperty("valid").GetBoolean())
                        Interlocked.Increment(ref bad);
                    lastSequence = sequence;
                    Interlocked.Increment(ref jsonCount);
                }
            }
            catch (OperationCanceledException) { }
        });
        using var engine = new TrackerEngine(new AppSettings
        {
            FreedPort = ((IPEndPoint)freedRx.Client.LocalEndPoint!).Port,
            JsonPort = ((IPEndPoint)jsonRx.Client.LocalEndPoint!).Port,
        });
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        engine.Start(new SimulatorPoseSource(), out _);
        Thread.Sleep(5000);
        double duration = elapsed.Elapsed.TotalSeconds;
        double poseRate = engine.PoseRateHz, readRate = engine.ReadRateHz;
        engine.Stop();
        Thread.Sleep(100); // 让接收器排空队列
        cancel.Cancel();
        Task.WaitAll(freedTask, jsonTask);
        double freedRate = freedCount / duration, jsonRate = jsonCount / duration;
        Check("两倍频读取接近 1000 Hz", readRate >= 900 && readRate <= 1050, $"{readRate:F1} Hz");
        Check("独立 Pose 接近 500 Hz", poseRate >= 450 && poseRate <= 525, $"{poseRate:F1} Hz");
        Check("Free-D 接收接近 500 Hz", freedRate >= 450 && freedRate <= 525, $"{freedRate:F1} Hz");
        Check("JSON 接收接近 500 Hz", jsonRate >= 450 && jsonRate <= 525, $"{jsonRate:F1} Hz");
        Check("报文有效且序号严格递增", bad == 0, $"errors={bad}");
        Check("回环无丢包", freedCount == engine.FreedPackets && jsonCount == engine.JsonPackets,
            $"Free-D {freedCount}/{engine.FreedPackets}, JSON {jsonCount}/{engine.JsonPackets}");
    }

    private static void TestOriginAndLensProjects()
    {
        Console.WriteLine("[10] 原点定位、持久化及镜头项目");
        var settings = new AppSettings { MountYmm = 200, MountYawDeg = 15 };
        var pipeline = new TrackerPipeline { Settings = settings };
        var sample = MakeDeviceSample(2, 3, 1.5, 35, 10, 7);
        var target = new OriginOptions { Name = "舞台 A", X = 4, Y = 5, Z = 6, Pan = 20, FullOrientation = true };
        pipeline.SetOrigin(sample, target);
        var frame = pipeline.Process(sample, 1, 0, 0);
        Check("光心原点落在指定坐标", Near(frame.X, 4, 1e-6) && Near(frame.Y, 5, 1e-6) && Near(frame.Z, 6, 1e-6));
        Check("完整姿态定位", Near(frame.Pan, 20, 1e-6) && Near(frame.Tilt, 0, 1e-6) && Near(frame.Roll, 0, 1e-6));
        pipeline.ClearOrigin();
        settings.NodalOffsetInUe = true;
        var uncorrected = pipeline.Process(sample, 2, 0.1, 0);
        Check("UE 补偿模式不重复安装偏移", Near(uncorrected.X, 2, 1e-6) && Near(uncorrected.Y, 3, 1e-6) && Near(uncorrected.Pan, 35, 1e-6));
        settings.NodalOffsetInUe = false;
        settings.MountYawDeg = 90;
        var localOffset = pipeline.Process(MakeDeviceSample(0, 0, 0, 0, 0, 0), 3, 0.2, 0);
        Check("安装向量使用摄像机局部坐标", Near(localOffset.X, 0.2, 1e-6) && Near(localOffset.Y, 0, 1e-6));

        string directory = NewTempDirectory();
        Directory.CreateDirectory(directory);
        string originPath = Path.Combine(directory, "origin.json"), lensPath = Path.Combine(directory, "lens.json"), scriptPath = Path.Combine(directory, "lens.py");
        try
        {
            var origin = new OriginCalibration { Source = "模拟器", Reference = sample, Target = target,
                CoordinateSignature = OriginCalibration.Signature(settings) };
            origin.Save(originPath);
            var restored = OriginCalibration.Load(originPath);
            pipeline.SetOrigin(restored.Reference, restored.Target);
            var restoredFrame = pipeline.Process(sample, 1, 0.3, 0);
            Check("原点文件往返", Near(restoredFrame.X, 4, 1e-6) && Near(restoredFrame.Pan, 20, 1e-6));

            settings.SendFreeD = settings.SendJson = false;
            using var engine = new TrackerEngine(settings);
            engine.Start(new SimulatorPoseSource(), out _);
            engine.LoadOrigin(originPath);
            Check("可加载相同源/坐标设置的原点", engine.CurrentOrigin?.Target.Name == "舞台 A");
            settings.MountXmm = 10;
            Check("安装参数变化自动作废原点", SpinWait.SpinUntil(() => engine.CurrentOrigin == null, 2000));
            bool incompatible = false;
            try { engine.LoadOrigin(originPath); } catch (InvalidOperationException) { incompatible = true; }
            Check("拒绝不匹配坐标设置的原点", incompatible);
            engine.Stop();

            var lens = new LensProject { ProjectName = "项目 A \"quoted\"", CameraModel = "custom", LensModel = "custom zoom",
                SensorWidthMm = 35.7, SensorHeightMm = 18.8, FocalLengthMm = 24, FilmbackConfirmed = true };
            lens.Save(lensPath);
            var loaded = LensProject.Load(lensPath);
            loaded.FocalLengthMm = 70;
            Check("项目配置独立可改", lens.FocalLengthMm == 24 && loaded.FocalLengthMm == 70);
            lens.ExportUnrealScript(scriptPath);
            string script = File.ReadAllText(scriptPath);
            Check("导出独立 UE 脚本", !script.Contains("__PROFILE_JSON__") && script.Contains("UNMEASURED") && script.Contains("json.loads("));
            lens.SensorHeightMm = 0;
            bool incomplete = false;
            try { lens.ExportUnrealScript(scriptPath); } catch (InvalidDataException) { incomplete = true; }
            Check("未填写有效画幅不能导出", incomplete);
            lens.SensorHeightMm = 18.8;
            lens.AssetPath = "/Game/../Bad";
            bool invalidPath = false;
            try { lens.Validate(); } catch (InvalidDataException) { invalidPath = true; }
            Check("拒绝无效 UE 资源路径", invalidPath);
        }
        finally
        {
            foreach (var path in new[] { originPath, lensPath, scriptPath }) if (File.Exists(path)) File.Delete(path);
            Directory.Delete(directory);
        }
    }

    private static void TestHighRateSmoothing()
    {
        Console.WriteLine("[11] 超采样时滤波时间常数保持一致");
        double Run(int rate)
        {
            var pipeline = new TrackerPipeline { Settings = new AppSettings { SmoothingEnabled = true, SmoothingTau = 0.03 } };
            pipeline.Process(MakeDeviceSample(0, 0, 0, 0, 0, 0), 0, 0, 0);
            TrackedFrame result = default;
            for (int i = 1; i <= rate / 10; i++)
                result = pipeline.Process(MakeDeviceSample(1, 0, 0, 0, 0, 0), i, i / (double)rate, 0);
            return result.X;
        }
        Check("500 / 2000 Hz 下滤波一致", Near(Run(500), Run(2000), 1e-8));
    }

    /// <summary>
    /// 直接用 xvsdk 文档定义的轴构造 SDK 四元数（不经过 TrackerPipeline 的换算常量）：
    /// 设备 X 右 / Y 下 / Z 前，世界 Y 下 / Z 前；Free-D X 右 / Y 前 / Z 上。
    /// 向右摇 = 绕设备 Y 正向；抬头 = 绕设备 X 正向；向右倾斜 = 绕设备 Z 正向。
    /// </summary>
    private static void TestDocumentedXvisioFrame()
    {
        Console.WriteLine("[12] xvsdk 文档坐标系 -> Free-D 方向");
        TrackedFrame Run(PoseSample sample)
        {
            var pipeline = new TrackerPipeline { Settings = new AppSettings() };
            return pipeline.Process(sample, 1, 0, 0);
        }

        var home = Run(PoseSample.FromPose(0, 0, 0, 0, 0, 0, 1, 1, 0));
        Check("启动姿态为零", Near(home.Pan, 0, 1e-9) && Near(home.Tilt, 0, 1e-9) && Near(home.Roll, 0, 1e-9));

        var qPan = PoseMath.FromAxisAngle(0, 1, 0, 30);
        var pan = Run(PoseSample.FromPose(0, 0, 0, qPan.X, qPan.Y, qPan.Z, qPan.W, 1, 0));
        Check("向右摇 30° -> Pan +30", Near(pan.Pan, 30, 1e-6), $"({pan.Pan:F4})");
        Check("纯摇摄不产生 Tilt/Roll", Near(pan.Tilt, 0, 1e-6) && Near(pan.Roll, 0, 1e-6));

        var qTilt = PoseMath.FromAxisAngle(1, 0, 0, 20);
        var tilt = Run(PoseSample.FromPose(0, 0, 0, qTilt.X, qTilt.Y, qTilt.Z, qTilt.W, 1, 0));
        Check("抬头 20° -> Tilt +20", Near(tilt.Tilt, 20, 1e-6), $"({tilt.Tilt:F4})");

        var qRoll = PoseMath.FromAxisAngle(0, 0, 1, 10);
        var roll = Run(PoseSample.FromPose(0, 0, 0, qRoll.X, qRoll.Y, qRoll.Z, qRoll.W, 1, 0));
        Check("向右倾斜 10° -> Roll +10", Near(roll.Roll, 10, 1e-6), $"({roll.Roll:F4})");

        var qAll = PoseMath.Multiply(PoseMath.Multiply(qPan, qTilt), qRoll);
        var all = Run(PoseSample.FromPose(0, 0, 0, qAll.X, qAll.Y, qAll.Z, qAll.W, 1, 0));
        Check("组合姿态", Near(all.Pan, 30, 1e-6) && Near(all.Tilt, 20, 1e-6) && Near(all.Roll, 10, 1e-6),
            $"({all.Pan:F2},{all.Tilt:F2},{all.Roll:F2})");

        var moved = Run(PoseSample.FromPose(1, 2, 3, 0, 0, 0, 1, 1, 0));
        Check("设备位置 (1,2,3) -> Free-D (1,3,-2)",
            Near(moved.X, 1, 1e-9) && Near(moved.Y, 3, 1e-9) && Near(moved.Z, -2, 1e-9),
            $"({moved.X:F2},{moved.Y:F2},{moved.Z:F2})");
    }

    private static void TestSettingsMigration()
    {
        Console.WriteLine("[13] 旧配置迁移");
        string directory = NewTempDirectory();
        string path = Path.Combine(directory, "settings.json");
        try
        {
            File.WriteAllText(path,
                "{\"SourceFrame\":\"RightHandedYUpZBack\",\"PositionScale\":0.001,\"FreedPort\":41000}");
            var settings = AppSettings.LoadFrom(path);
            Check("旧默认坐标系迁移到 XVSDK", settings.SourceFrame == SourceFramePreset.XvisioXRightYDownZForward,
                settings.SourceFrame.ToString());
            Check("迁移后版本 2", settings.SettingsVersion == AppSettings.CurrentVersion);
            Check("保留原有输出设置", settings.FreedPort == 41000);
            Check("迁移结果写回文件", File.ReadAllText(path).Contains("\"SettingsVersion\": 2"));

            File.WriteAllText(path, "{\"SourceFrame\":\"RosXForwardYLeftZUp\"}");
            Check("用户手选的 ROS 不被覆盖",
                AppSettings.LoadFrom(path).SourceFrame == SourceFramePreset.RosXForwardYLeftZUp);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static void TestAximmetryUlens()
    {
        Console.WriteLine("[14] Aximmetry XML -> UE .ulens");
        string fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "2026.10.15-24mm.xml");
        Check("测试 XML 存在", File.Exists(fixture), fixture);
        if (!File.Exists(fixture)) return;

        var document = AximmetryLensDocument.Load(fixture);
        Check("镜头名称", document.Name == "2026.10.15-24mm", document.Name);
        Check("传感器宽度", Near(document.SensorWidthMm, 35.7, 1e-9), $"{document.SensorWidthMm}");
        Check("传感器高度", Near(document.SensorHeightMm, 20.021695, 1e-5), $"{document.SensorHeightMm:F6}");

        var point = document.Points[0];
        Check("焦距", Near(point.FocalLengthMm, 24.4795475, 1e-7), $"{point.FocalLengthMm:F7}");
        Check("Fx = 0.5/tan(HFOV/2)", Near(point.Fx, 0.68570156, 1e-7), $"{point.Fx:F8}");
        Check("Fy = Fx × Aspect", Near(point.Fy, 1.22265101, 1e-7), $"{point.Fy:F8}");
        Check("PrincipalPoint", Near(point.PrincipalX, 0.5008149858, 1e-9) &&
            Near(point.PrincipalY, 0.494867021, 1e-9), $"({point.PrincipalX:F7},{point.PrincipalY:F7})");
        Check("K1/K2/K3", Near(point.K1, -0.0323239, 1e-9) && Near(point.K2, 0.01389615, 1e-9) && point.K3 == 0);

        string json = document.ToUlensJson(3840, 2160, "", "");
        using var parsed = System.Text.Json.JsonDocument.Parse(json);
        var root = parsed.RootElement;
        Check("Metadata type/version",
            root.GetProperty("metadata").GetProperty("type").GetString() == "LensFile" &&
            root.GetProperty("metadata").GetProperty("version").GetString() == "0.0.0");
        Check("Distortion model",
            root.GetProperty("metadata").GetProperty("lensInfo").GetProperty("distortionModel").GetString() == "Spherical");
        Check("Sensor 单位", root.GetProperty("sensorDimensions").GetProperty("units").GetString() == "Millimeters");
        Check("图像尺寸", root.GetProperty("imageDimensions").GetProperty("width").GetString() == "3840" &&
            root.GetProperty("imageDimensions").GetProperty("height").GetString() == "2160");

        var tables = root.GetProperty("cameraParameterTables");
        Check("四张参数表", tables.GetArrayLength() == 4);
        Check("焦距表头", tables[0].GetProperty("parameterName").GetString() == "FocalLengthTable" &&
            tables[0].GetProperty("header").GetString() == "FocusEncoder, ZoomEncoder, Fx, Fy");
        Check("光心表头", tables[1].GetProperty("parameterName").GetString() == "ImageCenterTable" &&
            tables[1].GetProperty("header").GetString() == "FocusEncoder, ZoomEncoder, Cx, Cy");
        Check("畸变表头", tables[3].GetProperty("parameterName").GetString() == "DistortionTable" &&
            tables[3].GetProperty("header").GetString() == "FocusEncoder, ZoomEncoder, K1, K2, K3, P1, P2");

        string[] focalRow = tables[0].GetProperty("data").GetString()!.Split(", ");
        string[] centerRow = tables[1].GetProperty("data").GetString()!.Split(", ");
        string[] distortionRow = tables[3].GetProperty("data").GetString()!.Split(", ");
        Check("焦距表首行", focalRow.Length == 4 &&
            Near(double.Parse(focalRow[2], CultureInfo.InvariantCulture), 0.68570156, 1e-6) &&
            Near(double.Parse(focalRow[3], CultureInfo.InvariantCulture), 1.22265101, 1e-6));
        Check("光心表首行", centerRow.Length == 4 &&
            Near(double.Parse(centerRow[2], CultureInfo.InvariantCulture), 0.5008149858, 1e-6) &&
            Near(double.Parse(centerRow[3], CultureInfo.InvariantCulture), 0.494867021, 1e-6));
        Check("畸变表首行", distortionRow.Length == 7 &&
            Near(double.Parse(distortionRow[2], CultureInfo.InvariantCulture), -0.0323239, 1e-6) &&
            Near(double.Parse(distortionRow[3], CultureInfo.InvariantCulture), 0.01389615, 1e-6) &&
            Near(double.Parse(distortionRow[4], CultureInfo.InvariantCulture), 0, 1e-9));

        using var reference = System.Text.Json.JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "fixtures", "vive-mars-reference.ulens")));
        // Independent, externally produced schema fixture: catches missing tables/types.
        void CompareShape(System.Text.Json.JsonElement actual, System.Text.Json.JsonElement expected, string path)
        {
            Check("Mars 字段类型 " + path, actual.ValueKind == expected.ValueKind);
            if (actual.ValueKind != expected.ValueKind) return;
            if (expected.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                Check("Mars 字段数 " + path, actual.EnumerateObject().Count() == expected.EnumerateObject().Count());
                foreach (var property in expected.EnumerateObject())
                {
                    bool exists = actual.TryGetProperty(property.Name, out var child);
                    Check("Mars 字段存在 " + path + "." + property.Name, exists);
                    if (exists) CompareShape(child, property.Value, path + "." + property.Name);
                }
            }
            else if (expected.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                Check("Mars 表数量 " + path, actual.GetArrayLength() == expected.GetArrayLength());
                for (int i = 0; i < Math.Min(actual.GetArrayLength(), expected.GetArrayLength()); i++)
                    CompareShape(actual[i], expected[i], path + "[" + i + "]");
            }
        }
        CompareShape(root, reference.RootElement, "root");
        foreach (string group in new[] { "cameraParameterTables", "encoderTables" })
        {
            var expected = reference.RootElement.GetProperty(group);
            var actual = root.GetProperty(group);
            for (int i = 0; i < expected.GetArrayLength(); i++)
                Check("Mars 表名及列顺序 " + group + i,
                    actual[i].GetProperty("parameterName").GetString() == expected[i].GetProperty("parameterName").GetString() &&
                    actual[i].GetProperty("header").GetString() == expected[i].GetProperty("header").GetString());
        }
        Check("未复制别的安装偏移", tables[2].GetProperty("data").GetString() == "");
        Check("缺少编码器标定时保留空表", root.GetProperty("encoderTables")[0].GetProperty("data").GetString() == "" &&
            root.GetProperty("encoderTables")[1].GetProperty("data").GetString() == "");
        string xml = File.ReadAllText(fixture);
        foreach (string invalid in new[] { xml.Replace("DescType=\"0\"", "DescType=\"2\""),
            xml.Replace("FOVX=\"72.19758\"", "FOVX=\"180\"") })
        {
            bool rejected = false;
            try { AximmetryLensDocument.Parse(invalid); }
            catch (InvalidDataException) { rejected = true; }
            Check("拒绝不支持的模型或无效视场角", rejected);
        }
        var oldCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Check("非英文系统小数点保持不变", document.ToUlensJson(3840, 2160, "", "") == json);
        }
        finally { CultureInfo.CurrentCulture = oldCulture; }

        var project = new LensProject();
        document.ApplyTo(project);
        Check("回填镜头项目", Near(project.SensorWidthMm, 35.7, 1e-6) &&
            Near(project.SensorHeightMm, 20.0217, 1e-4) &&
            Near(project.FocalLengthMm, 24.4795, 1e-4) && project.FilmbackConfirmed);
    }
}
