using System;

namespace DS80Bridge.Core;

/// <summary>
/// 四元数与欧拉角工具。约定：
/// 世界坐标系 Y 轴向上（DS80 / FreeD 均为 Y-up 右手系），
/// 欧拉角顺序为 yaw(Y) - pitch(X) - roll(Z)，角度单位统一为度。
/// </summary>
public static class PoseMath
{
    public const double Rad2Deg = 180.0 / Math.PI;
    public const double Deg2Rad = Math.PI / 180.0;

    public static (double X, double Y, double Z, double W) Normalize(double x, double y, double z, double w)
    {
        double n = Math.Sqrt(x * x + y * y + z * z + w * w);
        if (n < 1e-9) return (0, 0, 0, 1);
        return (x / n, y / n, z / n, w / n);
    }

    /// <summary>Hamilton 乘积 a * b，表示先施加 b 再施加 a。</summary>
    public static (double X, double Y, double Z, double W) Multiply(
        (double X, double Y, double Z, double W) a,
        (double X, double Y, double Z, double W) b)
    {
        return (
            a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
            a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
            a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
            a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);
    }

    public static (double X, double Y, double Z, double W) Conjugate((double X, double Y, double Z, double W) q)
        => (-q.X, -q.Y, -q.Z, q.W);

    public static (double X, double Y, double Z, double W) FromAxisAngle(double ax, double ay, double az, double degrees)
    {
        double half = degrees * Deg2Rad * 0.5;
        double s = Math.Sin(half);
        return (ax * s, ay * s, az * s, Math.Cos(half));
    }

    public static (double X, double Y, double Z, double W) FromYawPitchRoll(double yawDeg, double pitchDeg, double rollDeg)
    {
        var qy = FromAxisAngle(0, 1, 0, yawDeg);
        var qx = FromAxisAngle(1, 0, 0, pitchDeg);
        var qz = FromAxisAngle(0, 0, 1, rollDeg);
        return Multiply(Multiply(qy, qx), qz);
    }

    /// <summary>
    /// Free-D 约定下的 Pan / Tilt / Roll（度）。
    /// 零位：镜头朝 +Y，上方向 +Z，右方向 +X；Pan 向右为正，Tilt 向上为正，Roll 从机后看顺时针为正。
    /// </summary>
    public static (double Pan, double Tilt, double Roll) ToFreedAngles(
        double qx, double qy, double qz, double qw)
    {
        var q = Normalize(qx, qy, qz, qw);

        var forward = Rotate(q, 0, 1, 0);
        var right = Rotate(q, 1, 0, 0);
        var up = Rotate(q, 0, 0, 1);

        double pan = Math.Atan2(forward.X, forward.Y) * Rad2Deg;
        double tilt = Math.Asin(Math.Clamp(forward.Z, -1.0, 1.0)) * Rad2Deg;
        double roll = Math.Atan2(-right.Z, up.Z) * Rad2Deg;
        return (pan, tilt, roll);
    }

    /// <summary>由 Free-D 的 Pan / Tilt / Roll 构造四元数：R = Rz(-pan) · Rx(tilt) · Ry(roll)。</summary>
    public static (double X, double Y, double Z, double W) FromFreedAngles(double panDeg, double tiltDeg, double rollDeg)
    {
        var qz = FromAxisAngle(0, 0, 1, -panDeg);
        var qx = FromAxisAngle(1, 0, 0, tiltDeg);
        var qy = FromAxisAngle(0, 1, 0, rollDeg);
        return Multiply(Multiply(qz, qx), qy);
    }

    public static (double Yaw, double Pitch, double Roll) ToYawPitchRoll((double X, double Y, double Z, double W) q)
    {
        var (x, y, z, w) = Normalize(q.X, q.Y, q.Z, q.W);

        double sinPitch = 2.0 * (w * x - y * z);
        sinPitch = Math.Clamp(sinPitch, -1.0, 1.0);
        double pitch = Math.Asin(sinPitch);

        double yaw = Math.Atan2(2.0 * (w * y + x * z), 1.0 - 2.0 * (y * y + z * z));
        double roll = Math.Atan2(2.0 * (w * z + x * y), 1.0 - 2.0 * (x * x + y * y));

        return (yaw * Rad2Deg, pitch * Rad2Deg, roll * Rad2Deg);
    }

    public static (double X, double Y, double Z) Rotate(
        (double X, double Y, double Z, double W) q, double vx, double vy, double vz)
    {
        // v' = q * v * q^-1
        var (x, y, z, w) = Normalize(q.X, q.Y, q.Z, q.W);

        double tx = 2.0 * (y * vz - z * vy);
        double ty = 2.0 * (z * vx - x * vz);
        double tz = 2.0 * (x * vy - y * vx);

        return (
            vx + w * tx + (y * tz - z * ty),
            vy + w * ty + (z * tx - x * tz),
            vz + w * tz + (x * ty - y * tx));
    }

    /// <summary>球面线性插值，t=0 返回 a，t=1 返回 b。</summary>
    public static (double X, double Y, double Z, double W) Slerp(
        (double X, double Y, double Z, double W) a,
        (double X, double Y, double Z, double W) b, double t)
    {
        double dot = a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;
        if (dot < 0)
        {
            b = (-b.X, -b.Y, -b.Z, -b.W);
            dot = -dot;
        }

        if (dot > 0.9995)
        {
            return Normalize(
                a.X + t * (b.X - a.X),
                a.Y + t * (b.Y - a.Y),
                a.Z + t * (b.Z - a.Z),
                a.W + t * (b.W - a.W));
        }

        double theta0 = Math.Acos(Math.Clamp(dot, -1.0, 1.0));
        double theta = theta0 * t;
        double sinTheta = Math.Sin(theta);
        double sinTheta0 = Math.Sin(theta0);

        double s0 = Math.Cos(theta) - dot * sinTheta / sinTheta0;
        double s1 = sinTheta / sinTheta0;

        return Normalize(
            s0 * a.X + s1 * b.X,
            s0 * a.Y + s1 * b.Y,
            s0 * a.Z + s1 * b.Z,
            s0 * a.W + s1 * b.W);
    }
}
