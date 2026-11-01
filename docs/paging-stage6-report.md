# 第六阶段：透明度与切页节奏

状态（2026-11-01）：实现和自动复测完成，尚未发布。桌面逐帧、真实鼠标/高精度设备、实际不同刷新率与播放器交互仍待验收。v1.0.4 发布包不包含本轮改动。

## 最终行为

- 页面透明度由 `Pow(1-smoothstep(distance), 1.45)` 改为 `1-smoothstep(distance)`；隐藏阈值从 0.02 降为 0.002。无贡献的页面透明度归零、折叠并关闭命中；终态仅目标页可点击。
- 滚轮弹簧由 k=700/c=52 改为 k=1200/c=68，保留解析积分、目标追赶、连滚速度继承、边界钳制及标准 120 delta 累计规则。没有增加高精度设备特例或改回串行调度。
- 点击逐页动画从 240ms Quint 改为 200ms Cubic；滚轮接管时使用同一 Cubic 曲线的解析导数继承速度。
- 尺寸仍为第五阶段独立的 240ms 定时通道。页面、透明度和尺寸可以在不同回调结束，不把点击 200ms 错接为尺寸时长。
- 换歌动画继续只作用于标题、封面和控制区子元素，不覆盖媒体整页的切换透明度。

主要入口：`Infrastructure/PagingMotion.cs`、`MainWindow.WheelPaging.cs`、`MainWindow.xaml.cs`、`MainWindow.PagingDiagnostics.cs`。

## 同机基线与 A/B

先保留第五阶段二进制到 `artifacts/stage6-baseline-bin/`，重新采集 `artifacts/paging-stage6-before/`，未引用旧 Quint 路径的“74ms 消失”。以下均为三轮中位数，逻辑与可见性是应用诊断/Rendering 回调数据，不是显示器呈现时间。

| 方案 | 单格页面位置尾延迟（0.5 DIP） | 页面逻辑结束 | 旧页首次隐藏采样 |
|---|---:|---:|---:|
| 同机旧基线：700/52、指数 1.45、阈值 0.02 | 238.16ms | 286.45ms | 152.59ms |
| 仅透明度：指数 1.0、阈值 0.002 | 238.69ms | 289.35ms | 223.51ms |
| 透明度 + 1000/62 | 199.70ms | 240.13ms | 195.55ms |
| 透明度 + 1200/68 | 177.96ms | 228.16ms | 177.96ms |

1000/62 在 200ms 目标边缘，选择 1200/68 留出回调间隔余量。最终完整三轮中单格位置尾延迟中位数 177.89ms，范围 176.90–185.61ms；这是位置稳定口径，不能表述为“整岛 178ms 完成”。

点击正向单页的对照：

| 方案 | 页面位置尾延迟 | 页面逻辑结束 | 旧页首次隐藏采样 |
|---|---:|---:|---:|
| 240ms Quint，旧透明度 | 149.57ms | 248.90ms | 99.38ms |
| 240ms Cubic，新透明度 | 193.33ms | 245.45ms | 193.33ms |
| 200ms Cubic，新透明度 | 151.47ms | 204.00ms | 166.97ms |

选择 200ms Cubic：位置尾延迟接近旧方案，逻辑收尾更短，旧页保留更久。专项自检另对 200/240ms × Quint/Cubic 四种曲线做 1ms 数学采样，并核对解析速度与数值导数；该四格数据是模拟值，位于专项报告的 `clickCurves`，不冒充实际窗口测量。

## 像素与界面检查

`PagingExperience.SelfTest` 加载真实编译 MainWindow 与资源，不执行 Loaded，不创建 HWND、托盘或后台服务；测试数据和配置使用独立目录。

- 3 主题 × 4 材质 × 4 动画风格 × 有/无媒体 × 空/30 条历史，共 192 组。每组检查通知/剪贴板两个分段及九个页面位置，覆盖长标题、歌词、封面、六行日历和剪贴板 PNG。
- 检查中间态可见页、隐藏页禁用命中、终态唯一点击目标，以及标题/封面/控制区换歌动画不覆盖媒体页透明度。
- 12 组主题/材质真实 XAML 中点渲染 A/B，以同一场景隐藏页面后的图像作为背景，计算内容 RGB 差绝对值之和。新/旧比值约 1.36–1.40；这个指标表示该样本相对背景的内容对比，不是整页亮度，也不是理论覆盖率 0.75 的实测替代物。
- 已目视查看曜石/玻璃的动态→日历前后中点图，保留终态/布局裁剪与透明度交叠。离屏图不等于桌面动画录屏。
- 30/60/90/120/144/240Hz × 一/三/五格，18 组回调与行程模拟通过；这不是六台实际刷新率设备的验收。

结果及 PNG：`artifacts/paging-experience-stage6-verified/summary.json` 和同目录。早期夹具一度没有重新隐藏折叠层和 morph 层；这些早期图不采用，报告只引用修正后的 verified 目录。

## 完整三轮结果

`artifacts/paging-stage6-final/`，27 场景 × 3 轮，共 81 项调度/透明度终态/尺寸事件检查全部通过，无前置展开态失败。按本轮要求，没有额外复跑第五阶段专项自检；本节尺寸列来自第六阶段同一条测量轨迹。

| 场景 | 位置稳定（0.5 DIP） | 逻辑结束 | 页面透明度结束事件 | 尺寸结束事件 |
|---|---:|---:|---:|---:|
| 单格 | 177.89ms | 228.98ms | 228.85ms | 244.74ms |
| 三格连滚 | 194.02ms | 229.80ms | 229.73ms | 246.40ms |
| 五格连滚 | 199.81ms | 237.32ms | 237.18ms | 249.45ms |
| 动态→日历 | 183.07ms | 218.56ms | 218.49ms | 252.73ms |
| 点击单页 | 155.92ms | 205.55ms | 205.48ms | 245.27ms |
| 点击跨五页 | 995.65ms | 1030.64ms | 1030.56ms | 1079.81ms |

上述尾延迟从最后一次合成输入起算。跨五页点击仍逐页，不能拿它和一次滚轮五格混为同一种操作。透明度完成事件在逻辑结束函数之前记录，微小时间差来自调用顺序。

全场景共 6,581 个回调间隔，p50=16.213ms、p95=31.553ms、最大 81.675ms，>33ms 有 204 个。仍存在较长间隔，不能宣称完全没有掉帧。单格进程 CPU 原始三轮中位数：240/900ms 窗口为 31.25/78.12ms，对应静止基线 15.62/15.62ms；其他场景的原始值、基线及负的净值均保留在 JSON，不从这些数据直接归因于布局或阴影。

最终测量程序集 SHA256：`725d2c14e691c2600b75c84048124bc7f15592d9a0b30a3ae93ae578a3097eca`。原始基线、五个 A/B 目录与最终目录均保留；统一分析结果为 `artifacts/paging-stage6-comparison.json`，包含每个来源的程序集哈希。

## 复现

```powershell
dotnet build src/DynamicIsland/DynamicIsland.csproj -c Release
dotnet run --project tools/PagingExperience.SelfTest -c Release -- artifacts/paging-experience-stage6-new
python tools/paging_baseline.py --exe src/DynamicIsland/bin/Release/net10.0-windows10.0.19041.0/win-x64/DynamicIsland.exe --out artifacts/paging-stage6-new --repeats 3 --check-glide --check-size
```

单项对照可加 `--profile opacity|wheel1000|wheel1200|cubic240|cubic200|baseline` 之一及 `--cases one` 或 `--cases click_one click_one_reverse`；每次使用新输出目录。

```powershell
python tools/paging_compare.py --runs artifacts/paging-stage6-before artifacts/paging-stage6-ab-opacity artifacts/paging-stage6-ab-wheel1000 artifacts/paging-stage6-ab-wheel1200 artifacts/paging-stage6-ab-cubic240 artifacts/paging-stage6-ab-cubic200 artifacts/paging-stage6-final --out artifacts/paging-stage6-comparison-new.json
```

这些 A/B 参数仅在 `DI_PAGING_LOG` 诊断会话启用，不写入用户设置。`DI_PAGING_VISIBLE=1` 可为同一诊断进程增加任务栏入口，默认产品形态不变。CSV 追加 `opacity_settled`，记录 `opacity_start/finish`；脚本保留回调间隔分布、旧页隐藏采样和独立完成时间。旧基线没有透明度完成事件，相应分析值为 null，不伪造事件时间。

## 仍待完成的实际体验验收

诊断模式已使窗口出现在 computer-use 的窗口列表，但采集先返回 `FrameArrived timed out: timed out waiting on channel`，重新绑定窗口并重试后返回 `window capture timed out: timed out waiting on channel`。已停止使用该窗口的截图坐标，测试实例通过自身 IPC 退出。

因此未完成桌面逐帧、真实鼠标和高精度设备的 delta 采样、真实不同刷新率设备、实际媒体控制与换歌、滚动条鼠标捕获、菜单/HUD 的桌面命中区综合验收。没有把诊断 IPC 当成物理鼠标证据，也没有用离屏渲染替代这些项目。

本轮 Release 构建保留已有 SQLite 原生依赖 NU1903 提示；没有升级依赖、改写渲染器、启用 BitmapCache 或重构阴影。
