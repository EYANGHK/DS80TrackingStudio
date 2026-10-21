using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using DS80Bridge.Core;

namespace DS80Bridge;

public sealed class MainForm : Form
{
    private static readonly Color Bg = Color.FromArgb(16, 18, 24);
    private static readonly Color PanelBg = Color.FromArgb(26, 29, 38);
    private static readonly Color CardBg = Color.FromArgb(32, 36, 47);
    private static readonly Color TextMain = Color.FromArgb(230, 233, 239);
    private static readonly Color TextMuted = Color.FromArgb(138, 147, 166);
    private static readonly Color Accent = Color.FromArgb(47, 128, 237);
    private static readonly Color Good = Color.FromArgb(46, 204, 113);
    private static readonly Color Warn = Color.FromArgb(241, 196, 15);
    private static readonly Color Bad = Color.FromArgb(231, 76, 60);

    private readonly AppSettings _settings;
    private readonly TrackerEngine _engine;
    private readonly System.Windows.Forms.Timer _uiTimer;
    private readonly System.Windows.Forms.Timer _saveTimer;

    private RadioButton _rbSim = null!;
    private RadioButton _rbDevice = null!;
    private Button _btnConnect = null!;
    private Label _sdkLabel = null!;
    private Label _devStatus = null!;
    private Label _statusPill = null!;
    private Label _poseRateValue = null!;
    private Label _readRateValue = null!;
    private Label _ageValue = null!;
    private Label _confValue = null!;
    private Panel _confBar = null!;
    private Panel _confBarFill = null!;

    private Label _xValue = null!, _yValue = null!, _zValue = null!;
    private Label _panValue = null!, _tiltValue = null!, _rollValue = null!;
    private Label _quatValue = null!, _metaValue = null!;

    private TextBox _ipBox = null!;
    private NumericUpDown _freedPort = null!, _jsonPort = null!, _cameraId = null!;
    private CheckBox _sendFreeD = null!, _sendJson = null!;
    private ComboBox _frameCombo = null!;
    private ComboBox _unitCombo = null!;
    private CheckBox _invX = null!, _invY = null!, _invZ = null!, _invPan = null!, _invTilt = null!, _invRoll = null!;
    private NumericUpDown _mountX = null!, _mountY = null!, _mountZ = null!;
    private NumericUpDown _mountYaw = null!, _mountPitch = null!, _mountRoll = null!;
    private CheckBox _smooth = null!;
    private NumericUpDown _smoothTau = null!;
    private RichTextBox _log = null!;
    private Button _btnOrigin = null!;
    private bool _suppressEvents;
    private readonly bool _preferSimulator;

    public MainForm()
        : this(false)
    {
    }

    public MainForm(bool preferSimulator)
    {
        _preferSimulator = preferSimulator;
        _settings = AppSettings.Load();
        _engine = new TrackerEngine(_settings);
        _engine.Log += AppendLog;

        Text = "DS80 Tracking Studio";
        StartPosition = FormStartPosition.Manual;
        MinimumSize = new Size(1180, 640);
        Size = new Size(1400, 760);
        Location = new Point(12, 12);
        // 以窗口模式启动，避免 125% 缩放下内容溢出
        BackColor = Bg;
        ForeColor = TextMain;
        Font = new Font("Microsoft YaHei UI", 9f);
        KeyPreview = true;

        BuildUi();
        ApplySettingsToUi();
        RefreshSdkStatus();

        _uiTimer = new System.Windows.Forms.Timer { Interval = 50 };
        _uiTimer.Tick += (_, _) => UpdateUi();
        _uiTimer.Start();

        _saveTimer = new System.Windows.Forms.Timer { Interval = 600 };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); _settings.Save(); };
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        // 小屏（如 1366×768）自动收缩，避免窗口超出工作区
        var wa = Screen.FromControl(this).WorkingArea;
        int w = Math.Min(Width, wa.Width - 16);
        int h = Math.Min(Height, wa.Height - 16);
        if (w < Width || h < Height)
        {
            Size = new Size(w, h);
            Location = new Point(wa.Left + 8, wa.Top + 8);
        }
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Bg,
            Padding = new Padding(16, 12, 16, 12),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));

        root.Controls.Add(BuildHeader(), 0, 0);

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Bg,
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 312));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 350));
        content.Controls.Add(BuildLeftPanel(), 0, 0);
        content.Controls.Add(BuildCenterPanel(), 1, 0);
        content.Controls.Add(BuildRightPanel(), 2, 0);
        root.Controls.Add(content, 0, 1);

        root.Controls.Add(BuildLogPanel(), 0, 2);
        Controls.Add(root);

        KeyDown += OnKeyDown;
        FormClosing += (_, _) =>
        {
            _settings.Save();
            _engine.Dispose();
        };
    }

    private Control BuildHeader()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Bg,
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 240));

        var titleBox = new Panel { Dock = DockStyle.Fill, BackColor = Bg };
        var title = new Label
        {
            Text = "DS80 Tracking Studio",
            Font = new Font("Microsoft YaHei UI", 15f, FontStyle.Bold),
            ForeColor = TextMain,
            AutoSize = true,
            Location = new Point(0, 0),
            BackColor = Color.Transparent,
        };
        var subtitle = new Label
        {
            Text = "Xvisio DS80  ·  6DoF  →  Free-D / UDP  →  Unreal Engine 5  (无需 Aximmetry)",
            Font = new Font("Microsoft YaHei UI", 9f),
            ForeColor = TextMuted,
            AutoSize = true,
            Location = new Point(2, 34),
            BackColor = Color.Transparent,
        };
        titleBox.Controls.Add(title);
        titleBox.Controls.Add(subtitle);
        panel.Controls.Add(titleBox, 0, 0);

        _statusPill = new Label
        {
            Text = "● 未连接",
            Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold),
            ForeColor = Bad,
            TextAlign = ContentAlignment.MiddleRight,
            Dock = DockStyle.Fill,
            BackColor = Bg,
        };
        panel.Controls.Add(_statusPill, 1, 0);
        return panel;
    }

    private Control BuildLeftPanel()
    {
        var outer = new Panel { Dock = DockStyle.Fill, BackColor = Bg, Padding = new Padding(0, 0, 12, 0) };
        var stack = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Bg,
            AutoScroll = true,
        };

        var deviceBox = MakeCard("设备", 196);
        _rbSim = new RadioButton
        {
            Text = "模拟器（无硬件，验证数据链路）",
            ForeColor = TextMain,
            Checked = true,
            Location = new Point(14, 28),
            AutoSize = true,
            BackColor = Color.Transparent,
        };
        _rbDevice = new RadioButton
        {
            Text = "DS80 硬件（USB）",
            ForeColor = TextMain,
            Location = new Point(14, 52),
            AutoSize = true,
            BackColor = Color.Transparent,
        };
        _sdkLabel = new Label
        {
            Text = "SDK: 检测中…",
            ForeColor = TextMuted,
            Location = new Point(14, 80),
            Size = new Size(258, 20),
            AutoEllipsis = true,
            BackColor = Color.Transparent,
        };
        _btnConnect = MakeButton("连接", 14, 100, 258, Accent);
        _btnConnect.Click += (_, _) => ToggleConnect();

        _devStatus = new Label
        {
            Text = "未连接",
            AutoEllipsis = true,
            ForeColor = TextMuted,
            Location = new Point(14, 136),
            Size = new Size(258, 44),
            BackColor = Color.Transparent,
        };
        deviceBox.Controls.AddRange(new Control[] { _rbSim, _rbDevice, _sdkLabel, _btnConnect, _devStatus });
        stack.Controls.Add(deviceBox);

        var statsBox = MakeCard("运行状态", 186);
        _poseRateValue = MakeStat(statsBox, "Pose 频率", "-- Hz", 26);
        _readRateValue = MakeStat(statsBox, "读取频率", "-- Hz", 62);
        _ageValue = MakeStat(statsBox, "数据年龄", "-- ms", 98);
        _confValue = MakeStat(statsBox, "置信度", "--", 134);

        _confBar = new Panel
        {
            Location = new Point(112, 138),
            Size = new Size(150, 8),
            BackColor = Color.FromArgb(45, 50, 64),
        };
        _confBarFill = new Panel { Location = new Point(0, 0), Size = new Size(0, 8), BackColor = Good };
        _confBar.Controls.Add(_confBarFill);
        statsBox.Controls.Add(_confBar);
        stack.Controls.Add(statsBox);

        outer.Controls.Add(stack);
        return outer;
    }

    private Control BuildCenterPanel()
    {
        var outer = new Panel { Dock = DockStyle.Fill, BackColor = Bg };
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Bg,
            Padding = new Padding(0),
        };
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));

        var positionCard = MakeBigCard("位置  (米)");
        var posGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = CardBg,
            Padding = new Padding(0, 0, 0, 6),
        };
        posGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        posGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        posGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        _xValue = MakeBigValue(posGrid, "X  右", 0);
        _yValue = MakeBigValue(posGrid, "Y  前", 1);
        _zValue = MakeBigValue(posGrid, "Z  上", 2);
        positionCard.Controls.Add(posGrid);
        table.Controls.Add(positionCard, 0, 0);

        var rotationCard = MakeBigCard("姿态  (度)");
        var rotGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = CardBg,
            Padding = new Padding(0, 0, 0, 6),
        };
        rotGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        rotGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        rotGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        _panValue = MakeBigValue(rotGrid, "Pan  摇摄", 0);
        _tiltValue = MakeBigValue(rotGrid, "Tilt  俯仰", 1);
        _rollValue = MakeBigValue(rotGrid, "Roll  倾斜", 2);
        rotationCard.Controls.Add(rotGrid);
        table.Controls.Add(rotationCard, 0, 1);

        var metaCard = new Panel { Dock = DockStyle.Fill, BackColor = CardBg, Margin = new Padding(0, 8, 0, 0) };
        _quatValue = new Label
        {
            Text = "四元数  —",
            ForeColor = TextMuted,
            Font = new Font("Consolas", 10f),
            Location = new Point(16, 14),
            AutoSize = true,
            BackColor = Color.Transparent,
        };
        _metaValue = new Label
        {
            Text = "帧序号 —   时间戳 —",
            ForeColor = TextMuted,
            Font = new Font("Consolas", 10f),
            Location = new Point(16, 44),
            AutoSize = true,
            BackColor = Color.Transparent,
        };
        metaCard.Controls.Add(_quatValue);
        metaCard.Controls.Add(_metaValue);
        table.Controls.Add(metaCard, 0, 2);

        outer.Controls.Add(table);
        return outer;
    }

    private Control BuildRightPanel()
    {
        var outer = new Panel { Dock = DockStyle.Fill, BackColor = Bg, Padding = new Padding(12, 0, 0, 0) };
        var stack = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Bg,
            AutoScroll = true,
        };

        var outputBox = MakeCard("UDP 输出", 206);
        _ipBox = MakeTextBox(outputBox, "目标 IP", _settings.TargetIp, 30);
        _freedPort = MakeNumeric(outputBox, "Free-D 端口", _settings.FreedPort, 62, 1, 65535, 0);
        _jsonPort = MakeNumeric(outputBox, "JSON 端口", _settings.JsonPort, 94, 1, 65535, 0);
        _sendFreeD = MakeCheck(outputBox, "发送 Free-D 到 UE", _settings.SendFreeD, 124);
        _sendJson = MakeCheck(outputBox, "发送 JSON 调试数据", _settings.SendJson, 148);
        _cameraId = MakeNumeric(outputBox, "Free-D Camera ID", _settings.CameraId, 174, 0, 255, 0);
        stack.Controls.Add(outputBox);

        var calibBox = MakeCard("标定", 402);
        _btnOrigin = MakeButton("重置原点  (R)", 14, 28, 180, Color.FromArgb(0, 150, 136));
        _btnOrigin.Click += (_, _) => _engine.ResetOrigin();
        var originHint = new Label
        {
            Text = "当前位置 = 0，当前航向 = 0°",
            ForeColor = TextMuted,
            Location = new Point(200, 34),
            AutoSize = true,
            BackColor = Color.Transparent,
        };

        var frameLabel = new Label
        {
            Text = "设备坐标系",
            ForeColor = TextMuted,
            Location = new Point(14, 58),
            AutoSize = true,
            BackColor = Color.Transparent,
        };
        _frameCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new Point(120, 54),
            Width = 200,
            BackColor = CardBg,
            ForeColor = TextMain,
            FlatStyle = FlatStyle.Flat,
        };
        _frameCombo.Items.AddRange(new object[]
        {
            "X 右 / Y 上 / Z 后（右手）",
            "X 前 / Y 左 / Z 上（ROS）",
        });
        _frameCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_suppressEvents) return;
            _settings.SourceFrame = _frameCombo.SelectedIndex == 1
                ? SourceFramePreset.RosXForwardYLeftZUp
                : SourceFramePreset.RightHandedYUpZBack;
            QueueSave();
        };

        var unitLabel = new Label
        {
            Text = "位置单位",
            ForeColor = TextMuted,
            Location = new Point(14, 86),
            AutoSize = true,
            BackColor = Color.Transparent,
        };
        _unitCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new Point(120, 82),
            Width = 200,
            BackColor = CardBg,
            ForeColor = TextMain,
            FlatStyle = FlatStyle.Flat,
        };
        _unitCombo.Items.AddRange(new object[] { "米 (m)", "毫米 (mm)" });
        _unitCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_suppressEvents) return;
            _settings.PositionScale = _unitCombo.SelectedIndex == 1 ? 0.001 : 1.0;
            QueueSave();
        };

        var invertLabel = new Label
        {
            Text = "方向校正（现场标定用）",
            ForeColor = TextMuted,
            Location = new Point(14, 110),
            AutoSize = true,
            BackColor = Color.Transparent,
        };
        _invX = MakeMiniCheck(calibBox, "X", 14, 132);
        _invY = MakeMiniCheck(calibBox, "Y", 74, 132);
        _invZ = MakeMiniCheck(calibBox, "Z", 134, 132);
        _invPan = MakeMiniCheck(calibBox, "Pan", 194, 132);
        _invTilt = MakeMiniCheck(calibBox, "Tilt", 248, 132);
        _invRoll = MakeMiniCheck(calibBox, "Roll", 302, 132);

        _mountX = MakeNumeric(calibBox, "安装偏移 X (mm)", (decimal)_settings.MountXmm, 156, -5000, 5000, 0, 142);
        _mountY = MakeNumeric(calibBox, "安装偏移 Y (mm)", (decimal)_settings.MountYmm, 182, -5000, 5000, 0, 142);
        _mountZ = MakeNumeric(calibBox, "安装偏移 Z (mm)", (decimal)_settings.MountZmm, 208, -5000, 5000, 0, 142);
        _mountYaw = MakeNumeric(calibBox, "安装旋转 Yaw (°)", (decimal)_settings.MountYawDeg, 234, -180, 180, 2, 142);
        _mountPitch = MakeNumeric(calibBox, "安装旋转 Pitch (°)", (decimal)_settings.MountPitchDeg, 260, -90, 90, 2, 142);
        _mountRoll = MakeNumeric(calibBox, "安装旋转 Roll (°)", (decimal)_settings.MountRollDeg, 286, -180, 180, 2, 142);

        _smooth = MakeCheck(calibBox, "启用平滑滤波", _settings.SmoothingEnabled, 312);
        _smoothTau = MakeNumeric(calibBox, "时间常数 (s)", (decimal)_settings.SmoothingTau, 336, 0.001m, 1m, 3, 142);

        var resetCalib = MakeButton("清除原点标定", 14, 366, 180, Color.FromArgb(90, 98, 116));
        resetCalib.Click += (_, _) =>
        {
            _engine.Pipeline.ClearOrigin();
            AppendLog("原点标定已清除（输出回到设备原始坐标）");
        };

        calibBox.Controls.AddRange(new Control[]
        {
            _btnOrigin, originHint, frameLabel, _frameCombo, unitLabel, _unitCombo, invertLabel,
            _invX, _invY, _invZ, _invPan, _invTilt, _invRoll,
            _mountX, _mountY, _mountZ, _mountYaw, _mountPitch, _mountRoll,
            _smooth, _smoothTau, resetCalib,
        });
        stack.Controls.Add(calibBox);
        stack.Controls.Clear();

        // 右侧改用选项卡：在 1080p / 125% 缩放下也能完整显示
        outputBox.Dock = DockStyle.Top;
        calibBox.Dock = DockStyle.Top;
        calibBox.AutoScroll = true;

        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Appearance = TabAppearance.FlatButtons,
            ItemSize = new Size(88, 28),
            SizeMode = TabSizeMode.Fixed,
            Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold),
            DrawMode = TabDrawMode.OwnerDrawFixed,
            BackColor = PanelBg,
        };

        var pageOutput = new TabPage("输出") { BackColor = PanelBg };
        var pageCalib = new TabPage("标定") { BackColor = PanelBg, AutoScroll = true };
        pageOutput.Controls.Add(outputBox);
        pageCalib.Controls.Add(calibBox);
        tabs.TabPages.Add(pageOutput);
        tabs.TabPages.Add(pageCalib);

        tabs.DrawItem += (_, e) =>
        {
            var rect = tabs.GetTabRect(e.Index);
            bool selected = e.Index == tabs.SelectedIndex;
            using (var back = new SolidBrush(selected ? CardBg : PanelBg))
            {
                e.Graphics.FillRectangle(back, rect);
            }
            TextRenderer.DrawText(e.Graphics, tabs.TabPages[e.Index].Text, tabs.Font, rect,
                selected ? TextMain : TextMuted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        };

        outer.Controls.Add(tabs);
        return outer;
    }

    private Control BuildLogPanel()
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Bg, Padding = new Padding(0, 10, 0, 0) };
        _log = new RichTextBox
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(20, 22, 29),
            ForeColor = TextMuted,
            BorderStyle = BorderStyle.None,
            ReadOnly = true,
            Font = new Font("Consolas", 9f),
            DetectUrls = false,
        };
        panel.Controls.Add(_log);
        return panel;
    }

    // ---------- 控件工厂 ----------

    private static Panel MakeCard(string title, int height)
    {
        var card = new Panel
        {
            Width = 288,
            Height = height,
            BackColor = PanelBg,
            Margin = new Padding(0, 0, 0, 10),
        };
        var label = new Label
        {
            Text = title,
            ForeColor = TextMuted,
            Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold),
            Location = new Point(14, 10),
            AutoSize = true,
            BackColor = Color.Transparent,
        };
        card.Controls.Add(label);
        return card;
    }

    private static Panel MakeBigCard(string title)
    {
        var card = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = CardBg,
            Margin = new Padding(0, 0, 0, 8),
            Padding = new Padding(0, 34, 0, 0),
        };
        var label = new Label
        {
            Text = title,
            ForeColor = TextMuted,
            Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold),
            Location = new Point(16, 10),
            AutoSize = true,
            BackColor = Color.Transparent,
        };
        card.Controls.Add(label);
        return card;
    }

    private static Label MakeBigValue(TableLayoutPanel grid, string caption, int column)
    {
        var holder = new Panel { Dock = DockStyle.Fill, BackColor = CardBg };
        var name = new Label
        {
            Text = caption,
            ForeColor = TextMuted,
            Font = new Font("Microsoft YaHei UI", 9f),
            Dock = DockStyle.Top,
            Height = 22,
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Color.Transparent,
        };
        var value = new Label
        {
            Text = "0.000",
            ForeColor = TextMain,
            Font = new Font("Consolas", 18f, FontStyle.Bold),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Color.Transparent,
        };
        holder.Controls.Add(value);
        holder.Controls.Add(name);
        grid.Controls.Add(holder, column, 0);
        return value;
    }

    private static Label MakeStat(Panel parent, string caption, string initial, int y)
    {
        var name = new Label
        {
            Text = caption,
            ForeColor = TextMuted,
            Location = new Point(14, y + 3),
            AutoSize = true,
            BackColor = Color.Transparent,
        };
        var value = new Label
        {
            Text = initial,
            ForeColor = TextMain,
            Font = new Font("Consolas", 12f, FontStyle.Bold),
            Location = new Point(112, y),
            Size = new Size(162, 24),
            TextAlign = ContentAlignment.MiddleRight,
            BackColor = Color.Transparent,
        };
        parent.Controls.Add(name);
        parent.Controls.Add(value);
        return value;
    }

    private static Button MakeButton(string text, int x, int y, int width, Color color)
    {
        var button = new Button
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(width, 30),
            BackColor = color,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold),
            Cursor = Cursors.Hand,
        };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }

    private TextBox MakeTextBox(Panel parent, string caption, string value, int y)
    {
        parent.Controls.Add(new Label
        {
            Text = caption,
            ForeColor = TextMuted,
            Location = new Point(14, y + 4),
            AutoSize = true,
            BackColor = Color.Transparent,
        });
        var box = new TextBox
        {
            Text = value,
            Location = new Point(112, y),
            Width = 160,
            BackColor = CardBg,
            ForeColor = TextMain,
            BorderStyle = BorderStyle.FixedSingle,
        };
        box.Validated += (_, _) =>
        {
            _settings.TargetIp = box.Text.Trim();
            _engine.ApplyOutputSettings();
            QueueSave();
        };
        parent.Controls.Add(box);
        return box;
    }

    private static NumericUpDown MakeNumeric(Panel parent, string caption, decimal value, int y,
        decimal min, decimal max, int decimals, int labelWidth = 175, int fieldWidth = 120)
    {
        parent.Controls.Add(new Label
        {
            Text = caption,
            ForeColor = TextMuted,
            Location = new Point(14, y + 4),
            Size = new Size(labelWidth, 18),
            BackColor = Color.Transparent,
        });
        var box = new NumericUpDown
        {
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(value, min, max),
            DecimalPlaces = decimals,
            Location = new Point(14 + labelWidth, y),
            Width = fieldWidth,
            BackColor = CardBg,
            ForeColor = TextMain,
            BorderStyle = BorderStyle.FixedSingle,
            Increment = decimals == 0 ? 1 : 0.1m,
        };
        parent.Controls.Add(box);
        return box;
    }

    private static CheckBox MakeCheck(Panel parent, string text, bool value, int y)
    {
        var box = new CheckBox
        {
            Text = text,
            Checked = value,
            ForeColor = TextMain,
            Location = new Point(14, y),
            AutoSize = true,
            BackColor = Color.Transparent,
        };
        parent.Controls.Add(box);
        return box;
    }

    private CheckBox MakeMiniCheck(Panel parent, string text, int x, int y)
    {
        var box = new CheckBox
        {
            Text = text,
            ForeColor = TextMain,
            Location = new Point(x, y),
            AutoSize = true,
            BackColor = Color.Transparent,
        };
        parent.Controls.Add(box);
        return box;
    }

    // ---------- 设置绑定 ----------

    private void ApplySettingsToUi()
    {
        _suppressEvents = true;
        _ipBox.Text = _settings.TargetIp;
        _freedPort.Value = _settings.FreedPort;
        _jsonPort.Value = _settings.JsonPort;
        _cameraId.Value = _settings.CameraId;
        _sendFreeD.Checked = _settings.SendFreeD;
        _sendJson.Checked = _settings.SendJson;
        _frameCombo.SelectedIndex = _settings.SourceFrame == SourceFramePreset.RosXForwardYLeftZUp ? 1 : 0;
        _unitCombo.SelectedIndex = _settings.PositionScale < 0.01 ? 1 : 0;
        _invX.Checked = _settings.InvertX;
        _invY.Checked = _settings.InvertY;
        _invZ.Checked = _settings.InvertZ;
        _invPan.Checked = _settings.InvertPan;
        _invTilt.Checked = _settings.InvertTilt;
        _invRoll.Checked = _settings.InvertRoll;
        _mountX.Value = (decimal)_settings.MountXmm;
        _mountY.Value = (decimal)_settings.MountYmm;
        _mountZ.Value = (decimal)_settings.MountZmm;
        _mountYaw.Value = (decimal)_settings.MountYawDeg;
        _mountPitch.Value = (decimal)_settings.MountPitchDeg;
        _mountRoll.Value = (decimal)_settings.MountRollDeg;
        _smooth.Checked = _settings.SmoothingEnabled;
        _smoothTau.Value = (decimal)_settings.SmoothingTau;
        _suppressEvents = false;

        _sendFreeD.CheckedChanged += (_, _) => { _settings.SendFreeD = _sendFreeD.Checked; QueueSave(); };
        _sendJson.CheckedChanged += (_, _) => { _settings.SendJson = _sendJson.Checked; QueueSave(); };
        _freedPort.ValueChanged += (_, _) => { _settings.FreedPort = (int)_freedPort.Value; _engine.ApplyOutputSettings(); QueueSave(); };
        _jsonPort.ValueChanged += (_, _) => { _settings.JsonPort = (int)_jsonPort.Value; _engine.ApplyOutputSettings(); QueueSave(); };
        _cameraId.ValueChanged += (_, _) => { _settings.CameraId = (byte)_cameraId.Value; QueueSave(); };

        _invX.CheckedChanged += (_, _) => { _settings.InvertX = _invX.Checked; QueueSave(); };
        _invY.CheckedChanged += (_, _) => { _settings.InvertY = _invY.Checked; QueueSave(); };
        _invZ.CheckedChanged += (_, _) => { _settings.InvertZ = _invZ.Checked; QueueSave(); };
        _invPan.CheckedChanged += (_, _) => { _settings.InvertPan = _invPan.Checked; QueueSave(); };
        _invTilt.CheckedChanged += (_, _) => { _settings.InvertTilt = _invTilt.Checked; QueueSave(); };
        _invRoll.CheckedChanged += (_, _) => { _settings.InvertRoll = _invRoll.Checked; QueueSave(); };

        _mountX.ValueChanged += (_, _) => { _settings.MountXmm = (double)_mountX.Value; QueueSave(); };
        _mountY.ValueChanged += (_, _) => { _settings.MountYmm = (double)_mountY.Value; QueueSave(); };
        _mountZ.ValueChanged += (_, _) => { _settings.MountZmm = (double)_mountZ.Value; QueueSave(); };
        _mountYaw.ValueChanged += (_, _) => { _settings.MountYawDeg = (double)_mountYaw.Value; QueueSave(); };
        _mountPitch.ValueChanged += (_, _) => { _settings.MountPitchDeg = (double)_mountPitch.Value; QueueSave(); };
        _mountRoll.ValueChanged += (_, _) => { _settings.MountRollDeg = (double)_mountRoll.Value; QueueSave(); };

        _smooth.CheckedChanged += (_, _) => { _settings.SmoothingEnabled = _smooth.Checked; QueueSave(); };
        _smoothTau.ValueChanged += (_, _) => { _settings.SmoothingTau = (double)_smoothTau.Value; QueueSave(); };

        _rbDevice.CheckedChanged += (_, _) => RefreshSdkStatus();
    }

    private void QueueSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    // ---------- 运行控制 ----------

    /// <summary>命令行 --sim 启动时调用：直接以模拟器开始跟踪与发送。</summary>
    public void StartSimulator()
    {
        _rbSim.Checked = true;
        if (!_engine.IsRunning) ToggleConnect();
    }

    /// <summary>命令行 --device 启动时调用：直接尝试连接 DS80。</summary>
    public void StartDevice()
    {
        _rbDevice.Checked = true;
        RefreshSdkStatus();
        if (!_engine.IsRunning) ToggleConnect();
    }

    private async void ToggleConnect()
    {
        if (_engine.IsRunning)
        {
            _engine.Stop();
            SetConnectedUi(false);
            return;
        }

        if (_connecting) return;
        _connecting = true;
        _btnConnect.Enabled = false;
        _btnConnect.Text = "连接中…";
        _statusPill.Text = "● 正在连接";
        _statusPill.ForeColor = Warn;
        _devStatus.Text = _rbDevice.Checked
            ? "正在初始化 SDK 并搜索 DS80（最长约 10 秒）…"
            : "正在启动模拟器…";
        _devStatus.ForeColor = TextMuted;

        IPoseSource source = _rbDevice.Checked
            ? new XvisioPoseSource()
            : new SimulatorPoseSource { FramePreset = _settings.SourceFrame };

        var result = await Task.Run(() =>
        {
            bool started = _engine.Start(source, out string msg);
            return (started, msg);
        });

        _connecting = false;
        if (IsDisposed) return;
        _btnConnect.Enabled = true;

        if (!result.started)
        {
            source.Dispose();
            AppendLog("连接失败: " + result.msg);
            SetConnectedUi(false);
            _devStatus.Text = result.msg;
            _devStatus.ForeColor = Bad;
            MessageBox.Show(this, result.msg, "连接失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        AppendLog(result.msg);
        SetConnectedUi(true);
    }

    private bool _connecting;

    private void SetConnectedUi(bool connected)
    {
        _btnConnect.Text = connected ? "断开" : "连接";
        _btnConnect.BackColor = connected ? Color.FromArgb(192, 57, 43) : Accent;
        _rbSim.Enabled = !connected;
        _rbDevice.Enabled = !connected;
        _statusPill.Text = connected ? "● 跟踪中" : "● 未连接";
        _statusPill.ForeColor = connected ? Good : Bad;
        _devStatus.Text = connected
            ? $"已连接：{_engine.Source?.Name} · {_engine.Source?.Status}"
            : "未连接";
        _devStatus.ForeColor = connected ? Good : TextMuted;
    }

    private void RefreshSdkStatus()
    {
        if (!_rbDevice.Checked)
        {
            _sdkLabel.Text = "SDK: 模拟模式不需要";
            _sdkLabel.ForeColor = TextMuted;
            return;
        }

        string? dir = SdkEnvironment.LocateBinDirectory();
        if (dir != null)
        {
            _sdkLabel.Text = "SDK: " + dir;
            _sdkLabel.ForeColor = Good;
        }
        else
        {
            _sdkLabel.Text = "SDK: 未找到（请安装 XVSDK 或设置 XVSDK_BIN）";
            _sdkLabel.ForeColor = Warn;
        }
    }

    // ---------- UI 刷新 ----------

    private void UpdateUi()
    {
        var frame = _engine.Latest;

        _poseRateValue.Text = $"{_engine.PoseRateHz:F0} Hz";
        _readRateValue.Text = $"{_engine.ReadRateHz:F0} Hz";

        if (frame.Valid)
        {
            _xValue.Text = frame.X.ToString("F3");
            _yValue.Text = frame.Y.ToString("F3");
            _zValue.Text = frame.Z.ToString("F3");
            _panValue.Text = frame.Pan.ToString("F2");
            _tiltValue.Text = frame.Tilt.ToString("F2");
            _rollValue.Text = frame.Roll.ToString("F2");

            _quatValue.Text = string.Format(CultureInfo.InvariantCulture,
                "四元数  qx={0:F4}  qy={1:F4}  qz={2:F4}  qw={3:F4}", frame.Qx, frame.Qy, frame.Qz, frame.Qw);
            _metaValue.Text = $"帧序号 {frame.Sequence}   数据年龄 {frame.DataAgeMs:F0} ms";

            _ageValue.Text = frame.DataAgeMs < 5000 ? $"{frame.DataAgeMs:F0} ms" : "—";
            _confValue.Text = $"{frame.Confidence:P0}";
            _confBarFill.Width = (int)Math.Clamp(_confBar.Width * frame.Confidence, 0, _confBar.Width);
            _confBarFill.BackColor = frame.Confidence > 0.6 ? Good : frame.Confidence > 0.2 ? Warn : Bad;

            bool stale = frame.DataAgeMs > 500;
            _statusPill.Text = stale ? "● 数据中断" : "● 跟踪中";
            _statusPill.ForeColor = stale ? Warn : Good;
        }
        else if (_engine.IsRunning)
        {
            _xValue.Text = _yValue.Text = _zValue.Text = "--";
            _panValue.Text = _tiltValue.Text = _rollValue.Text = "--";
            _quatValue.Text = "四元数  —";
            _metaValue.Text = "等待有效 Pose…";
            _ageValue.Text = frame.DataAgeMs < 5000 && !double.IsInfinity(frame.DataAgeMs)
                ? $"{frame.DataAgeMs:F0} ms" : "—";
            _confValue.Text = "0%";
            _confBarFill.Width = 0;
            _statusPill.Text = "● 等待数据";
            _statusPill.ForeColor = Warn;
        }
    }

    private void AppendLog(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<string>(AppendLog), message);
            return;
        }

        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.R)
        {
            _engine.ResetOrigin();
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Space)
        {
            ToggleConnect();
            e.Handled = true;
        }
    }
}














