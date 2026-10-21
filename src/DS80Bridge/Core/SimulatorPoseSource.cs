using System;

namespace DS80Bridge.Core;

/// <summary>
/// 无硬件时使用的模拟跟踪源：让整条数据链路（坐标处理 / Free-D / UDP / UE）
/// 可以在没有 DS80 的情况下先跑通。
/// 内部先生成 Free-D 世界坐标下的运动，再换算回设备坐标系，
/// 因此经过 TrackerPipeline 后应得到预期的 Pan/Tilt/Roll 与位置。
/// 输出帧率固定为 120Hz，与 DS80 的典型 Pose 频率一致。
/// </summary>
public sealed class SimulatorPoseSource : IPoseSource
{
    private const double FrameInterval = 1.0 / 120.0;

    private readonly DateTime _start = DateTime.UtcNow;
    private readonly Random _random = new(1234);
    private readonly object _gate = new();
    private double _lastEmit = -1;
    private long _frameIndex;

    public string Name => "模拟器";
    public bool IsConnected { get; private set; }
    public string Status => IsConnected ? "模拟运动轨迹运行中" : "未连接";

    /// <summary>模拟数据所遵循的设备坐标系，需与设置中的“设备坐标系”一致。</summary>
    public SourceFramePreset FramePreset { get; set; } = SourceFramePreset.RightHandedYUpZBack;

    public bool Connect(out string message)
    {
        IsConnected = true;
        message = "模拟器已启动（无需硬件，用于验证数据链路）";
        return true;
    }

    public void Disconnect() => IsConnected = false;

    public bool TryRead(out PoseSample sample)
    {
        if (!IsConnected)
        {
            sample = PoseSample.Invalid;
            return false;
        }

        double elapsed = (DateTime.UtcNow - _start).TotalSeconds;
        lock (_gate)
        {
            if (_lastEmit >= 0 && elapsed - _lastEmit < FrameInterval)
            {
                sample = PoseSample.Invalid;
                return false;
            }

            _lastEmit = elapsed;
            _frameIndex++;
        }

        double t = elapsed;

        // Free-D 世界坐标：X 右 / Y 前 / Z 上
        double radius = 0.6;
        double x = radius * Math.Sin(0.35 * t) + 0.002 * (_random.NextDouble() - 0.5);
        double y = radius * Math.Cos(0.35 * t) - radius;
        double z = 1.4 + 0.08 * Math.Sin(0.7 * t);

        double pan = 25.0 * Math.Sin(0.15 * t);
        double tilt = 8.0 * Math.Sin(0.23 * t);
        double roll = 5.0 * Math.Sin(0.31 * t);

        var qFreed = PoseMath.FromFreedAngles(pan, tilt, roll);
        var qFrame = FramePreset == SourceFramePreset.RosXForwardYLeftZUp
            ? PoseMath.FromAxisAngle(0, 0, 1, 90)
            : PoseMath.FromAxisAngle(1, 0, 0, 90);

        // 反向换算：Free-D → 设备坐标
        var qDevice = PoseMath.Multiply(
            PoseMath.Conjugate(qFrame),
            PoseMath.Multiply(qFreed, qFrame));
        var pDevice = PoseMath.Rotate(PoseMath.Conjugate(qFrame), x, y, z);

        double conf = 0.96 + 0.02 * _random.NextDouble();

        sample = PoseSample.FromPose(pDevice.X, pDevice.Y, pDevice.Z,
            qDevice.X, qDevice.Y, qDevice.Z, qDevice.W, conf,
            (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds,
            Interlocked.Read(ref _frameIndex) * 1000);
        return true;
    }

    public void Dispose() => Disconnect();
}
