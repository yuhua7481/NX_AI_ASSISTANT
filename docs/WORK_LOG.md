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
