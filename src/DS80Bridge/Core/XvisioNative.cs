using System.Runtime.InteropServices;

namespace DS80Bridge.Core;

/// <summary>
/// 对 Xvisio 官方 C 接口封装 DLL (xvisio-CInterface-wrapper.dll) 的 P/Invoke 绑定。
/// 该 DLL 由 XVSDK 3.2.0 Windows 版安装包提供，位于 &lt;SDK&gt;\bin 目录。
/// </summary>
internal static class XvisioNative
{
    public const string DllName = "xvisio-CInterface-wrapper.dll";

    [StructLayout(LayoutKind.Sequential)]
    public struct Vector3F
    {
        public float X;
        public float Y;
        public float Z;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct QuaternionF
    {
        // 官方头文件注释: 四元数顺序为 [qx, qy, qz, qw]
        public float Qx;
        public float Qy;
        public float Qz;
        public float Qw;
    }

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int xv_device_init();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void xv_device_uninit();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int xv_start_slam();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int xv_stop_slam();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int xv_reset_slam();

    /// <summary>
    /// 读取当前 6DoF。返回非 0 表示成功。
    /// position 单位: 米; orientation 为欧拉角 (x=pitch, y=yaw, z=roll)，单位: 弧度。
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int xv_get_6dof(
        ref Vector3F position,
        ref Vector3F orientationEuler,
        ref QuaternionF quaternion,
        ref long edgeTimestamp,
        ref double hostTimestamp,
        ref double confidence);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int xv_get_6dof_prediction(
        ref Vector3F position,
        ref Vector3F orientationEuler,
        ref QuaternionF quaternion,
        ref long edgeTimestamp,
        ref double hostTimestamp,
        ref double confidence,
        double prediction);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int xv_get_imu(
        ref Vector3F accel,
        ref Vector3F gyro,
        ref long edgeTimestamp,
        ref double hostTimestamp);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int xv_get_sn(out IntPtr serialNumber);
}
