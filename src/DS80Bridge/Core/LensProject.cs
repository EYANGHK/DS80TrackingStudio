using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DS80Bridge.Core;

public enum LensModelKind { Spherical, Anamorphic }

/// <summary>项目准备参数；实际畸变/光心/nodal offset 由 UE 实拍求解并保存在 Lens File。</summary>
[DefaultProperty(nameof(ProjectName))]
public sealed class LensProject
{
    [Browsable(false)] public int Version { get; set; } = 1;
    [Category("1 项目"), DisplayName("项目名称")] public string ProjectName { get; set; } = "新项目";
    [Category("1 项目"), DisplayName("UE 版本")] public string UnrealVersion { get; set; } = "5.8";
    [Category("1 项目"), DisplayName("Lens File 资源路径"), Description("使用 /Game/ 开头的目录和英文资源名；导出脚本不会覆盖同名已有资源。")]
    public string AssetPath { get; set; } = "/Game/Calibration/Camera_Lens";
    [Category("2 相机与镜头"), DisplayName("相机型号")] public string CameraModel { get; set; } = "";
    [Category("2 相机与镜头"), DisplayName("镜头型号")] public string LensModel { get; set; } = "";
    [Category("2 相机与镜头"), DisplayName("镜头序列号")] public string SerialNumber { get; set; } = "";
    [Category("2 相机与镜头"), DisplayName("镜头模型")] public LensModelKind Model { get; set; }
    [Category("2 相机与镜头"), DisplayName("变形宽银幕挤压比")] public double SqueezeFactor { get; set; } = 1;
    [Category("3 当前采样点"), DisplayName("焦距 (mm)"), Description("当前拍摄焦距，也作为 UE 标定的初始估计。变焦后需选择或采集相应标定点。")]
    public double FocalLengthMm { get; set; }
    [Category("3 当前采样点"), DisplayName("对焦距离 (cm)"), Description("无镜头编码器时在 UE 手动输入同一数值；本软件不会将毫米当 Free-D 原始编码值发送。")]
    public double FocusDistanceCm { get; set; } = 100;
    [Category("4 有效画幅"), DisplayName("有效传感器宽度 (mm)")] public double SensorWidthMm { get; set; }
    [Category("4 有效画幅"), DisplayName("有效传感器高度 (mm)"), Description("填写当前录制模式的实际有效画幅，不从宽度自动推断。")]
    public double SensorHeightMm { get; set; }
    [Category("4 有效画幅"), DisplayName("录制画面宽度 (px)")] public int ImageWidth { get; set; } = 3840;
    [Category("4 有效画幅"), DisplayName("录制画面高度 (px)")] public int ImageHeight { get; set; } = 2160;
    [Category("4 有效画幅"), DisplayName("已核对有效画幅与录制分辨率"), Description("裁切、电子防抖、画面缩放等变化后应重新核对并重新标定。")]
    public bool FilmbackConfirmed { get; set; }
    [Category("5 跟踪补偿"), DisplayName("由 UE Lens File 应用 Nodal Offset"), Description("启用后 Bridge 停用安装平移/旋转补偿，保留原数值，防止与 UE 重复补偿。")]
    public bool NodalOffsetInUe { get; set; } = true;
    [Category("6 现场备注"), DisplayName("备注")] public string Notes { get; set; } = "";

    public void Validate(bool forExport = false)
    {
        if (Version != 1 || string.IsNullOrWhiteSpace(ProjectName) ||
            !Enum.IsDefined(Model) || !Regex.IsMatch(AssetPath ?? "", @"^/Game/(?:[A-Za-z0-9_]+/)*[A-Za-z0-9_]+$"))
            throw new InvalidDataException("项目名称、Lens File 资源路径或配置版本无效。");
        if (!new[] { FocalLengthMm, FocusDistanceCm, SensorWidthMm, SensorHeightMm, SqueezeFactor }.All(double.IsFinite) ||
            FocalLengthMm < 0 || FocusDistanceCm <= 0 || SensorWidthMm < 0 || SensorHeightMm < 0 ||
            ImageWidth < 1 || ImageHeight < 1 || SqueezeFactor < 1 ||
            (Model == LensModelKind.Spherical && SqueezeFactor != 1))
            throw new InvalidDataException("镜头数值无效：尺寸与焦距不能为负，对焦距离/分辨率须为正，球面镜头挤压比必须为 1。");
        if (forExport && (!FilmbackConfirmed || SensorWidthMm <= 0 || SensorHeightMm <= 0 || FocalLengthMm <= 0))
            throw new InvalidDataException("导出前请填写焦距和有效传感器宽高，并勾选已核对有效画幅与录制分辨率。");
    }

    public LensProject Clone() => JsonSerializer.Deserialize<LensProject>(JsonSerializer.Serialize(this))!;
    public void Save(string path)
    {
        Validate();
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
    public static LensProject Load(string path)
    {
        var project = JsonSerializer.Deserialize<LensProject>(File.ReadAllText(path))
            ?? throw new InvalidDataException("镜头项目文件为空。");
        project.Validate();
        return project;
    }
    public void ExportUnrealScript(string path)
    {
        Validate(forExport: true);
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("DS80Bridge.ue_lens_setup.py")
            ?? throw new FileNotFoundException("UE 初始化脚本资源未找到。");
        using var reader = new StreamReader(stream);
        string script = reader.ReadToEnd().Replace("__PROFILE_JSON__", JsonSerializer.Serialize(JsonSerializer.Serialize(this)));
        File.WriteAllText(path, script, new System.Text.UTF8Encoding(false));
    }
}
