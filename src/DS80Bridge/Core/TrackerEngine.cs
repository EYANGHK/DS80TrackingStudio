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

    public const int TargetPoseRateHz = PoseTiming.RateHz;
    // 超采样减少设备与主机时钟相位抖动导致的漏帧，重复帧不计入 Pose 频率。
    public int TargetReadRateHz => _source?.SuggestedReadRateHz ?? TargetPoseRateHz * 2;

    private readonly object _lifecycleLock = new();
    private readonly object _pipelineLock = new();
    private readonly object _frameLock = new();
    private readonly object _rateLock = new();
    private readonly TrackerPipeline _pipeline = new();
    private readonly UdpOutput _udp = new();

    private Thread? _thread;
    private volatile bool _running;
    private bool _disposed;
    private OriginCalibration? _origin;
    public OriginCalibration? CurrentOrigin { get { lock (_pipelineLock) return _origin; } }
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

    /// <summary>最近一帧 SDK 原始位姿（设备坐标系，米），用于诊断对照。</summary>
    public PoseSample LastRawSample
    {
        get { lock (_frameLock) return _lastRawSample; }
    }

    public bool Start(IPoseSource source, out string message)
    {
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running)
            {
                message = "跟踪已在运行";
                return true;
            }

            if (!ReferenceEquals(_source, source)) _source?.Dispose();
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

            lock (_frameLock)
            {
                _lastRawSample = PoseSample.Invalid;
                _latest = TrackedFrame.Empty;
            }
            lock (_rateLock)
            {
                _readCount = _poseCountTracker = 0;
                _poseRate = _readRate = _rateWindowStart = 0;
            }
            _lastSendTime = double.NegativeInfinity;
            ClearOrigin();
            _running = true;
            TimeBeginPeriod(1);
            _thread = new Thread(Run) { IsBackground = true, Name = "DS80 tracking loop", Priority = ThreadPriority.AboveNormal };
            _thread.Start();

            Log?.Invoke($"跟踪已启动，Pose 目标 {TargetPoseRateHz} Hz，读取 {TargetReadRateHz} Hz，数据源: {source.Name}");
            return true;
        }
    }

    public void Stop()
    {
        lock (_lifecycleLock)
        {
            if (!_running) return;
            _running = false;
            // 必须等待读取结束，避免 SDK 仍在读取时卸载设备。
            _thread?.Join();
            _thread = null;
            try { _source?.Disconnect(); } catch { }
            TimeEndPeriod(1);
            lock (_rateLock) _poseRate = _readRate = 0;
            lock (_frameLock)
            {
                _lastRawSample = PoseSample.Invalid;
                _latest = TrackedFrame.Empty;
            }
            Log?.Invoke("跟踪已停止");
        }
    }

    public void ClearOrigin()
    {
        lock (_pipelineLock)
        {
            _pipeline.ClearOrigin();
            _origin = null;
        }
    }

    public void LoadOrigin(string path)
    {
        var origin = OriginCalibration.Load(path);
        if (!_running || origin.Source != _source?.Name)
            throw new InvalidOperationException("请先连接与原点文件一致的数据源。");
        if (origin.CoordinateSignature != OriginCalibration.Signature(Settings))
            throw new InvalidOperationException("原点文件的坐标系、安装偏移或轴向设置与当前项目不一致，请重新定位原点。");
        lock (_pipelineLock)
        {
            _pipeline.SetOrigin(origin.Reference, origin.Target);
            _origin = origin;
        }
        Log?.Invoke("已加载原点：" + origin.Target.Name);
    }

    public void ResetOrigin()
    {
        PoseSample sample;
        bool fresh;
        lock (_frameLock)
        {
            sample = _lastRawSample;
            fresh = _latest.Valid && _latest.DataAgeMs <= 100;
        }

        if (!_running || !sample.HasPose || !fresh)
        {
            Log?.Invoke("定位原点失败：需要置信度达标且年龄不超过 100 ms 的 Pose");
            return;
        }

        try
        {
            var target = System.Text.Json.JsonSerializer.Deserialize<OriginOptions>(
                System.Text.Json.JsonSerializer.Serialize(Settings.Origin))!;
            target.Validate();
            lock (_pipelineLock)
            {
                _pipeline.SetOrigin(sample, target);
                _origin = new OriginCalibration { Source = _source!.Name, Reference = sample,
                    Target = target, CoordinateSignature = OriginCalibration.Signature(Settings) };
            }
            Log?.Invoke($"原点已定位：{target.Name}，目标 ({target.X:F3}, {target.Y:F3}, {target.Z:F3}) m / {target.Pan:F1}°");
        }
        catch (Exception ex) { Log?.Invoke("定位原点失败：" + ex.Message); }
    }

    private PoseSample _lastRawSample;

    private void Run()
    {
        var sw = Stopwatch.StartNew();
        double lastNewDataTime = double.NaN;
        long sequence = 0;
        _rateWindowStart = 0;

        using var timing = new PoseTiming(TargetReadRateHz);
        Func<bool> keepRunning = () => _running;
        while (_running)
        {
            double now = sw.Elapsed.TotalSeconds;

            bool gotNew = false;
            try
            {
                gotNew = _source!.TryRead(out PoseSample sample) && sample.HasPose;
                now = sw.Elapsed.TotalSeconds;
                if (gotNew && sample.HasPose)
                {
                    lock (_frameLock) _lastRawSample = sample;
                    lastNewDataTime = now;
                    sequence++;
                }
                else
                {
                    lock (_frameLock) sample = _lastRawSample;
                }

                double ageMs = !double.IsNaN(lastNewDataTime) ? (now - lastNewDataTime) * 1000.0 : double.PositiveInfinity;

                TrackedFrame frame;
                lock (_pipelineLock)
                {
                    if (_origin != null && _origin.CoordinateSignature != OriginCalibration.Signature(Settings))
                    {
                        _pipeline.ClearOrigin();
                        _origin = null;
                        Log?.Invoke("坐标或安装参数已改变，请重新定位原点。");
                    }
                    frame = _pipeline.Process(sample, sequence, now, ageMs);
                }
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
                gotNew = false;
                Thread.Sleep(50);
            }

            UpdateRate(now, gotNew);
            timing.WaitNext(keepRunning);
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
        lock (_lifecycleLock)
        {
            if (_disposed) return;
            Stop();
            _udp.Dispose();
            _source?.Dispose();
            _disposed = true;
        }
    }
}

