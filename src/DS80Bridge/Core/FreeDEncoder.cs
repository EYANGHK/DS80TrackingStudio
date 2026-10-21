using System;

namespace DS80Bridge.Core;

public enum FreedPositionOrder
{
    /// <summary>位置字段顺序 X, Y, Z（Vinten Free-D 文档常见顺序）。</summary>
    Xyz = 0,

    /// <summary>位置字段顺序 Z, Y, X（部分实现/相机使用）。</summary>
    Zyx = 1,
}

/// <summary>
/// Free-D (BBC/Vinten) 29 字节报文编码器。
/// 位置: 24 位有符号，单位 1/64 mm（约 ±131 m 量程）。
/// 角度: 24 位有符号，单位 1/32768 度。
/// 校验: 0x40 减去前 28 字节之和，取低 8 位。
/// </summary>
public static class FreeDEncoder
{
    public const byte Header = 0xD1;
    public const int PacketSize = 29;

    public static byte[] Encode(
        double xMm, double yMm, double zMm,
        double panDeg, double tiltDeg, double rollDeg,
        byte cameraId = 0,
        FreedPositionOrder order = FreedPositionOrder.Xyz,
        int zoom = 0,
        int focus = 0)
    {
        var packet = new byte[PacketSize];
        packet[0] = Header;
        packet[1] = cameraId;

        WriteSigned24(packet, 2, panDeg * 32768.0);
        WriteSigned24(packet, 5, tiltDeg * 32768.0);
        WriteSigned24(packet, 8, rollDeg * 32768.0);

        if (order == FreedPositionOrder.Xyz)
        {
            WriteSigned24(packet, 11, xMm * 64.0);
            WriteSigned24(packet, 14, yMm * 64.0);
            WriteSigned24(packet, 17, zMm * 64.0);
        }
        else
        {
            WriteSigned24(packet, 11, zMm * 64.0);
            WriteSigned24(packet, 14, yMm * 64.0);
            WriteSigned24(packet, 17, xMm * 64.0);
        }

        WriteUnsigned24(packet, 20, zoom);
        WriteUnsigned24(packet, 23, focus);

        packet[26] = 0;
        packet[27] = 0;
        packet[28] = Checksum(packet);
        return packet;
    }

    public static byte Checksum(ReadOnlySpan<byte> packet)
    {
        int sum = 0x40;
        for (int i = 0; i < 28; i++) sum -= packet[i];
        return (byte)(((sum % 256) + 256) % 256);
    }

    public static bool Verify(ReadOnlySpan<byte> packet)
        => packet.Length >= PacketSize && packet[0] == Header && packet[28] == Checksum(packet);

    private static void WriteSigned24(byte[] buffer, int offset, double value)
    {
        const int max = 0x7FFFFF;
        const int min = -0x800000;

        long v = (long)Math.Round(value, MidpointRounding.AwayFromZero);
        if (v > max) v = max;
        if (v < min) v = min;

        int raw = (int)v;
        buffer[offset] = (byte)((raw >> 16) & 0xFF);
        buffer[offset + 1] = (byte)((raw >> 8) & 0xFF);
        buffer[offset + 2] = (byte)(raw & 0xFF);
    }

    private static void WriteUnsigned24(byte[] buffer, int offset, int value)
    {
        value = Math.Clamp(value, 0, 0xFFFFFF);
        buffer[offset] = (byte)((value >> 16) & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 2] = (byte)(value & 0xFF);
    }

    public static int ReadSigned24(ReadOnlySpan<byte> buffer, int offset)
    {
        int raw = (buffer[offset] << 16) | (buffer[offset + 1] << 8) | buffer[offset + 2];
        if ((raw & 0x800000) != 0) raw |= unchecked((int)0xFF000000);
        return raw;
    }

    /// <summary>解码为便于日志/自检的字符串。</summary>
    public static string Describe(ReadOnlySpan<byte> packet, FreedPositionOrder order = FreedPositionOrder.Xyz)
    {
        if (packet.Length < PacketSize) return "FreeD: 报文长度不足";
        int pan = ReadSigned24(packet, 2);
        int tilt = ReadSigned24(packet, 5);
        int roll = ReadSigned24(packet, 8);

        double a = ReadSigned24(packet, 11) / 64000.0;
        double b = ReadSigned24(packet, 14) / 64000.0;
        double c = ReadSigned24(packet, 17) / 64000.0;

        double x = order == FreedPositionOrder.Xyz ? a : c;
        double y = b;
        double z = order == FreedPositionOrder.Xyz ? c : a;

        return $"FreeD id={packet[1]} pan={pan / 32768.0:F4} tilt={tilt / 32768.0:F4} roll={roll / 32768.0:F4} " +
               $"pos=({x:F3},{y:F3},{z:F3})m checksum={(Verify(packet) ? "OK" : "BAD")}";
    }
}
