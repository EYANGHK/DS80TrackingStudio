using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DS80Bridge.Core;

public sealed class AppSettings
{
    /// <summary>配置结构版本：2 起使用 XVSDK 原生米制 Pose 与文档坐标系。</summary>
    public const int CurrentVersion = 2;

    /// <summary>配置结构版本：2 起使用 XVSDK 原生米制 Pose 与文档坐标系；0 表示旧文件。</summary>
    public int SettingsVersion { get; set; }

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
    public SourceFramePreset SourceFrame { get; set; } = SourceFramePreset.XvisioXRightYDownZForward;
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
    public OriginOptions Origin { get; set; } = new();
    public LensProject Lens { get; set; } = new();
    // UE Lens File 应用 nodal offset 时，Bridge 跳过安装平移/旋转，避免重复补偿。
    public bool NodalOffsetInUe { get; set; }

    // ---- 运行 ----
    public bool AutoConnect { get; set; }
    public bool AutoSend { get; set; }
    public string DeviceSerialNumber { get; set; } = "";
    public int DeviceDataVersion { get; set; }

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

    public static AppSettings Load() => LoadFrom(SettingsPath);

    /// <summary>从指定文件读取；旧版本配置会就地迁移。</summary>
    public static AppSettings LoadFrom(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
                settings.Origin ??= new();
                settings.Lens ??= new();
                if (settings.SettingsVersion < CurrentVersion)
                {
                    settings.Migrate();
                    settings.SaveTo(path);
                }
                return settings;
            }
        }
        catch
        {
            // 配置损坏时回退默认值，不影响启动。
        }

        return new AppSettings();
    }

    /// <summary>
    /// v0.2.x → v0.3.0：
    /// - 厂商 C 封装返回毫米且把 X/Y/Z 取反，本版改为 XVSDK 原生米制 Pose；
    /// - 旧默认“Y 上 / Z 后”是猜测值，改为 XVSDK 文档坐标系（Y 下 / Z 前）。
    /// 用户手选的 ROS 等其他预设保持不变。
    /// </summary>
    private void Migrate()
    {
        if (SettingsVersion < 2)
        {
            if (SourceFrame == SourceFramePreset.RightHandedYUpZBack)
                SourceFrame = SourceFramePreset.XvisioXRightYDownZForward;
            SettingsVersion = 2;
        }
    }

    public void Save() => SaveTo(SettingsPath);

    public void SaveTo(string path)
    {
        try
        {
            SettingsVersion = CurrentVersion;
            File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
        }
        catch
        {
            // 忽略写入失败（例如只读目录）。
        }
    }
}
