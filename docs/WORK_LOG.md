# 开发日志（WORK_LOG）— 追加式

> 每个阶段/任务完成后在**文末追加**一条，不改写历史条目。每条注明：日期、任务、主要文件、解决的问题、验证结果（标注证据来源）、遗留风险、详细报告链接。
> "会话报告" = 当时对话中记录、本次归档时未逐项重新核验；"已核实" = 归档时重新执行/检查过。测试数字只代表当时的工作区状态。

---

## 2026-10-13 · 阶段 0：插件基础、NX 会话读取与聊天窗口壳

- **任务**：clean-room 建立全新仓库与分层解决方案；NX2512 环境只读调查；插件加载、菜单入口、活动部件只读读取验证。
- **主要文件**：`src/NXAI.Assistant.Core`、`src/NXAI.Assistant.UI`、`src/NXAI.Assistant.NxHost.NX2512`、`src/NXAI.Assistant.Entry`、`tools/NxCompatProbe`、`build.ps1`、`deploy/nxai_custom`。
- **解决的问题**：确认 NX2512 默认 .NET 8（CoreCLR 8.0.31）；确定 NXOpen 引用来源 `NXBIN\managed_core`（Private=false）；建立 custom directory 部署与 MenuScript 入口；**实测确立 NXOpen 单一类型宇宙硬约束**（不得 LoadFrom 复制第二份 NXOpen）。
- **验证结果**：run_journal 真实进程内只读验证通过（`artifacts/nxload_validation_result.txt`）【会话报告 + artifacts 存档】；构建 0 警告 0 错误【会话报告】。
- **遗留**：交互式 GUI（modeless 窗口单实例等）当时未实测。
- **报告**：[PHASE_0_REPORT.md](PHASE_0_REPORT.md)、[TECHNICAL_SPIKE_RESULTS.md](TECHNICAL_SPIKE_RESULTS.md)。

## 2026-10-14 · UI 技术栈定案：WPF 放弃，WinForms 永久

- **任务**：WPF 加载器实验（Default.Resolving 桌面框架解析）在真实 NX 中多轮实测失败后的最终决策。
- **解决的问题**：根因闭合——.NET 8 基础运行时自带 `WindowsBase` 4.0.0.0 桩 + NX nxclrldr 全量 TPA 枚举 + Default ALC 名字保护，三者组合使真 WPF WindowsBase 8.0 在 NX 进程内无法绑定；WinForms 链无桩遮蔽可干净绑定。WPF 迁移代码全部回退。
- **验证结果**：全量构建+部署通过【会话报告】。
- **影响**：后续所有 UI 为 WinForms 自绘；此为硬决策，不得重启 WPF 实验（除非 NX 升级改变前提）。
- **详细根因存档**：`PROJECT_STATUS.md` 之前版本及会话记录；要点已并入 [DEVELOPMENT_RULES.md](DEVELOPMENT_RULES.md) 强制约束 #3。

## 2026-10-15 · 修复轮：配置安全与测试污染处置

- **任务**：修复 checkpoint `30181cb`（2026-10-14 23:43）中半成品阶段 1 代码。
- **主要文件**：`ModelService.cs`、`NXAI.Assistant.UI.csproj`、`BlockFeatureCreator.cs`、新增 `BlockRequestValidator.cs`。
- **解决的问题**：① ModelService 配置路径硬编码导致"迁移测试"用假密钥**覆盖用户真实配置**（污染文件已保全为 `model-config.json.corrupted-by-tests.bak`，未删除）；② UI csproj 用 NoWarn 压制 CS8602/CS8618 掩盖坏代码，已还原；③ BlockFeatureCreator 校验与 NXOpen 调用混同导致 NX 外 JIT 失败——校验抽到 Core 层 `BlockRequestValidator`（NXOpen-free，可离线测试）；④ 手拼 JSON model 名未加引号，改 System.Text.Json；⑤ 删除 clean-room 下不存在的"旧 AES/Base64 迁移"死代码。
- **验证结果**：27 项测试通过【会话报告】。
- **教训**：配置测试必须注入隔离路径；C# `"\x01bad"` 是贪婪十六进制转义。

## 2026-10-15 · 稳定性修复轮：URL 规范化、错误分类与资源管理

- **主要文件**：`ModelService.cs`、`BlockFeatureCreator.cs`、`BlockRequestValidator.cs`、`ModelSettingsForm.cs`。
- **解决的问题**：① `NormalizeChatCompletionsUrl`（裸域名/尾斜杠/任意 /vN 版本段；拒绝相对/非 HTTP/查询/片段）；② `ModelChatResult`/`ModelChatError` 错误分类（401/403/404/400/429/5xx/网络/解析）；③ **发现并修复 P1：Authorization Bearer 头从未发送**；④ 注入式 HttpClient 支持测试；⑤ BlockFeatureCreator InvariantCulture("R") 数值格式、finally `Builder.Destroy()`、失败 `UndoToMark` 回滚 + `%TEMP%\NXAI_Assistant_operation_error.log`；⑥ BlockRequestValidator 补 NaN/±∞ 漏洞（`NaN<=0` 为 false 曾放行）与 sanity bound（原点 1e9mm、尺寸 1e6mm）；⑦ ModelSettingsForm 保存 try/catch + 失败不关窗 + 旧格式文件红字解释不改写。
- **验证结果**：81 项测试（80 过 1 跳）【会话报告】；`Session.UndoToMark`/`Builder.Destroy()` 签名经 NXOpen.xml 确认【会话报告】。

## 2026-10-14 ~ 2026-10-15 · Phase 1：真实模型对话链路接入

- **任务**：聊天窗口接入可配置模型 API，实现多轮真实对话（不涉及 NX 建模）。
- **主要文件**：`ModelService.cs`（Chat Completions 请求、配置加载/保存/DPAPI）、新增 `ChatSessionController.cs`（发送/取消/历史/生命周期协调、SynchronizationContext 回 UI 线程、窗口关闭 Detach 保护）、`AssistantChatForm.cs`（接入设置入口与发送）、`ModelSettingsForm.cs`、配套离线测试（`ChatCompletionsUrlTests`、`ChatSessionControllerTests`、`ModelServiceChatTests`、`ModelServiceConfigurationTests`）。
- **解决的问题**：模型请求与配置在不重启 NX 的前提下热加载；Info/Error 本地条目不发给模型；防重复发送；历史按 20 条上限裁剪；密钥留空保留语义。
- **验证结果**：代码审查时点 94 通过 / 0 失败 / 1 跳过【会话报告，对应审查前工作区】。本阶段与修复轮成果截至 2026-10-15 均为**未提交**工作区状态（分支 `recovery/pre-review-20261014-234252`）。

## 2026-10-15 · 聊天 UI 重构（Claude 风格三区布局）

- **任务**：修复截图暴露的 UI 问题（设置窗控件裁切、主窗无输入框、ListBox 蓝色高亮），按 Claude 桌面风格重构；只动 UI 与界面协调逻辑。
- **主要文件**：`AssistantChatForm.cs`（全重写）、`ModelSettingsForm.cs`（全重写）、新增 `ChatPalette.cs`、`ChatSessionControllerTests.cs` 增补 1 项。后端零修改。
- **解决的问题**：① 主窗三区布局（224px 侧栏 / 头部摘要 / AutoScroll 消息卡片流 / 底部固定输入区），消息按角色渲染（用户右侧气泡、助手通栏、Info 居中、Error 红气泡），窗口缩放自动重排无水平裁切；② 输入区始终可见，Enter 发送 / Shift+Enter 换行、请求中禁发送可取消、失败不丢原始输入；③ 设置窗两列 TableLayoutPanel 根治裁切，内联错误替代 MessageBox，焦点/Tab 序/密码遮挡/留空保留密钥全部落实；④ 新对话：请求中禁止、有内容先确认、`ChatLog.Clear()` 同步清空模型上下文（外发消息完全由 ChatLog 构建）。
- **验证结果**：Release 构建 0 警告 0 错误；**96 项：95 通过 / 0 失败 / 1 跳过**（归档时已重新执行核实，对应当前工作区）；部署 4 DLL 与 Release 产物 SHA-256 一致【已核实】；NX 外离线渲染冒烟（两窗体截图，含最小尺寸与消息注入）无布局异常【会话报告】。
- **遗留**：NX 内行为待实机验收（下方条目完成）。

## 2026-10-15 · NX2512 UI 实机验收：六项全部通过

- **验收人**：用户（真实 NX2512 + 上述部署产物）。证据：用户实测反馈（对话内确认），未附截图/日志存档。
- ① 主窗口布局（侧栏/消息区/输入区）✅
- ② 模型设置窗口（控件完整可见、保存/取消、取消不改配置）✅
- ③ 真实模型对话（发送、回复显示、自动滚动）✅
- ④ 请求期间行为（取消、窗口关闭、NX 主界面可操作）✅
- ⑤ 新建对话及旧上下文清理（旧消息不再发给模型）✅
- ⑥ Windows 显示缩放（100%/150%）✅
- **状态**：阶段 1 验收通过。详见 [reports/PHASE_1_CHAT_AND_UI_ACCEPTANCE.md](reports/PHASE_1_CHAT_AND_UI_ACCEPTANCE.md)。

## 当前未提交工作区（截至 2026-10-15 归档时）

分支 `recovery/pre-review-20261014-234252`，最近提交 `30181cb`。**未提交**：

- 已跟踪修改（12）：`deploy/nxai_custom/application/` 4 个 DLL；`ModelService.cs`、`NXAI.Assistant.Core.csproj`、`BlockFeatureCreator.cs`、`AssistantChatForm.cs`、`ModelSettingsForm.cs`、`NXAI.Assistant.UI.csproj`、`tests/.../BlockFeatureCreatorTests.cs`；删除 `tests/.../ModelServiceMigrationTests.cs`。
- 未跟踪（7）：`src/NXAI.Assistant.Core/BlockRequestValidator.cs`、`src/NXAI.Assistant.UI/ChatPalette.cs`、`src/NXAI.Assistant.UI/ChatSessionController.cs`、`tests/.../ChatCompletionsUrlTests.cs`、`ChatSessionControllerTests.cs`、`ModelServiceChatTests.cs`、`ModelServiceConfigurationTests.cs`。
- 处置：等待用户审查后决定提交/分批提交；未经授权不动 Git。
