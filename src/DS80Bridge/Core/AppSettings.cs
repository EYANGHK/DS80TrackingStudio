using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DS80Bridge.Core;

public sealed class AppSettings
{
    // ---- 输出 ----
    public string TargetIp { get; set; } = "127.0.0.1";
    public int FreedPort { get; set; } = 40000;
    public int JsonPort { get; set; } = 40001;
    public bool SendFreeD { get; set; } = true;
    public bool SendJson { get; set; } = true;
    public byte CameraId { get; set; } = 0;

    // ---- Free-D 编码 ----
    public FreedPositionOrder PositionOrder { get; set; } = FreedPositionOrder.Xyz;

    // ---- 坐标标定 ----
    public SourceFramePreset SourceFrame { get; set; } = SourceFramePreset.RightHandedYUpZBack;
    public bool InvertX { get; set; }
    public bool InvertY { get; set; }
    public bool InvertZ { get; set; }
    public bool InvertPan { get; set; }
    public bool InvertTilt { get; set; }
    public bool InvertRoll { get; set; }

    // ---- 安装偏移（DS80 相对摄像机光心）----
    public double MountXmm { get; set; }
    public double MountYmm { get; set; }
    public double MountZmm { get; set; }
    public double MountYawDeg { get; set; }
    public double MountPitchDeg { get; set; }
    public double MountRollDeg { get; set; }

    // ---- 跟踪处理 ----
    public bool SmoothingEnabled { get; set; }
    public double SmoothingTau { get; set; } = 0.03;
    public double MinConfidence { get; set; } = 0.1;
    public double PositionScale { get; set; } = 1.0;

    // ---- 运行 ----
    public bool AutoConnect { get; set; }
    public bool AutoSend { get; set; }

    public static string SettingsPath
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DS80TrackingStudio");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.json");
        }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                string json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
            }
        }
        catch
        {
            // 配置损坏时回退默认值，不影响启动。
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, Options));
        }
        catch
        {
            // 忽略写入失败（例如只读目录）。
        }
    }
}
