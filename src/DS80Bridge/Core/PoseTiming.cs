using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DS80Bridge.Core;

/// <summary>单调时钟调度；超时跳过周期，不突发补发旧帧。</summary>
internal sealed class PoseTiming : IDisposable
{
    public const int RateHz = 500;
    private readonly SafeWaitHandle _timer = CreateWaitableTimerEx(IntPtr.Zero, null, 2, 0x1F0003);
    private readonly double _period;
    private double _deadline = Stopwatch.GetTimestamp();

    public PoseTiming(int rateHz = RateHz)
    {
        _period = Stopwatch.Frequency / (double)rateHz;
    }

    public void WaitNext(Func<bool> keepRunning)
    {
        _deadline += _period;
        long now = Stopwatch.GetTimestamp();
        if (now > _deadline)
            _deadline += Math.Ceiling((now - _deadline) / _period) * _period;

        while (keepRunning())
        {
            double remaining = (_deadline - Stopwatch.GetTimestamp()) / Stopwatch.Frequency;
            if (remaining <= 0) return;

            // 高分辨率内核定时器承担大部分等待，最后 0.2 ms 微调。
            if (!_timer.IsInvalid && remaining > 0.0003)
            {
                long due = -(long)((remaining - 0.0002) * 10_000_000);
                if (SetWaitableTimer(_timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
                {
                    WaitForSingleObject(_timer, 10);
                    continue;
                }
            }

            if (remaining > 0.0015) Thread.Sleep(1);
            else Thread.SpinWait(64);
        }
    }

    public void Dispose() => _timer.Dispose();

    [DllImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", CharSet = CharSet.Unicode)]
    private static extern SafeWaitHandle CreateWaitableTimerEx(IntPtr attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long dueTime, int period,
        IntPtr completionRoutine, IntPtr argument, [MarshalAs(UnmanagedType.Bool)] bool resume);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
}
