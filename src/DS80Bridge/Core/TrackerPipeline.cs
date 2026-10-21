using System;

namespace DS80Bridge.Core;

/// <summary>DS80 设备坐标系预设。Free-D 世界坐标为 X 右 / Y 前 / Z 上（右手系）。</summary>
public enum SourceFramePreset
{
    /// <summary>X 右 / Y 上 / Z 后（右手系，常见相机坐标系）。</summary>
    RightHandedYUpZBack = 0,

    /// <summary>X 前 / Y 左 / Z 上（ROS REP-103 机体坐标系）。</summary>
    RosXForwardYLeftZUp = 1,
}

/// <summary>
/// 把 DS80 原始 Pose 转换为“摄像机”Pose（Free-D 世界坐标：X 右 / Y 前 / Z 上）：
/// 1. 从设备坐标系换到 Free-D 坐标系
/// 2. 原点标定（位置归零 + 航向归零）
/// 3. 安装偏移（DS80 不在摄像机光心时）
/// 4. 可选低通滤波
/// 5. 由姿态解算 Free-D 的 Pan / Tilt / Roll
/// 6. 方向校正（现场标定用）
/// </summary>
public sealed class TrackerPipeline
{
    private bool _originSet;
    private (double X, double Y, double Z) _originPosition;
    private (double X, double Y, double Z, double W) _originRotationInv;

    private bool _hasFiltered;
    private (double X, double Y, double Z) _filteredPosition;
    private (double X, double Y, double Z, double W) _filteredRotation;
    private double _lastFilterTime;

    public AppSettings Settings { get; set; } = new();
    public bool OriginSet => _originSet;

    /// <summary>设备坐标系 → Free-D 坐标系的旋转。两者均为右手系，因此是纯旋转。</summary>
    private (double X, double Y, double Z, double W) FrameRotation
        => Settings.SourceFrame == SourceFramePreset.RosXForwardYLeftZUp
            ? PoseMath.FromAxisAngle(0, 0, 1, 90)   // Rz(90°)
            : PoseMath.FromAxisAngle(1, 0, 0, 90);  // Rx(90°)

    public void ClearOrigin()
    {
        _originSet = false;
        _hasFiltered = false;
    }

    /// <summary>最近一次换算到 Free-D 坐标系后的姿态，用于“设为原点”。</summary>
    public void SetOrigin(PoseSample raw) => SetOrigin(ToFreed(raw));

    private (double X, double Y, double Z, double Qx, double Qy, double Qz, double Qw) ToFreed(PoseSample s)
    {
        var qc = FrameRotation;
        var q = PoseMath.Multiply(qc, PoseMath.Multiply(
            PoseMath.Normalize(s.Qx, s.Qy, s.Qz, s.Qw), PoseMath.Conjugate(qc)));
        double scale = Settings.PositionScale;
        var p = PoseMath.Rotate(qc, s.X * scale, s.Y * scale, s.Z * scale);
        return (p.X, p.Y, p.Z, q.X, q.Y, q.Z, q.W);
    }

    private void SetOrigin((double X, double Y, double Z, double Qx, double Qy, double Qz, double Qw) freed)
    {
        var (pan, _, _) = PoseMath.ToFreedAngles(freed.Qx, freed.Qy, freed.Qz, freed.Qw);
        var yawOnly = PoseMath.FromAxisAngle(0, 0, 1, -pan); // Pan 正方向 = 绕 +Z 顺时针
        _originRotationInv = PoseMath.Conjugate(yawOnly);
        _originPosition = (freed.X, freed.Y, freed.Z);
        _originSet = true;
        _hasFiltered = false;
    }

    public TrackedFrame Process(PoseSample s, long sequence, double now, double dataAgeMs)
    {
        if (!s.HasPose)
        {
            return new TrackedFrame { Valid = false, Sequence = sequence, Qw = 1, Timestamp = now, DataAgeMs = dataAgeMs };
        }

        var (px, py, pz, qx, qy, qz, qw) = ToFreed(s);
        var q = (X: qx, Y: qy, Z: qz, W: qw);

        if (_originSet)
        {
            double dx = px - _originPosition.X;
            double dy = py - _originPosition.Y;
            double dz = pz - _originPosition.Z;

            var rotated = PoseMath.Rotate(_originRotationInv, dx, dy, dz);
            px = rotated.X;
            py = rotated.Y;
            pz = rotated.Z;

            q = PoseMath.Multiply(_originRotationInv, q);
        }

        // 安装偏移：摄像机局部坐标（X 右 / Y 前 / Z 上）下，从 DS80 指向摄像机光心。
        double mx = Settings.MountXmm / 1000.0;
        double my = Settings.MountYmm / 1000.0;
        double mz = Settings.MountZmm / 1000.0;
        if (mx != 0 || my != 0 || mz != 0)
        {
            var offsetWorld = PoseMath.Rotate(q, mx, my, mz);
            px += offsetWorld.X;
            py += offsetWorld.Y;
            pz += offsetWorld.Z;
        }

        var mountRotation = PoseMath.FromFreedAngles(
            Settings.MountYawDeg, Settings.MountPitchDeg, Settings.MountRollDeg);
        q = PoseMath.Multiply(q, mountRotation);

        if (Settings.SmoothingEnabled && Settings.SmoothingTau > 0.001)
        {
            if (_hasFiltered)
            {
                double dt = Math.Max(0.001, now - _lastFilterTime);
                double alpha = 1.0 - Math.Exp(-dt / Settings.SmoothingTau);
                px = _filteredPosition.X + alpha * (px - _filteredPosition.X);
                py = _filteredPosition.Y + alpha * (py - _filteredPosition.Y);
                pz = _filteredPosition.Z + alpha * (pz - _filteredPosition.Z);
                q = PoseMath.Slerp(_filteredRotation, q, alpha);
            }

            _filteredPosition = (px, py, pz);
            _filteredRotation = q;
            _lastFilterTime = now;
            _hasFiltered = true;
        }

        var (panAng, tiltAng, rollAng) = PoseMath.ToFreedAngles(q.X, q.Y, q.Z, q.W);

        double pan = panAng * (Settings.InvertPan ? -1 : 1);
        double tilt = tiltAng * (Settings.InvertTilt ? -1 : 1);
        double roll = rollAng * (Settings.InvertRoll ? -1 : 1);

        bool valid = s.Confidence >= Settings.MinConfidence && dataAgeMs < 1000.0;

        return new TrackedFrame
        {
            Valid = valid,
            Sequence = sequence,
            X = px * (Settings.InvertX ? -1 : 1),
            Y = py * (Settings.InvertY ? -1 : 1),
            Z = pz * (Settings.InvertZ ? -1 : 1),
            Qx = q.X,
            Qy = q.Y,
            Qz = q.Z,
            Qw = q.W,
            Pan = pan,
            Tilt = tilt,
            Roll = roll,
            Confidence = s.Confidence,
            Timestamp = now,
            DataAgeMs = dataAgeMs,
        };
    }
}

