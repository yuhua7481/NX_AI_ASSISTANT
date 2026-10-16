# 阶段 2 详细报告：类型化工具调用与第一个建模工具（create_block）

日期：2026-10-15 ~ 2026-10-16。分支：`recovery/pre-review-20261014-234252`（成果为未提交工作区状态）。
状态术语定义见 [../PROJECT_STATUS.md](../PROJECT_STATUS.md)。

## 1. 本阶段范围

**做了**：类型化工具契约与注册表（仅 `create_block`）、ModelService 工具调用协议、安全 NXOpen 建模入口（无 SaveAs/自动保存）、执行前用户确认流、执行后真实几何验证、工具回合状态机、全量离线测试、Release 构建与部署核验。

**没做**（阶段边界）：真实 NX2512 实机验收（用户执行，见 §8）、第二个建模工具、附件、流式输出、并行工具调用、对话持久化。

## 2. 工具契约与消息流

### 2.1 Core 层（NXOpen-free）

| 文件 | 内容 |
|---|---|
| `ToolContract.cs` | `ToolDefinition`（名称/描述/JSON Schema）、`ToolCallRequest`、`ToolExecutionResult`（8 种结构化状态）、`ToolRegistry`（只允许调用注册工具）、`CreateBlockRequestParser`（参数解析与校验） |
| `CreateBlockTool.cs` | `create_block` 唯一工具的 schema 常量与注册表工厂 |
| `NxToolExecutor.cs`（Core） | `INxToolExecutor` 接口 + `CreateBlockExecutionResult` 结构化结果 |
| `OutboundMessage.cs` | 外发消息模型（system/user/assistant/tool + assistant tool_calls） |
| `ChatCompletionResult.cs` | 单回合结果：纯文本 / 工具调用 / 失败 三态 |

**参数约定**：`length/width/height/unit` 必填；`origin_x/y/z` 可选（毫米，默认 0,0,0，由本产品定义，模型不决定任何文件路径）；单位 mm/cm/m/inch 在解析时统一换算为毫米。schema `additionalProperties: false`，解析器拒绝未知字段、非法 JSON、NaN/±∞、非正数、越界（沿用 `BlockRequestValidator` 既有界限值，未放宽）。

**消息流**：

```text
用户消息 → [回合1] 请求携带 tools 声明
        → 模型 tool_calls（每回合至多解析 1 个，并行调用拒绝）
        → Core 解析+验证（失败→结构化错误回填，不执行）
        → UI 确认卡（显示操作/尺寸/单位/原点/目标部件）
        → 用户确认 → CreateBlockInWorkPart 执行一次（回合 1 仅此一次）
        → tool 消息（正确 tool_call_id）回填 → 回合 2 不再声明 tools
        → 模型生成最终文字回复
```

回合上限 `MaxModelResponsesPerTurn = 2`：回合 1 可请求工具（至多确认执行一次），回合 2 只生成文字、不广告 tools；模型在最终回合请求工具会被拒绝并显示错误。

### 2.2 ModelService 工具协议（阶段 B）

- 新增 `GetChatCompletionWithToolsAsync(messages, tools, ct)`；原 `GetChatCompletionAsync` 纯文本路径行为不变（无回归测试固化）。
- 请求：`tools` 声明（function type + name/description/parameters）。
- 响应解析：`choices[].message.tool_calls` → `ParsedToolCall(Id, Name, ArgumentsJson)`；缺 id/缺 function/非法形状均拒绝；**assistant 空 content + 合法 tool_calls 有效**；文本+tool_calls 视为工具回合。
- 消息序列化：assistant tool_calls（content 为 null 时省略）与 tool 结果（`tool_call_id` + content）正确序列化；Bearer 头保持不变（测试固化）。

## 3. 安全建模入口与保存行为（阶段 C）

`BlockToolExecutor.cs`（NxHost 层）新入口 `NxToolExecutor.ExecuteCreateBlock`：

- **不调用 `SaveAs`、不自动保存、不改路径、不重命名**——新入口不接收任何文件路径参数；旧 `BlockFeatureCreator.CreateBlock`（含 SaveAs）未连接到任何模型路径，行为未改动。
- 前置校验先于 NXOpen：参数验证（复用 `BlockRequestValidator.ValidateWorkingPartRequest`）→ 无工作部件拒绝 → **工作部件与确认时不一致则拒绝执行**（PreconditionsChanged）→ **非毫米部件拒绝**（不做自动单位换算猜测）。
- UndoMark 包裹；失败/验证不过时 UndoToMark 回滚；builder 全路径 `Destroy()`；失败详情追加 `%TEMP%\NXAI_Assistant_operation_error.log`（无敏感信息）。
- **执行后验证（真实 NXOpen API，已核对元数据）**：实体计数差 = 1 + JournalIdentifier 差集确定新实体 + `UF_MODL_ask_bounding_box` 读取包围盒并与请求尺寸比对（容差 0.01 mm）。验证不确定即回滚并返回 `verification_failed`，绝不把未知状态当成功。
- NXOpen 事实核验方式：对 `managed_core/NXOpen.dll`、`NXOpen.UF.dll` 做元数据探测（`UFModl.AskBoundingBox(Tag, Double[])`、`NXObject.JournalIdentifier`、`BasePart.PartUnits → Units{Inches/Millimeters/Meters/Micrometers/Mix}`），并经 `managed/NXOpen.xml`（用户提示路径）复核。

## 4. 确认 UI 与协调状态机（阶段 D）

- `ChatSessionController` 重构为工具回合状态机：解析→验证→确认（`ToolConfirmationRequest`/`ToolConfirmationDecision`）→ 单次执行 → tool 消息回填 → 最终回复。未注册工具/参数错误/重复请求均返回结构化结果；Detach 后迟到的"确认"不执行（`_detached` 防线 + 测试固化）。
- `AssistantChatForm.ShowToolConfirmationAsync`：确认卡显示操作名、长宽高（毫米）、原点、目标部件名+完整路径（确认时从 `IActivePartReader` 现场读取）；默认焦点"否"；确认时把现场部件路径交给执行器做二次核对。部件未打开/读取失败直接拒绝并提示。防重复确认由控制器构造保证（每回合至多一次执行）。
- 窗口 Dispose 时 `_detached = true` + 控制器 Dispose：在途请求取消、确认卡 abort、迟到结果丢弃。
- 确认对话框是同步模态（UI 线程），不阻塞网络线程；HTTP 全异步，NX 主线程只承担 NXOpen 执行。

## 5. 修改文件清单

修改（9）：`BlockRequestValidator.cs`（+工作部件几何校验重载）、`ModelService.cs`（工具协议）、`ChatSessionController.cs`（工具状态机重写）、`AssistantChatForm.cs`（确认流+生命周期）、`NxHostBoot.cs`（注入执行器）、`ChatSessionControllerTests.cs`（签名适配）、`deploy/.../application/` 4 个部署 DLL。

新增（10）：Core 5 个（见 §2.1 + `ChatCompletionResult.cs`）、`BlockToolExecutor.cs`、`ToolConfirmationFlow.cs`、测试 3 个（`CreateBlockToolContractTests` 19 项、`ModelServiceToolProtocolTests` 15 项、`ChatSessionControllerToolTests` 14 项）。

## 3'. 测试证据

- **Release 构建**：0 警告 0 错误（本会话最终态重跑核实）。
- **自动化测试**：**144 项 = 143 通过 / 0 失败 / 1 跳过**（2026-10-16 全量执行）。跳过项：`BlockFeatureCreatorTests.CreateBlock_ValidParameters_SucceedsInNX`（需真实 NX + 一次性部件，按设计跳过）。新增 48 项离线测试全部不触真实网络/配置/零件。
- `git diff --check`：通过（仅行尾 CRLF 提示，非 whitespace 错误）。
- 部署一致性：4 DLL SHA-256 与 Release 产物一致。
- 调试记录：测试宿主在 xUnit 环境下崩溃的问题已隔离——裸 await `TaskCompletionSource` 完成路径与控制器 fire-and-forget 链在 xUnit MaxConcurrencySyncContext 下冲突；console 复现同一流程完全正常。测试 helper 改用 `WhenAny+Delay` 等待（见 `ChatSessionControllerToolTests` 类注释）；控制器侧同步加挂 fire-and-forget 异常观察兜底。产品运行路径（WinForms/NX）无此环境。

## 4. 尚未完成：真实 NX2512 实机验收 → ✅ 已完成（2026-10-16 更新）

**八项实机验收全部通过，证据为用户报告（对话内确认，未附截图/日志存档）；本报告撰写会话未重新执行任何实机步骤。**

1. 一次性可丢弃测试部件：取消 → 无几何变化 ✅
2. 确认后创建实体出现（特征树可见）✅
3. 实测尺寸/包围盒与请求一致（验证 `UF_MODL_ask_bounding_box` 读数）✅
4. 部件路径未变、未自动保存 ✅
5. 非法尺寸请求被结构化拒绝 ✅
6. 再次有效请求成功 ✅
7. 工作部件切换后确认 → 中止并重提示 ✅
8. NX 退出时窗口不挂起（Phase 1 遗留项一并验证）✅

构建/测试/部署哈希等自动化数字（0 警告 0 错误、144 项 = 143/0/1、4 DLL SHA-256 一致）为 2026-10-15/16 会话结果，收尾归档轮未重新执行。

### 安全边界复核（2026-10-16 归档轮，源码复核，逐项通过）

1. `NxToolExecutor.ExecuteCreateBlock` 无 `SaveAs`/`Save`/重命名（grep 证实）。
2. 旧 `BlockFeatureCreator.CreateBlock`（含 SaveAs）在 src/ 无调用点，未接入 AI 工具路径（仅测试文件引用，按设计跳过）。
3. 未经确认不执行：`ProcessToolCallAsync` 三道门禁（decision==null→取消；!Confirmed→UserCancelled；_detached→取消）+ 回合上限，测试固化。
4. 确认后部件变化拒绝（FullPath 比对 → PreconditionsChanged，[BlockToolExecutor.cs:68](../../src/NXAI.Assistant.NxHost.NX2512/BlockToolExecutor.cs)）。
5. 未注册工具/非法参数/无效 JSON/重复执行均结构化拒绝，executor 零调用。
6. 成功仅由真实几何验证（UF 包围盒 + 实体计数 + JournalIdentifier）返回。
7. 失败与验证不过两路径均 UndoToMark 回滚，回滚结果随结果返回。
8. 密钥扫描无命中；API Key 仅 DPAPI 落盘，不入源码/日志/文档/Git。

## 5. 下一项唯一建议任务

**Phase 2 版本检查点**：将 24 个待同步文件提交并普通推送到 `origin/recovery/pre-review-20261014-234252`（待用户授权；不合并 main、不强制推送；`tests/TestResults/` 排除）。完整准备清单见 `WORK_LOG.md` 2026-10-16 收尾条目。之后由用户决定下一开发阶段（活动零件上下文查询或第二个建模工具）。
