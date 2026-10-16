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

**阶段 4（四种基本体统一协调框架 + create_cone + create_sphere）源码、离线测试、部署核验完成（2026-10-16）；实机十项验收待用户执行。**

- 四工具统一注册（block/cylinder/cone/sphere）与类型化分派：源码确认 + 自动化测试通过。
- create_cone（正圆锥，ConeBuilder DiametersAndHeight + TopDiameter=0 文档默认合法值）：源码确认 + 离线测试通过；**实机待验收**。
- create_sphere（真实球形实体，CenterPointAndDiameter）：源码确认 + 离线测试通过；**实机待验收**。
- 确认卡四形状分型显示（圆锥/圆台区分）、公共执行骨架（TryResolveWorkPart/Rollback/LogFailure）：源码确认 + 自动化测试通过。
- **聊天正文渲染（Phase 4.1 引入、4.2 重构为只读 RichTextBox）**：加粗/列表/分段 RTF 渲染 + 原生选择/Ctrl+C/右键复制（复制整条消息/复制/全选）；RTF 文本完整性有自动化证据（往返一致、注入阻断）；实机显示与选择复制手感待手动验收。
- create_block / create_cylinder：实机验收通过（用户报告），本阶段保持无回归（测试固化）。

## 当前分支与工作区

- 分支：`recovery/pre-review-20261014-234252`（主分支：`main`）；本地 HEAD = `7cf7dce`（Phase 2 检查点，已推送）。
- 远端：Phase 2 检查点已推送（`6b030bb`+`7cf7dce`，2026-10-16 用户授权执行）。Phase 3 开始时 `git ls-remote` 再次连接失败（GitHub 间歇性不可达）——远端当前状态未实测确认，Phase 3 成果**未提交未推送**（本任务未授权 Git 操作）。
- **工作区有未提交修改（Phase 3 全部成果）**：源码 7 改 + 3 新增、测试 2 改 + 1 新增、文档 3 改 + 1 新增（详见 `WORK_LOG.md` 2026-10-16 Phase 3 条目）；**未经用户明确授权不得提交**。

## 最新已核实验证结果

| 项目 | 结果 | 对应工作区状态 | 证据 |
|---|---|---|---|
| Release 构建 | 通过，0 警告 0 错误 | 当前工作区（Phase 4 全部修改），2026-10-16 本轮执行 | `dotnet build NXAIAssistant.sln -c Release` |
| 自动化测试 | **210 项：209 通过 / 0 失败 / 1 跳过**（Phase 4.2 后） | 同上，2026-10-16 本轮执行 | `dotnet test`；跳过项为需真实 NX 的 `CreateBlock_ValidParameters_SucceedsInNX`（按设计跳过） |
| 部署 DLL 一致性 | 4 个部署 DLL 与 Release 产物 SHA-256 一致（NX 退出后重跑部署成功） | 同上，2026-10-16 | `build.ps1` 部署后哈希比对 |
| **NX2512 实机验收（Phase 2 八项）** | **全部通过（用户报告，2026-10-16）** | Phase 2 部署产物 | 用户实测报告（对话内确认） |
| NX2512 实机验收（Phase 3 九项） | **未执行**（create_cylinder 待用户在一次性测试部件验收） | 当前部署产物 | 待用户实测 |
| 安全边界复核 | Phase 2 通过（2026-10-16 归档轮）；Phase 3 同骨架复用（同一执行器类、同一确认门禁、同一回滚/验证路径） | 当前工作区 | 逐项代码检查 |

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

**Phase 4 实机验收**（部署已完成、4 DLL 哈希一致）：用户在一次性测试部件执行十项验收（见 `reports/PHASE_4_PRIMITIVES_FRAMEWORK.md` §7，重点第 3/4 项目视核对真锥体与球形）。通过后更新本文件并决定版本检查点。

**实机验收前不要扩大建模工具范围（不做第五种形状）。**

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
| [reports/PHASE_3_CREATE_CYLINDER.md](reports/PHASE_3_CREATE_CYLINDER.md) | 阶段 3 详细报告（create_cylinder 契约/执行/验证/测试） |
| [reports/PHASE_4_PRIMITIVES_FRAMEWORK.md](reports/PHASE_4_PRIMITIVES_FRAMEWORK.md) | 阶段 4 详细报告（四形状统一框架 + cone/sphere） |
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
