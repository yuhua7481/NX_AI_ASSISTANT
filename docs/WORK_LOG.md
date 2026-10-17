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
