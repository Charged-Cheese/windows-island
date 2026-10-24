# 参考项目 UI 研究：Dynamic Island for Windows（devcode90）

> 调研对象：<https://github.com/devcode90/Dynamic-Island-for-Windows>
> 版本：Windhawk Mod 1.4.0，单文件 C++23（约 24000 行），Direct2D + DirectWrite 自绘，
> 通过 `UpdateLayeredWindow` 逐帧渲染，无任何 UI 框架。
> 目的：提炼其设计系统、布局与动画参数，作为本项目（WPF 灵动岛）改版的参照。

图片素材见 `docs/reference/`：`full-preview.png`、`media.png`、`hardware.png`、`idle.png`、
`themes.png`、`lyrics.png`。

---

## 1. 设计系统

### 1.1 形状与尺寸

| 项目 | 参考实现 | 说明 |
| --- | --- | --- |
| 设计画布 | 880 × 320 内容坐标 | 渲染时整体乘 `sizeScale`，所有布局常量为设计坐标 |
| 展开媒体 | 380 × 184 | 侧栏歌词时 +220 宽；内容最小展开高 60 |
| 折叠媒体 | 150 × 44 | 可选左侧时钟段（`MediaPillLayout`） |
| 空闲胶囊 | 36 高，宽 72–280 自适应 | 按实际文本测宽，`kWidthQuantum=2px` 取整，避免逐帧改窗 |
| 圆角 | Pill：`min(h/2, 44)`；Notch：16；Win11：8 | 形状三选一，Pill 为默认 |
| 全局缩放 | `sizeScale` 乘在渲染变换上 | 字体另有独立 `textScale` 0.7–1.6 |
| 渲染留白 | 四周 28 × 22 px | 给阴影/光晕留空间 |

### 1.2 颜色与主题

8 套调色板，统一为 4 个 token（背景 / 前景 / 次要文字 / 边框）：

| 主题 | bg | fg | secondary | border |
| --- | --- | --- | --- | --- |
| Obsidian（默认） | `#08080A` | `#FFFFFF` | `#9B9BA5` | `#1E1E22` |
| Graphite | `#1C1C1E` | `#FFFFFF` | `#A8A8AE` | `#323236` |
| Slate | `#111721` | `#E9EEF6` | `#92A2B8` | `#232C3A` |
| Nord | `#2E3440` | `#ECEFF4` | `#A7B0C0` | `#3B4252` |
| Evergreen | `#0C1512` | `#E4F1EA` | `#8CAE9D` | `#1A2A23` |
| Espresso | `#1A1512` | `#F6EEE7` | `#B7A395` | `#2E2420` |
| Plum | `#16111C` | `#F1EAF6` | `#AC9BB9` | `#281F33` |
| Porcelain（浅色） | `#F5F6F8` | `#14161A` | `#5B6270` | `#D8DBE1` |

- 次要文字每个主题都控制在约 4.5:1 对比度。
- 强调色默认从专辑封面采样，换歌时做指数 lerp（约 300ms 半衰期）。
- 边框 Auto 模式直接用强调色：媒体 60% alpha、idle 35%。
- 不接受 `#RRGGBBAA` 之外的透明度写法，alpha 与全局透明度相乘。

### 1.3 材质分层（自下而上）

1. **tint scrim**：压暗背景（有真毛玻璃时跳过）。
2. **base fill**：主题底色，可带 alpha。
3. **深度渐变**：顶部全透明 → 底部黑 8.5%（媒体/idle）或 6%，浅色主题再乘 0.55。
   刻意不做顶部高光，避免"描边发光"。
4. **强调色 bloom**：从顶部溢出的超软同色光晕，媒体 0.115、idle 0.055、其他 0.075，
   可调 0–200%。
5. **轮廓线**：0.8–1.0px，默认 `#1E1E22` 或强调色。

阴影：7 层同心圆角矩形堆叠，二次 alpha 衰减，`spread=14`，`yOffset=35%`。
有 DWM 毛玻璃背景时关闭阴影（避免把模糊压黑）。

### 1.4 排版

- 主字体 `Segoe UI Variable Display`，小字 `Segoe UI Variable Small`，图标 `Segoe Fluent Icons`。
- 字号阶梯（×textScale）：11 常规 / 12 粗 / 13 半粗 / 13.5 半粗（正文）/ 18 粗（时钟）/
  42 粗（大数字）/ 56 / 63。
- 歌词当前行 19.5 半粗，行距 25，邻行按距离淡出。

---

## 2. 布局

### 2.1 展开媒体（380×184）

```
┌──────────────────────────────────────────────┐
│ ┌──────┐                                     │
│ │ 封面 │  标题（13.5 半粗，y 34–54）       ║ │  ← 右侧频谱 32×20（right-24）
│ │ 64px │  艺人（11 常规，y 54–74）          ║ │
│ │ r16  │  专辑（可选，y 68–84）             ║ │
│ └──────┘  来源行（y 70/85）                 ║ │
│                                              ║ │  ← 垂直分页点（right-10）
│  1:42  ──────●──────────────  -2:19         │
│                                              │
│           ◀     ▶(r22)     ▶                │  ← 控件 y=148，间距 64
└──────────────────────────────────────────────┘
```

- 封面 `(24,20)` 起、64×64、圆角 16；折叠态 32px 正圆，展开时用高度驱动圆形→圆角方形 morph
  （120px 高度完成，0.26s 上限）。
- 标题左边界 = `art.right + 18`；右边界 = `频谱.left - 16`；超出走 marquee。
- 进度条 y=114：两侧 24 margin + 48 inset，左"已播放"、右"-剩余时间"（不是总时长），
  字色 80% 次要色；拖动时轨道加粗 2.5→3.5、滑块 4.5→6.5，带 accent 22% 光环。
  滑块是白色实心 + 1.6px accent 描边。
- 控件：播放 r22，前后 r16，热区各方向 +6。
- 源切换 dock：左上 20 起、30 高、最多 3 槽，换源时整组 14px 位移 + 220ms 交叉。
- 分页点：`x = right - 10`，间距 9，半径 2.5；未选中 22% 不透明度，选中项拉伸为
  `r+2.2+速度拉伸` 的胶囊，指示器随 tab 弹簧运动。

### 2.2 硬件卡

- 标题 16px 粗，`x=24`、`y=16`。
- 2×3 卡片网格：卡高 38，列间距 9，卡圆角 9，三行间距 6。
- 卡内三段式：图标 15px（左上 16,15）+ 标签 11px 60% 次要色 + 数值 13.5 半粗 + 底部
  2.5px 进度条（离底 6）。
- 颜色语义：<75% 用强调色，≥75% 琥珀，≥90% 红；网络上下行没有百分比条。

### 2.3 空闲胶囊

- 高 36，结构：`[时间] │ [天气图标 温度]`，左右 padding 14，槽间距 9，分隔线 1×18。
- 宽度按文本实测（"9:41" 22.7px 墨迹），min 72 / max 280，隐私点预留 18px。
- 时间用等宽数字格式缓存，避免跳宽。

### 2.4 歌词

- 折叠态：封面右侧一行当前歌词，随宽度渐显（175px 起、60px 淡入带）。
- 展开歌词页：顶部小圆封面 26 + 单行标题；三行歌词（说明见 2.1）；
  播放器整体缩到 0.90，围绕 `pivotY=176` 变换；行切换弹簧 190/26（近临界阻尼）。

---

## 3. 动画系统

### 3.1 弹簧

```cpp
// 固定 0.5ms 子步积分，避免掉帧时弹簧爆炸
for (dt in steps of 0.5ms) {
    a = -stiffness * (value - target) - damping * velocity;
    velocity += a * dt;  value += velocity * dt;
}
// 阈值 0.01 吸附
```

尺寸弹簧参数：

| 方向 | stiffness | damping |
| --- | --- | --- |
| 宽/高 扩张 | 380 | 26 |
| 宽/高 收缩 | 200 | 28 |
| 默认（无方向） | 280 | 24 |

风格预设（乘数）：

| 风格 | stiffness | damping |
| --- | --- | --- |
| Smooth | 1.0 | 1.35（临界阻尼，无回弹） |
| Default | 1.0 | 1.0 |
| Bouncy | 1.1 | 0.70（明显弹性） |
| Snappy | 1.5 | 1.25（快收） |

其他弹簧：悬停 nudge 280/24（-6px 微弹）；音量条 520/46（过阻尼，不允许溢出）；
tab 轮播 250/31，位移 34px、缩放 0.965、`alpha=(1-eased)^1.4`；歌词滚动 190/26。

### 3.2 过渡与细节

- **交叉淡入**：折叠/展开由高度驱动，`expandedAlpha=(h-60)/60`、`collapsedAlpha=(80-h)/30`，
  避免硬切和闪烁；折叠层淡出后展开层才成主角。
- **封面 morph**：折叠 32px 圆 ↔ 展开 64px r16 方，与高度同步、扩张侧限速 0.26s。
- **tab 轮播**：连续 `tabPos` 弹簧，所有可见 tab 按"与当前位置的距离"统一绘制
  （滑移 + 缩小 + 淡出），可随时反向、保留动量，没有进场/退场角色。
- **换歌**：标题/艺人上滑淡入（0.38s，easeInOutCubic），封面翻转，控件图标 0.3s 交叉。
- **karaoke 歌词**：当前行按字符做 accent 渐变擦除，擦除边界带柔和光带。
- **隐私点**：摄像头绿点 / 麦克风橙点，位于胶囊右上角、内容区之外。

### 3.3 帧率与性能

- 目标刷新率自适应（默认跟随显示器，最高 360/500Hz），动画时启用 1ms 定时器，
  弹簧运动用 `DwmFlush()` 对齐垂直同步；空闲 16ms 轮询，隐藏时完全停渲染。
- 帧间隔 `dt` 夹在 1–50ms，弹簧内部再按 0.5ms 子步积分。
- 只有"连续内容"或"弹簧运动"才要每帧绘制；静态画面不请求高精度定时器。

---

## 4. 与本项目（WPF）的对照与可借鉴项

| # | 参考做法 | 当前实现 | 建议 |
| --- | --- | --- | --- |
| 1 | 右侧垂直分页点，活动点拉成胶囊并跟弹簧 | 底部 5 个 tab 图标 | 保留底部图标，同时在右侧加 3–4px 指示点；切换 tab 用弹簧位移而非硬切 |
| 2 | 进度条右侧显示 `-剩余时间`，拖动时加粗 + halo | 右侧显示总时长 | 改为 "-m:ss"，拖动时轨道 4→6px、圆点外扩 + accent 光环 |
| 3 | 展开/折叠按高度交叉淡入 | 直接切 Visibility | 改用 `ProgressRatio` 式的高度驱动 Opacity，避免闪 |
| 4 | 封面折叠圆 → 展开圆角方形 morph | 两套模板（折叠封面 24、展开 38） | 用同一个 Image 元素 + RenderTransform/Clip，随展开进度插值尺寸与圆角 |
| 5 | 8 套主题 token（bg/fg/sec/border） | 固定深色 + `#6C8CFF` 强调 | 先加 3 套（Obsidian/Graphite/Porcelain），把颜色收进 ResourceDictionary |
| 6 | 强调色从封面取色 + 平滑过渡 | 固定强调色 | 用 `BitmapImage` 采样 + 300ms 指数插值，应用到进度条/图标/边框 |
| 7 | 硬件卡 2×3、三段式、颜色随负载 | 6 个进度条平铺 | 改卡片网格（38 高、r9），标签/数值/进度条三层，>75% 琥珀 >90% 红 |
| 8 | 动画风格预设（Smooth/Bouncy/Snappy） | 固定弹簧参数 | 把 `IslandSpringEngine` 的刚度/阻尼做成可配置档位 |
| 9 | 悬停 -6px 微弹 + 尺寸 overshoot | 尺寸弹簧已有 | 增加 nudge 弹簧，悬停/通知时给 6px 位移回弹 |
| 10 | 展开媒体：封面 64、标题/艺人/专辑三段、右上频谱 | 展开卡：封面 38 + 两行文字 + 无频谱 | 重排为左封面 64、右侧三段文字、右上音谱，进度条下移 |
| 11 | 歌词页沉浸式（三行 + karaoke 光带 + 播放器缩 0.90） | 三行 + 高亮 | 加 karaoke 擦除光带、行切换弹簧 |
| 12 | DWM blur/acrylic + 透明填充 | 纯色深底 | 可选接入 Windows 11 Mica/Acrylic（DwmSetWindowAttribute） |

### 优先级建议

- **第一批（视觉收益最大、改动可控）**：#2 进度条剩余时间 + 拖动 halo、#3 高度交叉淡入、
  #5 主题 token（3 套）、#9 悬停微弹。
- **第二批（需要重排布局）**：#10 媒体卡重排（64 封面 / 三行文字 / 音谱）、#7 硬件卡网格、
  #1 分页点。
- **第三批（新交互）**：#4 封面 morph、#6 封面取色、#11 karaoke、#8 动画预设。

---

## 5. 关键源码位置（便于回查）

| 内容 | 文件 | 行号 |
| --- | --- | --- |
| 主题调色板 | `dynamic-island-for-windows.wh.cpp` | 1052–1069 |
| 媒体布局常量 | 同上 | 671–741 |
| 歌词/沉浸布局 | 同上 | 743–776 |
| 空闲条布局 | 同上 | 826–849 |
| 弹簧 Step | 同上 | 1608–1634 |
| 弹簧参数/风格 | 同上 | 23262–23317 |
| 分页点 | 同上 | 15606–15642 |
| tab 轮播 | 同上 | 15459–15590 |
| 材质分层/阴影 | 同上 | 12852–13150 |
| 媒体绘制 | 同上 | 15685–16160 |
| 硬件卡 | 同上 | 14207–14342 |
| 尺寸圆角规则 | 同上 | 15671–15679 |
| 帧率调度 | 同上 | 23620–23712 |

---

## 6. 本项目落地记录（2026-10-24）

参考上述设计后，本项目采用/实现了以下改动（并非照搬，配色、尺寸与结构按本项目调整）：

1. **展开尺寸改为 380×184**，去掉底部 5 图标栏，改为**右侧竖排分页点**（活动项拉长成胶囊）。
   滚轮在展开态切页、折叠态调音量；分页点可点击。
2. **进度条**：右侧改为 `-剩余时间`；拖动时轨道 4→6px、滑块 8→10px，并显示强调色光晕。
   松手后保持选中位置直到播放器确认（4 秒超时）——`IslandViewModel` 的 seek hold。
3. **展开/收起交叉淡入**：`MainWindow.ApplyContentFade` 按岛体高度 smoothstep 计算折叠/展开层透明度，
   替代硬切 Visibility。
4. **悬停轻弹**：弹簧引擎新增 nudge 通道（260/22），悬停时岛体下沉 3px 后回弹；
   因岛体贴顶，方向改为向下。
5. **3 套主题**：曜石 / 石墨 / 瓷白（`ThemeManager`），12 个颜色 token 走 `DynamicResource`，
   右键菜单切换、持久化到 `settings.json` 的 `Theme` 字段。
6. **页面切换弹簧（果冻）**：用一个连续 `tabPosition` 弹簧（stiffness 200 / damping 10，欠阻尼）
   驱动 5 个页面和右侧分页点——页面按距离滑动 28px、缩放 0.925、淡出，分页点高度随位置
   连续变化并随速度拉伸；滚轮/分页点/外部命令统一走 `ActiveTab` 通知，快速连滚保留动量。
7. **歌词识别加固**（参考对方的候选评分思路）：同时接受 `syncedLyrics` 与 `plainLyrics`（纯文本
   按行均分到时长）；查询顺序为「精确 get → 不带 duration 的 get → 清洗标题+主艺人 get →
   搜索 → 标题搜索」，结果按标题/艺人/时长评分；标题去 `(Remastered)`、`feat.`、`- Topic` 等噪音；
   内存 + 磁盘缓存、失败负缓存、并发请求去重；启动时主动补拉首曲歌词（原实现会漏掉第一首）。
8. **图标坞启动修复**：根因是宿主（DSH）设置的 `ELECTRON_RUN_AS_NODE=1` 被子进程继承，导致
   Electron 应用以 Node 模式启动、无窗口秒退。现在 App 启动时清理该变量及 VS Code 相关变量；
   同时加入启动监测——启动前快照进程、4 秒后无进程（或 8 秒内退出）就在图标坞顶部提示。

改动文件：`ViewModels/IslandViewModel.cs`、`Infrastructure/IslandSpringEngine.cs`、
`Infrastructure/ThemeManager.cs`（新增）、`Infrastructure/Converters.cs`、`App.xaml`、
`MainWindow.xaml`、`MainWindow.xaml.cs`、`Core/Services/SettingsService.cs`。

## 7. 第二批落地记录（2026-10-24 下午）

在第一批基础上继续扩展，重点补功能与交互细节：

9. **设置面板**（`SettingsWindow.xaml/.cs`）：外观主题、天气城市（12 个预设 + Open-Meteo
   地理编码搜索 + 手填经纬度）、四个模块开关、歌词延迟补偿、滚轮音量步进、歌名跑马灯、
   开机自启、数据目录 / 日志入口。所有改动即时写入 `settings.json` 并通知各服务刷新；
   窗口与岛体联动（岛是 Topmost 工具窗，设置窗口以 Owner 关系挂在其上）。
10. **音量可视化**：媒体条新增音量按钮与音量条（拖动调音量、单击静音），
    并在岛体下方弹出独立音量浮层（折叠/展开都能用）；滚轮步进可在设置里改（默认 4%）。
11. **通知增强**：`NotificationApps` 把 handlerId / AUMID 解析到运行中进程的 exe，
    取真实应用图标（失败时用应用名首字的彩色占位）；点击通知唤起来源应用
    （AUMID 走 `shell:AppsFolder`，否则启动 exe）；通知页显示后端状态与「清空」按钮；
    点击通知卡直接跳到通知页。
12. **折叠态优化**：歌名跑马灯按「文本自然宽 vs 标题可用宽」测量，只在溢出时启动循环滚动；
    无媒体时折叠宽度自适应为 248，放下时钟 + 日期 + 天气。
13. **媒体卡升级**：封面 34→46、标题 13.5、歌手 10.5；多个播放器时出现「⇄」切换来源
    （`MediaService.SelectNextSession`，手动锁定直到系统切换会话）；无歌词时 6 根频谱柱
    随播放节拍跳动。
14. **沉浸歌词页（第 6 页）**：小封面 + 标题 + 剩余时间、三行大字、底部进度条；
    当前行按「行内进度」生成强调色渐变画刷，实现 karaoke 擦除。
15. **硬件页 sparkline**：CPU / 内存 / GPU / 磁盘各保留 28 个采样点，用 Polyline 画迷你曲线。
16. **图标坞排序与菜单**：拖拽用鼠标捕获 + 命中测试实现（不依赖 OLE 拖放，拖出图标范围
    也能正确落位）；右键图标弹出「打开文件位置 / 从图标坞移除」菜单。
17. **交互修复**：
    - 右键菜单伸出岛体后下半截会被 `WM_NCHITTEST` 判成透明、点击穿透 → 把菜单矩形并入命中区；
    - 子元素（进度条 / 音量条）捕获鼠标时 WPF 会补发 `MouseLeave`，导致岛误收起 →
      自动收起定时器检查 `Island.IsMouseOver` 后再决定；
    - 图标拖拽结束不收起岛；菜单打开时禁止收起；
    - 跑马灯测量时机：折叠宽度在弹簧里连续变化，必须等尺寸稳定后再测可用宽度。
18. **动画打磨**：尺寸弹簧按扩张/收缩给不同刚度阻尼（展开 380/23 略欠阻尼、收起 300/28），
    展开内容额外做 7px 渐进归位，通知卡入场从上方略小落位；分页点 6 个、切页弹簧保留动量。

改动文件：`SettingsWindow.xaml/.cs`（新增）、`ViewModels/NotificationEntryViewModel.cs`（新增）、
`Core/Services/NotificationApps.cs`（新增）、`ViewModels/IslandViewModel.cs`、`Core/Services/MediaService.cs`、
`Core/Services/SettingsService.cs`、`Infrastructure/IslandSpringEngine.cs`、`MainWindow.xaml`、
`MainWindow.xaml.cs`、`README.md`。

## 8. 第三批落地记录（2026-10-24 晚，对标上游 1.4.0 的系统感知能力）

上游 `devcode90/Dynamic-Island-for-Windows` 1.4.0（2026-10-20，Windhawk mod，C++23/Direct2D）的差异化
在系统感知而非媒体。本批把其中适合 WPF 的部分落地：

19. **折叠态歌词 + 胶囊时钟**：`CollapsedText` 优先取当前歌词行，否则歌名；跑马灯按实际可用宽度测量；
   专注计时进行中时钟让位给倒计时。设置里可分别关闭。
20. **隐私点**：`PrivacyService` 轮询 `ConsentStore\microphone|webcam`（含 `NonPackaged`），
   用 `LastUsedTimeStart/Stop` 判断真实占用（start>0 且 stop=0 或 start>stop），胶囊上显示橙/绿呼吸点。
21. **全屏自动隐藏 + 全局快捷键**：`IsForegroundFullscreen` 检查前景窗口矩形覆盖监视器**且无 `WS_MAXIMIZE`**
   （避免把最大化浏览器当全屏）；`Ctrl+Alt+D` 用 `RegisterHotKey` 隐藏/显示，隐藏时窗口不可见、托盘保留。
22. **系统事件浮层**：`SystemEventService` 轮询电源/电池（`GetSystemPowerStatus`）、Caps/Num
   （`GetKeyState`）、勿扰（注册表），USB 由 `WM_DEVICECHANGE` 转发；岛下方浮层统一展示。
23. **蓝牙**：`BluetoothService` 用 WinRT `DeviceWatcher`（`GetDeviceSelectorFromConnectionStatus(Connected)`）
   监听连接/断开，名称关键词分类，BLE GATT 电池服务读电量；启动首轮枚举不打扰。
24. **剪贴板**：`AddClipboardFormatListener` + `WM_CLIPBOARDUPDATE`，文本转 UTF-8、图片转 PNG 字节，
   最近 20 条去重；岛下缩略浮层 + 第 7 页历史 + 点击回写剪贴板。
25. **日历 + 专注**：第 8 页月历（UniformGrid 7 列、今日 accent 圆点、非本月淡化）；
   `FocusTimerService` 支持 25/50/5 分钟，右键菜单启动，折叠胶囊显示倒计时，结束弹系统浮层。
26. **动效**：展开弹簧 355/21、切页 215/11，系统/剪贴板浮层弹入（Y -7 → 0 + 淡入），
   隐私点呼吸，浮层互斥避免叠在一起。

改动文件：`Services/PrivacyService.cs`、`Services/SystemEventService.cs`、`Services/BluetoothService.cs`、
`Services/ClipboardService.cs`、`Services/FocusTimerService.cs`（均新增）、
`ViewModels/ClipboardEntryViewModel.cs`、`ViewModels/CalendarCell.cs`（新增）、
`ViewModels/IslandViewModel.cs`、`Core/Services/SettingsService.cs`、`MainWindow.xaml/.cs`、
`SettingsWindow.xaml/.cs`、`README.md`。
