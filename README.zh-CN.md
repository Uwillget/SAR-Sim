# 结构化航路仿真系统（SAR-Sim）

[![Unity](https://img.shields.io/badge/Unity-2022.3.62f1c1-black?logo=unity)](https://unity.com/)
[![Platform](https://img.shields.io/badge/Platform-Windows%20x64-0078D6?logo=windows)](#运行方式)
[![AirSim](https://img.shields.io/badge/AirSim-1.8.1%20%2B%20UE%204.27-3949AB)](#与-unreal-engine--airsim-的联合仿真)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![Download](https://img.shields.io/badge/Download-v1.0.0-2E7D32?logo=github)](https://github.com/Uwillget/SAR-Sim/releases/latest)
[![Demo](https://img.shields.io/badge/Demo-YouTube-FF0000?logo=youtube)](https://youtu.be/wICTW1oSdc0)

[English](README.md) | **简体中文**

> **可直接运行的 Windows 程序请在 [Releases 页面](https://github.com/Uwillget/SAR-Sim/releases/latest) 下载**——英文与简体中文两个便携版压缩包，无需安装 Unity。本仓库是开发用源码，可执行程序只作为 Release 资产发布。各压缩包的内容见[发行版](#发行版)一节。

> **先看演示：** [SAR-Sim 功能演示](https://youtu.be/wICTW1oSdc0)（YouTube）——英文便携版程序的录屏，展示工作台各项功能的实际操作。在下载或从源码构建之前，这是了解本程序能做什么最快的方式。

结构化航路仿真系统（Structured Air Route Simulation System，SAR-Sim）是面向城市低空公共航路体系的 Unity 研究平台。它把城市几何、多层空中路网、起降场地面设施、等待结构、连接走廊、交通需求编排、微观仿真与结果分析集中在一个可编辑环境中。

本项目提供两套能力等价的使用方式：

- **Unity 编辑器工程**：用于源码开发、场景编排、测试与构建；
- **便携式 Windows 工作台**：面向不需要安装 Unity 的使用者。

两套方式都支持航路与交通流编排、仿真参数编辑、单次与批量实验以及结果表导出。Unity 侧额外提供源码级 Inspector 与构建工具，便携版则用引导式桌面界面操作同一套仿真模型。

Unity 工程还可以把每一架仿真飞行器的位姿实时推送到运行 AirSim 插件的 Unreal Engine 宿主中，使同一批交通流在写实城市模型内被观察，而运动解算始终只由 Unity 完成。该联合仿真仅在 Unity 侧可用，便携版 Windows 程序不包含此功能。它目前是试点验证模块而非成品：单向位姿推送可用，位姿回读、闭环协调控制以及依据 AirSim 反馈调整交通模型均留待后续扩展，详见[联合仿真现状](#联合仿真现状)。

> SAR-Sim 是研究原型，不是通过适航认证的运行安全系统。碰撞、预约、容量与性能结果必须结合所选参数、输入数据质量以及重复实验一起解释。

## 主要功能

### 空域与设施编排

- 编辑中轴路网的节点、边与途径点，支持新增、移动、删除、撤销与属性查看。
- 对高度层执行重命名、复制、删除、排序与垂直位置调整，同时保持平面坐标不变。
- 全局 `Air Network` 显示开关与各图层眼睛控件共同决定显示：两者同时开启时图层才被渲染。
- 用雪花控件冻结图层。冻结层不可编辑、重命名、移动、删除或排序，其他图层也不能跨越它调整顺序。
- 中轴几何变化后重新生成派生车道。当建筑净空不足以容纳两条车道时，该边不生成车道。
- 高亮超过可配置高度阈值的建筑；运行时导入的城市网格同样参与车道净空判定。
- 编辑起降场边界、矩形／圆形／自由多边形停机坪、停机坪旋转角、地面进近节点以及有向进近边。
- 由锚点、等待环高度／半径／角度／节点数以及有向匝道构建等待结构。
- 编辑起降场走廊与车道之间的层间走廊，层间连接采用可编辑的三次贝塞尔几何。
- 四类主要编辑工作区默认使用正交顶视图，同时始终保留自由三维查看。

### 航路与交通流

- 选择并聚焦起点与终点停机坪。
- 经起降场走廊、普通车道与层间走廊，生成最短连通的车道级路径。
- 以有序中轴边序列编排手动路径，首条边被锁定，每次追加都做车道级连通性校验。
- 将中轴边序列解析为可执行车道序列，自动插入相邻变道与必需的层间连接。
- 添加单次任务，或按开始时刻、间隔与飞行器数量定义周期交通流。
- 以待执行任务列表查看、编辑、删除、导入与导出 JSON 或 XML 需求。
- Unity 的 `SimulationManager` Inspector／Scene 视图与便携版桌面界面共用同一套路径解析器与中轴选择规则。

### 仿真与实验控制

- 支持文件驱动的 JSON／XML 需求，或持续生成的随机需求。
- 配置飞行器运动、跟驰距离、变道行为、预约缓冲、等待阈值、信号配时、信息延迟与定位误差。
- 配置随机机队规模、到达后周转时间与最大仿真时长。
- 支持启动、停止、暂停、继续，以及 `0.25x–4x` 时间缩放。
- 可选在碰撞时自动暂停并聚焦。
- 每一段连续的无序飞行器对重叠只计一次碰撞，并导出可审计的碰撞分类明细。车道、进近、匝道、走廊、停机坪与未分类／混合资源各分项完备，残差为零即验证其和等于 `Total Collisions`。
- 显示有界飞行器尾迹，并在总览相机与飞行器跟随相机之间切换。
- 三种互斥的运行时车道配色模式：按作者设定的车道类型着色且不做实时重着色；按实时飞行器负载着色，空载为灰、随真实飞行器数量由绿转红；或按可通行性着色，畅通为绿、阻塞为红。信号／预约的合成哨兵记录不计入负载统计，交叉口信号标记保持为独立可视层。
- 仿真运行过程中可在 `Simulation` 页临时显示或隐藏任一高度层。空域结构编辑、路径与交通流、仿真运行、数据可视化各自保存独立的视图掩码，所有控件都逐字显示图层的作者定义名称，而不是硬编码的"下层／上层"。
- 基于共享参数目录定义任意数量的参数扫描变量，自动运行笛卡尔网格并增量写入 CSV 实验表。
- 批量实验停止或完成时恢复扫描前的参数配置。

### 分析与可视化

- 归档每次任务的完整轨迹，包括随机需求复用飞行器产生的重复行程。
- 选择并叠加显示多条轨迹。
- 显示三维交通热力图、路径预览与实验统计；生成可出版的分页 Python 交通图，每个所选高度层输出一张满幅方形路网总览，随后为每个归档起降场输出一张独立成帧的满幅总览，不绘制层间连接线干扰。
- 为单次运行与参数扫描批量实验保留场景级仿真历史，而不是覆盖上一次实验的结果。
- 在数据可视化页回收单条历史记录或整批实验。记录被移动到当前场景的 `SimulationHistory/.Trash/` 以便手动恢复；不允许单独删除某个批次成员而使父级汇总不一致。
- 将某次具体历史运行重新载入三维车道通行／资源预约热力图与轨迹视图，查看其归档的 `MetricsTable.csv`，或以双栏分页方式逐条查看批次父级的原始 `BatchExperimentRunSummaryTable.csv`。
- 选择若干已完成运行（或一个批次父级）进行对比。输出指标（Y）按类别折叠分组。高分辨率 Python 柱状对比图支持多个 Y 指标，直接以所选实验记录为 X 轴，不需要敏感性输入参数。参数响应图接受一至三个输入参数（X）与多个 Y 指标：一个 X 输出响应曲线，两个 X 输出方形标注热力图，三个 X 按第三个参数的实测水平在三维深度上堆叠半透明方形热力图平面。仅当仿真时长差异显著时，累计型指标才按秒归一化。
- 对显示的图表与结果表执行缩放、平移、重置与导出。
- 独立开关城市、路网、进近、等待、设施、起降场走廊、层间走廊与信号图层。
- 导出指标、占用记录、仿真记录与轨迹文件。

### 场景与显示管理

- 每个场景占据独立标签页，拥有各自的城市场景、路网与起降场数据。
- 每个场景的仿真历史保存在该场景目录内；切换活动标签页会同时切换可编辑拓扑与可用历史列表。
- 新建、复制、重命名、保存、关闭与重新打开场景。
- 导入 `.sarcity`、`.obj` 以及受支持的 `.dxf` 城市几何，并在当前视图中定位。
- 使用方位立方体切换透视／正交投影与六个正交方向。
- 支持可调整窗口与无边框全屏模式。
- 通过界面上的 `隐藏界面` 按钮或 `F10` 隐藏整个运行时工作台。右上角保留一个小的 `显示界面` 按钮，因此可以无遮挡地使用 Unity 的 Scene／Game 视图。

### 与 Unreal Engine / AirSim 的联合仿真（试点模块）

- 把每一架仿真飞行器镜像到运行 AirSim 1.8.1 的 Unreal Engine 4.27 宿主，Unity 作为唯一运动主控。
- 以 30 Hz 通过紧凑的二进制 UDP 帧推送位姿，数十架飞行器的场景也无需 Unity 逐机发起 RPC 调用。
- 自动为每个 Unity 飞行器创建对应的 AirSim 载具，无需为每次任务手工修改 `settings.json`。
- 由桥接进程驱动 Unreal 相机：跟随所选飞行器并可调距离与高度，或把控制权交还 Unreal 自带的手动模式相机按键。
- 按共享且带单元测试的约定完成 Unity ENU（米）到 AirSim NED（米）的转换与四元数映射，使 Unreal 几何与 Unity 几何在同一 WGS84 原点下对齐。
- 逐帧写出诊断 CSV（`bridge_diag.csv`），记录 Unity 原始位姿、转换后的 NED 位姿以及期望与平滑后的相机位姿，便于离线抖动分析。

本模块是试点集成，不是成品：当前只有单向推送，AirSim 状态回读与基于回读的闭环协调均在本次发布的范围之外。

## 运行方式

两套等价工作方式的图解引导见 [快速入门（英文图解）](OperationGuide/QuickStart/README.md)。

### 便携式 Windows 程序

从 [**Releases 页面**](https://github.com/Uwillget/SAR-Sim/releases/latest) 下载完整的 Windows x64 压缩包。解压后必须保持可执行文件与其 `_Data` 目录、以及随包提供的全部目录在一起。

YouTube 上的[功能演示](https://youtu.be/wICTW1oSdc0)就是在本压缩包的英文版上录制的，因此演示内容与解压后实际看到的一致。

本地构建命令会由同一个已验证的 Windows 播放器产出两个干净的发行目录：

```text
Builds/Releases/en-US/
Builds/Releases/zh-CN/
```

每个目录内都有一个紧邻可执行文件的 `SAR.locale.json`：英文包选择 `en-US`，中文包选择 `zh-CN`。两种情况下源码、场景对象名与 Inspector 标签均保持英文。

不要只复制可执行文件。便携程序在其所在目录读写自有数据，因此解压目录必须可写。

### Unity 编辑器工程

1. 用 Unity Hub 打开仓库根目录。
2. 使用锁定版本 `2022.3.62f1c1`。
3. 打开 `Assets/Resources/Scenes/1.unity`。
4. 在 Hierarchy 中选择相应管理器对象，通过 Inspector 与 Scene 视图操作。
5. 选择 `SimulationManager` 编排航路／交通流并控制仿真；仅在需要执行时才进入 Play 模式。

便携版运行时工作台在 Unity Play 模式下默认关闭。需要在编辑器中复现 Windows 程序的精确行为时，可在 `SimulationManager` Inspector 的 `Editor Play Mode Runtime UI` 中开启。运行中关闭会销毁生成的 Canvas、EventSystem、导航、编辑、路径、分析与文件拖放对象，而不只是把它们隐藏。该偏好仅作用于编辑器并会持久保存，不影响 Windows 构建产物的界面。

### 仓库内的场景

仓库只收录一个 Unity 场景 `Assets/Resources/Scenes/1.unity`，它就是论文实验所用的研究场景：新街口区域，两层高度层（75 米与 125 米）、102 个中轴节点、160 条中轴边、2 条层间走廊、7 个起降场、126 个停机坪，城市模型含 978 栋建筑。该场景自包含，也是唯一启用的构建场景，因此便携版程序与论文实验使用完全一致的数据。

作者工作副本里另有两个场景，均有意不随仓库发布：

| 场景 | 未收录原因 |
|---|---|
| `SampleScene.unity` | 早期单瓦片 CADMapper 试验，已被 `1.unity` 取代，仅作开发草稿保留。 |
| `ShenzhenSKPWhitebox.unity` | 用于联合仿真演示的 628 MB 制作场景，体量不适合 Git，改为单独发布，见 [EXTERNAL_ASSETS.md](EXTERNAL_ASSETS.md)。 |

如需在深圳演示场景上运行桥接，请先下载该场景。改用 `1.unity` 运行桥接可以验证同一条代码路径，但 Unreal 地图与新街口几何并不对应。

## Unity 编辑器工作流

主要的 Unity 工作流使用各管理器 Inspector 与 Scene 视图。`Tools > SAR Workbench` 是一个可选的概览／快捷窗口，含五个页签：

1. `Overview` —— 当前拓扑、设施与需求计数。
2. `Simulation` —— 需求导入、回放控制、完整 `SimulationConfig` 的入口以及批量控制。
3. `Network` —— 跳转到详细路网 Inspector、车道生成、Schema 校验与导出。
4. `Facilities` —— 跳转到详细的起降场／进近／等待结构 Inspector 与导出。
5. `Build` —— 打包数据检查与 Windows x64 构建。

航路与需求编排有意放在 `SimulationManager` Inspector 中：

1. 展开 `Routes & Traffic Flow (Inspector + Scene view)` 并刷新当前拓扑。
2. 选择起点与终点停机坪。
3. 生成自动最短路径，或选择 `Manual: click centerlines`。
4. 手动模式下直接在 Scene 视图中点击中轴线。被选中的路径以红／橙色高亮并按通行顺序编号；首条边被锁定，再次点击已选边会把路径回滚到该边。
5. 确认路径后，添加单次任务或周期交通流。待执行任务可在同一 Inspector 中编辑、删除、导入与导出。

建议顺序：

```text
编辑拓扑
  -> 生成派生车道
  -> 校验并导出拓扑
  -> 编排／导入航路与交通流
  -> 配置参数
  -> 运行单次或批量实验
  -> 查看输出
  -> 校验并构建
```

在编辑拓扑或应用新参数集之前请先停止仿真。

## 与 Unreal Engine / AirSim 的联合仿真

Unity 始终是运动主控。微观仿真产生的每一帧飞行器位姿都转发给一个小型 Python 进程，由它传送对应的 AirSim 载具与 Unreal 相机。AirSim 不承担任何飞行动力学、碰撞响应或感知计算：Unreal 场景只是 Unity 已解算完毕的交通流的高保真观察器。

```text
Unity（编辑器）                              Python 桥接                      Unreal Engine 4.27
AircraftAgent / FlightController            airsim_bridge.joint_bridge       AirSim 1.8.1 插件
        |                                           |                                |
        | ITS1 二进制帧，30 Hz                      | simSetVehiclePose              |
        +------ UDP 127.0.0.1:49001 -------------->|------------------------------->| 飞行器 Pawn
        |                                           | simSetObjectPose               |
        |                                           +------------------------------->| ExternalCamera
```

### 联合仿真现状

本模块是随源码发布的试点验证，便于他人查看与继续扩展。使用或引用时请注意它与成品之间的差距：

| 能力 | 状态 |
|---|---|
| Unity → AirSim 位姿推送（每架仿真飞行器一条） | 可用 |
| 按 Unity 飞行器自动创建 AirSim 载具 | 可用 |
| 桥接驱动的跟随相机与键盘控制 | 可用 |
| ENU → NED 转换与四元数映射（含单元测试） | 可用 |
| 诊断 CSV 记录 | 可用，部分列预留（见下） |
| AirSim → Unity 位姿回读并驱动仿真 | **未实现** |
| 基于 AirSim 状态的闭环协调、冲突消解或控制 | **未实现** |
| Unreal 侧碰撞或感知结果回馈交通模型 | **未实现** |

`bridge_diag.csv` 中的回读列目前仅为占位并固定写 0。任何依赖 AirSim 向 Unity 回传状态的功能——反馈控制、传感器驱动行为、多宿主协同——均有意留待后续扩展。请把本模块视为可视化桥接，而不是两个仿真器相互耦合的联合仿真。

桥接本身与场景无关，但开发时使用的是深圳白膜场景，该场景体量过大无法入库。获取方式与各部件关系见 [EXTERNAL_ASSETS.md](EXTERNAL_ASSETS.md)。

### 传输内容

每帧一个 UDP 数据报，承载全部飞行器：

| 字段 | 编码 |
|---|---|
| 帧头 | `ITS1` 魔数、1 字节版本、`uint16` 飞行器数量、`uint32` 序号 |
| 单机记录 | 1 字节名称长度、UTF-8 名称，随后十个小端 `float32` |
| 数值 | Unity 位置 `x y z`、Unity 旋转 `x y z w`、Unity 速度 `x y z` |

Unity 侧的飞行器名称在发送前会被过滤为 `A-Z a-z 0-9 _`，因为 AirSim 载具名必须是合法标识符，例如 `M-235753-0001` 到达时是 `M2357530001`。桥接进程以过滤后的名称作为载具键。

### 前置条件

| 组件 | 本文使用的版本 | 说明 |
|---|---|---|
| Unity | 2022.3.62f1c1 | 工程版本已锁定 |
| Unreal Engine | 4.27 | 任意已编译 AirSim 插件的工程 |
| AirSim | 1.8.1 | 插件与配套 Python 客户端 |
| Python | 3.10 或更高 | 需安装 `airsim` 包：`pip install airsim==1.8.1` |

### 配置 AirSim 宿主

1. 把 `JointSimulation/settings.example.json` 复制到运行 Unreal 那台机器的 `Documents/AirSim/settings.json`。
2. 保留 `"ViewMode": "Manual"`。在其他任何视图模式下 `ACameraDirector::Tick` 会持续把相机插值向飞行器而永不收敛，表现就是画面抖动。`Manual` 模式下导演只施加键盘增量，无人操作时为零，因此相机精确停留在桥接进程指定的位置。
3. 保持该文件中的 `OriginGeopoint` 不变。它必须与 `airsim_bridge/coords.py` 以及 Unity 场景中编译的原点一致，否则两个世界会整体错位。
4. `Vehicles` 里的出生坐标同样不要随意改动。`simSetVehiclePose` 以出生点为锚，桥接进程不做补偿，出生点移动多少载具就偏移多少。示例把 `ChaseCam` 放在上方 2 米是有意的。
5. 在 Unity 之前或之后启动 AirSim 工程（PIE 或打包版本皆可）——桥接进程会等待 RPC 端口，也能自行拉起 Unreal。

### 配置 Unity 侧

1. 选择 `SimulationManager` 并展开 `Play Control`。
2. 勾选 `Start Joint AirSim Simulation`。
3. 若桥接目录或解释器不在 `PATH` 中，请在 `AirSimJointBridge` 组件中填写。也可以改用环境变量，这在多台机器路径不一致时更方便：

| 变量 | 用途 | 默认值 |
|---|---|---|
| `SAR_BRIDGE_PYTHON` | 已安装 `airsim` 包的 Python 解释器 | 从 `PATH` 解析 `python.exe` |
| `SAR_BRIDGE_WORKDIR` | 包含 `airsim_bridge` 包的目录 | 当前目录 |
| `UE4EDITOR_EXE` | `UE4Editor.exe`，仅 `--launch-airsim` 使用 | UE 4.27 标准安装路径 |
| `UE4_PROJECT` | 要启动的 `.uproject`，仅 `--launch-airsim` 使用 | `SZ_TestArea.uproject` |
| `SAR_FOLLOW_CAMERA` | 相机跟随状态的保存位置 | `Documents/AirSim/follow_camera.json` |
| `SAR_UE_IMPORT_DIR` | `ITSAC/Export *` 网格导出器的输出目录 | `<工程根>/ue_import` |

4. 进入 Play 模式并启动仿真。桥接进程随运行启动而非随 Play 模式启动，并在运行停止时被终止。

### 手动运行桥接进程

桥接进程是普通的 Python 入口，因此可以单独观察其控制台，或把它指向不是由 Unity 启动的 AirSim：

```text
cd JointSimulation
python -m airsim_bridge.joint_bridge --launch-airsim
```

去掉 `--launch-airsim` 即连接到已在运行的 AirSim。若 49001 端口被占用，可用 `--bind` 与 `--port` 改变 UDP 端点。

### 相机操作

桥接进程注册了全局热键，在 Unreal 窗口获得焦点时生效：

| 按键 | 作用 |
|---|---|
| `H` | 在跟随模式与自由飞行之间切换 |
| `N` | 循环切换被跟随的飞行器 |
| `J` / `L` | 相机拉近／拉远 |
| `U` / `O` | 相机升高／降低 |

自由飞行模式下桥接进程不再干预相机，改由 Unreal 自带的 `Manual` 模式按键接管：方向键平移，`PageUp`／`PageDown` 改变高度，`A`／`D` 偏航，`W`／`S` 俯仰，`Q`／`E` 横滚，`Shift`／`Ctrl` 改变速度。AirSim 自带的相机热键（`F`、`B`、`/`、`M`、`K`、`-`、`I`）会离开 `Manual` 视图模式并重新引入抖动，之后按 `H` 即可交还桥接进程控制。

### 需要注意的行为

- 有意从不启用 AirSim 的 API 控制。`SimpleFlight` 在外部目标停止到达时会进入悬停模式，随后在每个物理步施加作用力，与传送相互冲突。桥接进程改为先传送，再把 AirSim 运动学状态清零，因此帧与帧之间不会有积累量。
- 载具通过 `simAddVehicle` 按需创建，一架 Unity 飞行器对应一个 AirSim 载具。因此在仿真过程中新增飞行器的运行也能正常工作。
- 载具传送走 `simSetVehiclePose`（以 Pawn 出生点为锚），相机走世界 NED 坐标下的 `simSetObjectPose`。桥接进程不做偏移补偿，因此出生点离开 Unreal 原点多远，载具就被位移多远；示例设置把 `ChaseCam` 放在上方 2 米，这是有意为之。
- `bridge_diag.csv` 写在桥接源码所在目录，每五帧采样一次，记录 Unity 原始位姿、转换后的 NED 位姿以及期望与平滑后的相机位姿；最后三列预留给 set/get 差值，目前固定写为 0。
- 联合仿真仅在编辑器可用，且相关代码用 `#if UNITY_EDITOR` 完全排除在 Windows 播放器之外。便携版没有该开关，因为它既不携带 Python 解释器也没有 Unreal 宿主。

### 桥接测试

两个独立脚本覆盖不需要 Unreal 的部分：

```text
cd JointSimulation
python airsim_bridge/test_coords.py      # ENU/NED 往返、四元数转换、locker 表
python airsim_bridge/test_pipeline.py    # 帧解析、平滑器收敛、抖动检测
```

`test_pipeline.py` 不需要 Unity、AirSim 或网络套接字。完整运行目前输出两条 `PASS` 与一条 `WARN`：平滑器把 ±5 毫米的飞行器噪声放大成约 17 毫米的相机位置抖动，超出 15 毫米的阈值。该警告是跟随相机平滑系数的已知特性，不影响仿真结果。桥接层面的完整参考见源码仓库中的 `JointSimulation/README.md`；该目录不属于 Windows 压缩包。

## 便携版工作台工作流

便携版界面包含四个模块：

- `Airspace Structure`（空域结构编辑）—— 场景数据、路网图层、起降场、等待结构与两类走廊。
- `Routes & Traffic Flow`（路径与交通流）—— 路径设计、任务／交通流编排、队列编辑与导入导出。
- `Simulation`（仿真运行）—— 需求来源、全部运行参数、批量网格、回放、尾迹、碰撞暂停与飞行器跟随。
- `Data Visualization`（数据可视化）—— 当前与历史轨迹／热力图、归档指标、多运行对比、参数敏感性与输出访问。

单次运行或参数扫描期间，工作台会停留在 `Simulation` 并锁定其他模块、场景标签、新建场景与场景数据替换。该运行时互锁防止编排、归档分析或场景状态替换仿真拥有的数据。`Runtime Lane Coloring` 提供三种互斥选择：车道类型配色、实时飞行器负载（空载为灰，随后按真实飞行器数量由绿转红）、可通行性（畅通为绿／阻塞为红）。信号与预约哨兵不计作飞行器，信号标记仍然独立。数据可视化中的归档热力图是独立状态，离开该模块时会被清除，因此不会影响后续仿真。

在便携版中，使用对齐的 `隐藏界面` 控件或 `F10` 获得无遮挡的仿真视图。右上角标题行 `设置` 旁保留一个紧凑的恢复控件。这是临时显示开关，不会停止仿真或删除当前工作区。

### 仿真历史与对比

每次运行都会在活动场景下生成带版本号的清单，而不是写入全局历史列表。`数据可视化` 页可以选择一条具体运行做历史三维热力图／轨迹回放或 `MetricsTable` 查看，也可以选择批次父级查看其原始汇总表或做统计分析。展开批次会显示每个成员及其因子取值。批次父级没有唯一的三维状态，不能载入三维视图；应展开后选择恰好一个具体成员。载入另一条单次运行或成员时，只有在其全部产物就绪后才提交，并原子地替换此前的热力图、轨迹与轨迹列表，从而不会混合两次运行的结果。归档在后台流式进行，只保留已完成任务的紧凑车道进入统计与降采样轨迹。限时运行结束时仍在飞行的飞行器不计入。归档拓扑仅用于可视化，永远不会替换当前正在编辑的场景。

对某条具体运行，`Traffic-Heatmap Altitude Layers` 独立控制归档三维叠加层与 Python 二维交通图。Python 渲染器产出一组有序页面：每个可见的作者定义高度层一张满幅方形路网总览，随后为每个归档起降场一张满幅局部总览。每个起降场页使用带留白的方形边界，覆盖其进近边、等待匝道与可预约的设施—路网走廊。节点、停机坪轮廓、设施边界与层间连接曲线被有意略去。由于设施走廊参与预约窗口，它们仍保留在图中，并按三维场景相同的、与高度相关的三次贝塞尔曲线重建。使用 `Previous Figure` 与 `Next Figure` 翻页，缩放、平移与导出作用于当前页。路网页使用车道通行计数，起降场页使用地面路径预约时长。选择另一条具体运行后，旧热力图、轨迹与已完成轨迹选择器会在新归档就绪后被自动替换。

可用图表模式由具有有效归档产物的已完成运行推导而来。已停止、中断、取消或失败的运行仍可用于诊断性三维／表格查看，但不进入对比图。输出指标（Y）必须有限且在所选运行之间共享，选择器把它们归入可折叠类别。柱状对比不需要输入参数，直接以所选实验记录为 X 轴；多个 Y 指标生成各自独立缩放并带标签的面板。参数响应要求输入参数（X）为数值型且确实变化：选择一个 X 输出响应曲线，两个 X 输出大型方形标注热力图，三个 X 则按第三个 X 参数的实测水平，在真正的三维堆叠中放置方形半透明标注热力图平面。平面保留实测因子组合，不暗示未测取值之间的插值。多个 Y 指标生成各自独立且排布一致的面板组。Python 图以高分辨率渲染，图表与表格视图都提供缩放、平移、重置与导出。批次汇总每页一条实验、双栏呈现，支持上一页／下一页与直接跳页。完全重复的参数组合只使用真实均值、样本标准差与样本数。未被选作 X 轴的变化参数保持为明确的固定上下文，而不是被静默平均；图表被表述为实测参数网格上的经验响应，而不是正式的 Sobol／Morris 全局敏感性指数。

## 基础场景操作

| 输入 | 作用 |
|---|---|
| 鼠标滚轮 | 缩放 |
| 中键拖动 | 平移 |
| 右键拖动 | 环绕并进入自由透视 |
| `F` | 把视图中心设为光标所在水平面位置 |
| 方位立方体中心 | 切换透视／正交投影 |
| 方位立方体锥体 | 选择六个正交方向之一 |
| `Tab` / `Shift+Tab` | 向后／向前切换被跟随的飞行器 |
| `F10` | 隐藏或恢复运行时工作台 |

## 便携版数据布局

Windows 播放器把自有文件存放在可执行文件旁边：

```text
Structured-Air-Route-Simulator.exe
SARScenes/
  scenes.json
  default/
    AxisNetwork.json
    VertiportData.json
    CityModel.sarcity
    SimulationHistory/
      run-<UTC 时间戳>-<id>/
        manifest.json
        MetricsTable.csv
        SimulationRecording.json.gz
        AxisNetworkSnapshot.json
        FullNetworkSnapshot.json
        VertiportsSnapshot.json
        OccupancyLog.json
      batch-<UTC 时间戳>-<id>/
        manifest.json
        BatchExperimentRunSummaryTable.csv
Network/
SARData/
Output/
  MetricsTable.csv
  BatchExperimentRunSummaryTable.csv
  OccupancyLog.json
  SimulationRecording.json
  RouteWorkspace.json
  Trajectories/
ImportCache/
Settings/
  display.json
```

每个 `manifest.json` 记录可移植的相对文件名、场景标识、运行状态与时长、参数、指标以及批次／成员关系。记录以 gzip 压缩的 JSON 归档，载入时透明解压。`Output/` 下的普通文件仍是最近一次运行的导出面，而 `SARScenes/<场景 id>/SimulationHistory/` 中的副本是与该场景标签关联的持久记录。

更早的、基于 LocalLow 目录的发行版数据会被一次性复制过来，且不删除旧源；已存在的便携文件优先。在 Unity 编辑器中，运行时场景数据继续使用 `persistentDataPath`，使测试与开发不会修改分发的 Windows 包。

可移植路径前缀：

| 前缀 | 解析为 |
|---|---|
| `scene://` | 当前场景目录 |
| `app://` | 工程／应用根目录 |
| `user://` | 运行时数据根目录；在 Windows 播放器中等于应用根目录 |
| `streaming://` | Unity `StreamingAssets` 目录 |

## 输入格式

### 场景文件

| 文件 | 内容 |
|---|---|
| `AxisNetwork.json` | 多层中轴节点、边、途径点与图层元数据 |
| `VertiportData.json` | 起降场、停机坪、进近、等待结构与设施走廊 |
| `CityModel.sarcity` | 可移植的烘焙城市网格与基础材质颜色 |
| `FullNetwork.json` | 由中轴几何与建筑净空生成的派生车道级路网 |

### 城市模型

运行时导入支持：

- `.sarcity` —— 推荐的可移植城市包；
- `.obj` —— 三角网格；
- `.dxf` —— 三维面／实体，或带非零厚度的闭合 `LWPOLYLINE` 实体。

纯二维 DXF 轮廓不会生成建筑体量。在 Unity 编辑器中，`Tools > SAR > CADMapper SketchUp Converter` 可把受支持的 SketchUp 导入转换为 `.sarcity`。仓库不包含需要授权的 SketchUp C SDK 运行时转换器，因此分发时建议使用 `.sarcity` 或 `.obj`。

### 航路需求

JSON 与 XML 文件可包含单次行程、命名航路与周期性 `flow` 记录。一个任务可以只给出端点以自动寻路，也可以给出显式的有向车道序列。无效记录会逐条报告，不影响其他有效记录。

示例：

- `Assets/StreamingAssets/SARData/RouteFile.example.json`
- `Assets/StreamingAssets/SARData/RouteFile.example.xml`

详见 [航路文件格式](OperationGuide/RouteFileFormat.md) 与 [交通控制](OperationGuide/TrafficControl.md)。

## 仓库结构

```text
Assets/
|- DataBase/                默认拓扑输入与 SimulationConfig
|- DataStructrue/           静态／动态数据结构（目录名为历史拼写）
|- Editor/                  Inspector、SAR Workbench、导出／转换与构建工具
|- Resources/               主场景、预制体、材质与内置城市资源
|- Scripts/
|  |- Network/              几何、连通性、变道、多层与信号工具
|  |- Reservation/          预约窗口与停机坪占用
|  |- Routes/               共享编排、JSON/XML 导入导出与显式路径解析
|  |- RuntimeUI/            桌面工作台、场景工具、可视化与可移植路径
|  |- Traffic/              跟驰、进入安全与设施节点间隔
|  |- AirSimJointBridge.cs  Unity 侧联合仿真（UDP 位姿流）
|  |- AirSimKinematicPublisher.cs  独立运动学从机的极简 ENU 发布器
|  |- SimulationManager.cs  仿真生命周期、需求、碰撞与指标
|  |- PathPlanner.cs        设施预约与车道级路径搜索
|  `- AircraftAgent.cs      飞行器任务状态机
|- StreamingAssets/
|  |- SARAnalytics/          Python 绘图 worker 源码、构建输入与打包产物
|  |- SARData/              打包的航路示例
|  `- SARLocalization/      运行时界面词条（zh-CN）
`- Tests/Editor/            EditMode 自动化测试

JointSimulation/
|- airsim_bridge/           Python 桥接：UDP 接收、AirSim 位姿写入、相机驱动
|- README.md                桥接层参考：协议、坐标、热键、诊断与排错
`- settings.example.json    AirSim 设置模板（Manual 视图模式、共享原点）

UnrealHost/SZ_TestArea/     联合仿真用的 Unreal Engine 4.27 宿主工程
                            （需自行安装 AirSim 1.8.1 插件，仓库不附带）

OperationGuide/             快速入门、格式、交通规则与构建指南
Packages/                   可复现的 Unity 包依赖
ProjectSettings/            Unity 2022.3 工程配置
Tools/SARPlotWorker/        绘图 worker 构建脚本、许可收集器与测试
```

构建产物、实验输出、与输出一同保存的绘图／可视化脚本、导入的城市缓存以及本地 QA 截图有意排除在 Git 之外。Windows 便携版 ZIP 应作为 GitHub Release 资产发布，而不是把构建产物提交到源码分支。

### 在 Windows 上克隆

`Assets/StreamingAssets/SARAnalytics/Worker/THIRD_PARTY_LICENSES/` 下收集的第三方许可文本中有 21 条路径超过 260 字符的旧限制。克隆前请先启用长路径，否则这些许可文件会被跳过：

```text
git config --global core.longpaths true
```

同时建议在 Windows 注册表中启用 `LongPathsEnabled`，或把仓库克隆到较短的根目录（如 `C:\src\`）。这些许可文本是必需的：构建校验会检查它们，`THIRD_PARTY_NOTICES.md` 也是据此做再分发合规声明。

另有两项体量很大的编辑素材不入库，因为它们是本机生成的。两者均单独发布，见 [EXTERNAL_ASSETS.md](EXTERNAL_ASSETS.md)：

- `Assets/Resources/Scenes/ShenzhenSKPWhitebox.unity` —— 约 628 MB 的编辑用场景，由把深圳 SketchUp／OBJ 城市模型导入 Unity 并添加碰撞体得到。使用它的 `ITSAC >` 菜单项（`Add Whitebox Colliders`、`Split Whitebox Into Buildings`、`Build Shenzhen Demo Airspace`、`Export Whitebox To UE`）需要在本地准备该场景；网格导出器写入由 `SAR_UE_IMPORT_DIR` 指定的目录。
- `Assets/ImportedCityModels/` —— 导入的城市网格缓存（`ITSAC > Import Hull OSM Whitebox` 在此写入 `OSM_Hull`，`Split Whitebox Into Buildings` 写入 `ShenzhenSplit`）。

打开、运行或构建所分发的程序都不需要这两项：分发场景是 `Assets/Resources/Scenes/1.unity`（新街口研究场景），它自包含且是唯一启用的构建场景。

但复现联合仿真仍然需要深圳场景，因为 Unreal 宿主工程正是基于该城市构建的。下载地址与目录布局记录在 `EXTERNAL_ASSETS.md` 中。

## 构建与验证

退出 Play 模式后运行：

```text
Tools > SAR > Validate Build
Tools > SAR > Build Windows x64
```

SAR 构建命令只重建受控的 `Builds/Windows` 暂存目录，嵌入当前航路示例与运行时本地化词条，校验绘图 worker 与第三方许可，然后在 `Builds/Releases` 下发布干净的英文与中文副本。发布校验器会拒绝泄漏的运行时数据，并确认两个包使用完全相同的播放器二进制。

如果打包的航路示例有改动，请先运行：

```text
Tools > SAR > Sync Packaged Route Data
```

自动化测试位于 `Assets/Tests/Editor`，发布前请从 Unity Test Runner 运行完整 EditMode 测试集。当前测试集覆盖航路文件、显式路径、多层连通性、几何、预约行为、随机需求生命周期、运行时编辑、场景存储、分析、参数校验、批量网格启动以及运行时界面布局。

### 重建绘图 worker

`Assets/StreamingAssets/SARAnalytics/Worker/SarSensitivityRenderer-win-x64-v1.zip` 是对同目录两个脚本的 PyInstaller 构建。构建校验会把它与 `Worker/manifest.json` 中的 SHA-256 比对，因此任一脚本改动后都必须重建：

```text
Tools/SARPlotWorker/build_worker.ps1 -PythonExecutable <path-to-python.exe>
```

脚本会冻结 `SensitivityAnalysis.py` 与 `TrafficHeatmapAnalysis.py`，把第三方许可收集到 `THIRD_PARTY_LICENSES/`，写出压缩包并更新清单哈希。worker 自身的测试位于 `Tools/SARPlotWorker/tests/`。`build_worker.ps1` 会重新生成 `SarSensitivityRenderer.spec`，该文件是构建产物，不纳入版本控制。

## 文档

- [英文便携版功能演示（YouTube）](https://youtu.be/wICTW1oSdc0)
- [快速入门：Unity 编辑器与 Windows 工作流（英文图解）](OperationGuide/QuickStart/README.md)
- [工作台与 Windows 构建指南](OperationGuide/WorkbenchAndBuild.md)
- [航路文件格式](OperationGuide/RouteFileFormat.md)
- [交通控制、变道与死锁规则](OperationGuide/TrafficControl.md)
- [外置素材与深圳场景](EXTERNAL_ASSETS.md)
- [English README](README.md)

## 发行版

便携式 Windows 程序以 ZIP 压缩包的形式发布，而不是作为源码：

| 压缩包 | 内容 | 选择器 |
|---|---|---|
| `SAR-Sim-Windows-x64-en-US-<version>.zip` | Windows 程序，English | `SAR.locale.json` → `en-US` |
| `SAR-Sim-Windows-x64-zh-CN-<version>.zip` | Windows 程序，简体中文 | `SAR.locale.json` → `zh-CN` |
| `SAR-Sim-UnrealHost-SZ_TestArea-<version>.zip` | 联合仿真用的 Unreal 宿主工程（不含 AirSim 插件） | —— |

两个 Windows 压缩包包含完全相同的播放器二进制，只有本地化选择器与随附文档不同。两种情况下 Unity 源码、场景对象名与 Inspector 标签都保持英文。每个压缩包的 `OperationGuide/` 下都已包含快速入门与三份参考指南，并同时附带 `README.md` 与 `README.zh-CN.md`。

把压缩包解压到可写目录，并从该目录运行 `Structured-Air-Route-Simulator.exe`。不要把可执行文件与它的 `_Data` 目录、`MonoBleedingEdge/` 或 `UnityPlayer.dll` 分离。程序还会在可执行文件旁读写 `SARScenes/`、`Network/`、`Output/`、`SARData/`、`Settings/` 与 `ImportCache/`；这些目录在缺失时会在首次启动时自动创建，因此解压目录必须可写。

每个压缩包都是干净构建：只含一个场景（`SARScenes/default`，即新街口研究场景），不含任何仿真历史、`Output/` 结果或遗留测试场景。若运行程序后 `Output/` 下出现结果表或轨迹，那是你自己那一次运行的产物，不是随包数据。

联合仿真不包含在上述 Windows 压缩包中。它需要 Unity 编辑器工程、Unreal Engine、AirSim 与 Python 解释器，因此改在源工程章节说明；Unreal 宿主工程作为上表第三个压缩包单独发布。

### 发布清单

从本仓库发布一个版本的步骤：

1. 确认源码树干净，并在 Unity 编辑器中通过 `Tools > SAR > Validate Build`。
2. 运行 `Tools > SAR > Build Windows x64`，它会写出 `Builds/Releases/en-US/` 与 `Builds/Releases/zh-CN/`，各自带有 `SAR.locale.json` 选择器。
3. 检查每个发行目录中仍然只有初始的 `default` 场景：`SARScenes/default/{AxisNetwork.json,VertiportData.json,CityModel.sarcity}`，没有 `SimulationHistory/`，且 `Output/` 为空。
4. 把两个目录分别压成 `SAR-Sim-Windows-x64-en-US-<version>.zip` 与 `SAR-Sim-Windows-x64-zh-CN-<version>.zip`。
5. 把 `UnrealHost/SZ_TestArea/` 压成 `SAR-Sim-UnrealHost-SZ_TestArea-<version>.zip`。不要放入 AirSim 插件，使用者自行安装 1.8.1。
6. 确认 [EXTERNAL_ASSETS.md](EXTERNAL_ASSETS.md) 中链接的 Hugging Face dataset 仍然可访问，且包含深圳场景与导入的城市缓存。该数据集已发布，只有这两项素材变更时才需重新上传。
7. 用标签 `v<version>` 创建 GitHub Release，附上三个压缩包，并粘贴下面的发布说明。

建议的发布说明正文：

```text
SAR-Sim <version>

Windows 便携版（无需安装 Unity）：
  SAR-Sim-Windows-x64-en-US-<version>.zip   英文界面
  SAR-Sim-Windows-x64-zh-CN-<version>.zip   简体中文界面

两个包使用完全相同的播放器二进制，仅 SAR.locale.json 与随附文档不同。
每个包只携带一个场景——新街口研究场景（两层高度层、102 节点、160 条边、
7 个起降场、978 栋建筑），不含仿真历史与既有结果。

联合仿真（试点）可选用的 Unreal 宿主：
  SAR-Sim-UnrealHost-SZ_TestArea-<version>.zip
需要 Unreal Engine 4.27 与 AirSim 1.8.1 插件。该地图所依据的深圳白膜场景
另行发布于 Hugging Face，见 EXTERNAL_ASSETS.md。

源码：请检出本版本对应的 git 标签。联合仿真为试点模块，位姿回读与闭环
协调属于后续工作。
```

## 研究背景

本仓库配套论文：

> **A Benchmark-Oriented Framework for Design, Simulation, and Evaluation of Urban Low-Altitude Structured Air Route Systems**
>
> Zichun Zhao、Yuejie Lin、Tao Ma

作者单位：Zichun Zhao 为东南大学交通学院（南京 211189）与 Zhongguancun Academy（北京 100094）；Yuejie Lin 为上海交通大学机械与动力工程学院（上海 200240）；Tao Ma 为东南大学交通学院（南京 211189）。

系统把组件编排、运行机制与实验配置相互分离，使其他路网、设施、路径或控制方法能够通过一致的数据与记录接口被评估。

### 术语对照：论文模型与代码实现

论文 III-A 节把航空器运行实例写成一个包含任务阶段、执行模式与生命周期状态的元组。代码库承载同一个设计，但记录的是各机制真正需要的动作粒度，因此标识符与取值边界都不是一一对应。

`AircraftState` 定义在 `Assets/DataStructrue/DynamicDataStructure.cs`。`MissionPhase` 与 `State` 都是普通 `string` 字段而非枚举，下表的值集合由 `AircraftAgent.HandlePhaseTransition` 与 `SimulationManager` 中的赋值约定构成，而不是由类型系统保证。

| 论文 | 实现 |
|---|---|
| `S_ph`，任务阶段 | `AircraftState.MissionPhase` |
| `S_md`，执行模式 | 无单一字段，见下 |
| `S_st`，生命周期状态 | `AircraftState.State` |

**任务阶段。** 进离场走廊被并入相邻的运动阶段，而不是单列为阶段，因此论文的六个取值对应代码的四个取值加两个记账取值：

| 论文 `S_ph` | `MissionPhase` | 说明 |
|---|---|---|
| `Idle` | `START` | 停在起始停机坪，尚未发出第一个坡道点。 |
| `Departure` | `TAKEOFF` | 经进近网络与等待结构的坡道轨迹。 |
| `Entry` | — | 不单列：进场走廊属于 `TAKEOFF` 的坡道轨迹，由其中的 `EntryMerge` 检查点把关。 |
| `Cruise` | `CRUISE` | 车道序列穿越。 |
| `Exit` | — | 不单列：`ExitCorridorId` 在车道序列走完后申请，是 `LANDING` 轨迹的第一段。 |
| `Arrival` | `LANDING` | 至目的停机坪的降落轨迹。 |
| — | `WAITING` | 论文无对应项：在停机坪等待，同时重试目的地或后续任务链。 |
| — | `END` | 论文无对应项：终止记账，总与 `SUCCESS` 或 `FAIL` 配对。 |

**执行模式。** `Proceed` 表现为"没有任何等待条件"，而不是一个存储的取值。`Hold` 分散在若干等待与访问受阻标志上：计划性释放用 `FlightController.HasPendingHold`，受阻用 `AircraftAgent.isWaitingForEntry`、`isWaitingForLaneChange`、`isHoldingForLandingPlan`、`isWaitingForVertiportResource`。`Replan` 由各条重试路径实现——`SimulationManager.TryRerouteCruise` 的换道重路由（受 `laneChangeRerouteTimeout` 约束）、最多 `maxCorridorWaitTime` 的延迟降落规划、最多 `maxHelipadWaitTime` 的停机坪重排队。

**生命周期状态。** `State` 直接对应：`ACTIVE`、`SUCCESS`、`FAIL` 即论文的 `Active`、`Success`、`Failure`。两个终止取值会释放该机的资源登记。

论文报告的任何结论都不依赖这些标识符，也不依赖取值边界落在哪里：元组描述的是规范层，字段以实现层真正被消费的粒度记录同一信息。

## 许可

以 [MIT 许可证](LICENSE) 发布。
