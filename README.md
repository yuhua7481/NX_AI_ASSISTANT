# NX AI Assistant

独立开发的 Siemens NX2512 自然语言建模插件。目标是在 NX 主界面打开一个 modeless 聊天窗口，通过可配置的模型 API 直接调用当前 NX 进程内的 NXOpen 能力，读取并修改当前活动零件。

## 核心原则

- 本项目是全新实现；`D:\AI\NX_MCP` 是只读参考项目，不是代码依赖。
- 不依赖 MCP、GUI Bridge、Claude Code、外部代理或另一个后台 NX 进程。
- 聊天窗口可以是可移动的 modeless 窗口，不要求 NX 原生 Dock。
- 网络请求在后台异步执行；所有 NXOpen 模型读写通过符合 NX 要求的 UI/主线程调度机制执行。
- 先实现可验证的插件加载、主界面按钮、聊天窗和活动零件读取，再逐步增加建模工具。
- 不能把“提示词解析成功”“C# 编译成功”当成建模成功。必须读取 NX 实际结果验收。
- 不修改用户零件，除非操作目标已确认并且具备可回滚/撤销的处理方式。

## 当前阶段

阶段 1（真实模型对话链路 + 聊天 UI）已交付，用户已在真实 NX2512 完成六项实机验收（布局 / 设置窗口 / 真实对话 / 请求期间行为 / 新对话上下文隔离 / 显示缩放）。当前准确状态、最新测试数字与下一项任务**以 [`docs\PROJECT_STATUS.md`](docs/PROJECT_STATUS.md) 为唯一入口**，本 README 不重复维护状态数据。技术事实与证据分级见 `docs\TECHNICAL_SPIKE_RESULTS.md`。

## 新会话固定阅读顺序

1. 本 `README.md`（项目是什么、怎么构建）。
2. `docs\PROJECT_STATUS.md`（当前状态、最新已核实验证、已知风险、下一项任务）。
3. `docs\DEVELOPMENT_RULES.md`（强制约束与推荐实践）。
4. `docs\ARCHITECTURE_AND_PHASES.md`（架构分层与阶段计划）。
5. 当前任务对应的详细报告（`docs\reports\`、`docs\PHASE_0_REPORT.md` 等）与 `docs\WORK_LOG.md`（历史脉络）。
6. 相关源码、测试与当前 Git 差异（`git status` / `git diff`）。

任务结束后必须更新：`docs\PROJECT_STATUS.md`（状态与下一项任务）与 `docs\WORK_LOG.md`（追加本次记录）；完成阶段或重要修复时更新/新增阶段报告。不要重写整个文档库。

快速构建/测试/部署：

```powershell
dotnet build NXAIAssistant.sln -c Release
dotnet test tests\NXAI.Assistant.Tests -c Release
powershell -ExecutionPolicy Bypass -File build.ps1
```

架构分层（版本升级边界）：

- `src\NXAI.Assistant.Core` — 共享模型/契约（对话消息、模型服务、校验规则），不引用 NXOpen
- `src\NXAI.Assistant.UI` — WinForms modeless 聊天窗与对话协调（UI 技术栈已定案 WinForms，2026-10-14），不引用 NXOpen
- `src\NXAI.Assistant.NxHost.NX2512` — 唯一引用 NXOpen（`NXBIN\managed_core`，.NET 8）的宿主适配层
- `src\NXAI.Assistant.Entry` — MenuScript 入口 shim（`ACTIONS NXAI.Assistant.Entry.dll` → `static Main()`）
- `tools\NxCompatProbe` — 版本/运行时只读探测器（安装器复用）
- `deploy\nxai_custom` — custom directory（菜单 + DLL），由 `deploy\nxai_custom_dirs.dat` 挂接

关键文档索引：`docs\PROJECT_STATUS.md`（状态入口）、`docs\DEVELOPMENT_RULES.md`（约束）、`docs\WORK_LOG.md`（开发日志）、`docs\RELEASE_CHECKLIST.md`（发布清单）、`docs\reports\`（阶段报告）、`docs\ARCHITECTURE_AND_PHASES.md`（架构与阶段计划）。

## 建议目录

- `src/`：全新源代码
- `tests/`：可重复测试
- `docs/`：需求、架构、阶段验收
- `tools/`：探测/验证工具（`tools\installer` 留给后续安装包）
- `artifacts/`：验证运行证据（journal 输出、探测 JSON）
- `samples/`：安全的测试用零件或示例
- `references/`：仅存放 API 资料、记录和摘要，不放旧项目复制来的实现代码
- `deploy/`：NX custom directory 部署产物（菜单、DLL、目录清单）
