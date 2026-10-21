using System;

namespace DS80Bridge.Core;

/// <summary>一帧 6DoF 原始数据（设备坐标系，未做原点和安装偏移处理）。</summary>
public readonly struct PoseSample
{
    public bool HasPose { get; init; }

    /// <summary>位置，单位米，设备坐标系。</summary>
    public double X { get; init; }
    public double Y { get; init; }
    public double Z { get; init; }

    /// <summary>姿态四元数（已归一化），设备坐标系。</summary>
    public double Qx { get; init; }
    public double Qy { get; init; }
    public double Qz { get; init; }
    public double Qw { get; init; }

    /// <summary>跟踪置信度 0..1，0 表示 SLAM 丢失。</summary>
    public double Confidence { get; init; }

    /// <summary>主机侧接收时间（秒，Stopwatch 基准）。</summary>
    public double HostTimestamp { get; init; }

    public long EdgeTimestampUs { get; init; }

    public static PoseSample Invalid => new() { HasPose = false, Confidence = 0, Qw = 1 };

    public static PoseSample FromPose(
        double x, double y, double z,
        double qx, double qy, double qz, double qw,
        double confidence, double hostTimestamp, long edgeTimestampUs = 0)
    {
        var n = PoseMath.Normalize(qx, qy, qz, qw);
        return new PoseSample
        {
            HasPose = true,
            X = x,
            Y = y,
            Z = z,
            Qx = n.X,
            Qy = n.Y,
            Qz = n.Z,
            Qw = n.W,
            Confidence = confidence,
            HostTimestamp = hostTimestamp,
            EdgeTimestampUs = edgeTimestampUs,
        };
    }
}
