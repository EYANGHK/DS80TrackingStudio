using System;

namespace DS80Bridge.Core;

public interface IPoseSource : IDisposable
{
    string Name { get; }
    bool IsConnected { get; }
    string Status { get; }
    /// <summary>主机轮询频率，可高于 Pose 更新频率以减少时钟相位造成的漏读。</summary>
    int SuggestedReadRateHz => 1000;

    /// <summary>建立连接。返回 false 时 message 给出原因。</summary>
    bool Connect(out string message);

    void Disconnect();

    /// <summary>读取最新一帧。无新数据时返回 false，但 out 中仍可能是上一帧。</summary>
    bool TryRead(out PoseSample sample);
}
