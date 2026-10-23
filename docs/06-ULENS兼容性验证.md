# ULENS 参考格式修订与验证

2026-10-23，v0.4.1。参考用户提供的 `UE5_1_24mm03.04.ulens`（VIVE Mars），转换输入为 `2026.10.15-24mm.xml`。

## 本次修改

- 尺寸字段采用与 Mars 相同的 JSON 数字字符串类型，缺少序列号时输出 `None`。
- 参数表按 Mars 的顺序输出：FocalLengthTable、ImageCenterTable、NodalOffsetTable、DistortionTable，列名及列顺序一致。
- 补齐 Focus、Iris 编码器表。没有数据的表保留空字符串。
- 保留 XML 的校准数值，不复制参考文件的焦距、光心、畸变或安装偏移。NodalOffsetTable 为空表示未转换，不是零偏移标定。
- 拒绝非 OpenCV 描述类型（DescType 不为 0）和无效视场角，避免套用错误公式。
- 图像尺寸继续取当前项目配置；本次文件沿用原转换文件的 3840×2160。XML 没有该信息，不能从另一台相机的 Mars 文件确定当前输入尺寸。35.7 mm 宽度来自 XML，高度约 20.021695 mm 是按 Aspect 推算的值，仍需核对实际录制画幅。

参考数据与本次 XML 来自不同标定，数值不必相同。仅把数值改成 Mars 文件的数值会丢失本次标定结果。

## 实际引擎测试

引擎版本：`5.8.4-58902052+++UE5+Release-5.8`。在隔离的临时项目中，用 Python commandlet 导入 VIVE Mars 原文件、用户 Downloads 中的旧转换文件和修订文件，生成 LensFile 资源，再导出 ULENS。

三个文件均通过尺寸和每张参数表的逐项回读比较（容差 1e-5，UE 文本导出保留约六位小数）。命令行编辑器返回 0，报告 0 errors / 0 warnings。旧文件的数字类型也能被这个 UE 版本接受，不能把类型差异当作已确认故障原因。

- 可重复测试脚本：`tests/ue_ulens_roundtrip.py`。
- 测试输入清单：`artifacts/ulens-regression/manifest.json`。
- 实测结果：`artifacts/ulens-regression/results/report.json`。
- 日志：`artifacts/ulens-regression/commandlet.log`。
- .NET 回归测试使用外部 Mars 文件作为结构基准，检查字段类型、表名、列顺序和空表，并保留 XML 数值转换检查。

这些测试验证交换格式和参数保留，**不验证实拍畸变、光心对齐和 Nodal Offset**。旧文件与修订文件的光学数值相同，格式整理本身不会改变实际光学效果。

## 用户提供的 Slate 报错

`SWidget.cpp:815`、`SAssetView.cpp`、`visibility ... changed during Paint` 指向内容浏览器控件的可见状态在绘制期间变化。所给调用栈只有 Slate / ToolWidgets 等界面路径，没有显示 LensFile 解析失败。上面的无界面测试没有复现该报错，不能据此宣称已经修复它。

使用新文件名 `20261015_24mm_fixed.ulens` 导入到新的内容目录，以便保留旧资源并对照。若仍出现同一报错，需要围绕当前编辑器的内容浏览器界面继续排查；若数值读入正常但画面对不齐，则检查实际视频尺寸、有效画幅、Lens Component 绑定及 Focus/Zoom 求值输入，使用同一画面验证光学结果。当前 XML 的唯一采样输入是 Focus=0、Zoom=0，不是 Zoom=24。

## 参数口径依据

- [Aximmetry 官方输入说明](https://aximmetry.com/learn/virtual-production-workflow/green-screen-production/tracked-camera-workflow/inputs-tracked-camera/)：DescType=0 是 OpenCV；其他描述类型的数据解释可能不同。
- [Aximmetry 官方关于光心偏移的答复](https://my.aximmetry.com/post/1161-camera-calibrator-radial-distortion)：CenterX = cx / width − 0.5，CenterY = cy / height − 0.5。因此转换回归一化主点时加 0.5。
- [Epic 镜头校准概述](https://dev.epicgames.com/documentation/unreal-engine/camera-lens-calibration-overview)：Lens File 的焦距及主点按画面宽高归一化。
