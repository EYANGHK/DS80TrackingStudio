using System.Runtime.InteropServices;
using System.Text;

namespace DS80Bridge.Core;

/// <summary>固定 C ABI，直接复制官方 C++ SDK Pose，不经过厂商 CInterface 包装层。</summary>
internal static class XvisioNative
{
    public const string DllName = "DS80NativeBridge.dll";
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int ds80_abi_version();
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int ds80_pose_size();
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int ds80_enumerate(StringBuilder serials, int capacity);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int ds80_connect(string serial, StringBuilder actualSerial, int capacity);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int ds80_read_pose(out SdkPose output, int capacity);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int ds80_test_pose(out SdkPose output, int capacity);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void ds80_disconnect();
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void ds80_last_error(StringBuilder output, int capacity);
    public static string LastError()
    {
        var text = new StringBuilder(4096);
        ds80_last_error(text, text.Capacity);
        return text.ToString();
    }
}

/// <summary>SDK 同一回调帧的原始值：位置米，欧拉角弧度，四元数 XYZW。</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct SdkPose
{
    public uint Version, Size;
    public ulong Sequence;
    public long EdgeTimestampUs;
    public double HostTimestamp, Confidence;
    public double X, Y, Z;
    public double Qx, Qy, Qz, Qw;
    public double Pitch, Yaw, Roll;
    public readonly bool IsUsable => Version == 1 && Size == 120 && Sequence > 0 &&
        double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z) &&
        double.IsFinite(Qx) && double.IsFinite(Qy) && double.IsFinite(Qz) && double.IsFinite(Qw) &&
        double.IsFinite(Pitch) && double.IsFinite(Yaw) && double.IsFinite(Roll) &&
        double.IsFinite(Confidence) && Confidence >= 0 && Confidence <= 1 &&
        Math.Abs(Qx * Qx + Qy * Qy + Qz * Qz + Qw * Qw - 1) < 0.01;
}
