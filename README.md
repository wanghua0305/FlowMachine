# FlowMachine

多工站可视化编排与模拟运行控制的 WPF 最小纵向实现。桌面端目标为 **.NET Framework 4.7.2 / C# 7.3**；核心运行库面向 `netstandard2.0`，不需要真实设备即可演示。

## 1. 假设、依赖与范围

当前仓库原先为空。第一阶段采用：

| NuGet 包 | 固定版本 | 兼容性依据 |
|---|---:|---|
| `Prism.Wpf` | `8.1.97` | NuGet 包目标包括 .NET Framework 4.6.1，适用于 net472。 |
| `Prism.DryIoc` | `8.1.97` | Prism 8.1.97 的 WPF DI 容器适配器，与同版本 Prism.Wpf 配套。 |
| `HandyControl` | `3.5.1` | NuGet 包提供 .NET Framework 4.0 目标，WPF 主题由应用资源字典加载。 |
| `Nodify` | `7.3.0` | 上游项目该版本的 `Nodify.csproj` 明确包含 `net472` 目标。 |

以上依赖的目标框架和 API 已在实施前核对；所选版本的 GitHub Advisory Database 检查未发现已知漏洞。版本和兼容性参考：[Prism.Wpf](https://www.nuget.org/packages/Prism.Wpf/8.1.97)、[Prism.DryIoc](https://www.nuget.org/packages/Prism.DryIoc/8.1.97)、[HandyControl](https://www.nuget.org/packages/HandyControl/3.5.1)、[Nodify 7.3.0](https://www.nuget.org/packages/Nodify/7.3.0) 及 [Nodify 项目文件](https://github.com/miroiu/nodify/blob/master/Nodify/Nodify.csproj)。

本阶段包括三种工站、开始/结束/延时/日志/条件节点、Nodify 画布、确定性顺序调度、异步总线控制、模拟设备服务、XML 配置和可运行的核心测试。条件结果由节点配置中的模拟布尔值决定，不包含并行、循环、真实 IO/轴动作或真实急停回路。气缸/轴节点配置与接口已经预留，但执行引擎会明确拒绝这些节点，直至接入真实的硬件节点处理器。

## 2. 目录与边界

```text
src/
  FlowMachine.Core/            工站、配置 DTO、节点/状态模型、硬件契约
  FlowMachine.Runtime/         图校验、执行引擎、暂停门、工站工厂、总线调度
  FlowMachine.Infrastructure/  XML 配置存储、模拟设备、资源锁、硬件配置校验
  FlowMachine.App/              Prism WPF Shell、HandyControl 主题、Nodify 编辑器
tests/
  FlowMachine.Tests/            无第三方测试框架的可执行核心行为测试
samples/
  demo-flowmachine.xml          三类工站和连接关系演示配置
```

`IStation` / `StationBase` 管理工站身份和状态；`IStationFactory` 使用稳定类型 ID 注册构造函数。`IWorkflowExecutor` 只执行不可变 `WorkflowSnapshot`；`IBusController` 统一调度总线和手动测试工站；`IDeviceService` 下的 IO、轴、气缸接口隔离厂商 SDK。Prism 模块注册依赖和区域导航，编辑器 ViewModel 只负责编辑配置与显示状态。

## 3. 核心契约和流程语义

- Station 类型 ID：`flow`、`home`、`test`。新增类型通过 `IStationFactory.Register` 加入，不修改总线中的工站类型分支。
- 节点及连接以 DTO 的稳定字符串 ID 保存；运行前复制节点和连接为快照。执行期间画布和参数面板锁定。
- 执行顺序仅由连接关系决定，不使用画布坐标。
- 普通节点只能有一条 `next` 输出；条件节点必须各有一条 `true` 和 `false` 输出；两条分支允许汇合到同一后续节点/结束节点。
- 当前校验拒绝未知节点、悬空边、非法分支、断开的/不可达节点、不完整条件分支和环；要求恰好一个开始和结束节点。
- 延时上限为 10 分钟；`TimeoutMilliseconds=0` 表示不设置节点超时。日志和故障包含工站与节点来源。
- XML 只序列化配置 DTO（格式版本、站点、节点、参数、位置和连接），不保存运行任务或取消/暂停对象。

`IStationScheduleStrategy` 当前实现按工站名称（忽略大小写）再按 ID 排序。以后可替换为并行或依赖调度策略。

## 4. 状态、操作与冲突处理

站点和总线均有 `Idle / Starting / Running / Pausing / Paused / Stopping / Completed / Faulted`，总线另有 `Homing`；站点回零运行时从 `Homing` 进入 `Running`。

主要合法转换：

- `Idle → Starting → Running → Completed`
- `Idle → Homing → Running → Completed`
- `Running → Pausing → Paused → Running`
- `Starting/Running/Pausing/Paused/Homing → Stopping → Idle`
- 执行错误使当前工站和总线进入 `Faulted`；空闲故障复位为 `Idle`。
- `Completed → Starting/Homing` 支持下一次单次执行；复位不会自动启动或回零。

| 总线状态 | 可用操作 |
|---|---|
| `Idle`、`Completed` | 启动、回零（有启用的相应工站时）、手动测试；也可复位 |
| `Starting`、`Running`、`Homing` | 停止、暂停 |
| `Pausing` | 停止；暂停命令等待安全边界 |
| `Paused` | 停止、继续 |
| `Stopping` | 等待清理完成 |
| `Faulted` | 复位；不允许新运行 |

总线普通启动只选择启用的 `FlowStation`；回零只选择启用的 `HomeStation`；`TestStation` 仅由所选工站的手动入口运行。所有运行入口共用单一活动任务和资源锁；同类型重复启动返回现有任务，冲突命令被拒绝。工站故障会终止本次总线任务并记录故障工站，不再调度后续工站。

暂停是节点边界暂停：当前节点可完成，暂停请求后不会开始下一节点；`PauseAsync` 在实际到达边界后才返回。继续沿用原节点位置，不重放完成节点。停止协作式取消运行和排队任务，并等待执行清理；复位在活动任务期间拒绝执行。软件停止**不等同于硬件急停**，真实设备安全回路不在本项目范围内。

## 5. 启动和演示

在安装 .NET Framework 4.7.2 Developer Pack / Windows Desktop Build Tools 的 Windows 环境：

```powershell
dotnet build FlowMachine.slnx
dotnet run --project src/FlowMachine.App/FlowMachine.App.csproj
```

运行核心测试：

```powershell
dotnet run --project tests/FlowMachine.Tests/FlowMachine.Tests.csproj
```

启动后可以切换左侧工站，使用画布左下角工具箱拖入节点；拖动节点移动，拖动输出连接器到输入连接器建立边；连接列表可删除连接，选中普通节点可编辑延时/日志参数，选中条件节点可编辑模拟结果。保存/加载使用当前工作目录的 `flowmachine.xml`。`samples/demo-flowmachine.xml` 可作为独立示例配置；将其复制到工作目录并点击“Load”即可查看三种工站和条件分支。运行日志在底部显示，当前节点在画布上高亮。

未启用或未配置的工站不会进入总线任务。运行期间不能编辑工站流程；运行失败后先复位再运行。硬件节点配置通过模拟设备点位列表和 `HardwareConfigurationValidator` 校验方向、设备标识和参数，但执行目前会提示尚未支持。

## 6. 测试覆盖

测试命令覆盖：普通启动只执行 FlowStation、回零只执行 HomeStation、TestStation 手动入口限制、重复启动不产生重复任务、节点边界暂停、条件 True/False 分支、重复暂停、继续不重放已完成节点、停止后清理、故障/超时传播、XML 保存加载后连接不变。测试使用可执行断言程序，避免为测试引入额外依赖。

## 7. 扩展

- **新增工站**：定义稳定类型 ID 和 `StationBase` 子类，在工厂注册创建函数；按需定义是否自动启动、回零或手动运行能力。
- **新增节点**：定义配置字段和稳定类型 ID；增加参数校验、执行处理、编辑器工具箱/属性模板和配置加载校验；补充运行测试。执行前保持图结构校验和配置快照隔离。
- **接入真实 IO/轴/气缸**：实现 `IDeviceService` 子接口，提供设备/点位目录和方向信息；节点通过硬件服务访问设备，并用 `IDeviceResourceLock` 按资源 ID 互斥。按设备能力定义超时、暂停和停止/安全策略；不要将厂商 SDK 或急停逻辑放入 UI。
- **格式迁移**：配置有 `formatVersion=1`，当前对不匹配版本明确报错；随着格式演进，在 `ConfigurationStore` 加入按版本逐步迁移 DTO 的迁移链。
