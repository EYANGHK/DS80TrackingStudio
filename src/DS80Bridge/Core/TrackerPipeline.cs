using System;

namespace DS80Bridge.Core;

/// <summary>
/// DS80 设备坐标系预设。XVSDK 文档定义：
/// 设备系 X 右 / Y 下 / Z 前（相机主方向），世界系重力对齐、启动时 Z 指向初始朝向。
/// Free-D 世界系与相机系均为 X 右 / Y 前 / Z 上。
/// </summary>
public enum SourceFramePreset
{
    /// <summary>XVSDK 官方定义（S80 / DS80）：X 右 / Y 下 / Z 前。默认。</summary>
    XvisioXRightYDownZForward = 0,

    /// <summary>ROS REP-103 机体坐标系：X 前 / Y 左 / Z 上。</summary>
    RosXForwardYLeftZUp = 1,

    /// <summary>其他设备的相机坐标系：X 右 / Y 上 / Z 后（右手）。</summary>
    RightHandedYUpZBack = 2,
}

/// <summary>
/// 把 DS80 原始 Pose 转换为“摄像机”Pose（Free-D 世界坐标：X 右 / Y 前 / Z 上）：
/// 1. 从设备坐标系换到 Free-D 坐标系（纯旋转，含世界系与相机系两次换基）
/// 2. 安装偏移（UE 负责 nodal offset 时跳过）
/// 3. 原点定位（对补偿后的相机设原点）
/// 4. 可选低通滤波
/// 5. 由姿态解算 Free-D 的 Pan / Tilt / Roll
/// 6. 方向校正（现场标定用）
/// </summary>
public sealed class TrackerPipeline
{
    private bool _originSet;
    private OriginOptions _originTarget = new();
    private (double X, double Y, double Z) _originPosition;
    private (double X, double Y, double Z, double W) _originRotationInv;

    private bool _hasFiltered;
    private (double X, double Y, double Z) _filteredPosition;
    private (double X, double Y, double Z, double W) _filteredRotation;
    private double _lastFilterTime;

    public AppSettings Settings { get; set; } = new();
    public bool OriginSet => _originSet;

    /// <summary>
    /// 设备坐标系 → Free-D 坐标系的旋转。两者都是右手系，因此是纯旋转：
    /// - XVSDK：Y 下 / Z 前 → Y 前 / Z 上 = Rx(-90°)
    /// - ROS：X 前 / Y 左 / Z 上 → X 右 / Y 前 / Z 上 = Rz(+90°)
    /// - Y 上 / Z 后 → Y 前 / Z 上 = Rx(+90°)
    /// 世界系与相机局部系使用同一约定，所以姿态按 R·q·R⁻¹ 共轭、位置按 R·p 旋转。
    /// </summary>
    public static (double X, double Y, double Z, double W) RotationToFreed(SourceFramePreset preset)
        => preset switch
        {
            SourceFramePreset.RosXForwardYLeftZUp => PoseMath.FromAxisAngle(0, 0, 1, 90),
            SourceFramePreset.RightHandedYUpZBack => PoseMath.FromAxisAngle(1, 0, 0, 90),
            _ => PoseMath.FromAxisAngle(1, 0, 0, -90),
        };

    private (double X, double Y, double Z, double W) FrameRotation => RotationToFreed(Settings.SourceFrame);

    public void ClearOrigin()
    {
        _originSet = false;
        _hasFiltered = false;
    }

    /// <summary>当前姿态，用于“设为原点”。</summary>
    public void SetOrigin(PoseSample raw) => SetOrigin(raw, new OriginOptions());

    public void SetOrigin(PoseSample raw, OriginOptions target)
    {
        target.Validate();
        var camera = ToCamera(raw);
        var rotation = (X: camera.Qx, Y: camera.Qy, Z: camera.Qz, W: camera.Qw);
        var (pan, _, _) = PoseMath.ToFreedAngles(rotation.X, rotation.Y, rotation.Z, rotation.W);
        var reference = target.FullOrientation ? rotation : PoseMath.FromFreedAngles(pan, 0, 0);
        var desired = PoseMath.FromFreedAngles(target.Pan * (Settings.InvertPan ? -1 : 1), 0, 0);
        _originRotationInv = PoseMath.Multiply(desired, PoseMath.Conjugate(reference));
        _originPosition = (camera.X, camera.Y, camera.Z);
        _originTarget = new OriginOptions { Name = target.Name, FullOrientation = target.FullOrientation,
            X = target.X, Y = target.Y, Z = target.Z, Pan = target.Pan };
        _originSet = true;
        _hasFiltered = false;
    }

    /// <summary>SDK 原始位姿（米）→ Free-D 位姿（米）。不做原点、安装偏移与方向校正。</summary>
    public (double X, double Y, double Z, double Qx, double Qy, double Qz, double Qw) ToFreed(PoseSample s)
    {
        var qc = FrameRotation;
        var q = PoseMath.Multiply(qc, PoseMath.Multiply(
            PoseMath.Normalize(s.Qx, s.Qy, s.Qz, s.Qw), PoseMath.Conjugate(qc)));
        var p = PoseMath.Rotate(qc, s.X, s.Y, s.Z);
        return (p.X, p.Y, p.Z, q.X, q.Y, q.Z, q.W);
    }

    private (double X, double Y, double Z, double Qx, double Qy, double Qz, double Qw) ToCamera(PoseSample s)
    {
        var p = ToFreed(s);
        if (Settings.NodalOffsetInUe) return p;
        var q = (X: p.Qx, Y: p.Qy, Z: p.Qz, W: p.Qw);
        q = PoseMath.Multiply(q, PoseMath.FromFreedAngles(Settings.MountYawDeg, Settings.MountPitchDeg, Settings.MountRollDeg));
        var offset = PoseMath.Rotate(q, Settings.MountXmm / 1000, Settings.MountYmm / 1000, Settings.MountZmm / 1000);
        return (p.X + offset.X, p.Y + offset.Y, p.Z + offset.Z, q.X, q.Y, q.Z, q.W);
    }

    public TrackedFrame Process(PoseSample s, long sequence, double now, double dataAgeMs)
    {
        if (!s.HasPose)
        {
            return new TrackedFrame { Valid = false, Sequence = sequence, Qw = 1, Timestamp = now, DataAgeMs = dataAgeMs };
        }

        var (px, py, pz, qx, qy, qz, qw) = ToCamera(s);
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

        if (Settings.SmoothingEnabled && Settings.SmoothingTau > 0)
        {
            if (_hasFiltered)
            {
                double dt = Math.Max(0, now - _lastFilterTime);
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
        else
        {
            _hasFiltered = false;
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
            X = px * (Settings.InvertX ? -1 : 1) + (_originSet ? _originTarget.X : 0),
            Y = py * (Settings.InvertY ? -1 : 1) + (_originSet ? _originTarget.Y : 0),
            Z = pz * (Settings.InvertZ ? -1 : 1) + (_originSet ? _originTarget.Z : 0),
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
