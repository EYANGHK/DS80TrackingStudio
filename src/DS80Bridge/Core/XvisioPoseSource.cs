using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DS80Bridge.Core;

/// <summary>按序列号选择 S80/DS80，原样接收与官方示例相同的 SLAM 回调。</summary>
public sealed class XvisioPoseSource : IPoseSource
{
    private readonly object _gate = new();
    private bool _connected;
    private PoseSample _last = PoseSample.Invalid;
    private ulong _lastSequence;
    private SdkPose _raw;
    public XvisioPoseSource(string serialNumber = "") => RequestedSerial = serialNumber;
    public string RequestedSerial { get; }
    public string SerialNumber { get; private set; } = "";
    public string Name => "Xvisio " + (SerialNumber.Length > 0 ? SerialNumber : "S80 / DS80");
    public int SuggestedReadRateHz => 2000;
    public bool IsConnected => _connected;
    public string Status { get; private set; } = "未连接";
    public SdkPose RawPose { get { lock (_gate) return _raw; } }

    public static string[] EnumerateDevices()
    {
        if (!SdkEnvironment.EnsureLoaded(out string message)) throw new InvalidOperationException(message);
        var serials = new StringBuilder(8192);
        if (XvisioNative.ds80_enumerate(serials, serials.Capacity) < 0)
            throw new InvalidOperationException(FriendlyError(XvisioNative.LastError()));
        return serials.ToString().Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>把 SDK 桥接的英文错误转成可直接展示的中文提示。</summary>
    private static string FriendlyError(string error) => error switch
    {
        var e when e.Contains("Multiple devices") =>
            "检测到多台 Xvisio 设备，请在设备下拉框中选择具体序列号。",
        var e when e.Contains("No Xvisio device") =>
            "未找到 Xvisio 设备：请检查 USB 连接和 libusb 驱动，并关闭官方 xvsdk-viewer。",
        var e when e.Contains("Selected serial is not connected") =>
            "所选序列号的设备当前未连接：" + e[(e.IndexOf(':') + 1)..].Trim(),
        var e when e.Contains("A device is already connected") =>
            "已连接一台设备，请先断开再切换。",
        _ => error,
    };

    public bool Connect(out string message)
    {
        lock (_gate)
        {
            if (_connected) { message = "已连接 " + SerialNumber; return true; }
            if (!SdkEnvironment.EnsureLoaded(out message)) { Status = "SDK 未就绪"; return false; }
            try
            {
                var sw = Stopwatch.StartNew();
                var serial = new StringBuilder(256);
                if (XvisioNative.ds80_connect(RequestedSerial, serial, serial.Capacity) == 0)
                {
                    Status = "连接失败";
                    message = "SDK: " + FriendlyError(XvisioNative.LastError());
                    return false;
                }
                SerialNumber = serial.ToString();
                _lastSequence = 0;
                _last = PoseSample.Invalid;
                _raw = default;
                _connected = true;
                Status = $"SN {SerialNumber} · 官方 SDK 原始 Pose";
                message = $"已连接 Xvisio，SN {SerialNumber}，初始化 {sw.ElapsedMilliseconds} ms；位置单位 m，SDK 原始回调。";
                return true;
            }
            catch (Exception ex) { Status = "连接异常"; message = ex.Message; return false; }
        }
    }

    public void Disconnect()
    {
        lock (_gate)
        {
            if (!_connected) return;
            XvisioNative.ds80_disconnect();
            _connected = false;
            _last = PoseSample.Invalid;
            _lastSequence = 0;
            _raw = default;
            Status = "已断开";
        }
    }

    public bool TryRead(out PoseSample sample)
    {
        lock (_gate)
        {
            sample = _last;
            if (!_connected) return false;
            int result = XvisioNative.ds80_read_pose(out SdkPose raw, Marshal.SizeOf<SdkPose>());
            if (result != 1 || raw.Sequence == _lastSequence) return false;
            if (!raw.IsUsable) { Status = "SDK 返回无效 Pose（ABI / 数值检查失败）"; return false; }
            _lastSequence = raw.Sequence;
            _raw = raw;
            double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            // 不取反、不缩放、不预测；世界坐标标定仅在 TrackerPipeline 内进行。
            _last = new PoseSample { HasPose = true, X = raw.X, Y = raw.Y, Z = raw.Z,
                Qx = raw.Qx, Qy = raw.Qy, Qz = raw.Qz, Qw = raw.Qw,
                Confidence = raw.Confidence, HostTimestamp = now, EdgeTimestampUs = raw.EdgeTimestampUs };
            sample = _last;
            return true;
        }
    }

    public static SdkPose ReadNativeFixture()
    {
        if (!SdkEnvironment.EnsureLoaded(out string message)) throw new InvalidOperationException(message);
        if (XvisioNative.ds80_test_pose(out SdkPose pose, Marshal.SizeOf<SdkPose>()) != 1)
            throw new InvalidOperationException("Native ABI self-test failed.");
        return pose;
    }
    public void Dispose() => Disconnect();
}
