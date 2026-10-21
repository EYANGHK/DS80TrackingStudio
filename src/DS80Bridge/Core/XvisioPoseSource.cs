using System;
using System.Diagnostics;

namespace DS80Bridge.Core;

/// <summary>
/// 通过 Xvisio 官方 C 接口封装库读取 DS80 的实时 6DoF。
/// 注意：该封装库一次只能管理一台设备，因此本程序当前按单设备设计。
/// </summary>
public sealed class XvisioPoseSource : IPoseSource
{
    private readonly object _gate = new();
    private bool _connected;
    private PoseSample _last = PoseSample.Invalid;
    private long _lastEdgeTimestamp = -1;

    public string Name => "DS80";
    public bool IsConnected => _connected;
    public string Status { get; private set; } = "未连接";

    /// <summary>
    /// 位置单位换算系数。SDK 输出为米时保持 1.0；
    /// 若实测发现单位不一致（例如输出毫米），可在此修正。
    /// </summary>
    public double PositionScale { get; set; } = 1.0;

    public bool Connect(out string message)
    {
        lock (_gate)
        {
            if (_connected)
            {
                message = "已连接";
                return true;
            }

            if (!SdkEnvironment.EnsureLoaded(out string loadMsg))
            {
                Status = "SDK 未就绪";
                message = loadMsg;
                return false;
            }

            try
            {
                var sw = Stopwatch.StartNew();
                int init = XvisioNative.xv_device_init();

                if (init == 0)
                {
                    Status = "未检测到设备";
                    message = "xv_device_init 失败：未检测到 DS80。请检查 USB 连接与 libusb 驱动（Zadig）。";
                    return false;
                }

                int slam = XvisioNative.xv_start_slam();
                if (slam == 0)
                {
                    XvisioNative.xv_device_uninit();
                    Status = "SLAM 启动失败";
                    message = "设备已找到，但 xv_start_slam() 返回失败。请先运行 SDK 自带的 all_stream.exe 验证。";
                    return false;
                }

                sw.Stop();
                _connected = true;
                Status = $"已连接（初始化 {sw.ElapsedMilliseconds} ms）";
                message = $"DS80 已连接并启动 6DoF，用时 {sw.ElapsedMilliseconds} ms";
                return true;
            }
            catch (DllNotFoundException ex)
            {
                Status = "缺少运行库";
                message = "缺少 SDK 运行库：" + ex.Message;
                return false;
            }
            catch (BadImageFormatException ex)
            {
                Status = "位数不匹配";
                message = "SDK 为 64 位版本，请确保程序以 x64 运行。" + ex.Message;
                return false;
            }
            catch (Exception ex)
            {
                Status = "连接异常";
                message = "连接 DS80 时发生异常：" + ex.Message;
                return false;
            }
        }
    }

    public void Disconnect()
    {
        lock (_gate)
        {
            if (!_connected) return;
            try { XvisioNative.xv_stop_slam(); } catch { /* 设备可能已拔出 */ }
            try { XvisioNative.xv_device_uninit(); } catch { /* 忽略 */ }
            _connected = false;
            _last = PoseSample.Invalid;
            _lastEdgeTimestamp = -1;
            Status = "已断开";
        }
    }

    public bool TryRead(out PoseSample sample)
    {
        lock (_gate)
        {
            if (!_connected)
            {
                sample = _last;
                return false;
            }

            try
            {
                var pos = default(XvisioNative.Vector3F);
                var euler = default(XvisioNative.Vector3F);
                var quat = default(XvisioNative.QuaternionF);
                long edge = 0;
                double host = 0;
                double conf = 0;

                int ok = XvisioNative.xv_get_6dof(ref pos, ref euler, ref quat, ref edge, ref host, ref conf);
                if (ok == 0)
                {
                    sample = _last;
                    return false;
                }

                // 同一帧被重复读出时不重复输出
                if (edge != 0 && edge == _lastEdgeTimestamp)
                {
                    sample = _last;
                    return false;
                }
                _lastEdgeTimestamp = edge;

                double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

                // 封装库结构体顺序为 q0..q3，官方头文件注明四元数顺序 [qx, qy, qz, qw]。
                var s = PoseSample.FromPose(
                    pos.X, pos.Y, pos.Z,
                    quat.Qx, quat.Qy, quat.Qz, quat.Qw,
                    conf, now, edge);

                _last = s;
                sample = s;
                return true;
            }
            catch (Exception ex)
            {
                Status = "读取异常: " + ex.Message;
                sample = _last;
                return false;
            }
        }
    }

    public void Dispose() => Disconnect();
}

