# 阶段 0 交付报告

日期：2026-10-13
工作区：`D:\AI\NX_AI_ASSISTANT`（全新 clean-room 仓库；未初始化 git，未做任何 commit）
旧项目 `D:\AI\NX_MCP`：仅只读调查（程序集/环境事实、加载机制证据、lessons learned 文档），未复制任何实现源码，未修改其中任何文件。

## 1. 交付物清单

### 新增源代码（编译通过，0 警告 0 错误）

| 文件 | 职责 | 依赖 NXOpen |
|---|---|---|
| `NXAIAssistant.sln` | 解决方案 | - |
| `src\NXAI.Assistant.Core\NXAI.Assistant.Core.csproj` | 共享层：net8.0，无 NXOpen | 否 |
| `src\NXAI.Assistant.Core\WorkPartSnapshot.cs` | 活动部件快照模型（无部件/错误工厂） | 否 |
| `src\NXAI.Assistant.Core\IActivePartReader.cs` | 读活动部件契约（宿主适配层实现） | 否 |
| `src\NXAI.Assistant.Core\ChatMessage.cs` | 聊天消息/日志模型（阶段 1 复用） | 否 |
| `src\NXAI.Assistant.Core\NxVersionProfile.cs` | 版本 Profile 模型 + 兼容状态门控（仅 Supported/IntegrationTested 可开修改工具） | 否 |
| `src\NXAI.Assistant.UI\NXAI.Assistant.UI.csproj` | UI 层：net8.0-windows + WinForms | 否 |
| `src\NXAI.Assistant.UI\AssistantChatForm.cs` | modeless 聊天窗壳：部件信息面板（名称/路径/单位/状态）+ 对话列表 + 输入框 + 刷新 | 否 |
| `src\NXAI.Assistant.NxHost.NX2512\*.csproj` | NX2512 宿主适配层：net8.0-windows，引用 `NXBIN\managed_core`（Private=false） | **是（版本隔离）** |
| `src\NXAI.Assistant.NxHost.NX2512\NxSessionReader.cs` | `Session.Parts.Work` → `WorkPartSnapshot`（只读） | 是 |
| `src\NXAI.Assistant.NxHost.NX2512\NxHostBoot.cs` | 窗口生命周期：重复点击激活既有窗口 | 是（间接） |
| `src\NXAI.Assistant.Entry\*.csproj + Entry.cs` | MenuScript 入口 shim（程序集名=全限定类名，`static Main()`） | 否（仅转调宿主） |

### 工具、部署与测试

| 文件 | 说明 |
|---|---|
| `tools\NxCompatProbe\Program.cs` | NX 安装/运行时只读探测器（JSON 输出），未来安装器复用 |
| `tools\NxLoadValidation\NxLoadValidation.cs` | 真实 NX 进程内加载验证 journal（run_journal 验证工具，非产品路径） |
| `src\NXAI.Assistant.Entry\nxai_assistant.men` | 菜单定义源文件（Toolbox → AI Assistant → AI Assistant Chat，`ACTIONS NXAI.Assistant.Entry.dll`）；build.ps1 部署到 `deploy\nxai_custom\startup\` |
| `deploy\nxai_custom\application\*.dll` | 构建输出：Core / UI / NxHost.NX2512 / Entry（4 个程序集） |
| `deploy\nxai_custom_dirs.dat` | custom 目录清单（保留旧项目目录 + 新增本项目目录；旧项目本身未改动） |
| `build.ps1` | 构建 + 复制到 deploy + 可选 `-Test` / `-Sign` |
| `tests\NXAI.Assistant.Tests\*` | xUnit 测试（快照模型、版本门控规则、聊天日志） |
| `artifacts\nxload_validation_result.txt` | 真实 NX 验证运行结果（证据） |
| `artifacts\nx_compat_probe_result.json` | NX2512 环境 profile（证据） |
| `docs\TECHNICAL_SPIKE_RESULTS.md` | 技术调查记录（证据分级） |

## 2. 构建与测试命令（可复现）

```powershell
# 位于 D:\AI\NX_AI_ASSISTANT
dotnet build NXAIAssistant.sln -c Release        # → 0 警告 0 错误（6 个项目）
dotnet test tests\NXAI.Assistant.Tests -c Release # → 13/13 通过
powershell -ExecutionPolicy Bypass -File build.ps1 # → 复制 4 个 DLL 到 deploy\nxai_custom\application\
```

实测结果：构建成功；测试 `通过: 13, 失败: 0`；部署复制成功。

## 3. 真实 NX2512 验证证据（run_journal 只读验证，非产品路径）

命令模式（已执行）：

```powershell
& "D:\Program Files\Siemens\DesigncenterNX2512\NXBIN\run_journal.exe" `
    tools\NxLoadValidation\NxLoadValidation.cs -args `
    "D:\AI\NX_AI_ASSISTANT\deploy\nxai_custom\application" `
    "D:\AI\NX_AI_ASSISTANT\artifacts\nxload_validation_result.txt" `
    "D:\NX_MCP_WORKSPACE\TEST001.prt"   # 可选：打开测试零件验证读取
```

两次运行（证据：`artifacts\nxload_validation_result.txt`）：

| 验证点 | 结果 |
|---|---|
| 真实 NX2512 进程运行时 | `NXOpen, 2512.0.0.0`，位置 `nxbin\managed_core\NXOpen.dll`，CoreCLR 8.0.31（**.NET 8 实锤**） |
| 插件程序集加载（从部署目录） | `NXAI.Assistant.NxHost.NX2512.dll` 加载成功，Core/UI 依赖解析成功 |
| NXOpen 单一类型宇宙 | 插件解析到的 NXOpen 与 NX 进程内为**同一实例**（identity: True） |
| 无活动部件读取 | `HasActivePart=False`，状态="当前 NX 会话中没有打开的活动工作部件。" ✓ |
| 打开 TEST001.prt 后读取 | `HasActivePart=True, DisplayName=TEST001, FullPath=D:\NX_MCP_WORKSPACE\TEST001.prt, Units=Millimeters` ✓ |
| 零件数据 | 测试零件仅在会话中打开，未修改、未保存 |

过程发现并修复的加载约束（对后续所有阶段都关键，详见 `docs/TECHNICAL_SPIKE_RESULTS.md` §4）：
`Assembly.LoadFrom` 会产生第二份 NXOpen 副本并破坏类型身份（`Unable to cast NXOpen.Session to NXOpen.TaggedObject`）；NX 的 ALC 是 collectible 且不探测插件目录；正确加载必须复用 NX ALC 已加载的 NXOpen 实例。**构建中 NXOpen 引用全部 `Private=false`，绝不复制进插件目录。**

## 4. 验证状态分级声明（如实区分）

| 层级 | 状态 |
|---|---|
| 源码完成 | ✅ 6 个项目全部完成 |
| 编译通过（针对真实 NX2512 managed_core 程序集） | ✅ 0 警告 0 错误 |
| 单元测试（无 NX） | ✅ 13/13 |
| **NX 实际运行：读路径**（run_journal 真实进程加载+读活动部件，无部件/有部件） | ✅ 已验证（见 §3） |
| **NX 实际运行：GUI 交互链路**（菜单出现、modeless 窗口、NX 保持可交互、重复点击激活、NX 退出清理） | ⏳ **待用户实测**——本会话无法操作 NX 图形界面；菜单回调 `Main()` 签名为最高置信推断但未经交互点击验证 |

**因此不宣称"插件已成功在交互式 NX 中运行"。** 已完成并验证到"真实 NX 进程内加载 + 活动零件读取"这一层。

## 5. 用户 GUI 实测步骤（阶段 0 收尾验收）

前提：关闭正在运行的 NX。

1. 把用户级环境变量指向新的目录清单（保留旧项目目录，旧插件不受影响）：
   ```powershell
   [Environment]::SetEnvironmentVariable('UGII_CUSTOM_DIRECTORY_FILE', 'D:\AI\NX_AI_ASSISTANT\deploy\nxai_custom_dirs.dat', 'User')
   ```
2. 启动 NX2512。
3. 菜单：**工具箱（Toolbox）→ AI Assistant → AI Assistant Chat**。
4. 验收点：
   - 窗口打开且为 modeless（NX 主界面仍可旋转/操作模型）；
   - 部件面板显示当前零件的名称/路径/单位，或明确显示"没有活动部件"；
   - 切换/打开零件后点"刷新"（或重新激活窗口）显示更新；
   - 重复点击菜单 → 激活同一窗口，不重复创建；
   - 关闭 NX → 窗口随进程正常关闭，无残留进程/报错弹窗。
5. 若点击菜单无反应/报"签名"错误：运行 `powershell -ExecutionPolicy Bypass -File build.ps1 -Sign`（注意：SignDotNet 要求程序集内嵌 NX resource bundle，当前构建未含——若失败，把报错反馈给我，需要按 Siemens 向导方式补 resource），或反馈报错内容。

## 6. 未解决问题与限制

1. **GUI 交互链路未实测**（§4/§5）——菜单回调 `Main()` 精确签名、modeless 行为、NX 退出清理，都待用户在交互式会话确认。
2. SignDotNet 签名路径未打通：要求程序集内嵌 "NX resource bundle"（Siemens 工程向导产物）。本机历史证据（旧无签名 add-in 曾长期运行）表明大概率不需要；若实测被拒需补做。
3. 聊天窗口的消息列表是本地壳（无网络）。阶段 1 接 Provider HTTP。
4. 部件信息在窗口打开/激活/手动刷新时读取；NX 内部切换零件不会自动推送事件（NXOpen 会话事件订阅在阶段 2 做）。
5. 本仓库未 `git init`（遵守"不提交 commit"边界）；需要时由用户决定何时初始化。

## 7. 下一步（进入阶段 1 的前置）

1. 用户完成 §5 GUI 实测 → 更新 `docs\SUPPORTED_NX_VERSIONS.md`（NX2512 → `loaded`，GUI 证据）。
2. 阶段 1：聊天壳 + Provider 接口 + OpenAI-compatible HTTP 适配器 + API Key 安全存储（DPAPI）+ 流式输出。HTTP 全部后台线程，不碰 NX UI 线程。
3. 阶段 2：会话事件订阅 + 实体/特征/选择集上下文查询。
4. 每步都按 CLAUDE.md：类型化工具、操作前后真实验证、不信任模型文本。
