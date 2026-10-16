# 项目当前状态（PROJECT_STATUS）

> 每次打开新编码会话时**首先阅读本文件**。每次任务结束后必须更新本文件（见文末"更新规则"）。
> 最后核验时间：2026-10-16（Phase 2 实机验收归档轮）。核验方式见各条目"证据"。

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

**阶段 2（类型化工具调用 + create_block 建模工具）已完成：源码、离线测试、部署核验，以及用户执行的 NX2512 八项实机验收（2026-10-16 报告通过）。AI 驱动建模闭环成立。**

- 工具契约与注册表（仅 `create_block`）：源码确认 + 自动化测试通过 + 实机验收通过（用户报告）。
- ModelService 工具协议（tools 声明 / tool_calls 解析 / tool 消息序列化）：源码确认 + 自动化测试通过（纯文本路径无回归）+ 实机通过。
- 安全建模入口（无 SaveAs/自动保存，UndoMark 回滚，UF 包围盒执行后验证）：源码确认（2026-10-16 归档轮复核）+ 自动化测试通过 + 实机验收通过（创建/取消/非法参数/失败恢复/部件切换，用户报告）。
- 确认 UI 与回合状态机：源码确认 + 自动化测试通过 + 实机通过（确认流、部件切换中止、退出清理）。
- 阶段 1 全部成果（聊天 UI / 模型对话 / 配置 / 部件只读）：实机通过（2026-10-15 六项）。

## 当前分支与工作区

- 分支：`recovery/pre-review-20261014-234252`（主分支：`main`）；本地 HEAD = `9cfd87a`（无 Phase 2 提交）。
- 远端：`origin/recovery/pre-review-20261014-234252` 最后已知为 `9cfd87a`（Phase 1 成果）；`origin/main` = `14c27d4`（本地 `main` 仍 `af4340d`，不同步）。**2026-10-16 归档轮 `git ls-remote` 连接 GitHub 失败（网络不可达），远端实际状态待网络恢复后确认**。
- **工作区有未提交修改（Phase 2 全部成果）**：13 个已跟踪修改 + 12 个未跟踪文件（含 `tests/TestResults/` 临时产物，提交时应排除）；截至 2026-10-16 归档未 commit——等待用户授权；**未经用户明确授权不得提交**。完整清单与分类见 `WORK_LOG.md` 2026-10-16 收尾条目。

## 最新已核实验证结果

| 项目 | 结果 | 对应工作区状态 | 证据 |
|---|---|---|---|
| Release 构建 | 通过，0 警告 0 错误 | 当前工作区（Phase 2 全部修改），2026-10-15/16 构建核验；**归档轮（2026-10-16）未重跑** | 上一会话 `dotnet build` + `build.ps1` |
| 自动化测试 | **144 项：143 通过 / 0 失败 / 1 跳过**（最近报告，2026-10-15/16；归档轮未重新执行） | 同上 | `dotnet test` 输出；跳过项为需真实 NX 的 `CreateBlock_ValidParameters_SucceedsInNX`（按设计跳过） |
| 部署 DLL 一致性 | 4 个部署 DLL 与 Release 产物 SHA-256 一致（2026-10-15/16 部署后核验；归档轮未重跑） | 同上 | 上一会话哈希比对 |
| **NX2512 实机八项验收** | **全部通过（用户报告，2026-10-16）**：① 取消确认→无几何变化 ② 确认创建→实体出现 ③ 实测尺寸/包围盒与请求一致 ④ 部件路径未变、未自动保存 ⑤ 非法尺寸请求被结构化拒绝 ⑥ 再次有效请求成功 ⑦ 工作部件切换后确认→中止并重提示 ⑧ NX 退出时窗口不挂起（Phase 1 遗留项一并验证） | 当前部署产物 | 用户实测报告（对话内确认，未附截图/日志存档） |
| 安全边界复核 | 通过（2026-10-16 归档轮，源码复核，见 WORK_LOG 同日条目） | 当前工作区 | 逐项代码检查：新入口无 SaveAs/自动保存；旧 CreateBlock 未接入 AI 路径；确认门禁/部件校验/结构化拒绝/回滚均落实；无密钥入码 |

历史测试数据（27/96/129 项等）属于当时的中间工作区，不代表当前结果；演进见 `WORK_LOG.md`。

## 当前已知风险与限制

1. **实机验收证据为用户报告**：八项验收通过的信息来自用户对话内确认，未附截图/日志存档；复现依赖"执行者 = 用户"的信任链，与 `RELEASE_CHECKLIST.md` 记录规则一致。
2. **XUnit 等待模式限制**：测试宿主对"裸 await TCS 完成"会崩溃（根因已隔离，见 `ChatSessionControllerToolTests` 类注释与 WORK_LOG 2026-10-16）；console 复现正常，产品路径不受影响。新增工具测试必须沿用 `WaitForCompletedAsync`（WhenAny+Delay）模式。
3. **NX 退出清理**：八项验收第 ⑧ 项（NX 退出不挂起）用户报告通过；仍依赖进程拆除兜底，无显式订阅（已知限制，非缺陷）。
4. **ChatLog 非线程安全**：单线程所有权模型（UI 线程）；流式输出/后台日志消费前必须重审。
5. **历史消息上限固定 20 条**，无对话持久化；确认等待期间窗口关闭会丢弃整个回合。
6. **未签名 DLL**：跨机器部署可能被 NX 拒载，出现时再打通 SignDotNet。
7. **并行工具调用不支持**（每条 assistant 消息至多 1 个 tool call）；模型一次请求多个调用会被拒绝为 InvalidResponse。
8. **Phase 2 成果尚未同步 GitHub**：远端恢复分支仍为 `9cfd87a`（Phase 1）；2026-10-16 归档轮查询远端时 GitHub 连接失败，实际远端状态待网络恢复后确认。同步清单见 WORK_LOG 同日条目。
9. 外部 API"图片请求导致编程界面无法继续"根因未定（与本项目无直接关系，见 `reports/EXTERNAL_API_DIAGNOSTICS.md`）。

## 下一项唯一建议任务

**Phase 2 版本检查点：将全部待同步文件提交并普通推送到 `origin/recovery/pre-review-20261014-234252`**（待用户授权后执行；不合并 main、不强制推送）。检查点准备清单已输出（见 WORK_LOG 2026-10-16 条目）。提交完成后再决定下一开发阶段（活动零件上下文查询或第二个建模工具，由用户决定）。

**不要在检查点之前扩大建模工具范围。**

## 详细文档索引

| 文档 | 用途 |
|---|---|
| [../README.md](../README.md) | 项目入口、构建/部署命令、阅读顺序 |
| [DEVELOPMENT_RULES.md](DEVELOPMENT_RULES.md) | 不可擅自改变的架构与安全约束（分强制/推荐） |
| [ARCHITECTURE_AND_PHASES.md](ARCHITECTURE_AND_PHASES.md) | 架构分层与阶段计划 |
| [WORK_LOG.md](WORK_LOG.md) | 追加式开发日志（每阶段实际完成内容） |
| [RELEASE_CHECKLIST.md](RELEASE_CHECKLIST.md) | 每个可交付版本的检查清单 |
| [reports/PHASE_1_CHAT_AND_UI_ACCEPTANCE.md](reports/PHASE_1_CHAT_AND_UI_ACCEPTANCE.md) | 阶段 1 详细报告（对话链路 + UI + 验收） |
| [reports/PHASE_2_TOOL_CALLING_AND_CREATE_BLOCK.md](reports/PHASE_2_TOOL_CALLING_AND_CREATE_BLOCK.md) | 阶段 2 详细报告（工具调用 + create_block + 确认/验证） |
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
