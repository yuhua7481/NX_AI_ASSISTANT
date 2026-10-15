# 项目当前状态（PROJECT_STATUS）

> 每次打开新编码会话时**首先阅读本文件**。每次任务结束后必须更新本文件（见文末"更新规则"）。
> 最后核验时间：2026-10-15。核验方式见各条目"证据"。

## 状态术语（全库统一）

| 术语 | 含义 |
|---|---|
| 源码确认 | 对应实现可以在当前代码中找到 |
| 自动化测试通过 | 指定测试在指定工作区实际执行并通过 |
| Release 构建通过 | 指定工作区完成 Release 构建，0 警告 0 错误 |
| NX2512 实机通过 | 用户或执行者在真实 NX2512 中实际验证 |
| 外部 API 通过 | 单独针对某个外部接口的实际请求验证（与本项目测试分开记录） |
| 待验证 | 尚无足够证据 |
| 已知限制 | 当前明确不支持或存在的风险 |

禁止用笼统的"已完成"代替以上分级。**一个 DLL 编译通过 ≠ 插件可加载 ≠ 功能可用。**

## 当前阶段

**阶段 1（真实模型对话链路 + 聊天 UI）已交付，用户实机验收通过。**
尚未开始阶段 2（活动零件上下文）。**尚未形成 AI 驱动 NX 建模闭环**——Tool Calling 与第一个建模工具（长方体）是下一阶段内容，当前代码中不存在。

- 聊天 UI（三区布局：侧栏 / 消息区 / 底部输入区）：源码确认 + 自动化测试通过 + **NX2512 实机通过**。
- 模型对话（OpenAI 兼容 Chat Completions，多轮历史、错误分类、取消）：源码确认 + 自动化测试通过 + **NX2512 实机通过**（真实模型聊天）。
- 模型 API 配置（TableLayoutPanel 布局、DPAPI 密钥落盘、留空保留密钥）：源码确认 + **NX2512 实机通过**。
- 活动工作部件只读读取：源码确认 + 阶段 0 实机验证（run_journal）+ GUI 实机通过（含头部摘要与刷新）。
- AI 驱动建模（Tool Calling / 长方体工具）：**未实现**。`BlockRequestValidator`（参数校验）与 `BlockFeatureCreator`（NXOpen 执行）已存在并通过离线测试，但未接入模型对话、无工具协议、无用户确认流、未在 NX 中实机验收。

## 当前分支与工作区

- 分支：`recovery/pre-review-20261014-234252`（主分支：`main`）。
- 最近提交：`30181cb`（2026-10-14 23:43，"Checkpoint current workspace before code review"）。
- **工作区有大量未提交修改**（12 个已跟踪文件修改 + 7 个未跟踪文件，含 4 个部署 DLL、Phase 1 全部核心源码与测试、聊天 UI 重构）。截至最后核验未做任何 commit——这是有意保留的状态，等待用户审查；**未经用户明确授权不得提交**。
- 未提交内容清单见 `WORK_LOG.md` 2026-10-15 条目。

## 最新已核实验证结果

| 项目 | 结果 | 对应工作区状态 | 证据 |
|---|---|---|---|
| Release 构建 | 通过，0 警告 0 错误 | 当前工作区（含全部未提交修改），2026-10-15 | 本会话 `dotnet build NXAIAssistant.sln -c Release` |
| 自动化测试 | **96 项：95 通过 / 0 失败 / 1 跳过** | 同上，2026-10-15 重新执行核实 | `dotnet test` 输出；跳过项为需真实 NX 的 `BlockFeatureCreatorTests.CreateBlock_ValidParameters_SucceedsInNX`（按设计跳过，见测试内注释） |
| 部署 DLL 一致性 | `deploy/nxai_custom/application/` 4 个 DLL 与 Release 产物 SHA-256 一致 | 同上，2026-10-15 | 本会话哈希比对 |
| NX2512 实机验收 | **六项全部通过**（用户实测）：① 主窗口布局 ② 模型设置窗口 ③ 真实模型对话 ④ 请求期间行为（取消/窗口关闭）⑤ 新建对话及旧上下文清理 ⑥ Windows 显示缩放 | 上述工作区部署产物 | 用户实测反馈（对话内确认） |

历史测试数据（27 项、81 项、94 项等）属于当时的中间工作区，不代表当前结果；演进过程见 `WORK_LOG.md`。

## 当前已知风险与限制

1. **NX 退出清理未显式实现**：窗口/控制器生命周期绑定 Form Dispose，无 NX 关闭订阅；依赖 NX 进程拆除兜底。需实测"NX 退出时窗口不挂起"（源码审查结论，见 WORK_LOG 2026-10-15 验收准备轮）。
2. **ChatLog 非线程安全**：单线程所有权模型（UI 线程）；当前通过 ChatSessionController 的 SynchronizationContext 保证安全。后续引入流式输出或后台日志消费时必须重新审查。
3. **历史消息上限固定 20 条**（`DefaultMaxOutboundMessages`），无对话持久化——窗口关闭即丢失。
4. **未签名 DLL**：SignDotNet 签名路径未打通（要求 NX resource bundle）；本机证据显示无签名 DLL 可加载，其他机器加载被拒时再处理。
5. **模型输出不执行任何工具**：当前模型回复仅作为文本展示；这是阶段 1 的设计边界，不是缺陷。
6. 外部 API 测试中"图片请求导致编程界面无法继续"根因未定（与本项目无直接关系，见 `reports/EXTERNAL_API_DIAGNOSTICS.md`）。

## 下一项唯一建议任务

**Phase 2：类型化工具调用与第一个建模工具（长方体），含执行前用户确认与执行后几何验证。**

范围：定义 typed tool 契约与注册表（不执行任意模型生成的 C#）；ModelService 增加工具调用协议；用户确认流；`BlockFeatureCreator` 接入确认后的执行路径；NX 实测（一次性可丢弃测试零件）。开始前先读 `DEVELOPMENT_RULES.md` 与 `reports/PHASE_1_CHAT_AND_UI_ACCEPTANCE.md` 的"接口缺口"。

**不要在本任务中顺手实现附件、流式输出或第二个建模工具。**

## 详细文档索引

| 文档 | 用途 |
|---|---|
| [../README.md](../README.md) | 项目入口、构建/部署命令、阅读顺序 |
| [DEVELOPMENT_RULES.md](DEVELOPMENT_RULES.md) | 不可擅自改变的架构与安全约束（分强制/推荐） |
| [ARCHITECTURE_AND_PHASES.md](ARCHITECTURE_AND_PHASES.md) | 架构分层与阶段计划 |
| [WORK_LOG.md](WORK_LOG.md) | 追加式开发日志（每阶段实际完成内容） |
| [RELEASE_CHECKLIST.md](RELEASE_CHECKLIST.md) | 每个可交付版本的检查清单 |
| [reports/PHASE_1_CHAT_AND_UI_ACCEPTANCE.md](reports/PHASE_1_CHAT_AND_UI_ACCEPTANCE.md) | 阶段 1 详细报告（对话链路 + UI + 验收） |
| [reports/EXTERNAL_API_DIAGNOSTICS.md](reports/EXTERNAL_API_DIAGNOSTICS.md) | 外部模型/LiteLLM 排查记录（与本项目测试分开） |
| [PHASE_0_REPORT.md](PHASE_0_REPORT.md) | 阶段 0 交付报告（2026-10-13） |
| [TECHNICAL_SPIKE_RESULTS.md](TECHNICAL_SPIKE_RESULTS.md) | 阶段 0 技术事实与证据分级 |
| [SUPPORTED_NX_VERSIONS.md](SUPPORTED_NX_VERSIONS.md) | NX 版本支持矩阵（以实测为准） |
| [VERSION_COMPATIBILITY_AND_UPDATES.md](VERSION_COMPATIBILITY_AND_UPDATES.md) | 版本兼容、升级与更新策略 |
| [PRODUCT_REQUIREMENTS.md](PRODUCT_REQUIREMENTS.md) | 产品需求 |

## 更新规则（每次任务结束后执行）

1. 更新本文件：当前状态、最新已核实验证结果（注明对应工作区状态与日期）、已知风险、下一项任务。
2. 在 `WORK_LOG.md` 追加本次实际工作与验证记录（不改写历史条目）。
3. 完成一个阶段或修复重要问题时，更新或新增对应阶段报告。
4. 不要重写整个文档库，不要复制重复的配置说明和测试结果。
