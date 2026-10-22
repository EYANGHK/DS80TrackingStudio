using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DS80Bridge.Core;

/// <summary>把跟踪帧同时以 Free-D（给 UE 内置 Live Link FreeD）和 JSON（给调试/自定义插件）通过 UDP 发出。</summary>
public sealed class UdpOutput : IDisposable
{
    private readonly object _gate = new();
    private UdpClient? _client;
    private IPEndPoint? _freedEndpoint;
    private IPEndPoint? _jsonEndpoint;
    private string _targetIp = "";
    private int _freedPort;
    private int _jsonPort;

    public long FreedPacketsSent { get; private set; }
    public long JsonPacketsSent { get; private set; }
    public string? LastError { get; private set; }

    public void Configure(string targetIp, int freedPort, int jsonPort)
    {
        lock (_gate)
        {
            if (_client != null && _targetIp == targetIp && _freedPort == freedPort && _jsonPort == jsonPort)
                return;

            _client?.Dispose();
            _client = new UdpClient(AddressFamily.InterNetwork);
            _client.EnableBroadcast = true;

            _targetIp = targetIp;
            _freedPort = freedPort;
            _jsonPort = jsonPort;

            if (!IPAddress.TryParse(targetIp, out var addr))
            {
                addr = IPAddress.Loopback;
                LastError = $"目标 IP \"{targetIp}\" 无效，已回退到 127.0.0.1";
            }

            _freedEndpoint = new IPEndPoint(addr, freedPort);
            _jsonEndpoint = new IPEndPoint(addr, jsonPort);
        }
    }

    public void Send(TrackedFrame frame, AppSettings settings)
    {
        lock (_gate)
        {
            if (_client == null) return;

            try
            {
                if (settings.SendFreeD && _freedEndpoint != null)
                {
                    byte[] packet = FreeDEncoder.Encode(
                        frame.X * 1000.0, frame.Y * 1000.0, frame.Z * 1000.0,
                        frame.Pan, frame.Tilt, frame.Roll,
                        settings.CameraId, settings.PositionOrder);
                    _client.Send(packet, packet.Length, _freedEndpoint);
                    FreedPacketsSent++;
                }

                if (settings.SendJson && _jsonEndpoint != null)
                {
                    byte[] json = JsonProtocol.Encode(frame);
                    _client.Send(json, json.Length, _jsonEndpoint);
                    JsonPacketsSent++;
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _client?.Dispose();
            _client = null;
        }
    }
}

/// <summary>JSON 行协议（UTF-8，无换行），字段稳定、便于第三方集成。</summary>
public static class JsonProtocol
{
    public const int Version = 1;

    public static byte[] Encode(TrackedFrame f)
    {
        var sb = new StringBuilder(320);
        sb.Append("{\"v\":").Append(Version);
        sb.Append(",\"seq\":").Append(f.Sequence);
        sb.Append(",\"t\":").Append(f.Timestamp.ToString("F6", CultureInfo.InvariantCulture));
        sb.Append(",\"valid\":").Append(f.Valid ? "true" : "false");
        sb.Append(",\"conf\":").Append(f.Confidence.ToString("F4", CultureInfo.InvariantCulture));
        sb.Append(",\"age\":").Append(double.IsFinite(f.DataAgeMs) ? f.DataAgeMs.ToString("F1", CultureInfo.InvariantCulture) : "null");
        sb.Append(",\"x\":").Append(f.X.ToString("F6", CultureInfo.InvariantCulture));
        sb.Append(",\"y\":").Append(f.Y.ToString("F6", CultureInfo.InvariantCulture));
        sb.Append(",\"z\":").Append(f.Z.ToString("F6", CultureInfo.InvariantCulture));
        sb.Append(",\"qw\":").Append(f.Qw.ToString("F6", CultureInfo.InvariantCulture));
        sb.Append(",\"qx\":").Append(f.Qx.ToString("F6", CultureInfo.InvariantCulture));
        sb.Append(",\"qy\":").Append(f.Qy.ToString("F6", CultureInfo.InvariantCulture));
        sb.Append(",\"qz\":").Append(f.Qz.ToString("F6", CultureInfo.InvariantCulture));
        sb.Append(",\"pan\":").Append(f.Pan.ToString("F4", CultureInfo.InvariantCulture));
        sb.Append(",\"tilt\":").Append(f.Tilt.ToString("F4", CultureInfo.InvariantCulture));
        sb.Append(",\"roll\":").Append(f.Roll.ToString("F4", CultureInfo.InvariantCulture));
        sb.Append('}');
        return Encoding.UTF8.GetBytes(sb.ToString());
    }
}
