# 开发规则（DEVELOPMENT_RULES）

> 本文件集中记录不可擅自改变的约束。开始任何任务前先读一遍；违反强制约束的代码即使能编译、能通过测试也不能合入。
> 完整产品规则另见根目录 `CLAUDE.md`（AI 开发执行约束）。两者冲突时以更严格者为准。

## 一、强制约束（违反即缺陷）

### 运行环境与架构

1. 目标平台为 **Siemens Designcenter NX2512**，插件以 in-process 方式运行在当前 NX 进程内，使用该进程的 NXOpen Session。禁止启动第二个 NX 进程、禁止 `run_journal.exe` 作为交互主路径（仅可作为验证工具）。
2. 保持现有 C# 分层：`Core`（模型/契约，不引用 NXOpen）→ `UI`（WinForms，不引用 NXOpen）→ `NxHost.NX2512`（唯一引用 NXOpen 的适配层）→ `Entry`（MenuScript 入口）。版本相关代码只允许出现在 NxHost 层。
3. **UI 技术栈已定案为 WinForms**（2026-10-14 用户拍板）。不引入 WPF、WebView、浏览器前端、第三方 UI 框架或新 NuGet 依赖。根因与历史见 `PROJECT_STATUS.md` 及 `WORK_LOG.md` 2026-10-14 条目——NX2512 进程内 WPF 被 WindowsBase 桩遮蔽锁死，不是偏好问题。
4. 不引入 MCP、GUI Bridge、外部编码代理、额外后台服务或 out-of-process 桥。
5. 模型通过**类型化、已注册的工具**操作 NX。禁止执行模型生成的任意 C# 代码、`eval`、动态编译或字符串拼接调用。
6. 不混用不同 NX 运行上下文/版本的程序集；不得把 NXOpen.dll 复制进插件部署目录；必须复用 NX 的 ALC 中已加载的 NXOpen 实例（单一类型宇宙——违反会导致 `Session→TaggedObject` cast 崩溃，阶段 0 已实测）。

### 线程与生命周期

7. HTTP/网络等待**禁止同步阻塞 NX UI 线程**；所有模型请求异步执行。
8. WinForms 控件只允许在 UI 线程更新；异步延续必须经 SynchronizationContext / BeginInvoke 回 UI 线程。窗口关闭后不得再触碰其控件与 ChatLog（Detach 机制）。
9. NXOpen 模型读写必须通过已验证的 NX 线程调度机制在合法线程执行。

### 几何修改安全

10. 几何修改前：校验参数（含 NaN/±∞ 与 sanity bound）、检查当前部件与所需引用；引用或意图不明确时**询问用户**，不得猜测。
11. 执行模型修改前需要用户确认；失败时回滚（Undo mark）并记录真实错误，**不得声称成功**。
12. 修改后通过查询 NX 验证真实几何结果，不信任模型返回文本。禁止在测试中修改用户零件；测试用零件必须是一次性可丢弃的。
13. `Builder` 等一次性 NXOpen 资源用完必须 `Destroy()`。

### 配置与敏感信息

14. 测试**禁止访问、读取或覆盖真实用户配置**（`%APPDATA%\NX AI Assistant\model-config.json`）。所有配置测试必须通过注入的隔离临时路径执行（`ModelService.Save/LoadConfiguration` 的 `configPath` 参数）。历史教训见 WORK_LOG 2026-10-15 修复轮。
15. 不记录、不打印、不提交 API Key、Authorization 头、解密后配置或敏感请求数据。密钥经 DPAPI 加密落盘；文档、日志、错误信息、聊天区一律不得出现密钥原文。
16. **模型/provider 配置保持 `nvidia_nim/z-ai/glm-5.3` 不变**；不修改用户的 API Key、真实模型连接配置或任何 `.bak` 备份。
17. 用户配置与密钥不进版本化二进制/安装目录。

### Git 与工作区

18. 未经用户明确授权，不执行 `git commit`、`push`、`reset`、`clean`、分支切换/强切。不覆盖、重置或丢弃已有修改；不得把 worktree 当主项目目录。
19. 开始任务先确认工作目录、分支与 `git status`；禁止为了整理文档而恢复/清理他人（或先前轮次）的未提交成果。

## 二、推荐实践（违反需说明理由）

1. 一轮只完成一个明确目标；修改、构建、测试、NX 实测分开进行。
2. 只读完成当前任务必需的文件；扩大读取范围先说明原因。
3. 每个可交付版本走一遍 `RELEASE_CHECKLIST.md`。
4. 新功能先补离线可重复测试（不依赖 NX），NX 依赖行为明确区分 mock 测试与实机验收。
5. 状态表述使用 `PROJECT_STATUS.md` 的统一术语（源码确认 / 自动化测试通过 / Release 构建通过 / NX2512 实机通过 / 外部 API 通过 / 待验证 / 已知限制），不用笼统"已完成"。
6. 任务结束后更新 `PROJECT_STATUS.md` 与 `WORK_LOG.md`（阅读顺序与更新规则见两文件文末）。
7. 新 NX 版本适配前先跑 `tools\NxCompatProbe` 并阅读 `SUPPORTED_NX_VERSIONS.md` 的升级流程。
