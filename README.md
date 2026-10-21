# DS80 Tracking Studio

把 **Xvisio SeerSense DS80** 双目 6DoF 模组变成 UE5 可直接使用的摄像机跟踪源。
不依赖 Aximmetry，不依赖 OptiTrack / Vive 基站。

```
┌──────────┐   USB-C   ┌─────────────────────┐   UDP   ┌──────────────────────────┐
│  DS80    │ ────────► │  DS80 Tracking Studio│ ──────► │  UE5 (Live Link FreeD)   │
│ 6DoF IMU │           │  标定 / 滤波 / 编码   │ 40000   │  CineCameraActor + 控制器 │
└──────────┘           └─────────────────────┘         └──────────────────────────┘
```

当前版本：**v0.1.0（第一阶段）** — 读取 6DoF、坐标标定、Free-D / JSON 输出、UE 接收链路。
自绘 Live Link 插件（带置信度、丢失状态、镜头元数据）在第二阶段实现。

![界面](docs/ui.png)

---

## 1. 现在就能跑（无需硬件）

```powershell
# 直接运行已编译版本
.\dist\DS80TrackingStudio\DS80Bridge.exe --sim --autostart
```

它会以模拟器产生 120Hz 的摄像机运动，并同时向本机发送：

| 数据 | 地址 | 用途 |
|---|---|---|
| Free-D | `127.0.0.1:40000` | UE5 内置 **Live Link FreeD** 插件直接接收 |
| JSON | `127.0.0.1:40001` | 调试、录制、或接入自定义插件 |

用自带监视器验证：

```powershell
python tools\pose_monitor.py --seconds 5
```

期望输出：`Free-D ... 平均 ≈ 105 Hz`，校验失败 0。

---

## 2. 连接真实 DS80

### 2.1 装 SDK

官方 Windows 包（XVSDK 3.2.0，msvc2019-x64）：

```
https://download.xvisiotech.com/sdk-download/last_version/Select_Platform_Download/For%20windows/XVSDK-3.2.0-msvc2019-x64.exe
```

> 该站点 TLS 证书已过期，浏览器/curl 会报证书错误；下载后建议核对文件大小
> `40,317,259` 字节。本机已静默安装到 `C:\Program Files\xvsdk`。

安装后应存在：

```
C:\Program Files\xvsdk\bin\xvisio-CInterface-wrapper.dll   ← 本程序调用的 C 接口
C:\Program Files\xvsdk\bin\xvsdk.dll
C:\Program Files\xvsdk\bin\all_stream.exe                  ← 厂商验证工具
C:\Program Files\xvsdk\include\xvsdk\xv-sdk.h
```

### 2.2 装 USB 驱动（关键）

按官方 SDK Guide，Windows 需要给 DS80 的 **VSC 接口** 安装 libusb 驱动：

1. 插入 DS80（直连主板 USB 3.x，不要经过 Hub）。
2. 下载 [Zadig](https://zadig.akeo.ie/downloads/zadig-2.4.exe)。
3. `Options → List All Devices`，选中 DS80 / Xvisio 的 **VSC interface**。
4. 目标驱动选 **libusb-win32**，点 `Install Driver`。

### 2.3 先验证厂商链路

```powershell
cd "C:\Program Files\xvsdk\bin"
.\all_stream.exe
```

能看到双目图像 + 6DoF 数据，说明硬件、驱动、固件都正常。
这一步不通过，后面的程序一定读不到数据。

### 2.4 再运行本程序

```powershell
.\dist\DS80TrackingStudio\DS80Bridge.exe
```

1. 选择 **DS80 硬件（USB）**
2. 点 **连接**（初始化最长约 10 秒，界面不会卡死）
3. 看到 `Pose 频率 ≈ 100–200 Hz`、置信度 > 0 即成功
4. 点 **重置原点 (R)** 把当前位置/航向设为零点

命令行探测（不打开界面）：

```powershell
dotnet run --project tests\DS80Bridge.SelfTest -c Release -- --device
```

---

## 3. 接入 UE5（零代码）

UE5 自带 **Live Link FreeD** 插件，本程序输出标准 Vinten Free-D D1 报文（29 字节）。

1. 编辑 → 插件：启用 **Live Link** 与 **Live Link FreeD**，重启编辑器。
2. 窗口 → Live Link → `Source → LiveLinkFreeD Source`：
   - IP：`127.0.0.1`
   - Port：`40000`
3. 出现 Subject **`Camera 0`**（编号来自程序里的 Free-D Camera ID）。
4. 场景中放一个 **Cine Camera Actor**，添加组件 **Live Link Controller**，
   Subject Representation 选 `Camera 0`。
5. 在 DS80 前走动 / 转动，虚拟摄像机应同步。

详细的轴向核对与标定流程见 [docs/02-UE5-FreeD集成.md](docs/02-UE5-FreeD集成.md)。

---

## 4. 目录结构

```
DS80TrackingStudio/
├─ src/DS80Bridge/            WinForms 主程序（.NET 8，x64）
│  ├─ Core/
│  │  ├─ XvisioNative.cs      xvisio-CInterface-wrapper.dll 的 P/Invoke 绑定
│  │  ├─ XvisioPoseSource.cs  DS80 设备数据源
│  │  ├─ SimulatorPoseSource.cs
│  │  ├─ TrackerPipeline.cs   坐标换算 / 原点 / 安装偏移 / 滤波
│  │  ├─ PoseMath.cs          四元数与 Free-D 角度
│  │  ├─ FreeDEncoder.cs      29 字节 Free-D 编码 + 校验
│  │  ├─ UdpOutput.cs         Free-D + JSON 发送
│  │  ├─ TrackerEngine.cs     采样主循环（~370Hz 读取，按帧发送）
│  │  └─ AppSettings.cs       配置持久化
│  └─ MainForm.cs
├─ tests/DS80Bridge.SelfTest/ 无硬件自检 + 设备探测
├─ tools/pose_monitor.py      UDP 监视器（Free-D 解码校验）
├─ docs/                      硬件 / UE / 协议 / 标定文档
└─ dist/                      发布产物
```

---

## 5. 已实现 / 未实现

**已实现并验证**

- SDK 动态定位与加载（`XVSDK_BIN` → 程序目录 `sdk` → `C:\Program Files\xvsdk\bin`）
- DS80 6DoF 读取（C 接口，官方 3.2.0 SDK）
- 坐标系换算：设备右手系 → Free-D 世界系（X 右 / Y 前 / Z 上）
- 原点标定（位置 + 航向归零）
- 安装偏移（DS80 不在摄像机光心时的平移/旋转补偿）
- 可选低通滤波
- Free-D 编码（位置 1/64 mm、角度 1/32768°，校验和）
- JSON 输出（含置信度、数据年龄、四元数）
- UDP 发送与回环自检（`dotnet run --project tests/DS80Bridge.SelfTest`）
- 模拟器全链路验证（UE 可在无硬件时先调试）

**未实现（第二阶段）**

- 自绘 Live Link 插件（直接推送 `FLiveLinkCameraFrameData`，带跟踪状态）
- 录制 / 回放（CSV）
- CSLAM 地图保存 / 加载（长距离拍摄抗漂移）
- 镜头元数据（DS80 没有变焦/对焦编码器，需外接）
- 时间码同步（genlock / PTP）

---

## 6. 已知限制

- **漂移**：Mix / VIO 模式会累积误差；长镜头建议用 CSLAM 建图后锁定地图。
- **重定位跳变**：遮挡后恢复时坐标可能跳变，实拍前务必走一遍“遮挡恢复”测试。
- **单设备**：厂商 C 封装一次只管理一台相机；多机位需要多进程 + 不同端口。
- **UE 5.8**：内置 Live Link FreeD 在 5.4–5.7 均存在，5.8 未实测（本机未装 5.8）。
  若 5.8 移除该插件，用第二阶段的 JSON 插件替代。

---

## 7. 构建

```powershell
dotnet build src\DS80Bridge\DS80Bridge.csproj -c Release -p:Platform=x64
dotnet run --project tests\DS80Bridge.SelfTest -c Release
powershell -ExecutionPolicy Bypass -File build.ps1   # 生成 dist/
```
