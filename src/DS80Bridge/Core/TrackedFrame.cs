using System;

namespace DS80Bridge.Core;

/// <summary>经过原点标定、安装偏移、滤波与轴向校正后的一帧数据（Free-D 坐标系）。</summary>
public readonly struct TrackedFrame
{
    public bool Valid { get; init; }
    public long Sequence { get; init; }

    /// <summary>位置，米。X 右 / Y 前 / Z 上。</summary>
    public double X { get; init; }
    public double Y { get; init; }
    public double Z { get; init; }

    // 姿态：四元数与 Free-D 的 Pan / Tilt / Roll（度）
    public double Qx { get; init; }
    public double Qy { get; init; }
    public double Qz { get; init; }
    public double Qw { get; init; }
    public double Pan { get; init; }
    public double Tilt { get; init; }
    public double Roll { get; init; }

    public double Confidence { get; init; }
    public double Timestamp { get; init; }

    /// <summary>距离上一次收到新 Pose 的时间（毫秒）。</summary>
    public double DataAgeMs { get; init; }

    public static TrackedFrame Empty => new() { Valid = false, Qw = 1 };
}
