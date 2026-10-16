# 架构与阶段计划

## 目标运行形态

全部运行在 NX 进程内。聊天 UI、模型 Provider、对话/tool 编排和 NXOpen 适配分层，但不通过外部代理服务互相通信。

```text
NX Main UI entry
      |
      v
Modeless Chat Window <----> Settings / Attachments
      |
      v
Conversation + Tool Orchestrator
      |                           \
      v                            v
Direct Provider HTTP Client      Typed NX Tool Registry
      |                            |
      v                            v
Configured Model API           NX Thread Dispatcher
                                   |
                                   v
                           NXOpen Active Session/Part
                                   |
                                   v
                           Postcondition Verification
```

网络请求在后台执行。NXOpen 状态查询与模型修改通过经过验证的 NX 线程调度机制执行。模型返回的是受约束的工具调用/参数，不是可随意执行的 C# 脚本。

## 版本可升级架构（必须从第一天保留边界）

建议按职责分层，而不是把全部逻辑放进单一绑定特定 NX 版本的 DLL：

```text
NXAI.Assistant.Core
  - 对话状态、工具契约、验证规则、设置模型
  - 不引用 NXOpen

NXAI.Assistant.Providers
  - 模型 HTTP API、流式响应、Provider 能力
  - 不引用 NXOpen

NXAI.Assistant.UI
  - modeless 聊天窗口、配置界面
  - 与具体宿主运行时的边界按实际兼容性验证决定

NXAI.Assistant.NxHost.<ReleaseProfile>
  - NX 菜单入口、生命周期、NXOpen Dispatcher、会话/部件/几何适配器
  - 针对明确的 NX 发行版/运行时编译和验证

Compatibility Manifest + Installer/Updater
  - 检测安装实例、选择匹配宿主适配器、设置迁移、升级与回滚
```

具体项目目标框架不预先臆定；阶段 0 先调查 NX2512 安装里的 `managed` / `managed_core`、官方示例、当前 NXOpen 程序集与目标运行时，再确定共享层和宿主适配器的项目目标。共享层可以在合适时选择跨目标框架方案，但不能为了“跨版本”而引入未经验证的多目标复杂度。

### 版本检测和兼容性配置

- 不只靠安装目录字符串判断版本：记录 NX 版本 API/产品标识、实际加载的 NXOpen assembly `Location`、程序集版本和运行时标识。
- 定义版本 Profile / Adapter 注册表，Profile 应包含目标发行版、运行时、程序集指纹、构建身份和已验证的工具能力。
- 只有兼容性状态达到 `supported` 的 Profile 才可启用模型修改工具；未知版本以只读诊断/明确不支持状态启动，不得静默执行修改。
- 通过类型化工具能力和适配器接口隔离 API 差异，避免在对话编排、Provider 或 UI 中散落 NX 版本分支。

### 每个更高版本的升级流程

1. 探测新 NX 安装及其官方支持的运行时和 NXOpen 参考程序集。
2. 对比 release notes 和 NXOpen API 变更；如果当前版本提供 NXOpenReporter，生成并审查 Changed/Deprecated/Deleted API 报告。
3. 为新版本重建相应宿主适配器（必要时 UI/插件壳也分别构建），不覆盖仍需支持的旧版本组件。
4. 运行不依赖 NX 的单元测试与 Provider/UI 测试。
5. 在目标 NX 版本实际加载插件，验证入口、生命周期、活动部件只读访问和独立测试部件中的代表性建模工具。
6. 更新支持清单和安装包；只有真实加载及集成测试通过后标记 `supported`。
7. 失败时回滚该版本的插件部署，并保留用户设置/旧版本安装。

### 兼容状态定义

- `planned`：已排入计划，未构建。
- `built`：对应目标版本编译成功。
- `loaded`：在该 NX 版本中真实加载成功。
- `integration-tested`：代表性操作已在独立测试部件真实验证。
- `supported`：当前回归和升级验收通过，可作为正式支持版本。

**不能承诺不经测试即可支持任何未来版本。** 可承诺的是架构隔离版本差异、升级流程明确，并且在新版本发布后能以可重复的流程加入支持，而不是重写整个产品。

## 技术选择原则

- 语言：C#（已在用）。
- UI：**已定案 WinForms（2026-10-14）**——modeless 浮动窗口，WinForms 自绘统一样式。WPF 路线已在真实 NX 中实测排除：NX2512 进程内 .NET 8 基础运行时的 `WindowsBase` 4.0.0.0 桩 + NX 加载器 TPA 全量枚举 + Default ALC 名字保护，三者组合使真 WPF 无法绑定；WinForms 链干净可绑。此为硬约束，不得重启 WPF 实验（详见 `WORK_LOG.md` 2026-10-14 条目）。
- 插件入口：MenuScript 菜单 + `ACTIONS` shim（已实现并实机验收）。
- NXOpen：仅 `NxHost` 层引用 `NXBIN\managed_core`（.NET 8，Private=false）；禁止混用 `managed` 与 `managed_core`；必须复用 NX ALC 已加载的 NXOpen 实例（单一类型宇宙）。
- 网络：插件内 `HttpClient`（支持测试注入）；不设任何本地 MCP/Bridge/代理依赖。
- 配置：JSON 设置；API Key 经 DPAPI 加密单独存储。
- 测试：离线单元测试（不依赖 NX）+ 真实 NX2512 实机验收；两者分栏记录（`RELEASE_CHECKLIST.md`）。

## 当前实现状态（2026-10-15 核验，详见 docs/PROJECT_STATUS.md 与 reports/PHASE_1_CHAT_AND_UI_ACCEPTANCE.md）

| 图中模块 | 状态 |
|---|---|
| Modeless Chat Window | **已实现并实机验收**（三区布局、消息卡片流、底部输入区、新对话、确认对话框、NX 退出不挂起） |
| Settings | **已实现并实机验收**（TableLayoutPanel、DPAPI 密钥、留空保留） |
| Conversation Orchestrator | **已实现并实机验收**（ChatSessionController：多轮历史、取消、生命周期保护 + 工具回合状态机：每回合至多一次建模执行、2 回合上限） |
| Direct Provider HTTP Client | **已实现**（OpenAI 兼容 Chat Completions、URL 规范化、错误分类 + tools 声明与 tool_calls 解析）；流式输出未实现 |
| Typed NX Tool Registry / Dispatcher / Postcondition Verification | **已实现（create_block + create_cylinder 两工具）**：Core 契约/注册表 + NxHost `NxToolExecutor`（无 SaveAs、UndoMark 回滚、UF 包围盒验证、毫米部件门禁、部件变化拒绝）。create_block 八项实机验收通过（2026-10-16，用户报告）；create_cylinder 源码+测试完成，实机待验收 |
| NX Context Awareness（Phase 5） | **源码 + 离线测试完成**：NxContextSnapshot DTO（Core）+ NxContextReader 采集（NxHost，选择分类/面中心法向/WCS/视图）+ 每轮 system 摘要注入；面相对圆柱（selected_face_center）。实机待验收 |
| Execution Modes（Phase 6） | **源码 + 离线测试 + 部署完成**：Core `ExecutionMode` 策略/允许清单/持久化（%APPDATA%\NX AI Assistant\execution-mode.json）、控制器分派阶段授权（自主模式仅四基本体跳过确认，执行前重查模式）、UI 橙色滑动开关（文字标签表达状态、DPI/键盘/无障碍）。实机待验收（Phase 6 报告 §7） |
| Attachments | 未实现（阶段 5 顺延） |

**AI 驱动建模闭环已建立并经用户 NX2512 实机验收通过（2026-10-16，八项；证据为用户报告）。**

## 阶段 0：环境调查与技术验证 — ✅ 已完成（2026-10-13，见 PHASE_0_REPORT.md）

- 只读调查 NX2512 的安装目录、示例、插件入口、目标运行时、菜单配置和部署方式。
- 创建全新项目仓库和解决方案，不从旧项目拷贝源码。
- 建立最小插件加载实验：NX 主界面入口可见，点击打开 modeless 窗口，窗口关闭及 NX 退出清理正常。
- 验证能在当前 NX 会话读取 `Session.Parts.Work` 的基本信息。
- 明确所有不确定项和真实运行证据。

## 阶段 1：聊天壳与配置 — ✅ 已完成并实机验收（2026-10-15，见 reports/PHASE_1_CHAT_AND_UI_ACCEPTANCE.md）

- 独立聊天窗口、消息列表、输入框、取消/错误状态。
- Provider 接口与首个可配置 HTTP API Provider（OpenAI 兼容 Chat Completions）。
- 设置保存、API Key 安全储存（DPAPI）。
- 本阶段不修改几何。
- 未做（顺延）：连接测试按钮、流式输出、附件。

## 阶段 2：活动零件上下文

- 查询工作部件、单位、实体、特征列表和当前选择。
- 将查询结果转换成适合模型理解的结构化摘要。
- 查询工具只读且可重复验证。

> 注：实际开发中工具调用与第一个建模闭环（create_block）先行交付（2026-10-16，实机验收通过）；本阶段（活动零件上下文查询）顺延为下一候选任务。

## 阶段 3：第一个实时建模闭环 — ✅ 已完成并实机验收（原阶段 2 内容，2026-10-16）

- 类型化工具（create_block）：schema、输入验证、确认流、操作日志与后置验证齐全（见 reports/PHASE_2_TOOL_CALLING_AND_CREATE_BLOCK.md）。
- 修改发生于当前 NX 活动部件，同一会话中特征树/几何可见（实机验收第 ② 项）。
- 失败回滚并返回真实错误（实机验收第 ⑤ 项覆盖）。
- 后续建模工具（圆柱、孔等）在闭环稳定后逐个引入，每个经实机验收后开放。

## 阶段 4：常用零件特征

逐个引入孔、拉伸、圆角、倒角、阵列、参数修改等操作。每项通过真实 NX 集成验收后才对模型开放。

> 注：create_cylinder/create_cone/create_sphere 已按此模式先行交付（2026-10-16），见 reports/PHASE_3/4 报告；面相对放置（Phase 5）见 reports/PHASE_5_CONTEXT_AWARENESS.md。

## 执行模式（Phase 6，2026-10-16 实现，实机待验收）

- 双交互模式：**确认模式**（默认，所有建模操作执行前显示确认卡，行为与之前版本一致）与**自主模式**（允许清单内的非破坏性基本体创建工具在参数/上下文/安全校验全部通过后直接执行）。
- 授权在后端分派阶段执行（`ExecutionModePolicy`），UI 开关只改变模式；模型无法修改模式、授权或允许清单。
- 允许清单封闭：create_block/create_cylinder/create_cone/create_sphere；未来破坏性操作不自动纳入。
- 模式经 `%APPDATA%\NX AI Assistant\execution-mode.json` 持久化，启动时后端与 UI 同源恢复；请求进行中禁止切换；切换不等价于对任何待执行操作的确认。
- 详见 reports/PHASE_6_EXECUTION_MODES.md。

## 阶段 5：附件与工程打包

完善文档/图像附件能力、设置迁移、安装/升级/卸载程序，在兼容环境中从干净安装验证。安装包要能发现多个 NX 安装实例，并部署各自明确支持的适配器版本。

## 阶段 6：更高 NX 版本适配与持续兼容

- 选择一个后续实际可获得的高版本 NX/Designcenter 安装作为目标，按版本升级流程进行完整演练。
- 加入 API 变更报告、版本 Profile、构建矩阵与兼容性状态记录。
- 验证旧 NX 与新 NX 并存、分别启动、各自载入匹配的适配器，且共享用户设置迁移不破坏既有配置。
- 维护每个正式支持版本的插件包；新版本未验收时明确标记而非假装兼容。

## 后续阶段

装配支持和仿真支持明确排在实时零件建模稳定之后。
