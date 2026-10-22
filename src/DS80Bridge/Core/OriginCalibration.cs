using System.ComponentModel;
using System.Text.Json;

namespace DS80Bridge.Core;

[DefaultProperty(nameof(Name))]
public sealed class OriginOptions
{
    [DisplayName("标定名称")]
    public string Name { get; set; } = "拍摄原点";
    [DisplayName("姿态全部归零"), Description("关闭时只归零航向，保留俯仰和横滚；开启时以当前完整姿态建立坐标系。")]
    public bool FullOrientation { get; set; }
    [DisplayName("原点目标 X / 右 (m)")]
    public double X { get; set; }
    [DisplayName("原点目标 Y / 前 (m)")]
    public double Y { get; set; }
    [DisplayName("原点目标 Z / 上 (m)")]
    public double Z { get; set; }
    [DisplayName("目标航向 (度)")]
    public double Pan { get; set; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || !double.IsFinite(X) || !double.IsFinite(Y) ||
            !double.IsFinite(Z) || !double.IsFinite(Pan) || Math.Abs(X) > 130 || Math.Abs(Y) > 130 ||
            Math.Abs(Z) > 130 || Math.Abs(Pan) > 180)
            throw new InvalidDataException("原点名称不能为空，位置须在 ±130 m 内，航向须在 ±180° 内。");
    }
}

public readonly record struct CoordinateConfiguration(SourceFramePreset Frame,
    double MountX, double MountY, double MountZ, double MountYaw, double MountPitch, double MountRoll,
    bool InvertX, bool InvertY, bool InvertZ, bool InvertPan, bool InvertTilt, bool InvertRoll, bool NodalOffsetInUe);

public sealed record OriginCalibration
{
    /// <summary>2：使用 XVSDK 原生米制位姿与文档坐标系；旧文件必须重新定位。</summary>
    public int Version { get; init; } = 2;
    public string Source { get; init; } = "";
    public CoordinateConfiguration CoordinateSignature { get; init; }
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
    public OriginOptions Target { get; init; } = new();
    public PoseSample Reference { get; init; }

    public static CoordinateConfiguration Signature(AppSettings s) => new(s.SourceFrame,
        s.MountXmm, s.MountYmm, s.MountZmm, s.MountYawDeg, s.MountPitchDeg, s.MountRollDeg,
        s.InvertX, s.InvertY, s.InvertZ, s.InvertPan, s.InvertTilt, s.InvertRoll, s.NodalOffsetInUe);

    public void Validate()
    {
        if (Version != 2 || Target == null || !Reference.HasPose || string.IsNullOrWhiteSpace(Source) ||
            !new[] { Reference.X, Reference.Y, Reference.Z, Reference.Qx, Reference.Qy, Reference.Qz, Reference.Qw }
                .All(double.IsFinite) ||
            Reference.Qx * Reference.Qx + Reference.Qy * Reference.Qy + Reference.Qz * Reference.Qz + Reference.Qw * Reference.Qw < 0.5)
            throw new InvalidDataException("原点文件版本或参考姿态无效。");
        Target.Validate();
    }

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    public static OriginCalibration Load(string path)
    {
        var result = JsonSerializer.Deserialize<OriginCalibration>(File.ReadAllText(path))
            ?? throw new InvalidDataException("原点文件为空。");
        result.Validate();
        return result;
    }
}
