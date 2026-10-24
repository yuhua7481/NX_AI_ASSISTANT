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

## 2026-10-16 · 阶段 2：类型化工具调用与第一个建模工具（create_block）源码与测试完成

- **任务**：Phase 2 全部五个子阶段（A 工具契约 / B ModelService 工具协议 / C 安全建模入口 / D 确认 UI 与状态机 / E 构建部署核验与文档）。真实 NX 实机验收仍待用户执行。
- **主要文件**（修改）：`ModelService.cs`、`BlockRequestValidator.cs`、`ChatSessionController.cs`、`AssistantChatForm.cs`、`NxHostBoot.cs`、`ChatSessionControllerTests.cs`、deploy 4 DLL。（新增）Core：`ToolContract.cs`、`CreateBlockTool.cs`、`NxToolExecutor.cs`、`OutboundMessage.cs`、`ChatCompletionResult.cs`；NxHost：`BlockToolExecutor.cs`；UI：`ToolConfirmationFlow.cs`；测试：`CreateBlockToolContractTests.cs`、`ModelServiceToolProtocolTests.cs`、`ChatSessionControllerToolTests.cs`。
- **解决的问题**：① 工具契约 + create_block 唯一注册（schema additionalProperties=false，拒绝未知字段/非法 JSON/NaN/非正数/越界，单位换算到 mm，界限沿用既有 Validator 未放宽）；② ModelService 增 tools 声明与 tool_calls 解析（空 assistant content + tool_calls 合法；并行调用拒绝；纯文本路径无回归）；③ 新安全入口 `NxToolExecutor.ExecuteCreateBlock`：不 SaveAs/不自动保存/不改路径，UndoMark 回滚 + builder Destroy + UF_MODL_ask_bounding_box 执行后验证（容差 0.01mm）+ 非毫米部件拒绝 + 工作部件与确认时不符拒绝；④ 确认流：尺寸/单位/原点/目标部件确认卡，默认"否"，确认时现场读部件路径供执行器二次核对，窗口 Dispose 即 abort；⑤ 回合状态机：每回合至多 1 次建模执行、2 回合上限、最终回合不广告 tools；⑥ 调试：xUnit 宿主崩溃根因隔离（裸 await TCS 完成路径与 fire-and-forget 链在 xUnit context 下冲突；console 复现正常），测试等待改为 WhenAny+Delay 模式。
- **验证结果**【已核实，本会话最终态】：Release 构建 0 警告 0 错误；测试 **144 项 = 143 通过 / 0 失败 / 1 跳过**（跳过项为需真实 NX 的旧验收测试）；`git diff --check` 通过；4 个部署 DLL SHA-256 与 Release 一致；部署脚本不触碰用户配置。
- **遗留**：真实 NX2512 实机验收未做（八步一次性测试部件流程，见任务书第十二节）；并行工具调用、对话持久化、附件未实现（阶段边界）。
- **报告**：[reports/PHASE_2_TOOL_CALLING_AND_CREATE_BLOCK.md](reports/PHASE_2_TOOL_CALLING_AND_CREATE_BLOCK.md)。

## 当前未提交工作区（截至 2026-10-16 归档时）

分支 `recovery/pre-review-20261014-234252`，远端已同步至 `9cfd87a`（Phase 1 同步完成核验）。**未提交**：

- 已跟踪修改（9）：`BlockRequestValidator.cs`、`ModelService.cs`、`NxHostBoot.cs`、`AssistantChatForm.cs`、`ChatSessionController.cs`、`tests/.../ChatSessionControllerTests.cs`、deploy 4 DLL。
- 未跟踪（10）：Core 5 个（`ChatCompletionResult`、`CreateBlockTool`、`NxToolExecutor`、`OutboundMessage`、`ToolContract`）、`BlockToolExecutor.cs`（NxHost）、`ToolConfirmationFlow.cs`（UI）、测试 3 个（`ChatSessionControllerToolTests`、`CreateBlockToolContractTests`、`ModelServiceToolProtocolTests`）、本阶段报告。
- 处置：等待用户审查后决定提交；未经授权不动 Git。

## 2026-10-16 · Phase 2 收尾：实机验收归档、安全边界复核与版本检查点准备

- **任务**：归档用户执行的八项 NX2512 实机验收；针对性安全边界复核；核实工作区/GitHub 状态；准备版本检查点（不执行 Git 操作）。
- **实机验收**：八项全部通过——**证据为用户报告（对话内确认）**，本轮未重新执行任何实机步骤：① 取消确认→无几何变化 ② 确认创建→实体出现 ③ 实测尺寸/包围盒与请求一致 ④ 部件路径未变、未自动保存 ⑤ 非法尺寸被拒绝 ⑥ 再次有效请求成功 ⑦ 工作部件切换后确认→中止并重提示 ⑧ NX 退出时窗口不挂起（Phase 1 遗留项一并验证）。此前构建/测试/部署哈希数字（0 警告 0 错误、144 项=143/0/1、4 DLL SHA-256 一致）为上一会话结果，本轮未重跑。
- **安全边界复核**【本轮源码复核，逐项核实】：① `BlockToolExecutor.ExecuteCreateBlock` 无 `SaveAs`/`Save`/重命名（grep 证实，仅注释提及 SaveAs 语义）；② 旧 `BlockFeatureCreator`（含 SaveAs）在 src/ 仅剩自身定义，无任何调用点（仅测试文件引用，且该测试按设计跳过）；③ 执行前强制确认（`ProcessToolCallAsync`：decision==null→取消、!Confirmed→UserCancelled、_detached→取消，测试固化）；④ 确认后部件变化拒绝（`BlockToolExecutor.cs:68-76`，比对 FullPath，PreconditionsChanged）；⑤ 未注册工具/非法参数/无效 JSON 均结构化拒绝（UnknownTool/InvalidParameters，executor 不被调用，测试固化）；⑥ 成功仅由 UF 包围盒+实体计数+JournalIdentifier 验证后返回；⑦ 失败/验证不过两条路径均 UndoToMark 回滚并报告回滚结果；⑧ 密钥扫描：无 `sk-`/`nvapi-` 等硬编码，API Key 仅经 DPAPI 落盘，日志无密钥。
- **Git 状态**【已核实】：本地 HEAD `9cfd87a`；`origin/recovery...` = `9cfd87a`、`origin/main` = `14c27d4`（本地 refs；本轮 `git fetch`/`ls-remote` 因 GitHub 连接失败，实际远端待网络恢复确认）；备份 `D:\AI\NX_AI_ASSISTANT_backup_20261015_230825` 存在；`git diff --check` 通过。
- **版本检查点准备**（未执行 commit/push，待授权）：待同步 24 文件 = 已跟踪修改 13（源码 6 + 文档 3 + 部署 DLL 4）+ 未跟踪 11（源码 8 + 文档 1）；提交说明与敏感信息检查清单见本条目下方"检查点准备"。
- **遗留**：远端状态未实测确认；`tests/TestResults/` 为本轮 blame 调试临时产物，不应入库。
- **报告**：[reports/PHASE_2_TOOL_CALLING_AND_CREATE_BLOCK.md](reports/PHASE_2_TOOL_CALLING_AND_CREATE_BLOCK.md)（本轮已追加实机验收与复核记录）。

## 检查点准备（2026-10-16，待用户授权执行）

1. **待同步文件清单（24 项 + 1 排除项）**
   - 源码（10）：修改 `BlockRequestValidator.cs`、`ModelService.cs`、`NxHostBoot.cs`、`AssistantChatForm.cs`、`ChatSessionController.cs`；新增 `ChatCompletionResult.cs`、`CreateBlockTool.cs`、`NxToolExecutor.cs`（Core）、`OutboundMessage.cs`、`ToolContract.cs`（Core）、`BlockToolExecutor.cs`（NxHost）、`ToolConfirmationFlow.cs`（UI）——其中修改 6、新增 7（含 2 个 NxHost/UI 新文件）。
   - 测试（4）：修改 `ChatSessionControllerTests.cs`；新增 `ChatSessionControllerToolTests.cs`、`CreateBlockToolContractTests.cs`、`ModelServiceToolProtocolTests.cs`。
   - 文档（4）：修改 `PROJECT_STATUS.md`、`WORK_LOG.md`、`ARCHITECTURE_AND_PHASES.md`、`RELEASE_CHECKLIST.md`；新增 `docs/reports/PHASE_2_TOOL_CALLING_AND_CREATE_BLOCK.md`。
   - 部署 DLL（4）：`deploy/nxai_custom/application/` 4 个（与 Release 产物哈希一致，2026-10-15/16 核验）。
   - **排除**：`tests/NXAI.Assistant.Tests/TestResults/`（崩溃调试临时产物，勿提交）。
2. **提交说明（预期）**：`feat: Phase 2 typed tool calling + create_block with confirmation and postcondition verification`——包含工具契约/注册表、ModelService 工具协议、安全建模入口（无 SaveAs）、确认流、执行后验证、八项实机验收记录；单提交。
3. **提交前敏感信息检查**：`git add` 前逐文件复查——无 API Key/Authorization 头原文（扫描已通过：`sk-`/`nvapi-` 无命中；DPAPI blob 仅落 `%APPDATA%` 不进仓库）；排除 `TestResults/`、`*.dmp`、`%TEMP%` 日志、`*.bak`；确认无临时探测脚本残留。
4. **推送目标**：`origin/recovery/pre-review-20261014-234252`（普通 push，fast-forward 自 `9cfd87a`）。与 `origin/main`（`14c27d4`）的关系：恢复分支不含 main 新提交，main 亦不含本分支成果；**不合并 main、不强制推送、不改写历史**。

> **远端状态更正（2026-10-16 收尾轮内）**：多次重试 `git ls-remote` 均无法连接 GitHub（连接重置/超时），远端实际状态无法本轮确认。以本地 refs 为准：`origin/recovery/pre-review-20261014-234252` 最后已知 `9cfd87a`、`origin/main` = `14c27d4`、本地 HEAD = `9cfd87a`（Phase 2 未提交、未推送）。文档中远端表述均按"最后已知 + 待网络恢复确认"记录。本轮 git diff --check 通过，备份目录存在，未执行任何 commit/push/reset/clean。

## 2026-10-16 · Phase 2 检查点：已提交并推送（用户授权执行）

- **提交**：`6b030bb` `feat: Phase 2 typed tool calling + create_block with confirmation and postcondition verification`——25 文件，+2621/−118；排除 `tests/TestResults/`（保持未跟踪）。
- **推送**：`git push origin recovery/pre-review-20261014-234252` 成功（`9cfd87a..6b030bb`，普通 fast-forward，无强制）；第三次重试成功（前两次 GitHub 连接失败）。`git ls-remote` 复核远端 refs = `6b030bb`，与本地一致。
- **提交前检查**：逐文件暂存（未用 `git add .`）；暂存 diff 密钥扫描 0 命中；无暂存临时文件。
- **工作区现状**：干净（仅剩未跟踪 `tests/TestResults/` 调试产物）；HEAD = 远端 = `6b030bb`。
- **与 main 关系**：`origin/main` 仍 `14c27d4`，未合并、未推送 main——保持恢复分支独立，合并决策归用户。

## 2026-10-16 · 阶段 3：create_cylinder 圆柱体工具（源码 + 离线测试 + 部署核验完成）

- **任务**：新增第二个受控建模工具 `create_cylinder`，复用现有工具注册/确认/回填机制（无平行框架）；真实 NX 实机验收待用户执行。本任务未授权 Git 操作，成果未提交。
- **主要文件**（修改）：`CreateBlockTool.cs`（注册双工具）、`NxToolExecutor.cs`（接口 +ExecuteCreateCylinder）、`BlockToolExecutor.cs`（圆柱执行 + VerifyCylinderCreated + Rollback/LogFailure 参数化）、`ChatSessionController.cs`（按工具名分派解析与执行）、`AssistantChatForm.cs`（确认卡按类型显示）、`CreateBlockToolContractTests.cs`、`ChatSessionControllerToolTests.cs`。（新增）`CreateCylinderSchema.cs`、`CreateCylinderTool.cs`、`GeometryToolJson.cs`、`CreateCylinderToolContractTests.cs`、`reports/PHASE_3_CREATE_CYLINDER.md`。
- **解决的问题**：① NXOpen 事实核验先行（XML 文档 + managed_core 元数据探测）：CylinderBuilder `Type=AxisDiameterAndHeight`、Diameter/Height 为 Expression（SetFormula 设值）、Origin 为 Point3d（底面圆心）、Direction 默认 +Z（实现保持 builder 默认，模型不可自选轴向）；② 单位换算与字段校验抽为 `GeometryToolJson` 共享助手（block/cylinder 复用，界限沿用 BlockRequestValidator 未放宽，直径同时约束 X/Y）；③ 执行后验证：实体计数差=1 + JournalIdentifier 差集 + UF 包围盒 X/Y=直径、Z=高（容差 0.01mm），体积校验未加（MeasureManager 实机稳定性未验证，不引入不可靠估算）；④ 确认卡按 ParametersKind 分支显示（圆柱：直径/高/原点(底面圆心)/+Z 轴），未重构界面；⑤ 混合工具序列由回合上限安全拒绝。
- **验证结果**【已核实，本轮执行】：`git diff --check` 通过；Release 构建 0 警告 0 错误；测试 **165 项 = 164 通过 / 0 失败 / 1 跳过**（跳过项为需真实 NX 的旧验收测试）；部署 4 DLL SHA-256 与 Release 一致；build.ps1 不触碰用户配置。
- **遗留**：① 九项实机验收待用户执行（重点：CylinderBuilder.SetFormula 真实行为、圆柱包围盒读数、create_block 无回归）；② GitHub 本轮多次 ls-remote 连接失败（远端状态未实测确认）；③ 工作区 Phase 3 成果未提交（本任务未授权 Git 操作）。
- **报告**：[reports/PHASE_3_CREATE_CYLINDER.md](reports/PHASE_3_CREATE_CYLINDER.md)。

## 2026-10-16 · 阶段 4：四种基本体统一协调框架 + create_cone/create_sphere（源码 + 测试完成）

- **任务**：把 block/cylinder/cone/sphere 四种基本体放入统一协调框架（复用现有注册表/解析/确认/回填机制，无 shape 字符串分发）；新增圆锥与球体；保留 block/cylinder 既有行为。实机验收与部署复制待用户配合执行；本任务未授权 Git 操作，成果未提交。
- **开始前保护**：新备份 `D:\AI\NX_AI_ASSISTANT_backup_20261016_092605`（含 Phase 3 未提交成果，仓库外）；`git ls-remote` 核验成功——远端 recovery 分支 = 本地 `7cf7dce`（Phase 2 检查点已同步确认）。
- **主要文件**（修改）：`CreateBlockTool.cs`（4 工具注册）、`NxToolExecutor.cs`（接口 +cone/sphere）、`BlockToolExecutor.cs`（+ExecuteCreateCone/ExecuteCreateSphere；前置检查抽为 TryResolveWorkPart 共享；Rollback/LogFailure 参数化；+VerifyConeCreated/VerifySphereCreated）、`ToolConfirmationFlow.cs`（ForCone/ForSphere）、`ChatSessionController.cs`（四工具类型化分派）、`AssistantChatForm.cs`（确认卡四分支）、2 个旧契约测试、`ChatSessionControllerToolTests.cs`（fake 执行器扩展 + 新用例）。（新增）`CreateConeTool.cs`、`CreateConeSchema.cs`、`CreateSphereTool.cs`、`CreateSphereSchema.cs`、`CreateConeToolContractTests.cs`、`reports/PHASE_4_PRIMITIVES_FRAMEWORK.md`。
- **NXOpen 事实核验**（XML 文档 + 元数据探测）：ConeBuilder `DiametersAndHeight`（builder 默认）+ `TopDiameter.Value=0` 为文档默认合法值（真圆锥）；放置经 `Axes.CreateAxis(Point, Direction, UpdateOption)`（ConeBuilder 无 Origin 属性，签名核实）；SphereBuilder `CenterPointAndDiameter`（CenterPoint 为 NXOpen.Point，经 PointCollection.CreatePoint）。体积校验（MeasureManager）未加——实机稳定性未验证不引入不可靠估算。
- **验证结果**【已核实，本轮执行】：`git diff --check` 通过；Release 构建 0 警告 0 错误；测试 **170 项 = 169 通过 / 0 失败 / 1 跳过**（跳过项需真实 NX）；用户退出 NX 后重跑 `build.ps1` 部署成功，4 DLL SHA-256 与 Release 一致。
- **遗留**：① 十项实机验收（圆锥/球体目视形状核对为关键项）；② 工作区未提交（未授权）。
- **报告**：[reports/PHASE_4_PRIMITIVES_FRAMEWORK.md](reports/PHASE_4_PRIMITIVES_FRAMEWORK.md)。

## 2026-10-16 · Phase 4.1：create_cone 支持圆台（top_diameter）+ 聊天 Markdown 显示修复

- **任务**：① `create_cone` 增加可选 `top_diameter`（0 或省略=标准圆锥；0 < top < base=圆台；负值/≥底面/NaN/Infinity/越界拒绝）——复用既有 ConeBuilder DiametersAndHeight 路径（TopDiameter=0 是文档默认合法值，真圆锥），SetFormula 设实际值；确认卡与结果消息区分"圆台/圆锥体"。② 聊天 Markdown 显示：模型回复中 `**加粗**`、`-/* 列表`、空行分段原样显示星号——新增 `ChatMarkdownParser`（显示层解析：成对 `**` 且内容首尾非空格才为粗体，CommonMark flanking 规则，杂散星号保持字面）+ `ChatMarkdownRenderer`（每行 FlowLayoutPanel + 逐 run Label，行内粗体真实生效）；仅 Info 短提示保持纯 Label。原始消息全文保留在 ChatLog（复制/tooltip/模型上下文不受影响），不解析 HTML、不改聊天 UI 结构。
- **开始前保护**：新备份 `D:\AI\NX_AI_ASSISTANT_backup_20261016_101242`（含 Phase 4 全部未提交成果）；旧备份未动；未执行 Git 操作。
- **主要文件**（修改）：`CreateConeTool.cs`（+TopDiameter 参数与校验）、`CreateConeSchema.cs`（schema+描述）、`BlockToolExecutor.cs`（SetFormula 实际值、形状名区分、回滚名区分）、`ChatSessionController.cs`（圆台 Info 文案）、`AssistantChatForm.cs`（MessageItem 渲染改 Markdown 视图 + 确认卡圆台分支）、`NXAI.Assistant.UI.csproj`（InternalsVisibleTo 测试）、`ChatSessionControllerToolTests.cs`（圆台用例 2 项）。（新增）`ChatMarkdownRenderer.cs`、`ChatMarkdownParserTests.cs`（10 项）、`CreateConeToolContractTests.cs`（13 项——补上 Phase 4 漏建的 cone 契约测试文件，Phase 4 报告中该数字有误，本次更正）。
- **验证结果**【已核实，本轮执行】：`git diff --check` 通过；Release 构建 0 警告 0 错误；测试 **197 项 = 196 通过 / 0 失败 / 1 跳过**（跳过项需真实 NX）；NX 未运行，部署成功，4 DLL SHA-256 与 Release 一致。
- **遗留**：真实 NX2512 验收待用户执行（圆台几何 + Markdown 显示 + 四形状无回归）；Phase 4 十项验收仍待执行。

## 2026-10-16 · Phase 4.2：聊天正文改只读 RichTextBox（修复文本错位/缺失 + 选择复制）

- **任务背景**：Phase 4.1 的 FlowLayoutPanel + 多 Label 拼接方案在实际运行中文字片段缺失、拼接错位、布局挤压；消息不可选择。根因（代码审查）：行内强制换行 spacer（Width≈int.MaxValue/4）在限宽 FlowLayoutPanel 中引发溢出，后续控件被挤出布局；Label 天生不可选择。
- **修复**：消息正文改用**只读 RichTextBox**（无边框、背景色随气泡/正文、WordWrap、ScrollBars=None）+ 程序生成 RTF：
  - 新增 `ChatRtfBuilder.cs`（含 `ChatMarkdownParser`/`ChatMarkdownRun`/`ChatMarkdownBlock`，自旧渲染器迁入）：`**粗体**`（CommonMark flanking 规则）、`- `/* ` 列表（\bullet）、段落（\par）、行内换行（\line）；所有非 ASCII 以 \uN? 转义（中文/emoji 不丢字）；`{}\` 转义、控制字符丢弃——模型输出无法注入 RTF 控制字。
  - `AssistantChatForm.MessageItem`：user/assistant/error 正文 = RichTextBox；ContentsResized 驱动高度自适应（+2 边距防末行隐藏）；ApplyViewportWidth 重设宽度并重灌 RTF 触发重排。Info 短提示保持纯 Label（无需选择）。
  - 右键菜单升级：**复制**（有选中复制选中，无选中复制整条）、**全选**、**复制整条消息**；RichTextBox ShortcutsEnabled=true，Ctrl+C 原生（窗体 KeyPreview 无全局拦截）。复制内容为纯文本（SelectedText），不含 RTF 控制码。
  - 删除 `ChatMarkdownRenderer.cs`（Label 拼接方案整体移除）。
- **开始前保护**：新备份 `D:\AI\NX_AI_ASSISTANT_backup_20261016_105917`；旧备份未动；未执行 Git 操作。
- **主要文件**（修改）：`ChatRtfBuilder.cs`（新增，含 parser 迁移）、`AssistantChatForm.cs`（MessageItem 重写 + 右键菜单）、（删除）`ChatMarkdownRenderer.cs`、（新增测试）`ChatRtfBuilderTests.cs` 13 项。
- **验证结果**【已核实，本轮执行】：`git diff --check` 通过；Release 构建 0 警告 0 错误；测试 **210 项 = 209 通过 / 0 失败 / 1 跳过**（跳过项需真实 NX）；NX 未运行，部署成功，4 DLL SHA-256 与 Release 一致。RTF 文本完整性有自动化证据（正文往返一致、无 ** 残留、转义注入被阻断）；**RichTextBox 实际显示/选择/复制手感属 WinForms 运行时行为，须按第九节手动验收清单在 NX2512 实测**，本轮未声称通过。
- **遗留**：实机手动验收（八步清单）待用户执行；Phase 4 十项验收仍待执行。

## 2026-10-16 · 检查点：Phase 3/4/4.1/4.2 已提交并推送（用户授权执行）

- **提交**：`b1bdc90` `feat: Phase 3/4/4.1/4.2 — cylinder, cone, sphere tools, frustum support, and selectable chat rendering`——30 文件，+3023/−98；排除 `tests/TestResults/`（保持未跟踪）。
- **推送**：`git push origin recovery/pre-review-20261014-234252` 成功（`7cf7dce..b1bdc90`，普通 fast-forward，首次重试即成功）；`git ls-remote` 复核远端 refs = `b1bdc90`，与本地一致。
- **提交前检查**：逐文件暂存（未用 `git add .`）；暂存 diff 密钥扫描 0 命中；无临时文件入库。
- **工作区现状**：干净（仅剩未跟踪 `tests/TestResults/`）；HEAD = 远端 = `b1bdc90`。
- **与 main 关系**：`origin/main` = `be1716c`（用户自行前进），未合并、未推送 main——恢复分支独立，合并决策归用户。
- **实机验收状态**：Phase 3/4/4.1/4.2 的 NX2512 实机验收仍未执行（不因提交而视为通过）。

## 2026-10-16 · 阶段 5：NX 状态感知、几何对象捕获与上下文建模（源码 + 离线测试完成）

- **任务**：捕获结构化 NX 上下文快照（工作部件/WCS/活动视图/当前选择分类与几何摘要），注入模型请求；create_cylinder 新增 selected_face_center 面相对放置（选中单个平面中心、沿面外法向）；真实 NX 实机验收待用户执行；本任务未授权 Git 操作，成果未提交。
- **开始前保护**：新备份 `D:\AI\NX_AI_ASSISTANT_backup_20261016_114205`（HEAD=远端=99f7d95，工作区干净）；旧备份未动；未执行 Git 操作。
- **NXOpen 事实核验**（元数据实测 + XML 文档，非猜测）：`NXOpen.Selection.GetNumSelectedObjects/GetSelectedTaggedObject/IsGlobalSelectionActive`（NXOpenUI.dll，读当前全局选择）；`Face.SolidFaceType → Face.FaceType`（Planar/Cylindrical/...）；`UFModl.AskFaceProps(tag, param[2], point[3], unit normal[3], du[3], dv[3], duu[3], dvv[3], uvp[2])`（UV 参数点求值）；`UFEvalsf.AskFaceUvMinmax(tag, uv[4])`（注意在 Evalsf 而非 Eval）；`BasePart.WCS`（大写）/`BasePart.Views.WorkView`。
- **主要文件**（修改）：`NxContextSnapshot.cs`（新增，Core DTO：NxContextSnapshot/SelectionItem/FaceInfo/EdgeInfo/PointInfo/BodyInfo/FeatureInfo/CoordinateSystemInfo/ViewInfo + 限长中文摘要 ToContextSummary，标注"状态数据非用户指令"，不含部件完整路径）；`NxContextReader.cs`（新增，NxHost 采集器：选择分类 Body/Face/Edge/Point/Curve(Line/Arc)/Feature/DatumPlane/DatumAxis/DatumCsys/CSYS/other 兜底，面中心+法向仅平面计算，WCS/视图读取，失败字段进 Unavailable 不编造）；`CreateCylinderTool.cs`+`CreateCylinderSchema.cs`（+placement 枚举 global_origin/selected_face_center，未知模式拒绝，旧行为默认不变）；`BlockToolExecutor.cs`（ExecuteCreateCylinder 分派 + ExecuteCreateCylinderOnSelectedFace：运行时从当前选择解析唯一平面，非面/多面/空选/非平面明确拒绝，面中心+法向放置，禁止回退 global_origin；VerifyFaceCylinderCreated 包围盒包含面中心+对角上限验证）；`ChatSessionController.cs`（StartSend 前捕获快照注入当轮 system 摘要；contextProvider 注入点）；`AssistantChatForm.cs`（面相对确认卡显示面中心/法向）；`NxHostBoot.cs`（接线）。
- **如实声明的限制**：① UV 中点不保证在实际面上（带孔/凹面）——NX2512 托管包装无可靠点-面包含查询，本版不做伪验证；确认卡显示计算的中心供用户目视核对，实机验收覆盖带孔面。② 绝对坐标系（ACS）读取未实现（记入 Unavailable）。③ 顶点（Vertex）无独立 NXOpen 托管类型，选择顶点按 other 兜底（实机确认实际类型后可细化）。④ 面相对圆柱验证为包围盒包含面中心 + 对角上限（倾斜圆柱的精确轴向验证靠实机目视）。
- **验证结果**【已核实，本轮执行】：`git diff --check` 通过；Release 构建 0 警告 0 错误；测试 **226 项 = 225 通过 / 0 失败 / 1 跳过**（新增 NxContextTests 16 项：DTO 摘要/空选/多选截断/长度上限/global_origin 兼容/placement 解析与拒绝/路径不入摘要；既有全部回归通过）；**部署失败——NX 运行中锁定 DLL，待退出后重跑 build.ps1**（构建产物已就绪）。
- **遗留**：① 部署（NX 退出后）；② 14 项实机验收待用户（选择识别/上下文反映/面相对创建/取消/部件切换中止/旧模式回归/复杂面拒绝/无自动保存/聊天无回归）；③ 工作区未提交。

## 2026-10-16 · 阶段 5.1：NX 闪退根因定位修复 + 选中面反馈（源码 + 离线测试 + 部署完成，实机验收待用户）

- **故障**：Phase 5 部署后，选中块体平面发送消息即 NX（ugraf.exe）闪退；选中面无任何聊天反馈。优先级高于新功能。
- **开始前保护**：仓库外完整备份 `D:\AI\NX_AI_ASSISTANT_backup_20261016_crash`（含未提交成果与未跟踪文件，旧备份未动）；故障版本 4 DLL SHA-256 已存档（见 Phase 5.1 报告 §6）；无 git reset/clean/commit/push。
- **根因定位（有实证）**：Windows 事件日志 `.NET Runtime` 2026-10-16 12:57:09 事件，ugraf.exe `AccessViolationException` 完整托管栈：`SendCurrentInput → StartSend → contextProvider → NxContextReader.Capture → CaptureSelection → DescribeFace → UFEvalsf.AskFaceUvMinmax`。**崩溃阶段 B（发送时上下文采集）**；`UF_EVALSF_ask_face_uv_minmax` 为 B-surface（自由曲面）专用求值器，对平面调用违反前置条件触发原生 AV。线程调度无过错（全栈在 NX UI 线程）。
- **第二缺陷（被崩溃掩盖）**：反射核验 `UFModl.AskFaceProps` 真实托管签名为 `(face_id, param[2], point[3], u1[3], v1[3], u2[3], v2[3], unit_norm[3], radii[2])`——Phase 5 代码按 `(tag, param, point, normal, du, dv, duu, dvv, uvp)` 传参，法向实际绑定到 u1（错误向量）、末参实为 radii[2]；长度兼容故不崩但几何数据错误。两处调用点同病。
- **修复**：① 两处 `Evalsf.AskFaceUvMinmax` → `Modl.AskFaceUvMinmax(Tag, uv[4])`（通用面 API，元数据核实）；② 两处 `AskFaceProps` 参数序按反射签名修正，法向取 unit_norm；③ `Core/PhaseLog.cs` 新增（线程安全、绝不抛、512KB 上限、%TEMP%\NXAI_Assistant_phase.log，覆盖采集/选择/面几何/Builder 提交/验证/回滚，无敏感信息）；④ 选中面反馈：发送时显示捕获摘要（含面中心/法向、空选/无部件/失败如实显示）+ 输入区"刷新上下文"按钮（UI 线程采集）；⑤ NxHostBoot provider 失败记录原因；⑥ NxContextReader 类头注释更正并记录教训（签名核实 ≠ 适用域核实）。
- **主要文件**（修改）：`NxContextReader.cs`、`BlockToolExecutor.cs`、`NxHostBoot.cs`、`ChatSessionController.cs`（+DescribeCapturedContext/CaptureContextNow）、`AssistantChatForm.cs`（+刷新按钮）、`NXAI.Assistant.Core.csproj`（+InternalsVisibleTo）；（新增）`Core/PhaseLog.cs`、`tests/PhaseLogTests.cs`、`tests/ContextFeedbackTests.cs`、`docs/reports/PHASE_5_1_CRASH_AND_CONTEXT_FEEDBACK_FIX.md`。
- **验证结果**【已核实，本轮执行】：`git diff --check` 通过；Release 构建 0 警告 0 错误；测试 **239 项 = 238 通过 / 0 失败 / 1 跳过**（新增 13 项）；NX 未运行，build.ps1 部署成功（未签名，与既有结论一致），4 DLL SHA-256 与构建产物逐一一致；修复后部署哈希见 Phase 5.1 报告 §6。**离线通过 ≠ 实机通过：崩溃根除以实机只读验收（Phase 5.1 报告 §5 第一、二项）为准。**
- **遗留**：① Phase 5.1 实机只读验收（8 步，只读先行，任一步异常立即停止）；② Phase 5 全部实机验收仍待执行；③ 工作区未提交（未授权）。

## 2026-10-16 · Phase 5/5.1 实机验收记录更新（用户执行；仅文档，无源码/部署/Git 变更）

- **任务**：按用户 NX2512 实测反馈更新验收记录与项目状态；不修改业务源码、不新增功能、不重新部署、不覆盖备份、不执行 Git 操作。
- **用户报告通过**（2026-10-16 实机）：
  1. 原崩溃路径（选中平面 → 发送消息）不再闪退——Phase 5.1 修复实机确认；
  2. 当前选择读取正常（平面 Face 识别与几何摘要）；
  3. "刷新上下文"入口正常；
  4. 空选择处理正常（如实提示）；
  5. 面相对圆柱（selected_face_center）：确认卡显示与确认后实际创建检查通过（简单平面场景，验收清单第 7、8 项）。
- **未测（保持待验收，不得视为通过）**：边、顶点、曲线、基准对象及其他对象类型的识别与摘要；切 WCS/切视图上下文反映；确认卡取消路径；确认中切部件/换面目标失效中止；旧 global_origin 模式与 block/cone/sphere 回归；复杂/曲面/歧义目标拒绝（含带孔/凹面）；部件未自动保存、路径未变核对；聊天渲染/选择复制回归。
- **限制保留**：复杂面 UV 中点可能不在实际面上（无可靠点-面包含查询）；倾斜圆柱精确轴向验证（包围盒验证不能证明轴向）——不因本次简单平面场景通过自动视为已验证。
- **文档变更**：`docs/reports/PHASE_5_CONTEXT_AWARENESS.md`（§6 拆分为清单 6.1 + 实测结果 6.2）、`docs/reports/PHASE_5_1_CRASH_AND_CONTEXT_FEEDBACK_FIX.md`（§5 增实测结果，§4 增实机确认引注）、`docs/PROJECT_STATUS.md`（当前阶段/验证表拆分为已测与未测两行/风险 0 改写/核验时间）、本文件（本条目）。验收结论以用户对话内报告为准，未附截图/日志存档。

## 2026-10-16 · Phase 6 确认/自主双模式实现（离线验证通过；实机验收待执行）

- **任务**：在现有确认/执行框架上增加可切换双交互模式——确认模式（默认，行为与 Phase 5.1 完全一致）与自主模式（允许清单内的基本体创建工具在校验全部通过后跳过逐次确认）。无平行执行框架、无聊天 UI 重写。
- **开始前保护**：仓库外完整备份 `D:\AI\NX_AI_ASSISTANT_backup_20261016_phase6_start`（15 MB，含未跟踪文件，旧备份未动）；无 git reset/clean/commit/push。
- **实现**：
  - `Core/ExecutionMode.cs`（新增）：`ExecutionMode` 枚举（Confirm/Autonomous）、`AutonomousToolAllowlist`（封闭清单：create_block/cylinder/cone/sphere 四个已注册工具）、`IExecutionModeStore`+`JsonFileExecutionModeStore`（`%APPDATA%\NX AI Assistant\execution-mode.json`，无密钥，损坏→null→默认确认）、`ExecutionModePolicy`（纯函数授权规则，**后端分派阶段执行**，UI 开关只改模式不改规则）、`ExecutionModeManager`（线程安全，SetMode 先持久化后发布，失败内存不变，启动时应用存储值→后端与 UI 启动态必然一致）。Core 不引用 NXOpen/WinForms。
  - `ChatSessionController.cs`：新增可选 `ExecutionModeManager` 参数（不传时行为与之前完全一致）；`ProcessToolCallAsync` 在分派阶段读模式+策略决定是否走确认；自主路径显示"自主模式：正在创建…"状态行（不谎称成功），执行前重查模式（切回确认→转确认流程）；自主执行 `expectedWorkPartFullPath=null`（执行器内工作部件/单位/UndoMark/回滚/后置验证全部保留）；工具描述文本与确认文本拆分，确认路径文本不变（既有测试断言保持）。
  - `UI/ModeToggleSwitch.cs`（新增）：自绘开关，OFF 灰轨左滑块"确认模式"/ON 橙轨右滑块"自主模式"，状态由文字标签表达（不只靠颜色），DeviceDpi 缩放，键盘（空格/回车）+Accessibility；控件只发 ToggleRequested，由窗体校验后回写 SetChecked。
  - `UI/AssistantChatForm.cs`：构造 `ExecutionModeManager` 并注入控制器；输入框上方模式条（执行模式标签+开关+状态文字）；切换规则——请求进行中拒绝并提示、持久化失败恢复真实状态+错误信息、切换成功写聊天说明；切换不触发建模、不等价于任何待执行操作的确认。
- **测试**：新增 `ExecutionModeTests.cs`（22 项）+`ExecutionModeControllerTests.cs`（13 项）：默认模式/持久化往返/损坏文件/启动恢复/持久化失败内存不变/允许清单封闭性/策略矩阵/确认模式回归/自主跳过确认恰好执行一次/非法参数与未知工具与未知字段不执行/pending 确认卡期间切自主不自动执行/用户取消无几何/在途切回确认走确认流程/无重复提交。注意：测试宿主裸 await TCS 崩溃限制仍适用，沿用 WhenAny+Delay 模式；ScriptedSender 扩展支持 `Func<Task<ChatCompletionResult>>`（可阻塞响应，用于确定性竞态测试）。
- **主要文件**（新增）：`Core/ExecutionMode.cs`、`UI/ModeToggleSwitch.cs`、`tests/ExecutionModeTests.cs`、`tests/ExecutionModeControllerTests.cs`、`docs/reports/PHASE_6_EXECUTION_MODES.md`；（修改）`UI/ChatSessionController.cs`、`UI/AssistantChatForm.cs`。
- **验证结果**【已核实，本轮执行】：`git diff --check` 通过；Release 构建 0 警告 0 错误；测试 **274 项 = 273 通过 / 0 失败 / 1 跳过**（+35 项，既有 226 项无回归；跳过项为需真实 NX 的验收测试，按设计跳过）；NX 未运行，4 DLL 复制部署至 `deploy/nxai_custom/application/`，SHA-256 与构建产物逐一一致（Core `eee89cb2…`、UI `42a69190…`、NxHost.NX2512 `5db56835…`、Entry `25d48833…`，完整哈希见 Phase 6 报告 §6）。**离线通过 ≠ 实机通过：Phase 6 报告 §7 的 11 项实机验收清单全部待执行，不得标记为通过。**
- **遗留**：① Phase 6 实机验收 11 项待执行；② Phase 5 剩余实机验收项仍待执行；③ 工作区未提交（未授权）。

## 2026-10-16 · Phase 6.1 修复执行模式开关显示不完整（离线验证通过；实机复验待执行）

- **现象**（用户实机验收报告）：模式切换功能正常，但聊天界面"执行模式"区域开关显示不完整。
- **根因**（代码检查确认）：① `modeBar` 固定 `Height=30` + 上下 Padding 2 → 内高 26px，而开关自绘高度按 DeviceDpi 缩放（125%=30px、150%=35px），超出部分被 TableLayoutPanel 裁剪（窗体 AutoScaleMode.Font 与 DPI 缩放系数不一致时必然出现）；② `_modeBarStateLabel` 创建并持续更新但从未加入任何容器（死控件，不可见）。
- **修复**（仅 `AssistantChatForm.cs` 模式条区域）：modeBar 改为内容驱动高度（Panel AutoSize + TLP RowStyle(AutoSize)，行高随开关 GetPreferredSize 的 DPI 缩放值自适应）；开关 Dock=Fill 垂直居中；删除死控件 `_modeBarStateLabel`（模式状态由开关自身文字标签表达）。未改动 ModeToggleSwitch 绘制、点击/键盘/无障碍/持久化、授权策略、允许清单、工具调用与 NX 执行链路。
- **验证**【本轮执行】：Release 构建 0 警告 0 错误；测试 **274 项 = 273 通过 / 0 失败 / 1 跳过**（无回归）；`git diff --check` 通过；NX 未运行，仅 UI.dll 变更并部署，SHA-256 与构建产物一致（`191589a4…`，完整值见 Phase 6 报告 Phase 6.1 节）。备份：`D:\AI\NX_AI_ASSISTANT_backup_20261016_phase61_start`。
- **遗留**：开关 OFF/ON 完整显示、无遮挡、DPI/缩放、点击键盘无障碍持久化共 5 项实机复验待执行（清单见 Phase 6 报告 Phase 6.1 节）；Phase 6 主清单与 Phase 5 剩余项仍待执行；工作区未提交（未授权）。

## 2026-10-16 · Phase 7 草图/拉伸/旋转/布尔实现（离线验证通过；实机验收待执行）

- **任务**：新增四个参数化建模工具——create_sketch（选中平面/基准面或 WCS XY/XZ/YZ + 矩形/圆轮廓）、create_extrude（草图法向拉伸）、create_revolve（显式轴旋转体，角度 (0,360]）、boolean_operation（unite/subtract/intersect，受控体引用）。复用 ToolRegistry/控制器/上下文/执行模式/确认卡/UndoMark/回滚/后置验证全链路；自主模式允许清单 4→8（同一封闭策略）。
- **API 核验**（本机 NXOpen.dll 元数据反射 + NXOpen.xml 文档，临时探针不入库）：SketchInPlaceBuilder2/PlaneReference(Plane)/AddGeometry(InferNoConstraints)/GetAllGeometry/Feature、CurveCollection.CreateLine/CreateArc、Sections.CreateSection+CreateRuleCurveFeature+AddToSection、ExtrudeBuilder(Limits.Start/EndExtend.Value.SetFormula)、RevolveBuilder.Axis(AxisCollection.CreateAxis；**DatumAxis≠Axis**)、BooleanBuilder.Operation/Targets/Tools（过时 API 以 pragma 声明使用）、NXMatrix.Element、Expression.SetFormula。**AskMassProps3d 结果数组布局不可核实→未采用**，后置验证用解析几何包围盒替代（如实记录）。
- **实现**：Core 四工具 Schema/解析器（GeometryToolJson 复用、未知字段/非有限/边界拒绝）、CreateBlockExecutionResult.DetailsJson（引用回流模型）、INxToolExecutor 4 新方法、NxBodyInfo.BodyRef（选中体引用）；NxHost `FeatureToolExecutor.cs`（partial NxToolExecutor，~1100 行）：平面解析/局部 u/v 坐标框架/草图几何/截面构建/轴解析与平面内校验/解析 AABB 验证（拉伸精确、旋转 360° 精确部分角度包络上界、布尔必要条件包含关系）；Controller 分派链 8 工具（修复了新分支被 sphere else 吞掉的 bug）；确认卡 4 新文案；自主路径状态行/执行前重查沿用 Phase 6。
- **测试**：新增 FeatureToolContractTests（25 项）+ 控制器新工具分派/模式 5 项；更新注册表计数（4→8）、允许清单（4→8）、工具广告（8）、fake executor 补 4 方法。**314 项 = 313 通过 / 0 失败 / 1 跳过**（既有无回归）。
- **验证**【本轮执行】：Release 构建 0 警告 0 错误；git diff --check 通过（修复一处 EOF 空行）；NX 未运行，4 DLL 部署 SHA-256 与构建产物一致。**离线通过 ≠ 实机通过：Phase 7 报告 §7 的 14 项实机验收全部待执行。**
- **已知限制**（报告 §5）：轮廓仅矩形/圆；选中面草图平面非关联；拉伸仅定距离法向/反向；旋转轴须在草图平面内、部分角度验证为上界；布尔工具体默认消耗；体积级验证待 mass-props 索引核实后补充。
- **遗留**：§7 实机验收全部待执行；GitHub 推送待重试（上轮连接失败）。

## 2026-10-16 · Phase 6/6.1 实机验收记录（用户执行；仅文档，无源码/部署/Git 功能变更）

- **任务**：按用户 NX2512 实测反馈更新验收记录；不修改业务源码、不重新部署。
- **用户报告通过（16 项全部）**：
  - Phase 6 §7 主清单 11 项：初始确认模式、开关外观与模式文字、确认模式圆柱确认卡、自主模式简单圆柱直接创建、自主模式选中面圆柱（中心/法向正确）、缺选择/非法尺寸/歧义目标不产生几何、取消无几何、切回确认恢复流程、不自动保存/不改路径、聊天与上下文与其他基本体无回归、重启后模式恢复一致。
  - Phase 6.1 开关显示 5 项：OFF 灰轨左滑块完整、ON 橙轨右滑块完整、圆角无裁剪无遮挡、窗口缩放/消息增多仍完整、点击/键盘/无障碍/持久化与 6.0 一致且无其他回归。
- **验收对应产物**：Phase 6.1 部署版本（UI.dll `191589a4…`）；Phase 7 新 DLL 其后部署，Phase 7 实机验收另行执行。
- **文档变更**：`docs/reports/PHASE_6_EXECUTION_MODES.md`（新增 §10 验收结果 + §9/6.1 更新）、`docs/PROJECT_STATUS.md`（当前阶段/验证表/下一任务）。验收结论以用户对话内报告为准，未附截图/日志存档。
- **仍待验收**：Phase 7 报告 §7 十四项；Phase 5 剩余项（边/顶点/曲线/基准/复杂面等）。

## 2026-10-16 · Phase 7.1 修复聊天 RichTextBox 已释放异常（离线验证通过；部署与实机复验待执行）

- **故障**：实机 `ObjectDisposedException`（RichTextBox.set_Rtf ← MessageItem.ApplyViewportWidth ← ApplyViewportWidthToAllItems ← WndProc），发生在视口宽度更新期间。
- **根因**（三处叠加）：① StartNewConversation 先循环 Dispose 消息控件（每次释放改变滚动条→同步触发 ClientSizeChanged→遍历仍含已释放项的 _messageItems），Clear 在循环后才执行；② _messageItems 平行列表与控件树双轨维护，Dispose 不同步；③ 窗体拆除时子控件（RTB）先于 MessageItem 离开树，期间布局回调仍可触达。
- **修复**（仅 AssistantChatForm.cs 消息生命周期/布局）：删除 _messageItems 平行列表→遍历 _messagesPanel.Controls.OfType<MessageItem>()（控件树=唯一事实来源）；ApplyViewportWidthToAllItems 加 _detached/IsDisposed/panel.IsDisposed 拆除门禁；MessageItem.ApplyViewportWidth 加自身+子视图存活检查（覆盖子控件先释放的拆除间隙）。MessageItem 改 internal（InternalsVisibleTo 已有）供测试。未动 Markdown 渲染/选择复制/滚动/建模/授权/持久化。
- **测试**：新增 ChatMessageItemLifecycleTests 7 项（STA 线程跑 WinForms 控件：正常宽度/长 Markdown 重排/气泡与标签路径/Dispose 后更新不抛/子视图先释放后更新不抛/重复 Dispose）。**321 项 = 320 通过 / 0 失败 / 1 跳过**（无回归）；Release 0 警告 0 错误；git diff --check 通过。
- **部署**【2026-10-16 完成】：用户确认 NX 关闭后部署 UI.dll，SHA-256 与构建产物核对一致（`f46eaa96…`）。（此前曾因 NX 运行锁定停止部署。）
- **遗留**：面板级 ClientSizeChanged/滚动条交互无法离线覆盖→实机手动验收 6 步（Phase 7.1 报告 §4）；部署待 NX 退出。

## 2026-10-16 · Phase 7.2 修复 create_extrude"不活动对象"错误（离线验证通过；实机验收待执行）

- **故障**：create_sketch 成功后 create_extrude 报"试图使用不活动的对象"（NX JAM `'Attempt to use an object that is not alive'`，错误串经 LOCALIZATION/simpl_chinese/libjam 溯源）。
- **诊断**（%TEMP% 日志实证，非推测）：① 20h 会话拉伸 commit 成功、NX 实测包围盒 (-15,-15,0)–(15,15,50) 正确，但被解析期望盒误判（Arc 分支取三维盒角点 z∈[-15,65]，多出两端 ±r）→ 回滚成功操作；② 19h 会话拉伸在 section/builder 阶段失败（无 execute.extrude 日志行=诊断盲区），推断为 Deactivate 后草图曲线作为 AddToSection seed 的存活问题（状态依赖，同代码路径 20h 成功）。
- **修复**（仅 FeatureToolExecutor.cs）：① 期望盒极值点改在圆自身平面内取（圆心±r·平面轴，轴来自 Conic.Matrix.Element 列——与 TryGetSketchFrame 同源已核实映射）；② Section 构建前激活草图（曲线存活）后还原，新增 IsDead 存活门禁（死对象明确拒绝不再翻译为意外错误）；③ PhaseLog 补全 execute.extrude/execute.section 全阶段（resolve/frame/section/builder/commit/verify/rollback/destroy，含异常类型+首行栈）。
- **测试**：全量 **321 项 = 320 通过 / 0 失败 / 1 跳过**（无回归）；Release 0 警告 0 错误；git diff --check 通过。验证/Section 逻辑需 NX 会话，离线不可直接单测（边界已在报告声明）。
- **部署**：NX 未运行，NxHost.NX2512.dll 部署 SHA-256 一致（`35769ba8…`）。
- **遗留**：实机验收 8 项（报告 §5：独立请求拉伸 d30×50、包围盒核对、两模式、失败取消路径、阶段日志核对）；缺陷 B 修复的实机有效性以验收为准（报告 §6 如实声明）。

## 2026-10-17 · Phase 7.3 修复工具调用协议标记泄漏到聊天（离线验证通过；实机验收待执行）

- **故障**：create_sketch 成功后，模型输出的 invoke/parameter 原始控制标记直接显示在聊天界面，无工具执行反馈。
- **审计**：ParseChatCompletionResponse 只认结构化 tool_calls；GLM 推理模型（nvidia_nim）部分轮次把调用写成 message.content 内标签块 → OkText → 聊天渲染原文。结构化路径（tool_call_id/回填）本身无缺陷；注册名确认为 create_extrude 不变。
- **修复**：新增 Core/ToolTagProtocol.cs 受限适配层——content 含完整良构 invoke 块→严格解析（函数名白名单正则、参数严格映射、重复/空名/未闭合/杂散文本全拒绝）→转为与结构化路径相同的 ParsedToolCall（合成 tagcall_N id）→下游注册表/Schema/ExecutionModePolicy/NX 线程/回滚原样生效；不完整块→协议错误（不渲染原文）；纯文本/结构化路径不受影响。标记常量源码内片段拼接。ModelService.ParseChatCompletionResponse 接入。
- **测试**：新增 ToolTagProtocolTests 15 项（含"错误信息不含原始标记"断言、run_shell 未注册工具用例、适配输出喂真实类型化解析器）。测试捕获并修复适配器边界 bug（Scan 块结束标签用错）。全量 **336 项 = 335 通过 / 0 失败 / 1 跳过**（无回归）；Release 0 警告 0 错误；git diff --check 通过。
- **部署**：NX 未运行，4 DLL SHA-256 一致（Core `22e6fe97…`、UI `488f5c05…`、NxHost `50580d4c…`、Entry `baf508fc…`）。
- **遗留**：实机验收 7 项（报告 §5，重点第 3 项：聊天不再显示原始标记）；Phase 7.2 拉伸实机验收与 Phase 6 遗留项仍待执行。

## 2026-10-17 · Phase 7.3.1 工具名别名与方向值映射（离线验证通过；实机验收待执行）

- **补充证据**：实机截图显示模型用短名 `extrude`（非注册名 create_extrude）和 `direction=positive_z`（非 schema 值）——7.3 适配层解析块成功但下游注册表/枚举拒绝。
- **修复**（仅 ToolTagProtocol.cs）：显式封闭映射表——工具别名 `extrude→create_extrude`（仅此一条，语义核实后准入，无模糊匹配）；方向值 `positive_z→normal`（仅 create_extrude.direction，schema 的 normal 即 XY 草图 +Z 法向）。`profile` 参数名**不**静默改写（无证据支持，交类型化解析器按未知字段拒绝并在报告中说明）；SKETCH_000 受控引用原样传递；结构化路径/回填/授权/执行链不变。
- **测试**：新增 ToolTagAliasTests 8 项（截图样例归一化、别名+方向直达真实类型化解析器、非法方向拒绝、受控引用不变、未知别名不重写、其他工具不受影响、结构化 id 保留）。修正 7.3 测试的 id 前缀断言（tagcall_N 计数器两位数时 [^1] 截断错误→StartsWith）。全量 **344 项 = 343 通过 / 0 失败 / 1 跳过**；Release 0 警告 0 错误；git diff --check 通过。
- **部署**：Core.dll SHA-256 `91780fdf…` 一致（NX 未运行）。
- **遗留**：实机验收 7 项；**若模型继续用 profile 参数名，类型化解析器将拒绝（未知字段）——复现时按截图证据决定是否准入参数名别名，当前不猜**。待推送提交：b9bf600、4ca020e、本轮新提交。

## 2026-10-17 · Phase 7.3.2 参数别名 + 串行两步链 + 回合上限扩展（离线验证通过；实机验收待执行）

- **补充证据**：模型稳定输出 `extrude / sketch=SKETCH_000 / distance=50 / unit=mm`（无 direction）——7.3.1 后 `sketch` 参数名仍被类型化解析器按未知字段拒绝；回合上限 2 使两步串行链无法单轮完成。
- **修复**：① ToolTagProtocol 参数名别名封闭表（仅 create_extrude：sketch→sketch_ref；别名与规范名并存→冲突拒绝；profile 仍未准入——本批证据是 sketch）；② direction 缺省=Normal 为既有产品契约（schema required 仅 sketch_ref/distance/unit），补测试锁定；③ ChatSessionController 回合上限 2→3（MaxToolExecutionsPerTurn=2），rounds 1-2 每轮至多一工具严格串行、结果回填、round 3 纯文本；两工具均在允许清单；泄漏阻断路径不变。
- **测试**：新增 ToolTagCompatAndChainTests 8 项 + 更新 3 个 round-cap 旧契约测试。全量 **352 项 = 351 通过 / 0 失败 / 1 跳过**；Release 0 警告 0 错误；git diff --check 通过。
- **部署**：4 DLL SHA-256 一致（Core `8767fbe5…`、UI `08376a86…`、NxHost `68d4ce4e…`、Entry `baf508fc…`；NX 未运行）。
- **遗留**：实机验收（自主模式一句话两步建模、确认模式逐步确认、取消/失败路径）；profile 参数名如再次出现按截图证据决定。

## 2026-10-17 · Phase 7.4 稳定性修复：圆弧平面对齐 + 旋转失效引用门禁 + 上下文 Sketch 识别（离线验证通过；实机验收待执行）

- **故障矩阵**（%TEMP% 日志 + 异常日志取证，非猜测）：
  ① XZ 平面 U45 圆草图 5 次失败——真实异常 `NXException: 圆弧的平面必须与草图平面平行`（AddGeometry 处），**不是**"不活动对象"（外层 catch 的误导翻译）；XY 同类创建成功。
  ② 绕 Z 轴旋转 360 失败——真实异常 `FeatureBuilder.CommitFeature()` 处**内存访问违例(AV)**；日志显示该次旋转引用的 SKETCH(1) 是 1 分钟前刚被回滚的草图——失效引用传入 RevolveBuilder 触发原生 AV。
  ③ "请求超时"= 模型 HTTP 阶段（日志大段无 execute.* 空档佐证）。
  ④ 上下文把选中对象报为 `other`——DescribeObject 无 Sketch case，用户选草图准备拉伸时被归为未知。
- **修复**：① BuildSketchProfile 圆构造改为 arcY = planeNormal × u（从草图附加平面自身法向推导 yDir，保证 arc 平面与草图平面精确对齐；XY/XZ/YZ 三 frame 数学验证）；② ExecuteCreateRevolve 解析草图后加 IsDead 门禁（7.2 只加在 extrude，revolve 漏了）+ resolve 阶段日志 + Commit 前警示注释（AV 进程级不可 catch，门禁是唯一防线）；③ NxContextReader 新增 Sketch case（kind=sketch，subtype=active/inactive）+ other 保留真实运行时类型名。
- **测试**：新增 PlaneFrameHandednessTests 4 项（三平面 arc 法向=附加平面法向不变式 + XZ 根因文档化）。全量 **356 项 = 355 通过 / 0 失败 / 1 跳过**；Release 0 警告 0 错误；git diff --check 通过。
- **部署**：4 DLL SHA-256 一致（Core `d0afe62e…`、UI `5638d87d…`、NxHost `d344c6d1…`、Entry `8d6a2b38…`；NX 未运行）。
- **遗留**：实机验收按 7.4 报告顺序（XY 圆→独立拉伸→XZ U45 圆→绕 Z 旋转 360→重复原故障流）；revolve limits 角度单位语义列入观察项；超时不自动重试已由既有回合机制保证。

## 2026-10-17 · Phase 8 结构化建模计划、串行协调器与 NX 几何自检（离线验证通过；实机验收待执行）

- **范围**：从单点修复转向完整闭环——自然语言→结构化计划→串行执行→几何验证。不新增几何命令。
- **新增**：
  - `Core/ModelingPlan.cs`：ModelingPlan/ModelingStep（工具+类型化参数+依赖+引用注入声明+预期几何）、PlanStepStatus/PlanStepResult、ModelingPlanResult（部分完成如实描述）、GeometrySelfCheckCase/Report（UI 可显示，Core 不含 NXOpen）、GeometricExpectation（SketchProfile/BodyBoundingBox/RevolutionEnvelope）。
  - `Core/ModelingPlanFactory.cs`：模型输出→计划归一化——结构化与标签协议同路（ToolTagProtocol.NormalizeCall 公开入口）、注册表+Schema 全检、依赖步骤引用注入声明（sketch_ref 占位符由依赖真实结果替换）、未知工具/非法参数/超步数（>2）拒绝。
  - `Core/PlanExecutionCoordinator.cs`：严格串行执行——确认模式每步一卡；自主模式计划级一次提示后连续执行（同一 ExecutionModePolicy）；引用交接（schema 字段名→payload 键 sketchRef 映射）；失败即停+后续 Skipped；描述含已完成/失败/未执行三态。
  - `NxHost/NxGeometrySelfCheck.cs`：进程内几何自检——PartCollection.FileNew()（已核实 API）创建未保存毫米临时部件，四场景（XY 圆 d30→拉伸 50（bbox (-15,-15,0)–(15,15,50)）；XZ U45 d10 圆→绕 Z 360°（torus 解析 bbox (-50,-50,-5)–(50,50,5)，形状类型注明待实机确认）；四基本体；失效引用拒绝）→ 结构化报告 → BasePart.Close 不保存 → 恢复用户工作部件。仅聊天窗体"几何自检"按钮显式触发，绝不在用户部件跑回归。
  - `NxHostBoot` 注入自检回调；侧栏新增"诊断/几何自检"入口（回调存在时显示）。
- **测试**：新增 ModelingPlanTests 14 项（场景 A/B 计划结构+torus 解析包络钉死 (-50,-50,-5)–(50,50,5)、未知工具/非法参数/超步数/空计划拒绝、别名归一化、确认/自主授权、取消、部分完成真话、引用提取）。开发中修复 2 个协调器缺陷（plan 级通知条件反逻辑、sketch_ref→sketchRef 键映射）。全量 **370 项 = 369 通过 / 0 失败 / 1 跳过**；Release 0 警告 0 错误；git diff --check 通过。
- **部署**：4 DLL SHA-256 一致（Core `e918b21d…`、UI `fe0a3863…`、NxHost `e93ebaa6…`、Entry `a3fed257…`；NX 未运行）。
- **遗留**：**NX2512 实机验收为完成前提**——①聊天窗体点"几何自检"跑全场景核对报告；②自主模式一句话执行场景 A/B（含圆环体确认 torus 而非同包围盒圆柱）；③确认模式分步确认；④失败/取消路径。FileNew 空模板名 `_blank_mm.prt` 是否匹配本机安装需实机确认（失败会体现在 selfcheck.setup 报告中）。圆环体积/特征类型 API（AskMassProps3d 布局）仍未核实，形状验证为包围盒+实机确认两级。

## 2026-10-17 · Phase 8.1 修复自检临时部件模板失败（离线验证通过；实机复验待执行）

- **故障**：实机点击"几何自检"第一步骤即失败——`selfcheck.setup: NXException: 选定的模板不存在`。根因：8.0 硬编码了 `_blank_mm.prt`（编造的模板名，本机不存在）。
- **核实**：本机 NX2512 模板目录 `UGII\templates` 下存在 `model-plain-1-mm-template.prt`（毫米建模模板）等真实模板；FileNew 属性语义经 NXOpen.xml 核实（TemplateFileName/NewFileName/Units/MakeDisplayedPart）；Session.GetEnvironmentVariableValue(string) 已核实存在。
- **修复**（仅 NxGeometrySelfCheck.cs）：新增 `DiscoverMillimeterTemplate` 模板发现——目录候选（UGII_TEMPLATE_DIR 环境变量 → Session 的 UGII_BASE_DIR\UGII\templates）、候选验证（File.Exists）、优先级（model-plain-*-mm 前两名 → 任意 *-mm-template.prt 兜底，不凭名称猜单位——按文件名 -mm- 标记）、失败时 SelfCheckException 报告已搜索目录与解决条件（绝不静默改用用户部件）；CreateTempPart 使用发现的**绝对路径**模板并在 PhaseLog 记录（template 行含搜索目录）。
- **测试**：新增 SelfCheckTemplateDiscoveryTests 3 项（优先级排序、兜底、空目录不静默回退）。全量 **373 项 = 372 通过 / 0 失败 / 1 跳过**；Release 0 警告 0 错误；git diff --check 通过。
- **部署**：4 DLL SHA-256 一致（Core `699a61aa…`、UI `a0ef6a58…`、NxHost `a5851fe5…`、Entry `a3fed257…`；NX 未运行）。
- **遗留**：实机复验——点击"几何自检"应创建临时部件并完成四场景；若 setup 仍失败，报告将给出实际模板目录与候选（不再是无诊断的"模板不存在"）。隔离/清理/恢复逻辑未改动。

## 2026-10-17 · Phase 8.1.1 FileNew 模板注册语义修复（离线验证通过；实机复验待执行）

- **再取证**：PhaseLog 证实 8.1 的模板发现已生效（绝对路径 `model-plain-1-mm-template.prt` 正确传入），但 `FileNew.Commit` 仍抛"选定的模板不存在"——问题不在路径，在**解析方式**。
- **根因核实**：本机 `UGII/templates/ugs_model_templates.pax` 注册表显示模板按 **Presentation name="Model" + Filename + Units=Metric** 注册；FileNew 的 TemplateFileName 解析走**模板注册表**（按 ApplicationName 键控），裸绝对路径不是注册表查找键。
- **修复**（仅 NxGeometrySelfCheck.CreateTempPart）：设置 `ApplicationName="Model"`（FileNew 属性已核实），**双候选依次尝试**——①注册表文件名（model-plain-1-mm-template.prt）+ Model 应用；②绝对路径兜底；每次尝试的异常类型+消息记入 PhaseLog 与最终诊断（`尝试记录：…`），候选循环中 builder Destroy 全路径保证。
- **测试**：全量 **373 项 = 372 通过 / 0 失败 / 1 跳过**（无回归）；Release 0 警告 0 错误；git diff --check 通过。
- **部署**：4 DLL SHA-256 一致（Core `72e12c11…`、UI `bbb91128…`、NxHost `2f8e6d30…`、Entry `a3fed257…`；NX 未运行）。
- **遗留**：实机复验自检按钮。若两候选都失败，诊断将包含两条尝试的精确异常——按证据决定下一步（如检查注册表应用名大小写/使用 udm 变体注册表）。

## 2026-10-17 · Phase 8.1.2 FileNew 完整属性契约修复（离线验证通过；实机复验待执行）

- **再取证**：8.1.1 双候选（注册文件名/绝对路径）均失败且异常相同——问题不在模板名解析，在 FileNew 属性集不完整。
- **根因核实**：本机安装的 NX 官方测试 journal（`AUTOMATED_TESTING/examples/UnitTest/Python/BlockStyler/ExtrudewithPreview/ATS_UnitTest_ExtrudewithPreview.py`）记录了 File→New（mm 模型模板）的**完整属性全集**——`ApplicationName="ModelTemplate"`（非调色板显示名"Model"！）、`TemplateType=Item`（非 Generic）、`TemplatePresentationName="Model"`、`UseBlankTemplate=False`、`RelationType=""`、`UsesMasterModel="No"`、`ItemType=""`、`Specialization=""`、`SetCanCreateAltrep(False)`、`MasterFileName=""`、`DisplayPartOption=AllowAdditional`——缺任何一项都会让 NX 以误导性的"选定的模板不存在"失败。
- **修复**（仅 NxGeometrySelfCheck.CreateTempPart）：按官方 journal 属性全集完整设置（TemplateFileName 仍用发现的注册文件名，绝对路径丢弃）；异常包装 SelfCheckException 并注明契约来源。
- **测试**：全量 **373 项 = 372 通过 / 0 失败 / 1 跳过**（无回归）；Release 0 警告 0 错误；git diff --check 通过。
- **部署**：4 DLL SHA-256 一致（Core `d1481a56…`、UI `01121f36…`、NxHost `f7119b33…`、Entry `a3fed257…`；NX 未运行）。
- **遗留**：实机复验自检按钮——官方 journal 属性全集是 NX 自身记录的 working 用法，预期成功；若仍失败，PhaseLog 会给出精确异常。

## 2026-10-17 · Phase 8.1.3 GetMatchingTemplates 查询 NX 认可模板（离线验证通过；实机复验待执行）

- **再取证**：8.1.2 官方 journal 属性全集仍失败且异常相同——属性集排除后，唯一剩余解释是 **TemplateFileName 期望的名字与磁盘文件名/注册文件名都不同**（本机 NX 会话实际注册的模板名未知）。
- **正解（API 核实）**：`FileNew.GetMatchingTemplates(FileNewApplication, Part.Units)`（NX2312+，本机元数据核实）返回 NX 自己注册的模板列表；`FileNewTemplate.GetName()` 即 TemplateFileName 期望的名字。**向 NX 问它认得的模板，而不是向文件系统猜。**
- **修复**（仅 NxGeometrySelfCheck.CreateTempPart）：FileNew 创建后先 `GetMatchingTemplates(Modeling, Millimeters)` 枚举（空表→SelfCheckException 指向 customer defaults 模板目录配置；每个模板名记 PhaseLog）；取第一个的 GetName() 设回 TemplateFileName（官方 journal 属性全集保持）；磁盘模板发现降级为交叉诊断信息。
- **测试**：全量 **373 项 = 372 通过 / 0 失败 / 1 跳过**（无回归）；Release 0 警告 0 错误；git diff --check 通过。
- **部署**：4 DLL SHA-256 一致（Core `7c8357d3…`、UI `2235deb2…`、NxHost `e9274a37…`、Entry `a3fed257…`；NX 未运行）。
- **遗留**：实机复验——PhaseLog 将列出 NX 认可的全部模板名；若查询返回空表，诊断指向 customer defaults 模板配置（环境问题，按报告解决）。

## 2026-10-17 · Phase 8.1.4 改用 GetApplicationNames + GetAvailableTemplates（离线验证通过；实机复验待执行）

- **再取证**：8.1.3 的 `GetMatchingTemplates(FileNewApplication, Units)` 抛"遇到无效的函数参数"——枚举版 API（NX9 起过时）在本机 NX2512 已不可用。
- **正解（NXOpen.xml 核实）**：`FileNew.GetApplicationNames()`（NX9+，返回全部注册应用名，文档明确"These names should be used while setting the ApplicationName"）+ `FileNew.GetAvailableTemplates()`（NX6+，无参数，返回**当前 builder 状态**下可用的模板名数组）。
- **修复**（仅 CreateTempPart）：①GetApplicationNames 枚举（全部记 PhaseLog）；②选含 "model" 的应用名设 ApplicationName；③GetAvailableTemplates 枚举（全部记 PhaseLog）；④优先含 "-mm-"/"-mm.prt" 的模板名（文件命名约定，不猜单位），否则取第一个；⑤官方 journal 属性全集提交。空列表均有指向 customer defaults 的诊断。
- **测试**：全量 **373 项 = 372 通过 / 0 失败 / 1 跳过**（无回归）；Release 0 警告 0 错误；git diff --check 通过。
- **部署**：4 DLL SHA-256 一致（Core `9d54a283…`、UI `77990507…`、NxHost `8450e292…`、Entry `a3fed257…`；NX 未运行）。
- **遗留**：实机复验——PhaseLog 的 `nx application`/`nx template` 行将列出 NX 认可的全部应用与模板名；若列表为空则按诊断指向环境配置。

## 2026-10-17 · Phase 8.1.5 模板候选循环 + 空白模板回退（离线验证通过；部署与实机复验待执行）

- **再取证（决定性）**：PhaseLog 显示 NX 的 `GetAvailableTemplates()` 在 ModelTemplate 应用下**明确列出** `model-plain-1-mm-template.prt`（还有 `空白` 中文条目）。之前 8.1.4 的选择逻辑 `FirstOrDefault(含 -mm-)` 误选了列表中更靠前的 `structuredesign-plain-mm-template.prt`（可能属其他应用授权）而未选 NX+磁盘双确认的 model-plain。
- **修复**（仅 CreateTempPart）：候选循环按实时证据排序——① `model-plain-1-mm-template.prt`（NX 列出+磁盘存在双重确认，8.1.2 用它失败是因当时属性集不完整，现已修复）；② 其余列出的 -mm- 模板；③ `空白` 条目（NX 的空白模板用 customer defaults 建件，UseBlankTemplate=true + TemplatePresentationName=""，无需模板文件）。每个候选用**全新 FileNew builder**（builder 单次使用，逐次销毁）；全部失败时 SelfCheckException 附 NX 报告的模板总数与每次尝试的精确异常。
- **测试**：全量 **373 项 = 372 通过 / 0 失败 / 1 跳过**（无回归）；Release 0 警告 0 错误；git diff --check 通过。
- **部署**：**NX 正在运行（ugraf.exe），DLL 被锁定——按规则停止部署，未强制覆盖。** 构建产物 SHA-256 已存档：Core `42159b16…`、UI `0a50c5ab…`、NxHost `876a0a58…`；用户退出 NX 后部署并核对。
- **遗留**：实机复验三候选链（model-plain 预期成功；若三候选全败，诊断含全部尝试异常）。

## 2026-10-17 · Phase 8.1.6 种子部件副本方案（离线验证通过；部署完成；实机复验需先放置种子）

- **决定性证据**：8.1.5 候选循环把 NX 自己列出的全部 430 个模板（含"空白"）逐一提交，全部以同一个"选定的模板不存在"失败——**FileNew 在本会话结构性不可用**（会话未加载任何可用的模板注册数据），属性集/名字/候选循环全部排除。
- **修复**（仅 NxGeometrySelfCheck.cs）：改为**种子部件副本优先**——①种子查找顺序：环境变量 `NXAI_SELFCHECK_SEED_PART` → 部署目录 `selfcheck/seed-mm.prt`（DLL 旁）→ `%TEMP%\nxai_selfcheck_seed_mm.prt`；②把种子**复制**到 %TEMP% 唯一副本（种子文件永不触碰）；③`PartCollection.OpenDisplay`（已核实签名）打开副本；④核对毫米单位；⑤测试后关闭不保存+删除副本；⑥OpenDisplay 失败逐种子记录并尝试下一位置。FileNew 路径（8.1.2/8.1.4 契约）保留为回退；种子+FileNew 全失败时诊断给出种子放置位置与一次性操作说明（NX 手工新建毫米部件另存为该名）。
- **用户一次性操作**：在 NX 中新建一个毫米模型部件（不画任何东西）→ 另存为 `deploy\nxai_custom\application\selfcheck\seed-mm.prt`。此后自检永久可用。
- **测试**：全量 **373 项 = 372 通过 / 0 失败 / 1 跳过**（无回归）；Release 0 警告 0 错误；git diff --check 通过。
- **部署**：4 DLL SHA-256 一致（Core `519e962f…`、UI `b1847164…`、NxHost `ab22848e…`、Entry `a3fed257…`；NX 未运行）；已创建 `deploy/nxai_custom/application/selfcheck/` 目录（空，待放种子）。
- **遗留**：用户放置种子部件 → 点"几何自检" → 四场景报告 → 场景 A/B 端到端。

## 2026-10-17 · Phase 8.2 XZ 草图圆弧平面对齐修复（离线验证通过；已部署；实机复验待执行）

- **自检首战结果**（种子方案生效）：setup/caseA（XY 圆→拉伸）/caseC（四基本体）/caseD（失效引用拒绝）全部通过；**caseB（XZ U45 d10 圆→绕 Z 360°）失败**——XZ 草图 committed 后随即被后置验证回滚，异常 `AddGeometry: 圆弧的平面必须与草图平面平行`（operation_error.log 实录）；旋转报"试图使用不活动的对象"是引用已回滚草图的次生错误。
- **根因**：BuildSketchProfile 用手工推算 frame（GetWcsPlaneFrame + u×v 推导）构造圆弧，但 **NX 给 XZ 平面草图实际分配的坐标系以 sketch.Orientation 为权威**，两者不一致——圆弧落在与草图平面不重合的平面上被拒。
- **修复**（仅 FeatureToolExecutor.cs BuildSketchProfile）：激活后直接从 `sketch.Orientation.Element` 读 NX 实际局部轴（u=列1/v=列2/n=列3，与 TryGetSketchFrame 同源已核实映射）；圆弧用草图自身 (u,v) 构造→法向=草图平面法向 by construction；center_u/v 沿 NX 实际 u/v 轴偏移。删除手工推算的 GetWcsPlaneFrame 与 PlaneU/VVector scratch 静态量。PhaseLog 新增 frame 输出行（u/v/n 实测值）。
- **测试**：全量 **373 项 = 372 通过 / 0 失败 / 1 跳过**（无回归）；Release 0 警告 0 错误；git diff --check 通过。
- **部署**：4 DLL SHA-256 一致（Core `085108f7…`、UI `29649286…`、NxHost `ee23ec5e…`、Entry `0a779111…`；NX 未运行）。
- **遗留**：实机复验——点"几何自检"预期四场景全 ✓；随后核对 XZ U45 的世界坐标映射（PhaseLog frame 行可证实 NX 的 XZ 局部轴方向）；场景 A/B 端到端。

## 2026-10-17 · 全功能综合验收（离线层全通过；实机层待复验清单明确）

- **验收范围**：按十一层要求逐层执行——构建/测试/架构/部署/实机边界。
- **实际执行**：Release 构建（0 警告 0 错误，退出码 0）；全量测试 **373 项 = 372 通过 / 0 失败 / 1 跳过**（实际运行器结果，非沿用旧数）；`git diff --check` 通过；Core 无 NXOpen 依赖核实（csproj 0 引用、无 using，源码命中均为注释）；ToolRegistry 8 工具与 Schema/解析器/允许清单一致；4 DLL 部署 SHA-256 与构建产物逐一一致（8.2 版本）。
- **功能清单与状态边界**：15 项功能，源码全部实现、离线测试全部覆盖；实机已验证 9 项（基本体×4、双模式、上下文部分、聊天 UI、模型服务、自检部分）；实机待复验 6 项（XZ 草图+旋转复验、布尔三运算、场景 A/B 端到端、确认/自主对比、Phase 5 剩余上下文项）。
- **报告**：`docs/reports/COMPREHENSIVE_ACCEPTANCE_20261017.md`（含清单表、分域测试统计、实机待执行清单）。
- **Git**：本地 HEAD=412b1ad（=上次确认的远端 HEAD，无新提交需推送）；验收期间 GitHub 探测连接失败（间歇性，不影响交付）。
- **结论**：离线层全通过；实机层待用户执行五项（几何自检复验、场景 A/B、布尔、模式对比）——在场景 A/B 真实通过前不开始新功能。

## 2026-10-17 · Phase 8.2 统一自动化验收入口 + 补充回归测试（离线验证通过；已部署）

- **新增 `scripts/run-acceptance.ps1`**：PowerShell 5.1 兼容（UTF-8 BOM），四种模式（Offline/NxSelfCheck/ModelE2E/All）；8 项检查（构建/全量测试/diff-check/Core 无 NXOpen/活跃执行器无保存路径/注册表 8 工具/允许清单/部署哈希）；输出 `artifacts/acceptance/latest.json` + `latest.md`；关键失败退出码 1。含一个已知修正：活跃执行器保存路径检查缩小到三个执行器文件（BlockFeatureCreator 是 Phase 0 遗留测试工具，不在当前工具链）。
- **新增 IndependentRequestChainTests 3 项**：跨轮独立请求（sketch→独立轮次 extrude 引用解析）、失效引用拒绝（不产生几何、无假成功）、空白模板候选契约（UseBlankTemplate=true + 空 TemplatePresentationName 配对钉死）。
- **验收运行结果**：Offline 模式 8/8 全部 Passed（重部署后）；deploy.hashmatch 在重建后正确检测过期部署（预期行为——重建后需重部署）。
- **部署**：4 DLL SHA-256 一致（Core `f9aa4e3c…`、UI `9b9b7959…`、NxHost `6916327c…`、Entry `d52187d3…`；NX 未运行）。
- **遗留**：a81bd72（综合验收报告）+ 本轮提交待推送（GitHub 间歇不可达）；实机五项清单不变。

## 2026-10-17 · Phase 8.3 XZ/YZ 草图用显式 Csystem 修复（离线验证通过；已部署；实机复验待执行）

- **决定性证据**（PhaseLog frame 实测）：XZ 平面请求的草图 NX 实际分配了 XY 坐标系（u=(1,0,0) v=(0,1,0) n=(0,0,1)）——NX 的 `PlaneOption.ExistingPlane + Plane(normal=(0,-1,0))` 被重定向到 XY。这解释了 7.4/8.2 的 XZ 圆弧失败：圆弧按 NX 实际的 XY frame 构造（8.2 修复后正确），但**语义上**用户要的是 XZ 平面上的圆，落在 XY 上导致旋转校验拒绝（Z 轴法向与 XY 草图法向相同→不平行）。
- **修复**（仅 FeatureToolExecutor.cs ExecuteCreateSketch）：XZ/YZ 平面改用 `Sketch.PlaneOption.NewCsys` + `Csystem = CreateCoordinateSystem(origin, Matrix3x3, isTemporary=true)`（API 已核实 NX3+），Matrix3x3 按各平面的正交基显式构造——XZ: local X=(1,0,0), local Y=(0,0,1)（=世界Z）, local Z(法向)=(0,-1,0)；YZ: local X=(0,0,1), local Y=(0,1,0), local Z=(1,0,0)。XY 保持 ExistingPlane 不变（NX 默认行为正确）。BuildSketchProfile 已从 sketch.Orientation 读实际 frame，修复后自然与 Csys 一致。
- **测试**：全量 **376 项 = 375 通过 / 0 失败 / 1 跳过**（无回归）；Release 0 警告 0 错误；git diff --check 通过。
- **部署**：4 DLL SHA-256 一致（Core `8dcd0b4d…`、UI `d7e5c600…`、NxHost `49eec4ba…`、Entry `d52187d3…`；NX 未运行）。
- **遗留**：实机复验——点"几何自检"预期四场景全 ✓（caseB 的 XZ 草图应实际落在 XZ 平面上，旋转校验通过，产生圆环）；XZ U45 世界坐标映射核对（center_u=45 应映射到世界 X=45）。

## 2026-10-17 · Phase 8.4 caseB 根因修复：临时 CSYS 导致 Commit 拒绝（离线验证通过；部署待 NX 退出）

- **决定性证据**（PhaseLog + operation_error.log）：8.3 的 NewCsys+Csystem 路径下，XZ 草图在 `Builder.Commit()` 抛 **"输入参数为空或无效"**——不再是 7.4 的"圆弧平面不平行"（该问题已被 8.2/8.3 的 frame 修复解决）。根因：`CreateCoordinateSystem(origin, Matrix3x3, isTemporary: true)` 创建的**临时 CSYS 不可被 SketchInPlaceBuilder 附着**（临时 CSYS 不显示、不保存、无 Tag 供特征引用）。
- **修复**（仅 FeatureToolExecutor.cs ExecuteCreateSketch）：`isTemporary` 从 `true` 改为 `false`——非临时 CSYS 成为真实基准对象供草图附着；在 UndoMark 内创建，失败草图回滚时一并回滚。
- **测试**：全量 **376 项 = 375 通过 / 0 失败 / 1 跳过**（无回归）；Release 0 警告 0 错误；git diff --check 通过。
- **部署**：**NX 正在运行（ugraf.exe），DLL 被锁定——按规则停止部署。** 构建产物 SHA：NxHost `afb1698d…`、Core `7bf7eeaa…`；用户退出 NX 后部署并核对。
- **遗留**：实机复验 caseB（预期：XZ 草图成功提交在 XZ 平面上→旋转校验通过→圆环产生）。

## 2026-10-17 · Phase 8.4 部署（用户确认 NX 关闭后）

- 4 DLL 部署至 `deploy/nxai_custom/application/`，SHA-256 与构建产物核对一致：Core `7bf7eeaa…`、UI `3bc7af21…`、NxHost `afb1698d…`、Entry `d52187d3…`。
- 待实机复验：几何自检四场景（重点 caseB——XZ 草图应成功提交在 XZ 平面上，旋转产生圆环）。
- Git：`f18bf1f` 本地提交完成；推送仍失败（GitHub 间歇不可达），网络恢复后重试。

## 2026-10-17 · Phase 8.5 NewCsys 补 OriginOption/SketchOrigin（离线验证通过；已部署；实机复验待执行）

- **再取证**：8.4 非临时 CSYS 仍报"输入参数为空或无效"——排除临时性后，唯一剩余解释是 NewCsys 方式还要求显式设置**原点**相关属性（反射核实 SketchInPlaceBuilder 有 OriginOption: OriginMethod（SpecifyPoint/WorkPartOrigin）与 SketchOrigin: Point 属性，二者从未被设置，默认 Infer 状态下 origin 未解析）。
- **修复**（仅 NewCsys 分支）：补 `OriginOption = OriginMethod.WorkPartOrigin` + `SketchOrigin = Points.CreatePoint(0,0,0)`（与 CSYS 构造原点一致）。
- **测试**：全量 **376 项 = 375 通过 / 0 失败 / 1 跳过**（无回归）；Release 0 警告 0 错误。
- **部署**：4 DLL SHA-256 一致（Core `044ef6a3…`、UI `4bd31bae…`、NxHost `7eb0108e…`、Entry `d52187d3…`；NX 未运行）。
- **遗留**：实机复验 caseB。若仍失败，下一排查项是 OriginOption=SpecifyPoint + SketchOrigin 指向 CSYS 原点（而非 WorkPartOrigin）。

## 2026-10-17 · Phase 8.6 SimpleSketchInPlaceBuilder 修复 XZ/YZ 草图（离线验证通过；已部署；实机复验待执行）

- **决定性证据**（用户录制的 NX journal，`Desktop/journal.py`）：NX GUI 在 XZ 平面创建草图用的是 **`SimpleSketchInPlaceBuilder`**（`CreateSimpleSketchInPlaceBuilder()`）——不是我们一直用的 `SketchInPlaceBuilder2`。journal 完整属性序列：`CreateFixedTypePlane(origin, matrix, update)` → `Directions.CreateDirection(origin, xDir)` → `Xforms.CreateXformByPlaneXDirPoint(plane, xDir, origin)` → `CoordinateSystems.CreateCoordinateSystem(xform, update)` → builder 设 `UseWorkPartOrigin=false`/`CoordinateSystem=csys`/`HorizontalReference.Value=xDir`/`SketchOrigin=origin` → `Commit()`。`SketchInPlaceBuilder2` 的所有变体（ExistingPlane/NewCsys+OriginOption）在 XZ/YZ 均被 NX 拒绝——**SimpleSketchInPlaceBuilder 才是 NX 自身记录的正确 builder**。
- **修复**（FeatureToolExecutor.cs ExecuteCreateSketch）：WCS 平面（XY/XZ/YZ）改用 SimpleSketchInPlaceBuilder 路径，精确镜像 journal 序列（CreateFixedTypePlane→CreateDirection→CreateXformByPlaneXDirPoint→CreateCoordinateSystem→builder 四属性→Commit）。`ResolveWcsFrame` 提供 XYZ 三平面的正交基（center_u/v 语义不变：XZ u=X/v=Z, YZ u=Y/v=Z）。SelectedPlane 模式保留原有 face/datum 路径不变（后续可单独迁移）。BuildSketchProfile 的 sketch.Orientation frame 逻辑不变（修复后自然与 CSYS 一致）。误删的 DescribeProfile/VecNormalize/VecDot/Cross/IsPlanarFace/GetPlanarFaceFrame 已恢复。
- **测试**：全量 **376 项 = 375 通过 / 0 失败 / 1 跳过**（无回归）；Release 0 警告 0 错误；git diff --check 通过。
- **部署**：4 DLL SHA-256 一致（Core `4890e4cb…`、UI `e84a0878…`、NxHost `4489bf65…`、Entry `d52187d3…`；NX 未运行）。
- **遗留**：实机复验——点"几何自检"预期四场景全 ✓（caseB XZ U45 圆草图应成功提交在 XZ 平面上→旋转产生圆环）→ 场景 A/B 端到端。

## 2026-10-18 · Phase 8.7 Revolve Commit AV 根因修复（实机验收全部通过；案例B闭环）

- **实机故障矩阵**（PhaseLog + operation_error.log 逐轮取证）：
  ① caseB 在 `RevolveBuilder.CommitFeature()` 处 NX 原生**内存访问违例（AV）**——仅发生在非关联 `CreateAxis(Point3d, Vector3d)` 轴的路径（实验 A 改关联 Point+Direction 重载后 AV 消失，但未能单独证实为根因）。
  ② 按用户录制 journal.cs（NX GUI 实录，XZ 草图+Revolve 成功序列）对齐后，AV 彻底消失——变为可捕获的托管异常（不再进程级）。
  ③ `RegionBoundaryRule` 两次实机失败"缺少截面线串"（seedPoint 从 sketch.Origin 改为圆心 (45,0,0) 后仍失败）——该规则在程序化构建语境不可用，回退到 caseA 实证成功的 `CurveFeatureRule`。
  ④ CommitFeature 成功、圆环真实产生（实测 (-50,-50,-5)–(50,50,5)）后，被自家后置校验**误拒**——校验包络算出 ±50.249。
- **根因**（层层排除后最终定位）：`ComputeRevolveEnvelopeAabb` 的 Arc 分支用 `center ± r` 在全三轴取点——落到圆自身平面外，产生假点（caseB 圆心 (45,0,0) r=5 → 伪造 Y=±5 点），径向距离膨胀 √(50²+5²)≈50.249 > 0.01mm 容差 → 正确圆环被拒回滚。同文件拉伸版 `ComputeProfileExtrudeAabb` 在 Phase 7.2 已修同款 bug（注释"never a 3D box-corner set"），旋转版漏修。
- **修复**（仅 FeatureToolExecutor.cs）：
  ① 新增 `CreateRevolveSectionFromSketch`（revolve 专用）：按 journal.cs 序列镜像——3 参 `CreateSection(0.0095,0.01,0.5)`+容差、`OnlyCurves`、`CurveFeatureRule`+`seed=null`+7 参 `AddToSection(chainWithinFeature=false)`、**`EvaluateAndAskOutputEntities`**（此前从未调用）、RegionBoundary 实机失败如实记录在注释。共享 `CreateSectionFromSketch`（extrude）不动。
  ② `ExecuteCreateRevolve` 配置段按 journal 顺序重建：builder→Limits→`Tolerance=0.01`→Section 早挂→`SmartVolumeProfile(FCI)`→`SetStart/EndLimitHelperPoint((0,0,0))`→`CreateAxis(null, Direction)`（journal :895-899 原文 null Point）+Axis 双赋值→`ParentFeatureInternal=true`→Commit。RegionBoundary 交换为已验证的 CurveFeatureRule。
  ③ `ComputeRevolveEnvelopeAabb` Arc 分支改为圆自身平面上的极端点（`center ± r·xDir / ± r·yDir`，`arc.Matrix` 列=局部轴），与拉伸版同款已验证数学。
- **测试**：新增 `RevolveEnvelopeAabbTests` 2 项（caseB 精确几何的环面包络钉死 (-50,-50,-5)–(50,50,5)、box-corner 必然膨胀 50.249 文档化）。全量 **378 项 = 377 通过 / 0 失败 / 1 跳过**（无回归）；Release 0 警告 0 错误。
- **部署**：NxHost DLL SHA-256 一致（`77378ea1…`；NX 未运行）。
- **实机验收（2026-10-18 07:45）**：几何自检 **4/4 全 ✓**——caseA (XY 圆 d30→拉伸 50, bbox 匹配) / caseB (XZ U45 d10 圆→绕 Z 360°，bbox 精确 (-50,-50,-5)–(50,50,5)) / caseC (四基本体) / caseD (失效引用安全拒绝)；**场景 B 端到端**对话链路成功——XZ 草图 SKETCH_000 创建→旋转 REVOLVED(2) 引用返回，AV 终结。
- **遗留**：远端推送（GitHub 间歇不可达）；本轮为 caseB 专项收官，布尔三运算等其余实机待复验项保持既有清单。

## 2026-10-20 · 倾斜轴 360° 旋转包络修复（实机验收通过；用户报告）

- **实机故障**（自主模式，XY 平面 d20 圆形草图，用户选中的关联直线特征为轴，360°）：`REVOLVED(3)` 提交成功（`commit done: bodies 0→1`）后被 360° 精确包围盒验证**误拒回滚**——真实实体 AABB (-206.144,-10.000,-147.596)–(10.000,203.023,147.596) 与预测 (-206.223,-10.080,-144.723)–(10.080,203.103,144.723) 不符（Z 两端各差 2.873mm）。
- **根因**（PhaseLog 10:43:53 链 + 数学重现）：`ComputeRevolveEnvelopeAabbCore` 对圆弧只采样圆心 ± r·e1/±r·e2 四点；对非轴对齐轴，圆周到轴的径向极值点位于任意方位角（本例 ~44.6°，恰在四采样点之间）→ maxR 低估 ~2.9mm（反推 r≈10 与 d20 草图吻合）。另经离线测试暴露结构性缺陷："全局轴向范围 × 全局 maxR" 圆柱界在轴向坐标与径向距离沿圆相关时（倾斜轴）会越界。
- **修复**（新增 Core 纯数学 + NxHost 接入，commit `12e39ea`）：
  ① `RevolveEnvelopeMath`：轴向范围闭式解 ± r·|n×u|（无采样）；逐轴极值函数 f±(θ)=u_i·t(θ)±s_i·r_p(θ) 用**确定性 Lipschitz 分支定界**求解（|f'| 界已证明，证书缺口 ≤ MaxRadialError=5×10⁻⁴mm）。
  ② NxHost `ComputeRevolveEnvelopeAabbCore`：完整圆弧（|a1−a0|=2π）走精确数学；部分弧/直线/矩形路径不变。
  ③ 修复过程中同步修正：`Vector` 移至命名空间级复用；一套"环面"测试预期被纠正（圆平面含轴时 360° 扫掠保持在自身平面内，Y 范围为 0 而非 ±r）。
- **测试**：新增 `RevolveEnvelopeMathTests` 9 项——实机失败重现（倾斜轴 d20，maxR 与**独立稠密参考解**（10 万点扫描，非被测算法自证）一致且大于旧 4 点值）、逐轴包含性不变量（±0.01mm）、已知几何（X 轴上球体 [-10,10]³、XZ 圆绕 Y 平面含轴退化）、非法输入拒绝（零向量轴/负半径/非有限半径）。全量 Release **534 项 = 533 通过 / 0 失败 / 1 跳过**；git diff --check 通过。
- **提交与部署**：commit `12e39ea` 快进推送至 `origin/recovery/pre-review-20261014-234252`（推送期间 GitHub 三次连接超时后成功）；`build.ps1` Release 构建并部署，Core `60e7cfc3…`、NxHost `455ebc5b…` 与构建产物一致（NX 已关闭）。
- **实机验收（2026-10-20，证据来源：用户报告）**：自主模式执行——WCS XY 平面创建 d20 圆（局部 U=45、V=0）成功（SKETCH_000）；使用用户实际选中的关联直线特征为轴 360° 旋转，`REVOLVED(2)` **返回成功**；用户确认验证通过：**无 verify FAIL、无回滚**。原始日志目录未另行提供，以用户在对话中提供的执行结果为证。
- **遗留**：本条仅关闭"倾斜轴 + XY 圆形草图 + 360° 旋转包络"失败案例；部分角度（60°/270°）事实驱动验证的进一步实机复验、以及其余 Phase 7 实机清单项，保持既有待验收状态。

## 2026-10-20 · 布尔未知状态探针补充修复：归因快照（离线验证通过；实机复验待执行）

- **补充根因**（前轮 `f55a574` 修复的遗留缺口）：`ProbeCommittedFeature` 对 `boolean_operation` 统一匹配 UNITE/SUBTRACT/INTERSECT 且**扫描全部工作部件特征**——零件中任何已有布尔特征都会被误当作本次未知操作的提交证据（待核差集 + 既有无关并集 → 误判"已提交"→ 拦截/恢复方向全错）；且结果描述固定报告第一个类型名（UNITE），与实际发现不符。
- **修复**（FeatureToolExecutor.cs + NxToolExecutor.cs + ChatSessionController.cs）：
  ① 归因快照：`INxToolExecutor` 新增只读 `ProbeFeatureSnapshot(toolName)`（按工具特征类型收集当前部件的 JournalIdentifier 集合）；控制器在**每次 BeginAttempt 之前**采集快照并按 ledger id 保存。
  ② 归因探针：`ProbeCommittedFeature(toolName, summary, preExistingFeatureIds)`——匹配类型但**不在快照中**的特征才可归因于本次尝试（CommittedFound，消息报告**实际发现的特征类型**，不再固定 UNITE）；仅有快照内特征（操作前已存在）→ NotCommitted（消息说明"均为待核操作开始前已存在"）；**无快照且存在匹配特征 → 无法归因 → 返回 null 安全停止**（不猜测）。
  ③ 控制器 `ResolveUnknownOutcome` 优先使用最新待决条目的快照调用三参探针。
- **测试**：`BooleanAfterContinuousModelingTests` 扩至 **17 项**——待核差集+既有无关并集不误判（继续执行）、待核交集+既有差集不混淆（继续执行）、可归因新交集→恢复终结事实并停止（防重复）、无快照+存在匹配特征→安全停止不猜测、三种布尔操作（unite/subtract/intersect）×自主/确认模式全部到达执行器且参数解析正确、无效引用拒绝。7 个测试替身补齐新接口成员（1 处正则批量补丁造成的 4 处语法损伤已逐一手工修复）。全量 Release **551 项 = 550 通过 / 0 失败 / 1 跳过**。
- **部署**：build.ps1 构建+部署成功（NX 已关闭），NxHost `2e1f1dd7…` 与构建产物一致。
- **遗留（实机验收设计修正）**：账本为 NX 进程级内存状态，**重启 NX 后旧 Attempting 条目不存在**——实机验收须区分两类：①普通差集验收（重启 NX 后在隔离部件验证参数解析/执行/实体状态）；②未知状态恢复验收（须在同一 NX 会话内**确实制造**待核条目——例如取消一次布尔确认后立即重试——并实际触发宿主探针；无法安全复现时明确标记为待验证，不宣称通过）。

## 2026-10-20 · 布尔未知状态探针二次补充：空快照基线与按操作类型归因（离线验证通过；实机复验待执行）

- **两个遗漏**（`fb6214b` 归因快照修复的遗留）：
  ① 快照判断用 `{ Count: > 0 }`——**非 null 的空集合**（快照成功采集且操作前没有匹配特征的有效基线）被误当成"无快照"，落入无法归因分支；
  ② 探针对 `boolean_operation` 同时匹配 UNITE/SUBTRACT/INTERSECT 三种特征——待核差集遇到新出现的 UNITE（或交集遇到 SUBTRACT）会被误判为"本次已提交"。
- **修复**（FeatureToolExecutor.cs + NxToolExecutor.cs + ChatSessionController.cs）：
  ① 快照语义：null = 快照不可用（无法归因，安全停止）；**非 null 空集合 = 有效基线**（操作前无匹配特征，任何新出现匹配特征都可归因）。
  ② 按操作类型归因：`FeatureKindsForTool` 接收结构化 `BooleanOperationType?`（unite→UNITE / subtract→SUBTRACT / intersect→INTERSECT，**绝不三合一**）；类型缺失时无法猜测→返回 null 安全停止。`ProbeFeatureSnapshot` 同样按类型采集快照。
  ③ 控制器：BeginAttempt 前按 `BooleanParameters.Operation` 结构化确定特征类型并采集对应快照；恢复时将 (快照, 操作类型) 成对传给探针——探针使用的操作类型与产生待核条目的实际操作一致，不依赖自然语言摘要。
- **测试**：`BooleanAfterContinuousModelingTests` 扩至 **21 项**——有效空快照+新 SUBTRACT→正确归因停止（防重复）；null 快照+存在匹配特征→安全停止；待核差集+新 UNITE→不误判（差集继续）；待核交集+新 SUBTRACT→不误判（交集继续）；三种操作×自主/确认模式参数解析与执行无回归。测试替身签名同步（3 参探针 + featureKind 快照）。全量 Release **555 项 = 554 通过 / 0 失败 / 1 跳过**。
- **部署**：build.ps1 构建+部署成功（NX 未运行），NxHost/Core 与构建产物一致。
- **遗留（实机验收设计，同前轮两类划分）**：①普通差集验收（重启 NX 后隔离部件）；②未知状态恢复验收（同一会话内确实制造待核条目并触发探针；无法安全复现时不宣称通过）。

## 2026-10-20 · 更正：eefd752 测试计数记录修正（文档更正，无代码变更）

- **更正内容**：上一条目（`eefd752`，探针按操作类型归因）记录的全量 Release 测试计数有误——实际运行 **556 项 = 555 通过 / 0 失败 / 1 跳过**，原记录 **555 项（554/0/1）少计 1 项通过**。专项测试 **21/21 通过**的记录不变。
- **证据**：TRX 归档 `tests/NXAI.Assistant.Tests/TestResults/verify_eefd752_20261020/eefd752_full.trx`（555 Passed + 1 NotExecuted，退出码 0）；4 个部署 DLL 与对应 Release 构建产物 SHA-256 一致。

## 2026-10-23 · Phase 9.2.1：修复四孔型工具 Schema 非法 JSON 实机错误（离线验证通过；已部署待实机复验）

- **实机错误**（2026-10-23 08:09:14，用户报告）：输入"创建100*60*40块几何中心位于原点"→ `工具 create_threaded_hole 的 JSON Schema 不是合法 JSON（内部错误）。`
- **根因**（对应排查分类第 1+4 种）：`StandardHoleTool.cs` 的 Schema 模板在 `["__REQ__"]` 与 `["__SIZES__"]` 处自带引号，而替换文本（`string.Join` 后的 `"size", "placement_face_ref", "unit"` 与九规格枚举）本身已带引号——展开产生 `[""size", …]` 非法 JSON，**四个孔型工具全部受影响**。`ModelService.SendAsync`（ModelService.cs:505-533）在**每次请求**构建 tools 载荷时统一解析全部 13 个工具 Schema，任一失败即整体 `InvalidRequest`——与提示词无关，"创建长方体"请求只是恰好触发，错误名取自循环中第一个失败工具。`create_block` Schema 本身一直合法（字面量无替换）。
- **修复**（最小化，仅模板去两处引号）：`"required": [__REQ__]`、`"enum": [__SIZES__]`。工具参数契约、类型、必需字段、安全边界零改动；未触碰 NXOpen API、标准 XML、业务逻辑。
- **测试**：新增 `ToolSchemaValidityTests` 5 项——①注册表全部 13 工具 Schema 经 `JsonDocument.Parse`（=ModelService 同款解析器）可解析；②序列化结构回环（type=object/properties/required 每项存在且带 type/additionalProperties=false）；③四孔型 Schema 枚举与深度字段解析后正确、`""` 双引号不再出现；④`create_threaded_hole` 不再触发内部错误；⑤`create_block` Schema 不变且有效。全量 Release **779 项 = 778 通过 / 0 失败 / 1 跳过**（退出码 0，无回归）。
- **构建部署**：`build.ps1` Release 构建成功（0 警告 0 错误）；4 DLL 部署至 `deploy/nxai_custom/application`，SHA-256 与构建产物逐一一致——Core `227affa5…`、UI `51e0d823…`、NxHost `55363e15…`、Entry `47a4eaa5…`（NX 未运行）；startup 目录仅 nxai_assistant.men，无探针/日志/NXOpen 程序集混入。
- **遗留（实机复验）**：用户重试"创建100*60*40块几何中心位于原点"确认 Schema 错误消失且长方体真实创建；四孔型按 C2–C7 清单抽查（沉头 M2/M1.6 结构化拒绝）。离线测试通过不等于实机通过。

## 2026-10-22 · Phase 9.2：四种标准孔（螺纹/间隙/埋头/沉头）正式集成（离线验证通过；实机验收待执行）

- **任务**：将此前已实机验证的孔探针（螺纹孔 9/9、Simple/Counterbored/Countersunk M6）整合进正式工具链，覆盖 36 种孔型×规格组合，部署到 `deploy/nxai_custom`。
- **标准数据核查（NX2512 实际 XML，只读）**：
  - 螺纹孔（`NX_Thread_Standard.xml`）：Metric Coarse + RadialEngage=0.75 九规格（M12→M1.6）全部唯一记录。
  - Simple/Counterbored（`NX_Screw_Clearance_Hole_Standard.xml`）：ISO + General Screw Clearance / Socket Head 4762 + Close (H12) 九规格全部唯一记录。
  - **Countersunk：ISO Socket Flat Head 10642 系列仅 M12–M3 有记录（7/9）——M2/M1.6 在该 ISO 标准中不存在**（DIN 95/97 Machine 系列才有）。按规则不套用其他标准数值：这两组实现为标准表级结构化拒绝（"没有匹配的标准记录"），并以离线测试钉死。
- **已验证探针读取**（未复制进正式代码，按架构整合）：
  - `C:\Users\yuhua\AppData\Local\Temp\threaded_hole_probe\{ScrewClearance,Counterbored,Countersunk}HoleProbe.cs`：HolePackageBuilder + SizeOfHole=ScrewClearance + 每族 TypeOfHole + 4 字符串属性（ScrewType/ScrewSize/ScrewStandard/ScrewFitOption，其中 ScrewStandard←表 Standard 字段是"找不到标准数据"修复）+ 全部 ScrewClearance* 表达式 SetFormula+读回 ±0.001 门。
  - `E:\Templates\ThreadedHoleTableMultiProbe.cs`：SizeOfHole=Custom + TypeOfHole=ThreadedHole + ThreadStandard/ThreadSize/RadialEngageOption + 11 项 Threaded* 表达式同步 + 深度门（thread<hole）。
- **主要文件**：
  - 新增 `src/NXAI.Assistant.Core/StandardHoleTable.cs`：纯 XML 表解析（唯一匹配/缺失/歧义/不支持规格全部结构化拒绝，离线可测）。
  - 新增 `src/NXAI.Assistant.Core/StandardHoleTool.cs`：四工具契约（create_threaded_hole / create_clearance_hole / create_counterbored_hole / create_countersunk_hole）+ 共享解析器（size 归一化、placement_face_ref 必填、螺纹深度成对提供且 thread<hole、埋头/沉头家族拒绝深度覆盖、diameter 永不接受）。
  - `HoleToolExecutor.cs` 新增 `ExecuteCreateStandardHole`：运行时读取 NX 安装真实标准 XML（UGII_ROOT_DIR 优先、安装路径兜底）→ 唯一匹配 → 放置面解析/平面门/UF 面框（复用 create_hole 已验证路径）→ 逐字段 SetFormula+读回门（读回不一致不提交）→ Validate 门 → 提交 → DoUpdate → 后置验证（alive+body 数不变+JournalIdentifier 回环）→ 失败回滚。
  - `CreateBlockTool.cs`（注册 4 工具）、`ExecutionMode.cs`（自主允许列表 +4）、`NxToolExecutor.cs`（接口 +1 方法）、`ChatSessionController.cs`（解析/路由/确认/分发）、`ToolConfirmationFlow.cs`（StandardHole 参数类型）、`AssistantChatForm.cs`（确认卡渲染：孔型中文名、规格、深度说明、放置面）。
- **测试**：新增 `StandardHoleTableTests`（49 项：36 组合矩阵、值钉死对照实表、Metric Fine 不误匹配、缺失/歧义/解析失败/不支持规格拒绝、NormalizeSize 规范化）+ `StandardHoleToolContractTests`（44 项：四解析器、注册 13 工具、允许列表 13 项、schema/解析器字段一致、M2/M1.6 沉头拒绝、深度规则）+ 控制器路由 4 项 + 5 处旧 9 计数断言更新为 13（10 个测试替身补齐新接口成员）。全量测试结果见 PROJECT_STATUS 本轮条目。
- **安全**：四工具均需确认（或自主模式按既有允许列表放行）；执行前工作部件/单位/确认部件路径校验；标准表缺失=拒绝不猜测；无任意代码执行面。
- **遗留（实机验收待执行）**：四工具×规格抽样在真实 NX2512 隔离部件中验证（含沉头 M2/M1.6 拒绝提示、孔特征树与"螺纹表未找到"警告核查、确认卡、回滚、自主模式路径）。离线测试通过不等于实机通过。

## 2026-10-23 · Phase 9.2.2：埋头孔语义修正——中文"埋头孔"路由到锥形沉孔（离线验证通过；已部署待实机复验）

- **实机问题**（用户报告）：输入"创建一个 M8 埋头孔"→ 模型调用 `create_counterbored_hole`，NX 创建出**圆柱形台阶扩孔**，工具却报成功并称之为"埋头孔"。用户明确：**中文"埋头孔"应指锥形沉孔**。
- **根因**（语义映射错误，非执行路径错误）：此前工具描述把"埋头孔"绑给了 Counterbored（圆柱沉头，ISO 4762），"沉头孔"绑给 Countersunk（锥形，ISO 10642）——与用户的中文习惯相反。两条 NX Builder 路径本身各自正确（Counterbored→`TypesOfHole.Counterbored`、Countersunk→`TypesOfHole.Countersink`），执行器从未混用。
- **修复**（语义反转，代码路径不动）：
  ① `create_countersunk_hole` 描述改称"锥形沉孔（Countersink，90° 锥面，ISO 10642）——用户所说'埋头孔'通常指本工具"，并保留"仅当用户明确要求创建埋头孔/锥形沉孔时调用"。
  ② `create_counterbored_hole` 描述改为"圆柱形沉头台阶孔（平底，ISO 4762 让位孔）……中文'埋头孔'通常指锥形沉孔——用户说'埋头孔'时请改用 create_countersunk_hole"，不再自称"埋头孔"。
  ③ 执行器成功消息明确报告**真实 NX 孔型**（锥形沉孔（Countersink，90° 锥面）/圆柱形沉头台阶孔（Counterbored）/螺纹孔/简单孔），details 增加 `nxHoleType` 字段；`StandardHoleTable`/NxHost/UI 三处族名文案同步（"埋头孔"→锥形沉孔、"沉头孔"→锥形沉孔、"圆柱形沉头台阶孔"）。
  ④ **提交前孔型读回门**（防误报成功的结构性守卫）：提交前核对 `builder.TypeOfHole` 与请求家族一致（ThreadedHole/Simple/Counterbored/Countersink），不一致即拒绝提交——防止字符串赋值静默打回 builder 默认值导致"读回全对但几何形状错"。
- **M8 锥形孔标准记录**（实际 XML 唯一匹配，已核）：Form=Countersunk + ScrewType='Socket Flat Head, 10642' + Size=M8 + Fit='Close (H12)' + Standard=ISO → HoleDiameter=8.4、CSinkDia=18.25、CSinkAngle=90、CSinkReliefDepth=1.2、Start/EndChamfer 0.6/45、HoleTipAngle=118。此前"沉头直径 15 / 深度 8.8"是 ISO 4762 圆柱头螺钉的**圆柱形扩孔**参数，属于 create_counterbored_hole 路径，不适用于锥形孔。执行器对 CSink* 字段逐项 SetFormula+读回 ±0.001 门，无猜值。
- **测试**：`StandardHoleToolContractTests` 新增 5 项语义守卫（埋头孔词归 Countersunk 描述、锥形+10642 明示、Counterbored 描述自述圆柱并显式改派 Countersunk 工具、M8 锥形记录=CSinkDia 18.25/90°且无 CBore 字段、M8 圆柱记录=CBoreDia 15/8.8 保持独立）→ 49/49 通过。全量 Release **784 项 = 783 通过 / 0 失败 / 1 跳过**（退出码 0）。
- **构建部署**：NX 未运行；`build.ps1` 成功（0 警告 0 错误）；4 DLL 部署 SHA-256 一致——Core `da79fa27…`、UI `d5b00b28…`、NxHost `844d35b5…`、Entry `e7f9f17c…`。`application\selfcheck\seed-mm.prt` 为既有自检种子（非本轮引入），保留。
- **遗留（实机复验）**：用户重试"创建一个 M8 埋头孔"→确认真实几何为**90° 锥面沉孔**（沉头直径 18.25）、成功消息报告"锥形沉孔（Countersink）"、无螺纹表警告；圆柱台阶孔路径抽查一次确认仍独立正确。离线测试通过不等于实机通过。

## 2026-10-23 · Phase 9.2.3：中文孔型映射定案——沉头孔=Counterbored（圆柱）、埋头孔=Countersink（锥形）（离线验证通过；已部署待实机复验）

- **实机证据**（用户报告）：输入"创建M8沉头孔"→ 模型调用 `create_countersunk_hole`，生成 `COUNTERSUNK SCREW HOLE(2)`（锥形）；同时用户确认"埋头孔"已正常创建锥形孔。用户指出此前 9.2.2 的描述把两个词都引向了锥形工具。
- **正式中文映射（用户 2026-10-23 定案，项目正式规定，不按外部习惯调换）**：
  - 沉头孔 → `create_counterbored_hole` → `TypesOfHole.Counterbored`（圆柱形扩孔、平底台阶，ISO 4762）
  - 埋头孔 → `create_countersunk_hole` → `TypesOfHole.Countersink`（锥形扩孔、斜面，ISO 10642）
  - 螺纹间隙孔 → `create_clearance_hole` → `TypesOfHole.Simple`（普通螺钉过孔，无内螺纹）
  - 螺纹孔 → `create_threaded_hole`（内螺纹孔）
- **修复**（仅工具描述与显示文案，几何/标准表/执行路径零改动）：
  ① `create_counterbored_hole` 描述改称"标准沉头孔：圆柱形扩孔、平底台阶……当用户明确说'沉头孔'（或'圆柱形沉头孔/台阶孔/让位孔'）时必须调用此工具；不可用于锥形埋头孔"，并声明 NX 类型 `TypesOfHole.Counterbored`。
  ② `create_countersunk_hole` 描述改称"标准埋头孔：锥形扩孔、斜面……当用户明确说'埋头孔'或'锥形埋头孔'时调用此工具；**用户只说'沉头孔'时，不可调用此工具**（沉头孔=圆柱形平底台阶，用 create_counterbored_hole）"，并声明 `TypesOfHole.Countersink`。
  ③ `create_clearance_hole` 描述认领"螺纹间隙孔"并明示"不创建内螺纹"；`create_threaded_hole` 描述认领"螺纹孔"并声明 `TypesOfHole.ThreadedHole`。
  ④ `StandardHoleTable`/NxHost 成功消息/UI 确认卡四处族名同步：沉头孔（Counterbored）/埋头孔（Countersink）/螺纹间隙孔/螺纹孔；成功消息 NX 孔型文案改为"锥形埋头孔（Countersink，90° 锥面）"/"圆柱沉头孔（Counterbored，平底台阶）"。9.2.2 中"用户说'埋头孔'时请改用…"的反向改派文案删除，消除双词歧义。
- **路由层核查**：`ChatSessionController.StandardHoleFamilyForTool` 按工具名（英文常量）确定性映射，无中文别名硬编码；自然语言的词→工具选择完全发生在模型侧，本地唯一路由面是工具 Description——四条描述现已互斥（Counterbored 独占"沉头孔"，Countersink 独占"埋头孔"并显式拒绝仅"沉头孔"请求）。
- **测试**：`StandardHoleToolContractTests` 重写语义守卫（Counterbored 独占"沉头孔"+TypesOfHole 声明+拒绝锥形；Countersunk 独占"埋头孔"+"只说'沉头孔'时不可调用"+指回 Counterbored 工具；Clearance/Threaded 认领各自中文词；工具名→family→NX 孔型确定性映射；无"埋头孔/沉头孔"混合歧义文案）→ **53/53 通过**。全量 Release **788 项 = 787 通过 / 0 失败 / 1 跳过**（退出码 0）。
- **构建部署**：NX 未运行；`build.ps1` 成功（0 警告 0 错误）；4 DLL 部署 SHA-256 一致——Core `cca9aa12…`、UI `eb029638…`、NxHost `7adae5fa…`、Entry `c47438c0…`；`nxai_assistant.men` 源/部署哈希相同（`319ee927…`，字节级未变，本轮未修改菜单）。
- **限制声明**：单元测试能保证工具描述互斥、工具名→孔型映射确定、M8 两族标准记录独立（Counterbored CBoreDia 15/CBoreDepth 8.8；Countersink CSinkDia 18.25/90°）；**不能保证模型对任意自然语言输入必然选择正确工具**——该选择由模型基于描述做出，须实机验证。
- **遗留（实机复验）**："创建M8沉头孔"→圆柱形平底台阶孔（CBoreDia 15/深 8.8）；"创建M8埋头孔"→锥形孔（CSinkDia 18.25/90°）；两个结果均以真实执行记录和 NX 几何为准。离线测试通过不等于实机通过。

## 2026-10-23 · Phase 9.3：四种标准孔支持"选定点"放置（离线验证通过；已部署待实机验收）

- **实机问题**（用户报告）：选中一个点（NX 上下文显示 `PointFeature`）后输入"创建M6螺纹孔"，插件提示"选中的对象是一个点，不是平面"。现有流程只支持选面中心。
- **参考实现**（用户提供的实机录制 journal，`E:\Templates\journal.cs`）：`PointFeature`→`FindObject("POINT 1")`→`NXOpen.Point`（模型坐标）；点的 Section 挂载 = `CreatePoint(point, null Xform, WithinModeling)` + `CreateRuleCurveDumbFromPoints` + `AddToSection`——与现有 position section 模式一致，仅来源点不同。
- **根因**：`NxContextReader.DescribeFeature` 把选中的 PointFeature 归为 `Kind="feature"` 不带坐标；契约要求 `placement_face_ref`（点模式无此引用）→ 双重阻断。
- **实现**（沿用既有架构，最小扩展）：
  ① **上下文层**（NxContextReader）：PointFeature 经 `Feature.GetEntities` 解析为唯一 `NXOpen.Point`（0/多点如实标注不可解析，不猜坐标）→ `Kind="point"` + `Subtype="point_feature"` + 模型 XYZ；独立点 `Subtype="point"`。`NXOpen.Point.Coordinates` 即模型坐标（草图点同为模型空间对象，草图 2D 坐标不外泄）。
  ② **契约层**（StandardHoleTool）：新增 `placement_mode`（`selected_face_center` 默认 / `selected_point`）+ `StandardHolePlacementMode` 枚举；点模式下 `placement_face_ref` 必须缺省（提供即拒绝）、`location_u/v` 不支持；面中心模式原规则不变（缺 face_ref 的拒绝信息教模型改用 selected_point）。四工具 Schema 增加枚举字段、`placement_face_ref` 移出 required（条件必填）；四条描述同步说明两种放置方式。模型永不传坐标——点由宿主从**当前实际选区**解析。
  ③ **执行器**（HoleToolExecutor.ExecuteCreateStandardHole）：新增 `ResolveSelectedPointForHole`——当前选区解析唯一点，接受三类来源：独立 `NXOpen.Point` / `PointFeature`（GetEntities 解析，必须恰 1 个活点）/ 草图（`GetAllGeometry` 仅当**恰含 1 个点**时接受；0 或多点拒绝并要求用户明确选点，绝不擅自挑选）；多选/混选/无选择均拒绝。`FindUniqueSolidPlanarFaceContaining`——扫描工作部件所有实体（排除片体）的平面：面属活实体 + 平面 + 点到平面距离 ≤0.01mm（与 section 容差一致）+ **UV 参数域内包含**（点在无限平面上但超出面域即拒绝；参数增量 = 面内偏移÷导数模，平面导数恒定）+ 多面交界（>1 匹配）拒绝。孔心=选定点原坐标（不吸附、不投影、不回退面中心）；方向沿用面法向既有逻辑。全程 PhaseLog 记录：点来源/坐标、匹配面与体、距离与 UV 包含验证值、模式与方向。
  ④ **UI**：确认卡与请求描述区分两种模式（"选定点（孔心=该点，放置面由宿主解析）"）。
- **测试**：新增 12 项（四族点模式解析、点模式带 face_ref/location 拒绝、非法/非字符串 placement_mode 拒绝、面中心缺 face_ref 拒绝且提示 selected_point、四 Schema 广告 placement_mode 枚举且 placement_face_ref 不再 required、点→面接受/拒绝几何规则离线数学（面内/出界/离面/容差外）、多面歧义规则）→ 孔相关 119 项全通过。全量 Release **800 项 = 799 通过 / 0 失败 / 1 跳过**（退出码 0）。
- **构建部署**：NX 未运行；build.ps1 成功（0 警告 0 错误）；4 DLL SHA-256 一致——Core `66d7ef49…cbadc`、UI `070d8be6…1c76a`、NxHost `066cc0bb…b7451`、Entry `37f8ec7f…1ae55`；菜单字节级未变（`319ee927…`）。备份：`D:\AI\NX_AI_ASSISTANT_point_backup_20261023_103056`。
- **待实机验收**（一次性测试零件，草图点与三维点各一，均位于实体平面内部）：①选三维点 →"创建M6螺纹孔"→ 孔心=点坐标（非面中心）、无螺纹表警告；②选草图点 → 同上；③选整个草图（多点）→ 拒绝要求明确选点；④点在面边缘/外 → 拒绝不回退；⑤四种孔型点放置路径抽查；⑥原选面中心回归。离线测试通过不等于实机通过。

## 2026-10-23 · Phase 9.4：倒圆角与倒斜角正式集成（离线验证通过；已部署待实机验收）

- **参考 journal 分析**（用户提供，仅作实现参考）：
  - `Round corners of R2.cs`：`Features.EdgeBlendBuilder`（`FeatureCollection.CreateEdgeBlendBuilder(Feature)`）+ `ScCollector` + `EdgeMultipleSeedTangentRule(seedEdges, 0.5, true)` + **`AddChainset(collector, "2")`——半径是链集的表达式字符串**（mm），非独立属性；Tolerance=0.01；提交 `CommitFeature()`；特征类型 `EdgeBlend`。
  - `Inverted C2 bevel.cs`：`Features.ChamferBuilder`（`CreateChamferBuilder(Feature)`）+ `EdgeTangentRule` 挂 `SmartCollector` + **对称距离经 `FirstOffsetExp.SetFormula("2")` 写入**（C2=对称等距 2mm，`ChamferOption.SymmetricOffsets`）；`Option` 枚举 = SymmetricOffsets/TwoOffsets/OffsetAndAngle，Method = EdgesAlongFaces/FacesAndTrim。
  - 两个 journal 都未展示非对称倒角路径——正式工具只实现 SymmetricOffsets（不可靠的模式不做）。
- **API 核验**（NXOpen.xml 元数据）：`ChamferBuilder.Option/Method/Angle/FirstOffset/SecondOffset/FirstOffsetExp/SmartCollector/Tolerance`、`EdgeBlendBuilder.AddChainset(ScCollector,string)/GetChainsetAndSectionValue(...)/Tolerance/RemoveSelfIntersection` 等、工厂 `CreateChamferBuilder(Feature)/CreateEdgeBlendBuilder(Feature)`、规则 `CreateRuleEdgeDumb(Edge[])`。
- **正式实现**（新增 `create_fillet` / `create_chamfer`，全链路同既有工具）：
  - 契约（Core/EdgeFinishTools.cs）：fillet 收 radius、chamfer 收 distance（正数、≤1000mm 上限、unit 换算）；**目标边不进契约**——模型永不传边引用，宿主从当前选区解析。描述互斥（fillet 不提倒斜角；chamfer 明示 SymmetricOffsets 对称等距、非对称"暂不支持"）。
  - 执行器（NxHost/EdgeFinishToolExecutor.cs）：`ResolveSelectedEdges`——当前选区只接受实体 Edge（Feature 节点仅当解析出恰 1 条活边；混入非边目标即拒绝，不静默处理子集；去重按 JournalIdentifier）；确认后 `RevalidateEdges` 重验证每条边仍活着；**用 `CreateRuleEdgeDumb` 而非 journal 的相切链规则**（相切规则会吸收用户未选的相切邻边——工具不得触碰未选边）。圆角：`AddChainset(collector, radiusExpr)` + `GetChainsetAndSectionValue` 读回半径表达式 ±0.001 门；倒角：`Option=SymmetricOffsets` + `FirstOffsetExp.SetFormula` + 读回门 + **Option 提交前读回门**。后置验证：特征活、body 数不变、FeatureType 必须为 EDGE_BLEND/CHAMFER（错型即回滚）、JournalIdentifier 回环；Undo mark、失败回滚、Builder Destroy 全套沿用。
  - UI：解析分支、确认卡（半径/距离+目标说明）、执行分派、待办描述；自主允许列表 +2。
- **测试**：新增 `EdgeFinishToolContractTests` 25 项（R2/C2 解析与单位换算、非法半径/距离拒绝、1000mm 上限、未知字段拒绝、chamfer 拒绝 angle 字段（offset+angle 模式未提供）、注册 15 工具、允许列表 15 项、Schema 解析与 required 正确（radius vs distance 不互换）、描述互斥（fillet 不提倒斜角、chamfer 钉死 SymmetricOffsets+对称+暂不支持）、两条路由到执行器）。计数断言 13→15 七处更新；10 个测试替身补齐 2 个新接口成员。全量 Release **825 项 = 824 通过 / 0 失败 / 1 跳过**（退出码 0）。
- **构建部署**：NX 未运行；build.ps1 成功（0 警告 0 错误）；4 DLL SHA-256 一致——Core `76d86b68…946c`、UI `4ed0497d…1b96`、NxHost `5fd3c6c1…416a`、Entry `e8ae1245…0905`；菜单字节级未变（`319ee927…`）。备份：`D:\AI\NX_AI_ASSISTANT_edgefinish_backup_20261023_112521`。
- **待实机验收**：一次性部件选边 →"给当前选中的边倒 R2 圆角"→ 确认卡半径 2、特征 EDGE_BLEND、真实圆角几何；选边 →"给当前选中的边倒 C2 斜角"→ 特征 CHAMFER、对称 2mm；取消无几何变化；空选/选面等无效目标清晰拒绝；失败回滚。离线测试通过不等于实机通过。

## 2026-10-23 · Phase 9.4.1：修复圆角后置验证误回滚（type=BLEND 被 EDGE_BLEND 误判 + 回滚顺序；离线验证通过；已部署待实机复验）

- **实机日志链**（用户提供 + `%TEMP%\NXAI_Assistant_phase.log` 逐行核实）：边解析成功（1/2/4/12 条）→ Chainset 添加成功 → 半径读回正确（`radius expr '3' readback=3 delta=0`）→ `CommitFeature()` 返回 `EdgeBlend BLEND(2) type=BLEND update=ok` → `alive=True bodyCount=1→1` → **`roundTrip=False` 判定失败并回滚** → 回滚后 Builder Destroy 报 `NXException: 试图使用不活动的对象`。`execute.chamfer` 无任何日志（本轮只修圆角；倒角待单独核实）。
- **根因**（对照代码逐一确认）：
  ① **类型误判**：代码要求 `FeatureType=="EDGE_BLEND"`，真实 NX 返回 **`BLEND`**（实机日志证实）→ roundTrip 第一个子条件即 false。
  ② **roundTrip 依赖不稳定的 JournalIdentifier 字符串回环**（在工作部件特征树重找特征）——即使类型名修正也不可靠。
  ③ **清理顺序错误**：先 `UndoToMark` 回滚（使 Builder 失效）再 `Destroy()` → "试图使用不活动的对象"。
  ④ `expectedPart=(null)` 不影响判定（TryResolveWorkPart 对空路径跳过部件一致性检查，属既有约定）；bodyCount 1→1 通过。
- **修复**（EdgeFinishToolExecutor.cs，验证不关闭、只换可靠证据）：
  ① 类型判定改为**托管类型精确检查** `committedFeature is NXOpen.Features.EdgeBlend / is NXOpen.Features.Chamfer`（NXOpen.xml 核实的真实类，编译期保证），不再比较 FeatureType 字符串。
  ② 归属证明改为 `Feature.GetFaces()`（NX1899+ API，"faces created by the feature"）**非空**——圆角/倒角必然产生新面，零面=未实际生效；这是可验证的几何证据。判定逻辑提取为纯函数 `EdgeFinishPostcondition(committed, alive, typeMatches, faceCount, bodiesBefore, bodiesAfter)`，每个子条件独立、失败原因互异（离线可测）。
  ③ **清理顺序**：验证失败与异常路径均改为**先 Destroy builder（置 null 防重复销毁）再 UndoToMark 回滚**；destroy 失败只记日志不掩盖原判定；成功路径 finally 幂等清理不变。
- **测试**：新增 `EdgeFinishPostconditionTests` 9 项（实机失败案例重现：BLEND 健康提交必须 PASS；无特征/死特征/错类型/零面/body 数变化各自失败且原因互异；执行器源码含 `is EdgeBlend/is Chamfer` 且无 `== "EDGE_BLEND"` 残留）。NxHost csproj 加 InternalsVisibleTo（同 Core 模式）。边加工 34 项全通过；全量 Release **834 项 = 833 通过 / 0 失败 / 1 跳过**（退出码 0）。
- **构建部署**：NX 未运行；build.ps1 成功（0 警告 0 错误）；4 DLL SHA-256 一致——Core `76d86b68…`（未变）、UI `4ed0497d…`（未变）、NxHost `a7dc829b…c3ecc`（**本轮唯一变化**）、Entry `d884958f…`（未变）；菜单字节级未变。备份：`D:\AI\NX_AI_ASSISTANT_postfix_backup_20261023_122042`。
- **待实机复验**：重新执行"给选中的边倒 R2 圆角"→ 特征**保留不被撤销**、真实圆角几何生效、日志出现 `postcondition verdict: pass=True`；倒斜角随后单独核实（可能存在同型后置验证问题——其 FeatureType 需实机确认）。离线测试通过不等于实机通过。

## 2026-10-23 · Phase 9.5：自主几何感知——空选区自动解析"周边"目标（离线验证通过；**部署被 NX 占用阻塞**）

- **需求**（产品方向升级）：人工预选不得成为自动建模的常规前置。空选区时助手应从最近成功的建模结果 + 当前特征树 + 实际几何解析目标；"没选对象"≠"无法定位对象"。
- **架构**（职责分离，复用既有层）：
  - **意图→候选**：控制器新增 `LastCreatedBodyRef`（仅记录**执行器验证成功**结果 DetailsJson 里的 `newBodyRef`，如 BLOCK(5)；模型文本永不作为来源）+ `ResetTargetResolution()`（新对话清空，防复用失效引用）；执行器经 `NxTargetResolutionSink` 接口以委托读取（每次解析现读，非陈旧副本）。
  - **决策**（Core/EdgeTargetResolution.cs，纯函数离线可测）：候选数量规则（0 实体拒/单实体取/有记忆引用取候选/多实体无引用拒并请示）；**"周边"安全规则**——仅当 Body 为**标准长方体**（6 平面、12 线性边、每边 2 顶点、8 顶点各 3 边、每面四边）时解析为全部 12 条外棱边；任何不满足（7 面/11 边/曲面/顶点异常）都拒绝并说明原因+指引，绝不扩大选择。
  - **几何采集**（NxHost/EdgeFinishToolExecutor）：`ResolveEdgeFinishTarget` 三级决策——①选区含边→用选区（场景 B 原行为不动）；②选区非空但无边→保留原有诊断拒绝；③空选区→读记忆候选→`FindLiveBodyByRef` 在**活部件**重新解析（已删/已撤销即拒）→`CollectBodyEdgeFacts`（Face/Edge/顶点实测，AskFaceProps 同款只读路径）→长方体判定→收集 12 条真实边（JournalIdentifier 去重、实测数≠12 再拒）。确认后既有 `RevalidateEdges` 再次验证每条边仍活。
  - **执行**：圆角/倒角 Builder 路径与 9.4.1 后置验证完全未动；两工具共用目标解析。
- **工具描述**：create_fillet/create_chamfer 增加三级放置说明（选中边优先/空选区对刚创建长方体解析周边 12 棱边/不明确则拒绝）。
- **测试**：新增 `EdgeTargetResolutionTests` 12 项（标准长方体→12 边、六类非标准形状拒绝且原因含指引、0/单/多实体与记忆引用决策、控制器生命周期）+ 控制器 4 项（成功结果记录 ref、失败不记录、Reset 清空、执行器 sink 默认 null fail-safe）→ 16 项新测试。全量 Release **850 项 = 849 通过 / 0 失败 / 1 跳过**（退出码 0）。
- **构建**：Release 0 警告 0 错误。**部署被阻塞**：build.ps1 复制 Core.dll 时 NX（ugraf.exe）正在运行、DLL 被占用，`Copy-Item` IOException 中止。按规则未强行覆盖、未终止用户进程。**待 NX 完全退出后重跑 `powershell -ExecutionPolicy Bypass -File build.ps1` 完成部署**（构建产物已就绪）。备份：`D:\AI\NX_AI_ASSISTANT_target_backup_20261023_130215`。
- **待实机验收**（部署后）：①创建 60×60×60 长方体→清空选择→"周边倒 R3 圆角"→确认卡显示目标与 12 边→确认→圆角保留；②"给刚创建的长方体所有棱边倒 R3"；③多实体歧义拒绝；④已修改实体（打了孔）拒绝；⑤新对话不复用失效引用；⑥选中边场景回归；⑦倒斜角对应检查。

## 2026-10-23 · Phase 9.5 部署补记：NX 退出后重跑 build.ps1，部署完成

- **部署**：NX（ugraf.exe）退出后重跑 `build.ps1`——构建 0 警告 0 错误，4 DLL 复制成功；SHA-256 与构建产物逐一一致——Core `00915c81…bdb1a`、UI `6993df03…bd34d`、NxHost `33484dc5…4c35b`、Entry `f4e794a2…a370c`；菜单字节级未变（`319ee927…`）；application 目录仅 4 DLL + 既有 selfcheck 种子，无杂物。
- **状态**：Phase 9.5 部署核验通过；实机验收清单见 PROJECT_STATUS 下一项任务。Git 无操作。

## 2026-10-23 · Phase 9.6：线性阵列正式集成——特征/几何体双模式（离线验证通过；已部署待实机验收）

- **需求**：create_linear_pattern，必须显式区分**阵列特征（PatternFeature）**与**阵列几何体（PatternGeometry）**两种模式（用户提供实机 journal："阵列特征"=PatternFeatureBuilder、"阵列几何特征"=PatternGeometryBuilder）。
- **API 核验**（**真实程序集反射**，临时探针工具 `System.Reflection.MetadataLoadContext` 直读 `NXBIN\managed_core\NXOpen.dll`，非猜名）：
  - `PatternFeatureBuilder`（`FeatureCollection.CreatePatternFeatureBuilder(Feature)`）：`FeatureList`(SelectFeatureList)、`PatternMethod{Variational,Simple,SingleOutput}`、`OutputOption{PatternFeature,CopiesOfInputFeatures,CopiesOfInputFeaturesInGroup}`、`PatternService`(PatternDefinition)、`ReferencePointService.IsReferencePointInferred`（替代已弃用 UseInferredReferencePoint）、`SetReferencesToReuse(Feature,NXObject[])`。
  - `PatternGeometryBuilder`（`CreatePatternGeometryBuilder(PatternGeometry)`）：`GeometryToPattern`(SelectDisplayableObjectList，接受 bodies/faces/curves/datum 等——**本工具仅支持 Body 种子**，face/curve 阵列的额外约束未验证不做)、`HideOriginal`、`IsAssociative`、`CopyThreads`。
  - 共用：`PatternDefinition`（`PatternType{Linear,…}`）→ `RectangularDefinition`(RectangularPattern)：`XDirection`(Direction，`Part.Directions.CreateDirection(Point3d,Vector3d,UpdateOption)`)、`UseYDirectionToggle`(1-D 保持 false)、`XSpacing`=**`DistancePatternSpacing`**（`SpaceType`=Offset 逐实例间距、`NCopies` Expression、`PitchDistance` Expression）。
  - 提交后类型：`Features.PatternFeature` / `Features.PatternGeometry`（均含 `GetAllContainedFeatures`/`GetAssociatedBodies` 实例证据 API）。
- **实现**（新 Core/LinearPatternTool.cs + NxHost/LinearPatternToolExecutor.cs）：
  - 契约：`mode`（feature/geometry，**必填显式**——两种模式对应不同 Builder 与目标类型，禁止默认归 Feature）、`direction`（6 个绝对轴向枚举，**方向永不猜测**，无法确定先问用户）、`count`（2–100 整数，含原始实例）、`pitch`（正数 ≤1000mm）、`include_seed`（默认 true）。
  - 目标解析：Feature 模式=控制器记忆的 `LastCreatedFeatureRef`（本轮新增，来自标准孔/圆角/倒角执行器成功结果的 featureRef）；Geometry 模式=`LastCreatedBodyRef`；均为执行器验证过、执行时在活部件重解析（失效即拒）；模型永不传对象引用。
  - 执行：PatternMethod=Simple + OutputOption=PatternFeature（特征模式）；IsAssociative=true + HideOriginal=false（几何模式，原始保留）；1-D 线性定义（XDirection+XSpacing，Y 不启用）；表达式设置后记录读回（copies/pitch 值）。
  - 后置验证（纯函数 `LinearPatternPostcondition`，子条件离线可测）：托管类型匹配（is PatternFeature/is PatternGeometry）、存活、**Feature 模式实例证据**（GetAllContainedFeatures 非空）、**Geometry 模式 body 数增长=副本数**（count−seed，源不计入新增）、失败先 Destroy 后回滚（9.4.1 顺序）。
  - 控制器：`LastCreatedFeatureRef` 记录/清空与 bodyRef 同套生命周期；确认卡显示模式/目标/方向/数量/间距。
- **测试**：新增 `LinearPatternToolTests` 26 项（双模式解析、13 类非法输入拒绝含 mode 缺失/direction 猜测禁止/count 非整与越界/pitch 非法、注册 16 工具、Schema required、描述双模式+自动解析+方向先问、后置判定 6 子条件、路由）。计数断言 15→16 七处（修正一处误伤的 depth:15 断言）；10 个测试替身补新接口成员。全量 Release **876 项 = 875 通过 / 0 失败 / 1 跳过**（退出码 0）。
- **构建部署**：NX 未运行；build.ps1 成功（0 警告 0 错误）；4 DLL SHA-256 一致——Core `cd9afa12…87ab`、UI `858b74c7…f5a`、NxHost `99fa05d4…eed0`、Entry `6bcc7e97…69de`；菜单未变（`319ee927…`）。备份：`D:\AI\NX_AI_ASSISTANT_linpat_backup_20261023_154413`。
- **待实机验收（两种模式分别报告，不得互相代表）**：
  - 特征模式：创建孔特征 →"把这个孔特征沿 +X 阵列 4 个、间距 20"→ 确认卡显示模式=feature/方向/4 实例/20mm → 执行 → PatternFeature 特征保留、实例证据>0；
  - 几何体模式：创建长方体 →"把这个实体沿 +Y 阵列 3 个、间距 80"→ 模式=geometry → 执行 → 部件实体数 1→4（源保留）；
  - 方向歧义（"随便哪个方向"）→ 询问而非猜测；失效引用拒绝；取消无几何变化；失败回滚。

## 2026-10-23 · Phase 9.7：通用目标重定位 + Phase 9.8 双向原生矩形阵列（离线验证通过；已部署；实机验收由用户确认通过）

- **需求**：①新对话清空 `LastCreatedBodyRef`/`LastCreatedFeatureRef` 后，助手必须能从当前工作部件、真实特征树和几何重新定位已有目标（通用特征识别，不限倒角工具、不要求常规手选）；②"X+ 数量 4 间距 15，Y+ 数量 3 间距 15"必须一次原生矩形阵列创建 4×3=12 位置（禁止两次嵌套、禁止把 PatternFeature 当第二方向源）。
- **API 核验**（NXOpen.xml 直读）：`GeometricUtilities.RectangularPattern`="specification along two linear axes"（NX7.5+），含 `YDirection`/"any vector not parallel to the x axis"、`YSpacing`、`UseYDirectionToggle`；`PatternDefinition.SeedOnlyToggle` 语义确认。**修正一处核实错误**：孔特征托管类名是 `NXOpen.Features.Hole`（"HoleFeature" 是 Builder 工厂名，非特征类）。双向经 NX2512 程序集编译二次确认。
- **实现**：
  - Core/EdgeTargetResolution.cs：新增 `InventoryEntry` + `ResolveBodyFromInventory`（单实体直用/多实体唯一标准长方体自动解析/多长方体或无长方体拒绝）与 `ResolveFeatureFromInventory`（按托管类型定位，多行按唯一显示名匹配，歧义拒绝）。
  - NxContextReader/NxContextSnapshot：上下文快照新增 `FeatureTree` 清单（≤50 行：JournalIdentifier/托管类型/显示名，仅活性未抑制特征），模型可直接引用真实引用。
  - EdgeFinishToolExecutor：空选区时无记忆引用 → 实体清单+长方体拓扑探测 → 通用决策；仅在无记忆引用时探测（避免重复读取）。
  - LinearPatternTool.cs：参数新增可选 `direction2`/`count2`/`pitch2`（三者同提供或同缺省，第二轴禁平行）；schema/描述同步。
  - LinearPatternToolExecutor：`ApplyLinearDefinition` 双向分支（`UseYDirectionToggle=true`+`YDirection`+`YSpacing`，一次 CommitFeature）；几何模式 expectedNewBodies=count×count2−seed；成功消息/details 双向字段。
  - ChatSessionController：确认卡显示"双向：X+ 4 个 × Y+ 3 个，一次原生矩形阵列共 12 个实例"。
- **测试**：新增 `GenericTargetRelocationTests` 11 项 + `LinearPatternToolTests` 双向 13 项。全量 **901 项 = 900 通过 / 0 失败 / 1 跳过**。Release 0 警告 0 错误。
- **部署**：NX 未运行；build.ps1 成功；4 DLL SHA-256 一致——Core `20fd35a1…`（以本轮末次部署为准，见 9.8 下一条）、UI `ece58e18…`、NxHost `d070be42…`、Entry `de4088c1…`；**部署脚本复制了 startup 菜单文件，内容与此前字节相同（非"未复制"）**。

## 2026-10-23 · Phase 9.7.1/9.8.1 实机问题修复：R1 选区优先目标解析 + R2 非法参数与重复调用抑制 + 标记清洗（离线验证通过；已部署；实机验收由用户确认通过）

- **实机现象**：①新对话中"把这个实体 +X 阵列 4 个间距 15；+Y 阵列 3 个间距 15（双向必须一次创建）"，NX 显示已选 1 个实体、部件有 2 实体，执行器仍拒绝"当前工作部件有2个实体且没有明确目标"（`preconditions_changed`）；②模型向 create_linear_pattern 传入不受支持的 `target=CYLINDER(5)`；③失败后出现重复调用，且原始 `.invoke` 协议内容疑似泄漏到聊天文本。
- **根因**：R1——LinearPatternToolExecutor **几何体模式从不读取 NX 选区**（无任何 SelectionManager 调用），只走"记忆引用→实体计数"，2 实体无记忆引用即拒绝，选区被完全忽略。R2——未知字段由 `RejectUnknownFields` 正确拒绝（schema 未放宽），但控制器对参数类失败无重提交防护；`ToolTagProtocol` 类本身为 internal，UI 无法直接做显示侧清洗。
- **修复**：
  - R1（LinearPatternToolExecutor）：几何体模式统一优先级——①当前选区恰一个有效 Body（含特征树节点唯一解析出的实体）→直接作种子，其他实体不否决；②混入选区/多实体选中/无可用 Body→结构化拒绝；③空选区→既有"记忆引用→单实体"路径。复用 `CollectSelectionObjects`/`FindLiveBodyByRef`，未建第二套架构。
  - R2（ChatSessionController）：新增本轮 (tool, 参数 JSON) 去重闸门——`InvalidParameters` 失败后字节相同的重提交在解析前拒绝并要求修正参数；目标解析类失败不去重（允许前置条件变化后重试）。`_failedParameterCallsThisTurn` 每轮清空。
  - 泄漏清洗（ToolTagProtocol 公开化 + `StripInvokeMarkup`；ChatSessionController.`SanitizeModelTextForDisplay`）：完整块剔除保留散文、未闭合块剥除标记、纯协议内容换简短提示；应用于 finalText 与两处错误卡。
- **测试**：新增 `TargetResolutionRegressionTests` 13 项（选区决策不变量、target 拒绝、schema 不放宽、完整/未闭合/纯标记清洗、去重键语义、双向 4×3=12、平行拒绝、结构化消息无标记）。全量 **914 项 = 913 通过 / 0 失败 / 1 跳过**。Release 0 警告 0 错误。
- **部署**：NX 未运行（ugraf 计数 0）；4 DLL SHA-256 与 Release 产物逐项一致——Core `20fd35a1e683124f…`、UI `ece58e1818d16afd…`、NxHost `d070be4217329c52…`、Entry `de4088c153ea2e11…`。**部署脚本复制了 startup 菜单文件（内容与此前相同）；后续部署仅复制发生变化的 DLL，不复制/不更改 `nxai_assistant.men`。** Git 无操作。
- **实机验收**：用户确认 R1/R2 测试通过（选区双向几何体阵列、唯一目标自动定位、歧义澄清、未知字段拒绝不重复调用）。

## 2026-10-23 · Phase 9.9：环形阵列 create_circular_pattern（离线验证通过；已部署；实机验收待执行）

- **需求**：独立环形阵列工具，feature/geometry 双模式与线性阵列语义一致；旋转轴必须显式（绝对轴/选中基准轴/选中直线边），不能把 Body 当轴、不能无声猜测；count=总实例数含源（2–100）；完整圆周 360° 默认、部分圆弧 total_angle_deg∈(0,360]；原生 Circular Pattern，不允许多次变换冒充。
- **API 核验**（NXOpen.xml 直读 + **编译验证**，逐项修正假设）：`PatternDefinition.PatternEnum.Circular` → `CircularDefinition`（`CircularPattern`，"angular and radial directions"，NX7.5+）；`CircularPattern.RotationAxis` 是 **`NXOpen.Axis`** 智能对象（Types={NonAssociative,Other,PointAndDirection}，`Point`/`Direction` 可设）；`AngularSpacing`（`PatternSpacing` 基类 `NCopies`+`AngularPatternSpacing.PitchAngle/SpanAngle/UsePitchOption`，**UsePitchOptions 是枚举 {Angle,Distance} 不是 bool**）；`DatumAxis.Direction` 是**标量 Vector3d**、`DatumAxis.Origin` 是**标量 Point3d**（编译纠正了两版假设）。角度语义：完整圆周 pitch=360/count（首尾不重复）；部分圆弧设 SpanAngle（末实例落在结束角）。
- **实现**（新 Core/CircularPatternTool.cs + NxHost/CircularPatternToolExecutor.cs）：
  - 契约：mode（feature/geometry 必填）、axis（plus_z/plus_x/plus_y/datum_axis/line_edge 必填，**枚举值不含"随便轴"**）、count（2–100 含源）、total_angle_deg（可选）；**schema 不含任何对象引用字段**（R2 教训：种子与轴对象都由宿主解析，模型只描述意图）。
  - 种子解析：完整复用 R1 选区优先级——选区恰一个有效 Body/Feature 直接作种子（同选区可同时携带轴对象，两个解析器各取各的类型）；空选区走记忆引用/唯一候选。
  - 轴解析（宿主侧）：plus_x/y/z=过原点绝对轴（Directions.CreateDirection+Points.CreatePoint）；datum_axis=选区恰一个活 DatumAxis（或 DatumAxisFeature 唯一解析），读取其 Direction/Origin 标量重建自有智能对象；line_edge=选区恰一条活**直线**边（SolidEdgeType 校验，非直线拒绝），由两端点建轴。0/2 个轴对象均结构化拒绝，**Body 永不被当作轴**。
  - 执行：PatternMethod=Simple+OutputOption=PatternFeature（特征模式）/IsAssociative=true+HideOriginal=false（几何模式）；`ApplyCircularDefinition`：PatternType=Circular、axis.Point/axis.Direction 赋值、NCopies=count、完整圆周→PitchAngle=360/count（UsePitchOption=Angle）、部分圆弧→SpanAngle=total；后置判定 `CircularPatternPostcondition` 纯函数（托管类型/存活/实例证据或 body 增长= count−1）；先 Destroy 后回滚（9.4.1 顺序）。
  - 接线：CreateRegistry 注册（16→17 工具）、AutonomousToolAllowlist、ToolConfirmationFlow（新 CircularPattern kind+ForCircularPattern）、AssistantChatForm 确认卡（模式/种子/轴/数量/角度）、ChatSessionController 路由+确认分支+执行分发。
- **测试**：新增 `CircularPatternToolTests` 25 项（解析/12 类拒绝含 target 与 axis_ref 引用字段、注册+allowlist、schema required 与无引用字段、后置判定 6 子条件）；9 个测试替身补 `ExecuteCreateCircularPattern`；工具总数断言 16→17（9 文件 13 处）+ 广告清单补 create_circular_pattern。全量 **939 项 = 938 通过 / 0 失败 / 1 跳过**。Release 构建通过。
- **部署**：NX 未运行（ugraf=0）；4 DLL SHA-256 与 Release 产物一致——Core `2b51e31b…`、UI `f264c4ea…`、NxHost `3aa8dc4b…`、Entry `b23dbd89…`。**部署脚本复制了 startup 菜单（内容与此前相同）；后续部署仅复制变化 DLL。** Git 无操作。
- **待实机验收（不得用离线结果代替）**：
  1. 两实体部件：选中一个 → "把选中的实体绕 +Z 轴环形阵列 6 个" → 确认卡显示种子=选中实体/轴=+Z/6 实例/360° → 确认 → PatternGeometry 保留、部件 1→6 个实体、周向均布；
  2. 部分圆弧："环形阵列 5 个，总角度 120°" → 首末实例夹角恰 120°；
  3. 孔特征 + "绕基准轴环形阵列 8 个"（选中基准轴）→ axis=datum_axis 生效；
  4. line_edge：选中直线边作轴；
  5. 拒绝路径：未选轴要求 datum_axis → 结构化拒绝；选两条边 → 拒绝；count=1 → 拒绝；
  6. 取消确认无几何变化；失败回滚完整。

## 2026-10-23 · Phase 9.9.1：T1 实机失败修复——账本空引用 + 完整圆周角度归一化（离线验证通过；**部署被 NX 占用阻塞**）

- **T1 实机现象**：选区（1 feature + 1 datum_axis）+ "几何体环形阵列 6 实例 360°" → `请求发生意外错误: Object reference not set to an instance of an object`；确认消息显示"相邻实例角距约72°"（正确应 60°）。
- **根因定位**（日志 + 代码走读，阶段证据：`context.capture end`（21:01:43，选区=2）之后无任何 `execute.cir_pattern` 行——异常发生在执行器进入之前）：
  - **NRE**：`ChatSessionController.DescribePendingOperation` 的 switch **缺 CircularPattern case**，落入 `_ => 布尔 {request.BooleanParameters!.Operation}` 通配——环形请求的 `BooleanParameters` 为 null → NullReferenceException（`ExecuteConfirmedTool` 的 `BeginAttempt` 处抛出，被 `RunRequestAsync` 外层 catch 包装为"请求发生意外错误"）。该 switch 自 Phase 2 起靠"新工具必须加 case"的约定维护，环形工具接线时漏改。
  - **72°**：模型把完整圆周请求表述为 `total_angle_deg=360`，解析器接受（≤360 合法）并进入部分弧分支，确认卡用 `deg/(count−1)=360/5=72°`；执行器也会走 SpanAngle 路径。语义缺口：360 未归一化为完整圆周。
- **修复**：
  - `DescribePendingOperation` 增加 `CircularPattern` case（`环形阵列 {count}×{axis}`），并 internal 化以便测试；不再落入布尔通配。
  - `CreateCircularPatternRequestParser`：`total_angle_deg` **≥360 归一化为 null**（完整圆周路径 PitchAngle=360/count）；描述文案同步（360 或省略=完整圆周、相邻角距=360/count；部分弧相邻角距=总角度/(count−1)、末实例落在结束角）。
  - `CircularPatternToolExecutor`：EXCEPTION 日志增加 `EXCEPTION-DETAIL`（ex.ToString() 堆栈）与 `EXCEPTION-INNER`（仅写入诊断日志，不进聊天界面）；执行路径增加 stage 标记（builder 创建/种子加入/定义应用/commit/DoUpdate），T1 类故障可直接从日志定位首个失败阶段。
- **测试**：新增 `CircularPatternLiveIncidentTests` 8 项（DescribePendingOperation 环形 case 不抛、5 种轴不落布尔通配、total_angle_deg=360 归一化→60°、省略角=60°、部分弧 120°/5=30° 保持、(0,360) 开区间部分弧边界、确认卡文案与执行器公式一致性两向）。全量 **947 项 = 946 通过 / 0 失败 / 1 跳过**。Release 构建通过。
- **部署（补跑完成）**：首次 build.ps1 因 NX（ugraf.exe）运行中 Copy-Item IOException 阻塞；NX 退出后直接从本轮 Release 产物复制 4 个 DLL（未重建、源码未变），SHA-256 逐项一致——Core `b7e6987c…`、UI `2c38c4f9…`、NxHost `ab83daba…`、Entry `37cf6538…`；菜单文件未触碰（application 目录仅 4 DLL + 既有 selfcheck）。
- **实机状态**：T1 修复**未验证**；T2–T6 未执行。部署完成后先重跑 T1（选区 feature+datum_axis、6 实例 360°：应出现确认卡且角距 60°、确认后执行成功），通过后再决定 T2–T6。

## 2026-10-23 · Phase 9.9.2：T1 第二次失败修复——RotationAxis 空对象 NRE（真根因）+ 回滚后重复调用防护 + 参数标记泄漏（离线验证通过；已部署；T1 实机复验待执行）

- **T1 第二次实机现象**（部署哈希 4/4 一致，确为新版运行）：确认卡正确显示"完整圆周360°（相邻实例角距60°）"→ 执行仍 `NullReferenceException`（已回滚）→ 约 40 秒后模型原样重试再次失败 → 聊天窗口出现原始工具协议内容。
- **日志证据**（`%TEMP%\NXAI_Assistant_phase.log` 1280–1307 行）：两次尝试均走完 `seed resolved: 实体 BLOCK(1)`（**特征树节点→Body 解析正确**）、`axis: 基准轴 resolved (dir=(0,0,1) point=(0,0,-14.89))`（**DatumAxis 读取正确**）、`stage: apply circular definition` 后**立即** NRE——失败点锁定在 `ApplyCircularDefinition` 内。`EXCEPTION-DETAIL` 无堆栈行（NRE 堆栈未输出），但阶段标记已闭合定位。
- **真根因**：`CircularPattern.RotationAxis` 在新 PatternDefinition 上 **getter 返回 null**（NX 未预建 Axis 智能对象）——旧代码 `axis.Point = …` 对 null 解引用。修复（NXOpen.xml 探测 + 编译验证）：**`workPart.Axes.CreateAxis(point, direction, WithinModeling)` 创建自有 Axis 智能对象**（重载 `CreateAxis(NXOpen.Point, NXOpen.Direction, UpdateOption)` 已核实），整对象赋给 `circular.RotationAxis`；属性名是 `BasePart.Axes`（非 AxisCollection 属性）。轴的 (point, direction) 来自既有解析器输出，类型端到端贯通。
- **重复调用防护扩展**（ChatSessionController）：记录条件从仅 `InvalidParameters` 扩展为 **InvalidParameters ∪ (VerificationFailed|ExecutionFailed 且 RolledBack)**——已回滚部件复原、参数指纹相同（工具名+参数 JSON）的同一轮内重试在解析前拒绝（T1 40 秒重试即此类别）；未回滚失败/PreconditionsChanged 仍可同轮重试（NX 状态可能已变）；每轮清空，跨轮由用户操作后合法发起。
- **参数标记泄漏修复**（ToolTagProtocol + ChatSessionController）：`ContainsToolMarker` 增测 **ParamOpen**（截断调用只残留参数标记、无 invoke 包裹时旧检查直接放行——本次泄漏路径）；`StripInvokeMarkup` 支持剥离孤立参数标记（含未闭合时丢弃后续）；`SanitizeModelTextForDisplay` 三分支重写（完整块→CleanText+残余再清洗；无块但有标记→剥参数标记；畸形→Strip+提示），普通尖括号散文不受影响。
- **测试**：新增 `CircularPatternT1SecondIncidentTests` 8 项（T1 参数路径归一化 60°、回滚失败指纹语义、重复记录判定真值表、孤立参数标记检测/清洗/未闭合丢弃、普通尖括号保留、结构化拒绝无堆栈文本）。全量 **955 项 = 954 通过 / 0 失败 / 1 跳过**。Release 构建通过。
- **部署**：NX 未运行；4 DLL SHA-256 一致——Core `c636944c…`、UI `c943c4d3…`、NxHost `3409e140…`、Entry `37cf6538…`；菜单未改动内容。Git 无操作。
- **实机状态**：T1 **未在最新部署上复验**；T2–T6 未执行。重测 T1（同参数：1 feature + 1 datum_axis、6 实例 360°）预期：确认卡 60° → 执行成功（环形阵列创建、部件 1→6 实体）；若再失败，日志含 stage 标记 + EXCEPTION-DETAIL 可直接定位。

## 2026-10-23 · Phase 9.9.3：T2 角度语义修正 + T4 直线特征轴解析（离线验证通过；已部署；T1–T4 复测待执行）

- **实机状态**（部署 4/4 哈希一致，确为最新版）：T1 通过；T2 失败（5 实例 120°，确认卡 30° 但 NX 实测相邻 10°）；T3 通过；T4 失败（选区 Block + LINE(7)，报"特征LINE(7)解析出0个实体(预期1)"）。
- **日志证据**：T2（1386 行）`copies=5 fullCircle=False readback: copies='5' usePitch=Angle`——SpanAngle=120 提交后 NX 实际相邻 10°，证明 **NX2512 该定义的 SpanAngle 不是"总跨度"**；T4（1441 行）`feature=LINE(7) type=AssociativeLine outputs=0 -> unresolvable`——LINE(7) 是 **AssociativeLine 特征**（无 Body 输出），被种子解析器误当实体候选。
- **T2 修复**（CircularPatternToolExecutor）：统一为 **PitchAngle 每步** 配置（UsePitchOption=Angle）：完整圆周 pitch=360/count（T1 已证 60°）；部分弧 pitch=total/(count−1)（5 位置跨 120° → 0°/30°/60°/90°/120°，末实例落在结束角）。SpanAngle 不再设置；readback 增记 pitch/span 实际值。
- **T4 修复**（CircularPatternToolExecutor + 契约/确认卡文案）：①新增 `IsAxisLikeFeature`（**仅** `NXOpen.Features.AssociativeLine`——NXOpen.xml 核实，未猜名）；两个种子解析器跳过轴类直线特征（不再计入种子候选、不产生"0 个实体"误诊）；②line_edge 轴解析扩展为接受**拓扑直线边 ∝ NXOpen.Line 对象 ∝ AssociativeLine 特征**（`Feature.GetEntities()→Line` 取真实 StartPoint/EndPoint），从线自身几何构建旋转轴；多条直线歧义拒绝、退化拒绝、无直线结构化拒绝——**无全局轴兜底、无静默替换**；③契约描述/schema/确认卡同步"直线（边或直线特征）"。
- **测试**：新增 `CircularPatternT2T4Tests` 7 项（5×120°→30° 每步与位置序列、6×360°→60°、统一步进公式（归一化后）、line_edge+geometry 请求形状、契约含"直线特征"、歧义结构化拒绝、部分弧仍有效）。修正测试自身一处公式错误（归一化前 360 走了部分弧公式）。全量 **962 项 = 961 通过 / 0 失败 / 1 跳过**。Release 构建通过。
- **部署**：NX 未运行；4 DLL SHA-256 一致——Core `e749ea95…`、UI `37e314b6…`、NxHost `4c56f4d1…`、Entry `cc70d7f3…`；菜单未改内容。Git 无操作。
- **复测清单（T1–T4 依次，T2 必须实测位置角）**：T1 重跑（6×360°→60°，回归）；T2（5×120°：NX 中实测相邻位置 30°、0°/30°/60°/90°/120°，不能只看确认卡）；T3 回归；T4（选 Block+LINE(7)：确认卡轴="当前选中的直线（边或直线特征）"、执行成功、记录 LINE(7) 实际类型 AssociativeLine 与最终轴对象）。通过后再 T5/T6。环形阵列整体仍**不标实机通过**。

## 2026-10-24 · Phase 9.9 收尾：圆形阵列实机验收确认（T1–T6 全部通过；仅文档同步，无代码改动）

- **验收结论**：用户确认 `create_circular_pattern` 在 NX2512 实机 **T1–T6 全部通过**（T1 完整圆周 6×360°→60°、T2 部分圆弧 5×120°→30° 步距、T3 孔特征绕基准轴、T4 直线特征作轴、T5 拒绝路径、T6 取消与回滚）。本条为收尾记录，不修改任何业务代码、不重新部署、不重启 NX。
- **证据链**：T1/T3 通过见 9.9.3 前实机记录与用户报告；T2/T4 修复后的实机通过、T5/T6 的具体执行日志与测量值**未单独记录**（以用户最终确认的通过结论为准，未补写细节）。Phase 9.9 验收清单定义见 2026-10-23 Phase 9.9 条目。
- **功能范围（已有证据支持）**：feature/geometry 双模式（PatternFeatureBuilder/PatternGeometryBuilder + 原生 Circular 定义，PatternType=Circular）；旋转轴五类显式来源（plus_x/y/z 绝对轴、选中基准轴、选中直线——拓扑边或 AssociativeLine 特征），Body 永不作轴、歧义结构化拒绝；count=总实例含源（2–100）；完整圆周 PitchAngle=360/count、部分圆弧 PitchAngle=total/(count−1)（NX2512 SpanAngle 非"总跨度"——T2 实测确立）；种子解析复用选区优先（R1 同款）；先 Destroy 后回滚；回滚后同参数同轮重试防护。
- **未单独核实（如实标注，未补写）**：T5/T6 逐项执行日志；T2 复测时的实例位置实测读数；本轮（收尾时点）未重新执行的自动化测试计数、Release 构建结果与部署哈希——最近一次有记录的值为 Phase 9.9.3（962 项=961 过/0 失败/1 跳；Core `e749ea95…`、UI `37e314b6…`、NxHost `4c56f4d1…`、Entry `cc70d7f3…`）。
- **本轮操作**：仅更新本文档与 PROJECT_STATUS.md、RELEASE_CHECKLIST.md 的验收状态；无 Git 提交、无部署、无菜单改动。

## 2026-10-24 · Phase 10：镜像 create_mirror 双模式实现（离线验证通过；**未部署**，待实机验收）

- **需求**：几何体镜像（一个或多个明确选中的 Body）+ 特征镜像（一个或多个明确选中的 Feature）；镜像面支持基准面**和实体上的普通平面面**；对象与镜像面按角色分离解析。
- **API 核验**（NXOpen.xml 探针 + **编译验证**，XML 不完整处由编译器裁决）：
  - `Features.MirrorBodyBuilder`（几何体）：`MirrorBodyCollector`（ScCollector，配 `ScRuleFactory.CreateRuleBodyDumb(Body[])` 精确规则——XML 无此方法但编译验证存在，与既有 CreateRuleEdgeDumb 同族）、`Plane`（**SelectDatumPlane 选择器**——编译纠正"Plane 对象"假设）、`DeleteSourceBody`（NX2506+，保持 false=保留原体）、`FeatureOption=OneFeatureForAllBodies`（N 实体一个特征）。
  - `Features.MirrorFeatureBuilder`（特征）：`FeatureSet`（逐项 Add）、`Plane`（**SelectISurface 选择器**——接受 DatumPlane 或平面面引用）、`PlaneOption=Existing`。
  - `DatumPlane.Normal/Origin`（标量，用于诊断日志）；`MirrorBody`/`MirrorFeature` 提交类型；`BodyDumbRule` 构造器签名不适用故用工厂。
- **实现**（新 Core/MirrorTool.cs + NxHost/MirrorToolExecutor.cs）：
  - 契约：`mode`（geometry/feature 必填）+ `keep_original`（默认 true）；**schema 无任何对象引用字段**（种子与镜像面均由宿主从选区解析，R2 教训）。
  - 选区角色分离：平面候选（DatumPlane/平面 Face/基准面特征唯一解析）与种子（geometry=Body/唯一拥有实体的特征；feature=非基准面特征）**分表解析**——镜像面永不计入种子；0 个种子/混入不兼容对象/多平面候选/非平面面/空选区均结构化拒绝；**无默认面、无绝对坐标面兜底**。
  - 平面面真实几何读取：`AskFaceUvMinmax` 取 UV 中点 → `AskFaceProps` 取原点+单位法向（UFEvalsf 禁用——2026-10-16 崩溃记录），仅用于诊断日志；builder 直接接收 Face 引用（选择器原生支持，无中间对象、无生命周期清理负担）。
  - 后置判定 `MirrorPostcondition` 纯函数：托管类（geometry=MirrorBody/Mirror、feature=MirrorFeature）、存活、geometry 模式特征须拥有实体且部件实体数恰增种子数；feature 模式不断言 body 增长（镜像孔在既有体内减切——数量可能不变）；失败先 Destroy 后回滚。
- **接线**：注册表（17→18）、allowlist、ToolConfirmationFlow（Mirror kind+ForMirror）、AssistantChatForm 确认卡、ChatSessionController 路由+确认分支+执行分发+**DescribePendingOperation 补 Mirror case**（T1 教训：新工具必须补 case 防 NRE）；11 个测试替身补 ExecuteCreateMirror；工具计数断言 17→18（9 文件 13 处）+广告清单补 create_mirror。
- **测试**：新增 `MirrorToolTests` 19 项（解析、6 类拒绝含 target/plane_ref/seeds 引用字段、注册+allowlist、schema 无引用字段、**DescribePendingOperation 双模式不落布尔通配**、后置判定 7 子条件含 feature 模式 body 增长豁免）。全量 **981 项 = 980 通过 / 0 失败 / 1 跳过**。Release 构建通过。
- **未执行（按任务约束）**：部署（DLL 复制与哈希核对）、Git 提交/推送、`nxai_assistant.men` 改动（无）。
- **实机验收方案（G1–G4/F1–F4/N1–N2，未执行）**：G1 单实体+基准面；G2 多实体+基准面；G3 单实体+普通平面面；G4 多实体+平面面；F1 单特征；F2 多特征；F3 依赖特征组；F4 特征+平面面；N1 歧义镜像面/无效种子拒绝；N2 取消/可控失败无残留。每例核对：确认卡对象数与镜像面、提交后特征类型（MirrorBody/MirrorFeature）、实体数变化、原体保留、撤销完整性。日志关键字 `execute.mirror`。

## 2026-10-24 · Phase 10.1：自检几何体扩充（对象识别与诊断）+ 镜像部署（离线验证通过；已部署；镜像实机验收待执行）

- **自检扩充**（NxGeometrySelfCheck.cs，复用既有 RunAll 隔离部件框架，未建第二套）：新增 `CheckObjectRecognition` 用例——临时部件创建块+固定基准面（`CreateFixedDatumPlane(Point3d, Matrix3x3)`，编译验证签名）后**只读分类**：Body/建模 Feature/DatumPlane/普通平面面四类识别；平面面经 `AskFaceUvMinmax`+`AskFaceProps` 输出可核验原点+单位法向（断言块顶面 Z=20、法向 +Z）；双平面候选计数（歧义检测输入）；新增 `ClassifyFeature`（特征树节点：DatumPlaneFeature→datum_plane、唯一拥有实体→body、其余→feature 行）。全程不动用户模型（隔离部件关闭不保存、恢复工作部件）。
- **测试**：新增 `GeometrySelfCheckRecognitionTests` 7 项（用例注册、四类识别、平面几何断言、歧义候选、只读契约、验证读取路径对、报告 DTO）；修正两处断言（feature 行由 ClassifyFeature 实现后通过；UFEvalsf 禁令改为禁 API 调用而非注释提及）。全量 **988 项 = 987 通过 / 0 失败 / 1 跳过**。Release 构建通过。
- **部署**（NX 关闭，ugraf=0；未跑完整脚本，未触碰 nxai_assistant.men）：定向复制 4 DLL 并逐一核对——Core `b177da85…`、UI `fd7905a8…`、NxHost `c476f501…`（自检扩充+镜像载体）、Entry `e57b425b…`；菜单 `319ee927…` 未变。
- **镜像实机验收状态**：G1–G4/F1–F4/N1–N2 **全部未执行**（自检按钮的实时识别用例亦需 NX 内人工触发）。清单见 WORK_LOG Phase 10 条目；自检新用例结果将出现在自检报告中（caseE.recognition）。

## 2026-10-24 · Phase 10.2：几何自检全覆盖（18 工具 13 用例；离线验证通过；已部署；NX2512 实机自检待执行）

- **覆盖矩阵**（现有 5 用例 → 13 用例，注册表 18 工具全部经真实 Executor 调用，无 Mock 成功）：
  - 既有：caseA/B 草图+拉伸/旋转圆环、caseC 四基本体、caseD 失效引用拒绝、caseE.recognition 对象识别。
  - 新增：**caseF.cone**（圆锥+圆台双变体）；**caseG.booleans**（Unite/Subtract/Intersect 各自独立夹具对，实体数变化分别断言：并 −1/减 −1/交 −1）；**caseH.hole**（简单孔顶面 φ10×12）；**caseH.std_holes**（四标准孔族 M6 各独立夹具块，标准匹配由执行器 XML 表验证）；**caseI.edge_finishes**（R2/C2 各独立块 12 棱边，**真实 NX 选区**驱动，断言特征类型含 BLEND/CHAMFER）；**caseJ.linear_pattern**（geometry +X×4 实体+3 / feature +X×3 实体数不变）；**caseK.circular_pattern**（geometry +Z 6 实例实体+5 / feature 4 实例不变）；**caseL.mirror**（双体+基准面 / 单体+平面面 / 特征+基准面，实体数与特征类型断言）；**caseM.negative**（空选区镜像、双镜像面歧义、无效引用拉伸全拒且零几何）。
- **关键技术事实（编译/反射验证）**：NXOpenUI SelectionManager 全局选区驱动成员为 **`RequestSelections(TaggedObject[])` / `ClearGlobalSelectionList()`**（自建 tools/SelectionProbe 反射探针枚举全部成员后确定；SetSelection/SelectTaggedObjects 各参数数猜测均被编译否决）。
- **报告首页字段**（GeometrySelfCheckReport 扩展，带默认值向后兼容）：RegisteredToolCount(=18)/CoveredToolCount（按用例组计数：基本体 4+孔 5+边缘 2+阵列 2+镜像 1+布尔 1）/NegativePassed-Failed（caseD+caseM）/CleanupResult（cleanup/restore 失败即列名上报）。
- **测试**：新增 `GeometrySelfCheckCoverageTests` 9 项（13 用例调度、注册表 18 工具数对齐、15 个 Executor 全被调用+三布尔+双模式枚举、负向三场景、真实选区驱动、隔离恢复四要素、报告五字段、覆盖计数分组）。全量 **997 项 = 996 通过 / 0 失败 / 1 跳过**。Release 构建通过。
- **部署**（NX 关闭 ugraf=0；定向复制，未跑完整脚本）：4 DLL 哈希核对 MATCH——Core `78c426ea…`、UI `6ba70210…`、NxHost `481b8686…`、Entry `6ab1e578…`；菜单 `319ee927…` 未变。
- **NX2512 实机自检：未执行**（不虚报）。待执行：启动 NX → 自检按钮 → 核查 13 用例逐项结果与首页覆盖数/负向计数/清理结果；失败项按日志（`selfcheck.*`/`execute.*`）定位修复后重跑受影响场景与全量回归。

## 2026-10-24 · Phase 10.3：实机自检失败修复（9/5 → 待复验；离线验证通过；已部署）

- **完整日志定位第五失败项**（phase.log 1891–1899 行）：caseL.mirror 三场景全败——G2 场景只选了基准面没选两个种子 Body（`REFUSED: no seed`）→ G3 场景空选区 → F1 场景上一轮 datum 残留选区（`2 plane candidates`）。**5 个失败 = std_holes / edge.edges / lin.geo / cir.geo / mirror**，与首页计数一致，无矛盾。
- **根因表**：
  - P1 `M6x1.0` 被拒（生产解析器过严）：真实 NX_Thread_Standard.xml 中 M6 全名即 `Size="M6 x 1.0" Pitch="1"`；NormalizeSize 只规范化纯数字。**修复**（StandardHoleTable.cs）：NormalizeSize 拆 `M{nom}x{pitch}` 形；Lookup 阶段核验螺距与标准表一致（`ExtractRequestedPitch` + `GetThreadedPitch` 读真实表 M6→Pitch=1），**错螺距（M6x1.25）仍拒绝**、非螺纹族带螺距拒绝、深度字段仍取同一条记录行（语义未动）。
  - P2 边数 24（测试夹具）：面遍历共享边未去重。**修复**：`CollectBlockEdges` 按 JournalIdentifier 去重 + 逐边核验归属同一 body（`edge.GetBody()`）。
  - P3/P4 阵列无目标（测试夹具）：19/20 实体歧义拒绝是**生产保护正确**，夹具未传种子。**修复**：caseJ/caseK 每次调用前 `SelectInNx(种子)`（几何体模式选 Body、特征模式选特征）+ finally `ClearNxSelection()`；未关闭任何生产歧义保护。
  - P5 mirror（测试夹具）：caseL G2 场景选区补入两个种子 Body；每场景前后清理选区。
- **测试**：新增 `StandardHoleSizeNormalizationTests` 16 项（归一化 9 例、螺距提取、M6x1.0 查表、错螺距拒绝、裸名不回归、不支持规格拒绝、非螺纹族螺距拒绝、深度字段同源）。全量 **1013 项 = 1012 通过 / 0 失败 / 1 跳过**。Release 构建通过；git diff --check 无空白错误、无无关改动。
- **部署**（NX 关闭 ugraf=0；定向复制；未跑完整脚本）：4 DLL 哈希 MATCH——Core `00fd33df…`、UI `debe8bc8…`、NxHost `b5a5760a…`、Entry `0f2c30ed…`；菜单 `319ee927…` 未变。
- **实机复验：未执行（不虚报）**。重跑完整自检预期 13/13；重点复验 M6x1.0 与 M6 双输入、caseI 边数 12、caseJ/K 实体数增量、caseL 三场景。

## 2026-10-24 · Phase 10.4：实机自检三失败项修复（离线验证通过；已部署；复验待执行）

- **P1 caseI.edge.chamfer 边引用失效**（phase.log 2125-2128 行）：chamfer 收到 **24** 条边（fillet 的 12 条已失效旧边残留全局选区 + 自身 12 条），revalidate 对 `EDGE*1*3` MISS。根因=**测试夹具选区污染**（RequestSelections 前未清列表）。**修复**：`SelectInNx` 先 `ClearGlobalSelectionList()` 再 `RequestSelections`（仍走真实选区路径，生产 revalidate 保护未动）。
- **P2 caseK.cir.feat.count 断言错误**（2185-2186 行实机证据）：PatternFeature **提交成功**（alive、instances=6）且 **bodies 31→34（+3）**——圆形特征阵列把块实体自身阵列出真实实体副本（4 实例含源 → 3 新实体），与"线性孔特征阵列实体数不变"语义不同。**修复**：caseK 断言改为 `beforeFeat+3`（依据实机证据，非放宽）；成功消息同步。
- **P3 caseL.mirror RuntimeBinderException**（2201 行）：`SelectDatumPlane` 无 `Add`。**反射确认**（tools/SelectionProbe 读真实 NXOpen.dll）：两平面属性是**单选持有者**——`SelectDatumPlane.Value : DatumPlane`、`SelectISurface.Value : ISurface`，均 `set_Value` 可赋。**修复**（MirrorToolExecutor）：`WireMirrorPlane` 重写为两个类型化重载（SelectDatumPlane/SelectISurface），`Value` 直接赋 DatumPlane/Face 引用；geometry 重载保持单基准面结构门；删除 dynamic+Add 路径。
- **测试**：新增 `MirrorPlaneWiringTests` 2 项（typed Value 赋值、无 dynamic/Add 残留、单基准面门）；`GeometrySelfCheckCoverageTests` 更新 2 项（选区先清后设正则锁定、caseK 断言依据实机证据）。全量 **1016 项 = 1015 通过 / 0 失败 / 1 跳过**。Release 构建通过；git diff --check 干净。
- **部署**（NX 关闭 ugraf=0；**仅复制变化的 2 个 DLL**）：NxHost `682533f9…`、Entry `48ce1f85…` MATCH；Core `00fd33df…`、UI `debe8bc8…` 未动仍一致；菜单 `319ee927…` 未变。
- **实机复验：未执行（不虚报）**。重跑完整自检预期 13/13；重点：caseI 两特征（12 边、BLEND/CHAMFER、生成面）、caseK feature（提交+实体 +3+源保留）、caseL 三场景（Value 赋值路径）、自检结束选区已清/临时部件关闭/工作部件恢复。

## 2026-10-24 · Phase 10.4 后续：caseL.mirror.geo.face 平面面路径修复（离线验证通过；已部署；复验待执行）

- **实机失败**（13/1）：`caseL.mirror.geo.face` — `InvalidOperationException: geometry 模式镜像面必须恰好一个基准面`（已回滚）。
- **调用链追踪**：夹具选 [body, planarFace] → 选区解析通过（planarFaces=1）→ `WireMirrorPlane(mbb.Plane,…)` —— **mbb.Plane 编译时类型是 SelectDatumPlane**，C# 重载按编译时绑定到 geometry 重载，其守卫 `datumPlanes.Count != 1 → throw` 在 datumPlanes=0 时触发。
- **根因=生产执行器能力缺口**：工具契约明确镜像面支持"基准面**或**普通平面面"（任务需求也明确不得限于 Datum Plane），选区解析两 kind 都收，但 geometry 模式的 wire 步骤只实现了基准面路径——平面面被"恰好一个基准面"守卫拦截。
- **API 核实**（tools/SelectionProbe 反射真实 NXOpen.dll）：`SelectDatumPlane.Value` 仅接受 DatumPlane（SetValue(DatumPlane…)）；**Face 实现了 ISurface** 但 MirrorBodyBuilder.Plane 属性类型固定为 SelectDatumPlane——平面面无法直传。**可行生产路径**：从平面面真实几何（ReadPlanarFaceDefinition：AskFaceUvMinmax UV 中点 + AskFaceProps 原点/单位法向）`CreateFixedDatumPlane(origin, matrix)`（matrix Z 轴=面法向，右手系）创建**临时基准面**，`Value = tempDatum`；**提交成功后 `UFModl.DeleteFeature(临时datum所属Feature)` 删除**（反射验证：DatumPlane 无 Destroy，UFModl.DeleteFeature(Tag[]) 存在）；失败路径由既有 Undo 回滚清理。
- **实现**（MirrorToolExecutor）：geometry 重载签名改为 `(SelectDatumPlane, Part workPart, …, out DatumPlane? tempDatumToDestroy)`——基准面路径不变；平面面路径=转换+提交后删除；守卫消息改为"基准面或平面面"。调用量点在 commit+DoUpdate 成功后删除临时 datum 并记录日志（删除失败仅记录，不影响镜像特征）。
- **测试**：`MirrorPlaneWiringTests` 更新/新增 3 项（守卫消息、临时 datum 转换+DeleteFeature、验证读取路径对、UFEvalsf 禁令）。全量 **1017 项 = 1016 通过 / 0 失败 / 1 跳过**。Release 构建通过；git diff --check 干净。
- **部署**（NX 关闭 ugraf=0；**仅 1 个 DLL**）：NxHost `f490b5f1…` MATCH；Core/UI/Entry 未动仍一致；菜单 `319ee927…` 未变。
- **实机复验：未执行（不虚报）**。重点：caseL.geo.face（平面面镜像成功且自检报告无残留辅助 datum）、caseL 另两场景回归、自检结束清理恢复正常。

## 2026-10-24 · Phase 10.4 闭环：临时 datum 删除时机修复（离线验证通过；**部署被 NX 占用阻塞**）

- **实机失败**（phase.log 2866-2869 行，顺序铁证）：`stage: commit` → `temp datum feature deleted (post-commit)` → `stage: DoUpdate` → `NXException: 试图使用不活动的对象`。**根因**：镜像特征在 `DoUpdate` 之前未求值——在 Commit 与 DoUpdate 之间删除临时 datum 的 feature，使未更新的镜像特征失去平面引用。
- **修复**（MirrorToolExecutor）：临时 datum 的删除从"Commit 后立即"移至 **DoUpdate + 后置验证成功之后**（`tempDatumToDestroy` 提升为方法级变量；删除失败仅记录不影响镜像成功）。**未吞 NXException**——DoUpdate 阶段的异常仍走原 EXCEPTION-DETAIL 记录与回滚。
- **测试**：新增 `TempDatum_DeletedOnlyAfterDoUpdate` 顺序锁（"post-update" 删除标记必须在 DoUpdate 之后；旧 "post-commit" 删除点已消失）。全量 **1018 项 = 1017 通过 / 0 失败 / 1 跳过**。Release 构建通过；git diff --check 干净。
- **部署（NX 退出后补跑完成）**：定向复制 NxHost DLL（唯一变化文件），SHA-256 核对 MATCH——NxHost `e14fe5e8…`；Core `00fd33df…`、UI `debe8bc8…`、Entry `48ce1f85…` 未动仍一致；菜单 `319ee927…` 未变；application 目录仅 4 DLL + selfcheck 种子。
- **实机复验：未执行（不虚报）**。部署后重跑完整自检，重点 caseL.geo.face（平面面镜像成功+无残留辅助 datum）与清理恢复。

## 2026-10-24 · Phase 10.4 闭环 v2：临时 datum 删除级联毁镜像——改为保留并托管（离线验证通过；已部署；复验待执行）

- **实机失败**（phase.log 3205-3208 行）：`bodies=39→40`（提交+更新均成功、postcondition pass）→ `temp datum feature deleted (post-update)` → **caseL.geo.face.count 计数 39 ≠ 40**——**DeleteFeature 级联删除了依赖该 datum 的 MirrorBody**（镜像的平面定义依赖 datum，删除 datum 连带移除镜像体；DoUpdate 前删是"不活动对象"，DoUpdate 后删是"实体丢失"——同一依赖的两种表现）。
- **修复（MirrorToolExecutor）**：临时 datum **不再删除**，改为**保留并托管**——它就是镜像体平面定义的支撑对象（NX 自身部件中长期保留 datum 是惯例）：`Layer = 250`（专用图层，不占常规层）+ `RedisplayObject()` + `Blank()`（不可见），仅命名尝试被 NXObject.Name 只读拦下后移除。DeleteFeature 路径整体移除。失败提交仍由既有回滚清理。
- **测试**：`MirrorPlaneWiringTests` 更新（DeleteFeature 断言消失、Layer=250/Blank/kept 断言加入）。全量 **1018 项 = 1017 通过 / 0 失败 / 1 跳过**。Release 构建通过；git diff --check 干净。
- **部署**（NX 关闭 ugraf=0；仅 NxHost）：NxHost `697d1c6a…` MATCH；其余未动一致；菜单 `319ee927…` 未变。
- **实机复验：未执行（不虚报）**。重点：caseL.geo.face（实体数 +1 保持、datum 在 250 层不可见）、caseL 另两场景、自检结束清理恢复。若用户要求物理删除辅助 datum，需改用"镜像后重建平面引用"的深路径（本轮未做，避免无证据猜测 API）。
