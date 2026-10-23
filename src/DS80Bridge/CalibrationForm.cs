using System.Text;
using DS80Bridge.Core;

namespace DS80Bridge;

/// <summary>原点和镜头项目各自保存；镜头实测数据归 UE Lens File 管理。</summary>
public sealed class CalibrationForm : Form
{
    private readonly TrackerEngine _engine;
    private readonly AppSettings _settings;
    private readonly PropertyGrid _originGrid = new() { Dock = DockStyle.Fill, ToolbarVisible = false };
    private readonly PropertyGrid _lensGrid = new() { Dock = DockStyle.Fill, ToolbarVisible = false };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 54, Padding = new Padding(12), AutoEllipsis = true };
    private LensProject Lens => (LensProject)_lensGrid.SelectedObject;

    public CalibrationForm(TrackerEngine engine, AppSettings settings)
    {
        _engine = engine;
        _settings = settings;
        Text = "项目校准 · 原点 / UE Lens File";
        Size = new Size(860, 760);
        MinimumSize = new Size(720, 560);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Microsoft YaHei UI", 9);
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var origin = new TabPage("定位原点");
        var lens = new TabPage("镜头项目 / UE Lens File");
        tabs.TabPages.AddRange(new[] { origin, lens });
        Controls.Add(tabs);
        Controls.Add(_status);

        _originGrid.SelectedObject = System.Text.Json.JsonSerializer.Deserialize<OriginOptions>(
            System.Text.Json.JsonSerializer.Serialize(settings.Origin));
        var originButtons = Buttons(
            ("以当前姿态定位", CaptureOrigin), ("保存原点文件", SaveOrigin),
            ("加载原点文件", LoadOrigin), ("清除原点", () => { _engine.ClearOrigin(); Status("原点已清除。"); }));
        origin.Controls.Add(_originGrid);
        origin.Controls.Add(originButtons);
        origin.Controls.Add(Hint("坐标单位：米，X 右 / Y 前 / Z 上。关闭“姿态全部归零”时保留俯仰和横滚。\n加载原点仅适用于相同 SLAM 地图坐标系；设备重启或重定位后应重新定位。UE 应用 Nodal Offset 时，这里定位的是跟踪器。"));

        _lensGrid.SelectedObject = settings.Lens.Clone();
        Lens.NodalOffsetInUe = settings.NodalOffsetInUe;
        lens.Controls.Add(_lensGrid);
        lens.Controls.Add(Buttons(
            ("导入 Aximmetry XML → .ulens", ImportAximmetry),
            ("新建", () => { _lensGrid.SelectedObject = new LensProject(); Status("已新建未应用的项目。"); }),
            ("加载项目", LoadLens), ("保存项目", SaveLens), ("应用项目", ApplyLens), ("应用并导出 UE 脚本", ExportLens)));
        lens.Controls.Add(Hint("所有相机、镜头、画幅和焦距均按项目编辑。导出的是 Lens File 初始化脚本，不是已完成的标定。\n畸变、光心、焦距和 Nodal Offset 在 UE 中使用真实画面求解。变焦/对焦改变后需采集相应标定点。\nAximmetry XML 可转成 UE .ulens（VIVE Mars 兼容格式）；不含跟踪器安装偏移，导入后需验证光学对齐。"));
        Status("原点：" + (_engine.CurrentOrigin?.Target.Name ?? "未定位") + "；镜头：" + settings.Lens.ProjectName);
    }

    private static Label Hint(string text) => new() { Dock = DockStyle.Top, Height = 96, Padding = new Padding(12), Text = text };
    private FlowLayoutPanel Buttons(params (string Text, Action Action)[] actions)
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 80, Padding = new Padding(8), AutoScroll = true };
        foreach (var action in actions)
        {
            var button = new Button { Text = action.Text, AutoSize = true, Height = 32, Margin = new Padding(4) };
            button.Click += (_, _) =>
            {
                try { ValidateChildren(); action.Action(); }
                catch (Exception ex) { Status(ex.Message); MessageBox.Show(this, ex.Message, "校准操作未完成", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            };
            panel.Controls.Add(button);
        }
        return panel;
    }
    private void Status(string message) => _status.Text = message;
    private void CaptureOrigin()
    {
        var target = (OriginOptions)_originGrid.SelectedObject;
        target.Validate();
        _settings.Origin = target;
        var previous = _engine.CurrentOrigin;
        _engine.ResetOrigin();
        if (ReferenceEquals(previous, _engine.CurrentOrigin))
            throw new InvalidOperationException("定位失败：请连接设备并等待稳定、有效的 Pose。");
        _settings.Save();
        Status("原点已定位：" + target.Name);
    }
    private void SaveOrigin()
    {
        var origin = _engine.CurrentOrigin ?? throw new InvalidOperationException("请先定位原点。");
        using var dialog = new SaveFileDialog { Filter = "DS80 原点|*.ds80origin.json", FileName = "origin.ds80origin.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        origin.Save(dialog.FileName);
        Status("已保存：" + dialog.FileName);
    }
    private void LoadOrigin()
    {
        using var dialog = new OpenFileDialog { Filter = "DS80 原点|*.ds80origin.json|JSON|*.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _engine.LoadOrigin(dialog.FileName);
        _settings.Origin = _engine.CurrentOrigin!.Target;
        _originGrid.SelectedObject = System.Text.Json.JsonSerializer.Deserialize<OriginOptions>(
            System.Text.Json.JsonSerializer.Serialize(_settings.Origin));
        _settings.Save();
        Status("已加载原点。请核对真实参考点；SLAM 地图坐标系必须与保存时一致。");
    }
    private void LoadLens()
    {
        using var dialog = new OpenFileDialog { Filter = "DS80 镜头项目|*.ds80lens.json|JSON|*.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _lensGrid.SelectedObject = LensProject.Load(dialog.FileName);
        Status("已载入编辑器，点击“应用项目”生效。");
    }
    private void ImportAximmetry()
    {
        using var open = new OpenFileDialog { Filter = "Aximmetry 镜头校准|*.xml|XML 文件|*.xml" };
        if (open.ShowDialog(this) != DialogResult.OK) return;

        var document = AximmetryLensDocument.Load(open.FileName);
        var project = Lens.Clone();
        document.ApplyTo(project);
        _lensGrid.SelectedObject = project;

        using var save = new SaveFileDialog
        {
            Filter = "Unreal Lens File|*.ulens",
            FileName = SafeFileName(document.Name) + ".ulens",
        };
        if (save.ShowDialog(this) != DialogResult.OK)
        {
            Status("已解析 XML 并填入镜头项目；尚未导出 .ulens。");
            return;
        }

        string json = document.ToUlensJson(project.ImageWidth, project.ImageHeight, project.LensModel, project.SerialNumber);
        File.WriteAllText(save.FileName, json, new UTF8Encoding(false));

        var point = document.Points[0];
        Status($"已导出 {Path.GetFileName(save.FileName)}：{document.Points.Count} 个采样点，" +
               $"Fx={point.Fx:F5} Fy={point.Fy:F5}，K1={point.K1:F5}。安装偏移表为空；UE 需使用对应 Focus/Zoom 采样输入。");
    }

    private static string SafeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "lens" : name;
    }

    private void SaveLens()
    {
        using var dialog = new SaveFileDialog { Filter = "DS80 镜头项目|*.ds80lens.json", FileName = "project.ds80lens.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        Lens.Save(dialog.FileName);
        Status("已保存项目：" + dialog.FileName);
    }
    private void ApplyLens()
    {
        Lens.Validate();
        if (_settings.NodalOffsetInUe != Lens.NodalOffsetInUe) _engine.ClearOrigin();
        _settings.Lens = Lens.Clone();
        _settings.NodalOffsetInUe = Lens.NodalOffsetInUe;
        _settings.Save();
        Status("项目已应用；安装补偿由 " + (Lens.NodalOffsetInUe ? "UE Lens File" : "Bridge") + " 负责。镜头光学参数需在 UE 应用。");
    }
    private void ExportLens()
    {
        Lens.Validate(forExport: true);
        using var dialog = new SaveFileDialog { Filter = "UE Python 脚本|*.py", FileName = "setup_lens.py" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        Lens.ExportUnrealScript(dialog.FileName);
        ApplyLens();
        Status("已导出。UE 中启用 Camera Calibration / Python，选中 CineCamera 后执行此脚本：" + dialog.FileName);
    }
}
