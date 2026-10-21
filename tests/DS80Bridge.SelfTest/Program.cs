using System;
using System.Net;
using System.Net.Sockets;
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

    private static void Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (args.Length > 0 && args[0].Equals("--device", StringComparison.OrdinalIgnoreCase))
        {
            ProbeDevice();
            return;
        }

        TestFreeDRoundTrip();
        TestFreeDChecksum();
        TestFrameConversion();
        TestOriginReset();
        TestMountOffset();
        TestUdpLoopback();

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "全部自检通过 OK" : $"自检失败 {_failures} 项");
        Environment.ExitCode = _failures == 0 ? 0 : 1;
    }

    /// <summary>探测本机是否已安装 SDK、是否检测到 DS80（不会进入跟踪循环）。</summary>
    private static void ProbeDevice()
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

        using var source = new XvisioPoseSource();
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

        for (int i = 0; i < 200; i++)
        {
            if (source.TryRead(out var s) && s.HasPose)
            {
                Console.WriteLine($"Pose: x={s.X:F4} y={s.Y:F4} z={s.Z:F4} " +
                                  $"q=({s.Qx:F4},{s.Qy:F4},{s.Qz:F4},{s.Qw:F4}) conf={s.Confidence:F3}");
            }
            Thread.Sleep(5);
        }

        source.Disconnect();
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

    /// <summary>把 Free-D 世界坐标下的位姿换算成设备坐标（默认 X 右 / Y 上 / Z 后）。</summary>
    private static PoseSample MakeDeviceSample(double x, double y, double z, double pan, double tilt, double roll)
    {
        var qFrame = PoseMath.FromAxisAngle(1, 0, 0, 90);
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
}
