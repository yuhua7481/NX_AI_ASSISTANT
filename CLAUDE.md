# NX AI Assistant — Project Rules

## Product goal
Build a self-contained Siemens NX2512 in-process AI assistant plugin. The user opens a modeless chat window from a button/menu in the NX main UI, configures a model API, types natural-language modeling requests, and sees changes made directly to the active NX part and feature tree.

## Clean-room rebuild rules
- New workspace: `D:\AI\NX_AI_ASSISTANT`.
- Legacy workspace `D:\AI\NX_MCP` is read-only reference only.
- Do not copy/paste/reuse legacy Engine, MCP, GUI Bridge, generator, or server implementation source as the new implementation. You may inspect old source, test evidence, paths, lessons learned, and API facts; reimplement cleanly in this repository.
- No project dependency on MCP, GUI Bridge, an external coding agent, a separate local service, or an out-of-process NX session.
- The plugin itself calls the configured model API directly.
- Preserve current user's model/provider setup outside this repository; do not change it.

## Product decisions
- NX main interface entry (menu/button) opens a modeless floating chat window. Native docking is not required.
- Chat window can be moved beside NX; NX remains interactive while it is open.
- Repeated clicks focus/reuse the same window instead of spawning duplicates.
- The window and plugin lifetime are tied to the NX process/session; close cleanly when NX exits.
- API endpoint, model name, API key, and request settings are configurable. Design a provider interface and begin with OpenAI-compatible HTTP APIs; do not hard-code a single provider.
- API keys must not be committed or logged. Prefer Windows DPAPI or Credential Manager for stored secrets.
- File attachments should be staged behind a provider capability abstraction (text/document first; image/multimodal only when provider supports it).
- Initial product scope is part modeling and active-part inspection. Assembly comes later; simulation is explicitly later.

## Architecture guardrails
- Keep UI, model-provider transport, conversation/tool orchestration, and NXOpen integration separate.
- HTTP/network work must not block the NX UI thread.
- NXOpen model reads and mutations must run on the valid NX execution/UI thread using a verified in-process dispatch strategy.
- Tool calls must be typed, validated, logged, and limited to registered operations. Avoid arbitrary C# execution from model output.
- Before modifying geometry, inspect the current part and validate required bodies/faces/points/parameters. Ask the user when a required reference or intent is ambiguous.
- Verify postconditions by querying NX, not by trusting the LLM's text.
- Prefer transactions/undo marks where supported and record failure context.
- No model mutation during tests unless the test explicitly uses a disposable test part.
- No use of `run_journal.exe` as the main interactive product path. It can remain a validation tool only.

## Runtime and future-version compatibility rules
- NX2512 is the first baseline target, not the only intended supported release. The product must be designed for planned upgrades to future higher NX/Designcenter releases.
- Never assume one compiled plugin DLL can run against every NX release. The in-process NXOpen adapter must be built and validated against the exact target release/runtime where required; keep shared domain/provider/tool-contract code isolated from version-specific NXOpen integration.
- At startup/install/update, detect the actual NX release, NXOpen assembly version/location, and host runtime. Select an explicitly supported compatibility profile; if unknown, do not silently enable model-mutating tools.
- Use a version/capability profile or adapter registry rather than spreading release-number checks throughout business logic. Discover capabilities where possible, and declare unsupported features explicitly.
- Keep user configuration and API secrets outside versioned plugin binaries/install directories. Version and migrate configuration schemas without losing provider settings, chat history, or user files.
- Installer/updater must detect multiple side-by-side NX installations and install the matching adapter per supported release without replacing another release's files. Support rollback and preserve settings.
- For each new NX release, inspect release notes and available NXOpen API migration tools/reports (including NXOpenReporter when available), rebuild against that release's own references, run automated tests, then perform actual in-process load and disposable-part integration tests before marking it supported.
- Maintain a support matrix with states such as `planned`, `built`, `loaded`, `integration-tested`, and `supported`. Never describe a future release as supported just because the code compiles.
- Do not guess target framework or NXOpen assembly path. Inspect the installed target environment and official/local examples first.
- Do not mix assemblies from different NX runtime/load contexts.
- A passing unit test or DLL compilation does not prove the plugin loads or a model operation works in NX.

## Workflow
1. Inspect the current workspace and `git status` before edits.
2. Implement in small vertical slices and keep the app buildable.
3. Add repeatable tests for logic that does not need NX.
4. For NX-dependent behavior, explicitly distinguish mock tests from real NX acceptance.
5. Back up or preserve user data; no destructive cleanup, reset, or commit unless asked.
6. Report file changes, commands run, exact results, and remaining unverified assumptions.



## AI 开发执行约束

### 项目架构

* 目标平台：Siemens Designcenter NX2512。
* 直接在 NX 进程内使用 NXOpen 操作当前会话。
* UI 使用 WinForms（2026-10-14 定案），不再继续 WPF 迁移；不引入 WPF，不再实施 WindowsBase 加载器实验。
* 现代化界面通过 WinForms 自绘、统一样式、圆角卡片和现代布局实现。
* 不引入 MCP、GUI Bridge、外部建模进程或额外后台服务。
* 保留现有 NX 菜单入口、单实例窗口和活动工作部件读取能力。

### 工作目录与 Git

* 开始任务时先确认当前工作目录、分支及 Git 状态。
* 主项目目录为 D:\AI\NX_AI_ASSISTANT。
* 不得把独立 Worktree 当成主项目目录。
* 不覆盖、重置或丢弃已有修改。
* 未经明确授权，不提交 Git、不合并分支。

### 降低模型重复分析

* 先阅读本任务直接涉及的文件，不默认扫描整个仓库。
* 修改前先检查相关项目文件及调用关系。
* 仅在确有必要时读取额外文件，并说明原因。
* 不重复分析已经验证过的模块。
* 不输出冗长的计划和重复总结，优先执行明确的小任务。


## AI 分析效率约束

1. 任务开始后，只读取完成该任务必需的文件，不默认扫描整个仓库。
2. 优先检查当前模块的项目文件、目标源文件和直接调用者。
3. 不重复读取已经确认且没有变化的文件。
4. 需要扩大读取范围时，先说明具体原因。
5. 不输出冗长的思考过程、重复计划或与任务无关的架构分析。
6. 优先直接实施范围明确的修改，并尽早给出可检查的差异。
7. 一轮只完成一个目标；修改、构建、测试和 NX 实测分别执行。
8. 如果持续思考但没有可见进展，应停止当前任务，检查已完成的工作，而不是重新扫描项目。

### 任务执行

* 一轮只完成一个明确目标。
* 修改、构建、测试、NX 实测分开进行。
* 未经要求，不同时实现多个新功能。
* 遇到异常先报告实际错误，不自行扩大修改范围。
* 如果长时间没有可见进展，停止并汇报当前状态。
* 每轮报告修改文件、验证结果和遗留问题。


### 当前开发顺序

1. 在真实 NX2512 中验证恢复后的 WinForms 窗口（单实例、无模式、活动部件读取与刷新）。
2. 现代化界面改造（WinForms 自绘、统一样式、圆角卡片、现代布局）。
3. 开发聊天输入与消息展示。
4. 开发文件附件。
5. 接入独立可配置的模型 API。
