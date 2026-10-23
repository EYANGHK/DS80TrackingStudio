using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace DS80Bridge.Core;

/// <summary>Aximmetry CameraLensDesc 的一个变焦/对焦采样点。</summary>
public sealed record AximmetryLensPoint
{
    public required double Zoom { get; init; }
    public required double Focus { get; init; }
    public required double FocalLengthMm { get; init; }
    public required double FovXDeg { get; init; }
    public required double Aspect { get; init; }
    /// <summary>光心相对画面中心的偏移，X 按宽度、Y 按高度归一化。</summary>
    public required double CenterX { get; init; }
    public required double CenterY { get; init; }
    public required double K1 { get; init; }
    public required double K2 { get; init; }
    public required double K3 { get; init; }

    /// <summary>UE Lens File 的 Fx：焦距 / 传感器宽度（等价 0.5 / tan(HFOV/2)）。</summary>
    public double Fx => FovXDeg > 0
        ? 0.5 / Math.Tan(FovXDeg * PoseMath.Deg2Rad * 0.5)
        : FocalLengthMm / SensorWidthMm;

    /// <summary>UE Lens File 的 Fy：同一焦距下按画面高宽比归一化。</summary>
    public double Fy => Fx * Aspect;

    /// <summary>由外层文档回填，便于单点换算。</summary>
    internal double SensorWidthMm { get; init; }

    /// <summary>UE PrincipalPoint：画面左上为 (0,0)、右下为 (1,1)。</summary>
    public double PrincipalX => 0.5 + CenterX;
    public double PrincipalY => 0.5 + CenterY;
}

/// <summary>
/// 解析 Aximmetry「Camera Calibrator」导出的镜头 XML，并生成 UE 可直接拖入的
/// <c>.ulens</c>（ULensFile 文本交换格式）。
///
/// 映射关系（与 UE 5.8 CameraCalibration 的 LensFileExchangeFormat 一致）：
/// - FocalLengthTable：Fx = 0.5/tan(HFOV/2)，Fy = Fx × Aspect
/// - ImageCenterTable：Cx = 0.5 + Center.X，Cy = 0.5 + Center.Y
/// - DistortionTable：K1,K2,K3 直接取 Radial，P1=P2=0（Aximmetry 与 UE 都用 OpenCV 归一化坐标）
/// </summary>
public sealed class AximmetryLensDocument
{
    private AximmetryLensDocument(string name, double sensorWidthMm, IReadOnlyList<AximmetryLensPoint> points)
    {
        Name = name;
        SensorWidthMm = sensorWidthMm;
        Points = points;
        SensorHeightMm = sensorWidthMm / points[0].Aspect;
    }

    public string Name { get; }
    public double SensorWidthMm { get; }
    public double SensorHeightMm { get; }
    public IReadOnlyList<AximmetryLensPoint> Points { get; }

    public double PrimaryFocalLengthMm => Points[0].FocalLengthMm;

    public static AximmetryLensDocument Load(string path) => Parse(File.ReadAllText(path));

    public static AximmetryLensDocument Parse(string xml)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException("XML 解析失败：" + ex.Message, ex);
        }

        XElement root = document.Root
            ?? throw new InvalidDataException("XML 没有根节点。");
        if (root.Name.LocalName != "AximmetryCameraLensDesc")
            throw new InvalidDataException($"不是 Aximmetry 镜头文件（根节点为 {root.Name.LocalName}）。");

        double sensorWidth = RequiredDouble(root, "SensorWidth");
        if (sensorWidth <= 0)
            throw new InvalidDataException("SensorWidth 必须大于 0。");

        string name = ((string?)root.Attribute("Name"))?.Trim() is { Length: > 0 } value ? value : "Aximmetry Lens";

        var keys = root.Descendants()
            .Where(e => e.Name.LocalName == "key" && (string?)e.Attribute("type") == "CameraLensDescID")
            .ToList();
        var items = root.Descendants()
            .Where(e => e.Name.LocalName == "item" && (string?)e.Attribute("type") == "CameraLensDescItem")
            .ToList();

        if (items.Count == 0)
            throw new InvalidDataException("XML 中没有 CameraLensDescItem 采样点。");

        var points = new List<AximmetryLensPoint>(items.Count);
        for (int i = 0; i < items.Count; i++)
        {
            XElement item = items[i];
            if (RequiredDouble(item, "DescType") != 0)
                throw new InvalidDataException($"第 {i + 1} 个采样点不是 OpenCV 描述类型（DescType=0），不能直接转换畸变参数。");
            double zoom = i < keys.Count ? OptionalDouble(keys[i], "Zoom", 0) : 0;
            double focus = i < keys.Count ? OptionalDouble(keys[i], "Focus", 0) : 0;

            double focal = OptionalDouble(item, "FocalLength", 0);
            double fovx = OptionalDouble(item, "FOVX", 0);
            double aspect = OptionalDouble(item, "Aspect", 0);

            if (aspect <= 0)
                throw new InvalidDataException($"第 {i + 1} 个采样点缺少有效 Aspect（画面高宽比）。");
            if (focal <= 0 && fovx <= 0)
                throw new InvalidDataException($"第 {i + 1} 个采样点缺少 FocalLength / FOVX。");
            if (fovx <= 0 && focal > 0)
                fovx = 2 * Math.Atan(sensorWidth / (2 * focal)) * PoseMath.Rad2Deg;
            if (focal <= 0 && fovx > 0)
                focal = sensorWidth / (2 * Math.Tan(fovx * PoseMath.Deg2Rad * 0.5));
            if (fovx <= 0 || fovx >= 180)
                throw new InvalidDataException($"第 {i + 1} 个采样点 FOVX 必须在 0 到 180 度之间。");

            var center = ParseComponents(item, "Center", 2, i);
            var radial = ParseComponents(item, "Radial", 3, i);

            foreach (double v in new[] { zoom, focus, focal, fovx, aspect, center[0], center[1], radial[0], radial[1], radial[2] })
                if (!double.IsFinite(v))
                    throw new InvalidDataException($"第 {i + 1} 个采样点包含非有限数值。");

            points.Add(new AximmetryLensPoint
            {
                Zoom = zoom,
                Focus = focus,
                FocalLengthMm = focal,
                FovXDeg = fovx,
                Aspect = aspect,
                CenterX = center[0],
                CenterY = center[1],
                K1 = radial[0],
                K2 = radial[1],
                K3 = radial[2],
                SensorWidthMm = sensorWidth,
            });
        }

        return new AximmetryLensDocument(name, sensorWidth, points);
    }

    /// <summary>生成 UE .ulens（UTF-8 JSON，可直接拖入 Content Browser）。</summary>
    public string ToUlensJson(int imageWidth, int imageHeight, string lensModelName, string serialNumber)
    {
        if (imageWidth < 1 || imageHeight < 1)
            throw new InvalidDataException("录制分辨率必须大于 0。");

        string focalData = string.Join("; ", Points.Select(p => Row(p.Focus, p.Zoom, p.Fx, p.Fy)));
        string centerData = string.Join("; ", Points.Select(p => Row(p.Focus, p.Zoom, p.PrincipalX, p.PrincipalY)));
        string distortionData = string.Join("; ", Points.Select(p => Row(p.Focus, p.Zoom, p.K1, p.K2, p.K3, 0, 0)));

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();

            writer.WritePropertyName("metadata");
            writer.WriteStartObject();
            writer.WriteString("type", "LensFile");
            writer.WriteString("version", "0.0.0");
            writer.WritePropertyName("lensInfo");
            writer.WriteStartObject();
            writer.WriteString("serialNumber", string.IsNullOrWhiteSpace(serialNumber) ? "None" : serialNumber);
            writer.WriteString("modelName", string.IsNullOrWhiteSpace(lensModelName) ? Name : lensModelName);
            writer.WriteString("distortionModel", "Spherical");
            writer.WriteEndObject();
            writer.WriteString("name", Name);
            writer.WriteString("nodalOffsetCoordinateSystem", "OpenCV");
            writer.WriteString("fxFyUnits", "Normalized");
            writer.WriteString("cxCyUnits", "Normalized");
            writer.WritePropertyName("userMetadata");
            writer.WriteStartArray();
            writer.WriteEndArray();
            writer.WriteEndObject();

            writer.WritePropertyName("sensorDimensions");
            writer.WriteStartObject();
            // Match the VIVE Mars exchange file's numeric strings. UE also accepts numbers.
            writer.WriteString("width", Number(SensorWidthMm));
            writer.WriteString("height", Number(SensorHeightMm));
            writer.WriteString("units", "Millimeters");
            writer.WriteEndObject();

            writer.WritePropertyName("imageDimensions");
            writer.WriteStartObject();
            writer.WriteString("width", imageWidth.ToString(CultureInfo.InvariantCulture));
            writer.WriteString("height", imageHeight.ToString(CultureInfo.InvariantCulture));
            writer.WriteEndObject();

            writer.WritePropertyName("cameraParameterTables");
            writer.WriteStartArray();
            WriteTable(writer, "FocalLengthTable", "FocusEncoder, ZoomEncoder, Fx, Fy", focalData);
            WriteTable(writer, "ImageCenterTable", "FocusEncoder, ZoomEncoder, Cx, Cy", centerData);
            // An empty table means no calibrated offset. Never borrow another rig's values
            // or insert an identity sample and present it as a measured calibration.
            WriteTable(writer, "NodalOffsetTable", "FocusEncoder, ZoomEncoder, Qx, Qy, Qz, Qw, Tx, Ty, Tz", "");
            WriteTable(writer, "DistortionTable", "FocusEncoder, ZoomEncoder, K1, K2, K3, P1, P2", distortionData);
            writer.WriteEndArray();

            writer.WritePropertyName("encoderTables");
            writer.WriteStartArray();
            WriteTable(writer, "Focus", "FocusEncoder, FocusCM", "");
            WriteTable(writer, "Iris", "IrisEncoder, IrisFstop", "");
            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public void ApplyTo(LensProject project)
    {
        project.ProjectName = Name;
        project.SensorWidthMm = Math.Round(SensorWidthMm, 4);
        project.SensorHeightMm = Math.Round(SensorHeightMm, 4);
        project.FocalLengthMm = Math.Round(PrimaryFocalLengthMm, 4);
        project.FilmbackConfirmed = true;
        if (string.IsNullOrWhiteSpace(project.LensModel)) project.LensModel = Name;
        project.Notes = string.IsNullOrWhiteSpace(project.Notes)
            ? $"由 Aximmetry XML 转换（{Points.Count} 个采样点，{DateTime.Now:yyyy-MM-dd HH:mm}）"
            : project.Notes;
    }

    private static void WriteTable(Utf8JsonWriter writer, string parameterName, string header, string data)
    {
        writer.WriteStartObject();
        writer.WriteString("parameterName", parameterName);
        writer.WriteString("header", header);
        writer.WriteString("data", data);
        writer.WriteEndObject();
    }

    private static string Row(params double[] values)
        => string.Join(", ", values.Select(Number));

    private static string Number(double value) => value.ToString("G17", CultureInfo.InvariantCulture);

    private static double RequiredDouble(XElement element, string attribute)
    {
        string? text = (string?)element.Attribute(attribute);
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            throw new InvalidDataException($"缺少或无法解析 {attribute} 属性。");
        return value;
    }

    private static double OptionalDouble(XElement element, string attribute, double fallback)
        => double.TryParse((string?)element.Attribute(attribute), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : fallback;

    private static double[] ParseComponents(XElement item, string attribute, int count, int index)
    {
        string? text = (string?)item.Attribute(attribute);
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidDataException($"第 {index + 1} 个采样点缺少 {attribute}。");

        string[] parts = text.Split(',');
        if (parts.Length < 2 || parts.Length > count)
            throw new InvalidDataException($"第 {index + 1} 个采样点的 {attribute} 应有 2–{count} 个分量。");

        var values = new double[count];
        for (int i = 0; i < count; i++)
        {
            if (i < parts.Length)
            {
                if (!double.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                    throw new InvalidDataException($"第 {index + 1} 个采样点的 {attribute} 第 {i + 1} 个分量无法解析。");
            }
            else
            {
                values[i] = 0;
            }
        }

        return values;
    }
}
