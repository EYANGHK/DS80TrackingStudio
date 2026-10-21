using System;
using System.Diagnostics;
using System.Threading;
using System.Runtime.InteropServices;

namespace DS80Bridge.Core;

/// <summary>跟踪主循环：按固定频率从数据源取样、处理、并通过 UDP 输出。</summary>
public sealed class TrackerEngine : IDisposable
{
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint uPeriod);

    private readonly object _frameLock = new();
    private readonly object _rateLock = new();
    private readonly TrackerPipeline _pipeline = new();
    private readonly UdpOutput _udp = new();

    private Thread? _thread;
    private volatile bool _running;
    private IPoseSource? _source;
    private TrackedFrame _latest = TrackedFrame.Empty;

    private long _readCount;
    private double _lastSendTime;
    private double _rateWindowStart;
    private double _poseRate;
    private double _readRate;

    public TrackerEngine(AppSettings settings)
    {
        Settings = settings;
        _pipeline.Settings = settings;
    }

    public AppSettings Settings { get; }
    public TrackerPipeline Pipeline => _pipeline;
    public bool IsRunning => _running;
    public IPoseSource? Source => _source;

    public double PoseRateHz { get { lock (_rateLock) return _poseRate; } }
    public double ReadRateHz { get { lock (_rateLock) return _readRate; } }
    public long FreedPackets => _udp.FreedPacketsSent;
    public long JsonPackets => _udp.JsonPacketsSent;
    public string? OutputError => _udp.LastError;

    public event Action<string>? Log;

    public void ApplyOutputSettings()
        => _udp.Configure(Settings.TargetIp, Settings.FreedPort, Settings.JsonPort);

    public TrackedFrame Latest
    {
        get { lock (_frameLock) return _latest; }
    }

    public bool Start(IPoseSource source, out string message)
    {
        if (_running)
        {
            message = "跟踪已在运行";
            return true;
        }

        _source = source;
        _pipeline.Settings = Settings;
        _udp.Configure(Settings.TargetIp, Settings.FreedPort, Settings.JsonPort);

        if (!source.IsConnected)
        {
            if (!source.Connect(out message)) return false;
        }
        else
        {
            message = "数据源已连接";
        }

        _running = true;
        TimeBeginPeriod(1);
        _thread = new Thread(Run) { IsBackground = true, Name = "DS80 tracking loop", Priority = ThreadPriority.AboveNormal };
        _thread.Start();

        Log?.Invoke($"跟踪已启动，数据源: {source.Name}");
        return true;
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _thread?.Join(1000);
        _thread = null;

        try { _source?.Disconnect(); } catch { }

        TimeEndPeriod(1);
        Log?.Invoke("跟踪已停止");
    }

    public void ResetOrigin()
    {
        PoseSample sample;
        lock (_frameLock) sample = _lastRawSample;

        if (!sample.HasPose)
        {
            Log?.Invoke("重置原点失败：当前没有有效 Pose");
            return;
        }

        _pipeline.SetOrigin(sample);
        Log?.Invoke("原点已重置：位置归零，航向归零");
    }

    private PoseSample _lastRawSample;

    private void Run()
    {
        var sw = Stopwatch.StartNew();
        double lastNewDataTime = 0;
        long sequence = 0;
        _rateWindowStart = 0;

        while (_running)
        {
            double now = sw.Elapsed.TotalSeconds;

            bool gotNew = false;
            try
            {
                gotNew = _source!.TryRead(out PoseSample sample);
                if (gotNew && sample.HasPose)
                {
                    lock (_frameLock) _lastRawSample = sample;
                    lastNewDataTime = now;
                    sequence++;
                }
                else if (!sample.HasPose)
                {
                    lock (_frameLock) sample = _lastRawSample;
                }

                double ageMs = lastNewDataTime > 0 ? (now - lastNewDataTime) * 1000.0 : double.PositiveInfinity;

                var frame = _pipeline.Process(sample, sequence, now, ageMs);
                lock (_frameLock) _latest = frame;

                // 有新帧就发；没有新帧时以 10Hz 发送“保持帧”，避免 UE 端因超时丢失 Subject
                if (gotNew || now - _lastSendTime >= 0.1)
                {
                    if (Settings.SendFreeD || Settings.SendJson)
                    {
                        _udp.Send(frame, Settings);
                    }
                    _lastSendTime = now;
                }
            }
            catch (Exception ex)
            {
                Log?.Invoke("跟踪循环异常: " + ex.Message);
                Thread.Sleep(50);
            }

            UpdateRate(now, gotNew);
            Thread.Sleep(2);
        }
    }

    private void UpdateRate(double now, bool gotNew)
    {
        lock (_rateLock)
        {
            _readCount++;
            if (gotNew) _poseCountTracker++;

            if (now - _rateWindowStart >= 1.0)
            {
                double window = now - _rateWindowStart;
                _poseRate = _poseCountTracker / window;
                _readRate = _readCount / window;
                _poseCountTracker = 0;
                _readCount = 0;
                _rateWindowStart = now;
            }
        }
    }

    private long _poseCountTracker;

    public void Dispose()
    {
        Stop();
        _udp.Dispose();
        _source?.Dispose();
    }
}

