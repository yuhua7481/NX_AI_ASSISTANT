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

阶段 0（插件壳 + 活动零件读取）已实现并完成真实 NX2512 进程内验证（run_journal 只读验证：插件加载、NXOpen .NET 8 单一类型宇宙、活动部件名称/路径/单位读取）。交互式 GUI 入口验收步骤见 `docs\PHASE_0_REPORT.md` §5。技术事实与证据分级见 `docs\TECHNICAL_SPIKE_RESULTS.md`。

快速构建/测试/部署：

```powershell
dotnet build NXAIAssistant.sln -c Release
dotnet test tests\NXAI.Assistant.Tests -c Release
powershell -ExecutionPolicy Bypass -File build.ps1
```

架构分层（版本升级边界）：

- `src\NXAI.Assistant.Core` — 共享模型/契约，不引用 NXOpen
- `src\NXAI.Assistant.UI` — WinForms modeless 聊天窗壳，不引用 NXOpen
- `src\NXAI.Assistant.NxHost.NX2512` — 唯一引用 NXOpen（`NXBIN\managed_core`，.NET 8）的宿主适配层
- `src\NXAI.Assistant.Entry` — MenuScript 入口 shim（`ACTIONS NXAI.Assistant.Entry.dll` → `static Main()`）
- `tools\NxCompatProbe` — 版本/运行时只读探测器（安装器复用）
- `deploy\nxai_custom` — custom directory（菜单 + DLL），由 `deploy\nxai_custom_dirs.dat` 挂接

## 建议目录

- `src/`：全新源代码
- `tests/`：可重复测试
- `docs/`：需求、架构、阶段验收
- `tools/`：探测/验证工具（`tools\installer` 留给后续安装包）
- `artifacts/`：验证运行证据（journal 输出、探测 JSON）
- `samples/`：安全的测试用零件或示例
- `references/`：仅存放 API 资料、记录和摘要，不放旧项目复制来的实现代码
- `deploy/`：NX custom directory 部署产物（菜单、DLL、目录清单）
