# 项目当前状态（PROJECT_STATUS）

> 每次打开新编码会话时**首先阅读本文件**。每次任务结束后必须更新本文件（见文末"更新规则"）。
> 最后核验时间：2026-10-17（Phase 7.3 工具协议适配轮，离线验证）。核验方式见各条目"证据"。

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

**Phase 7.3 工具协议适配已完成离线验证（2026-10-17）：GLM 推理模型把工具调用写成 content 内标签块导致原始标记泄漏聊天的问题已修复（受限适配层，335 项测试通过，4 DLL 部署一致）。待实机验收：Phase 7.3 协议 7 项（报告 §5）+ Phase 7.2 拉伸 8 项 + Phase 7 四工具 14 项。Phase 6/6.1 已实机验收通过（16 项）。实机未测项一律不视为通过。**

**2026-10-20 追记**：具体失败案例"倾斜轴（用户选中的关联直线特征）+ XY 圆形草图 + 360° 旋转包络误拒回滚"已**实机验收通过**（自主模式，d20 圆 U45/V0，`REVOLVED(2)` 成功、无 verify FAIL、无回滚；证据来源为用户报告，详见 WORK_LOG 2026-10-20 条目）。该修复引入了 Core 级精确包络数学（`RevolveEnvelopeMath`，逐轴 Lipschitz 分支定界，替代四点采样与圆柱界）。**此项通过不改变 Phase 7 其余实机清单的待验收状态。**

**2026-10-20 第二次追记（布尔未知状态恢复）**：布尔未知状态曾被探针映射缺口永久阻塞（`boolean_operation` 无探针映射 → 全部"无法判断"硬停），已修复（commit `f55a574`）；随后发现映射修复本身存在**归因缺陷**（任意已有布尔特征都会被误当作本次未知操作的提交证据），已补充**归因快照**修复——探针按 `ProbeFeatureSnapshot`（BeginAttempt 前的只读特征身份快照）区分操作前已有特征与本次尝试新增特征，无法归因时安全停止。实机复验分两类：①普通差集（重启 NX 后隔离部件验证参数/执行/实体）；②未知状态恢复（同一会话内确实制造待核条目并触发宿主探针，未安全复现前不宣称通过）。详见 WORK_LOG 2026-10-20 两条目。

**2026-10-20 第三次追记（探针二次补充）**：归因快照修复仍有两个遗漏已修正——①**空快照语义**：非 null 空集合是有效基线（操作前无匹配特征），不再误判为"无法判断"；②**按操作类型归因**：待核布尔操作按结构化 `BooleanOperationType` 只匹配对应 NX 特征类型（unite→UNITE / subtract→SUBTRACT / intersect→INTERSECT，不再三合一），类型缺失时安全停止。实机验收分类（普通差集 vs 未知状态恢复两类）同前次追记。

- **NX 闪退（Phase 5 引入）**：根因已实证定位——`UFEvalsf.AskFaceUvMinmax`（B-surface 专用求值器）被用于平面面，发送消息采集上下文时触发 AccessViolation 终止 ugraf.exe（Windows 事件日志完整托管栈）。已改用 `UFModl.AskFaceUvMinmax`（通用面 API）并修正 `AskFaceProps` 参数序（原法向会绑定到 u1）。**用户实机验证（2026-10-16）：原崩溃路径未复现闪退。**
- **选中面无反馈**：已修复并经用户实机验证——发送时显示捕获摘要（数量/类型/面中心法向），"刷新上下文"按钮正常，空选如实提示。
- 新增 PhaseLog 阶段日志（%TEMP%\NXAI_Assistant_phase.log）：上下文采集/选择数/面几何/Builder 提交/验证/回滚全程可追踪，不含敏感信息。
- Phase 5 其余条目（上下文 DTO、placement 协议、面相对执行）：**平面面路径经用户实机验证**；其余对象类型与拒绝/中止/回归路径仍待实机验收。

## 当前分支与工作区

- 分支：`recovery/pre-review-20261014-234252`（主分支：`main`）；本地 HEAD = `7cf7dce`（Phase 2 检查点，已推送）。
- 远端：Phase 2 检查点已推送（`6b030bb`+`7cf7dce`，2026-10-16 用户授权执行）。Phase 3 开始时 `git ls-remote` 再次连接失败（GitHub 间歇性不可达）——远端当前状态未实测确认，Phase 3 成果**未提交未推送**（本任务未授权 Git 操作）。
- **工作区有未提交修改（Phase 3 全部成果）**：源码 7 改 + 3 新增、测试 2 改 + 1 新增、文档 3 改 + 1 新增（详见 `WORK_LOG.md` 2026-10-16 Phase 3 条目）；**未经用户明确授权不得提交**。

## 最新已核实验证结果

| 项目 | 结果 | 对应工作区状态 | 证据 |
|---|---|---|---|
| Release 构建 | 通过，0 警告 0 错误 | 当前工作区（Phase 7 全部修改），2026-10-16 本轮执行 | `dotnet build NXAIAssistant.sln -c Release` |
| 自动化测试 | **321 项：320 通过 / 0 失败 / 1 跳过**（Phase 7.1 后，+7 项生命周期测试） | 同上，2026-10-16 本轮执行 | `dotnet test`；跳过项为需真实 NX 的既有验收测试 |
| 部署 DLL 一致性 | **通过**：Phase 7 四 DLL + Phase 7.1 UI.dll 全部部署，SHA-256 与构建产物逐一一致（7.1 UI.dll `f46eaa96…`，NX 关闭后部署） | 当前工作区（Phase 7.1） | sha256sum 比对 |
| Phase 7 API 核验 | **通过**：Sketch/Extrude/Revolve/Boolean 全部关键签名经本机程序集元数据+XML 文档核实；AskMassProps3d 数组布局不可核实→未采用（解析包围盒替代，体积验证列入实机验收） | 当前工作区 | 反射探针 + NXOpen.xml（Phase 7 报告 §2） |
| NX2512 实机验收（Phase 7 四工具） | **未执行**：草图矩形/圆、取消、拉伸、旋转（WCS 轴+选中轴）、三布尔、安全拒绝、模式路径、回归、无自动保存等 14 项 | 当前部署产物 | 待用户实测（清单见 Phase 7 报告 §7） |
| NX2512 实机验收（Phase 7.1 聊天布局） | **未执行**：多消息+Markdown 缩放、新对话清空（原崩溃路径）、清空后缩放、选择/复制/滚动、窗体关闭无异常、建模抽查共 6 项（Phase 7.1 报告 §4） | 当前部署产物（已部署） | 待用户实测 |
| NX2512 实机验收（Phase 6/6.1） | **通过（用户报告，2026-10-16）**：双模式 11 项 + 开关显示 5 项 = 16 项全部通过（确认/自主执行、确认卡保持、选中面圆柱、拒绝/取消路径、切回恢复、不自动保存、开关 OFF/ON 完整显示、DPI/缩放、交互与持久化、重启模式恢复、聊天无回归） | Phase 6.1 部署产物（UI.dll `191589a4…`） | 用户实测报告（对话内确认，未附截图/日志存档） |
| NX2512 实机验收（Phase 5.1 崩溃路径 + Phase 5 核心路径） | **通过（用户报告，2026-10-16）**：选中平面发送消息不闪退；选择读取/上下文刷新/空选正常；面相对圆柱确认卡与确认后实际创建通过 | Phase 5.1 部署产物（修复后哈希见 Phase 5.1 报告 §6） | 用户实测报告（对话内确认） |
| NX2512 实机验收（Phase 5 剩余项） | **未执行**：边/顶点/曲线/基准/其他对象识别、切 WCS/视图、取消路径、目标失效中止、global_origin 与 block/cone/sphere 回归、复杂/曲面/歧义目标拒绝、无自动保存核对、聊天渲染回归 | 同上 | 待用户实测 |
| NX2512 实机验收（Phase 2 八项） | **全部通过（用户报告，2026-10-16）** | Phase 2 部署产物 | 用户实测报告（对话内确认） |
| 安全边界复核 | Phase 2 通过（2026-10-16 归档轮）；Phase 3/5 同骨架复用；Phase 5.1 复审确认线程调度无过错；Phase 6 授权决策在后端分派阶段（模式不依赖 UI/提示词），允许清单封闭 | 当前工作区 | 逐项代码检查 + 崩溃栈 |

历史测试数据（27/96/129 项等）属于当时的中间工作区，不代表当前结果；演进见 `WORK_LOG.md`。

## 当前已知风险与限制

0. **Phase 6 实机验收未执行**：双模式的实机行为（开关外观、自主直接执行、拒绝路径、重启恢复等 11 项）全部待用户实测（清单见 Phase 6 报告 §7）；离线测试通过不等于实机可用。**Phase 5 未测路径仍开放**：边/点/曲线/基准/复杂面等对象识别、取消与目标失效中止、旧模式与四形状回归尚未实测。复杂面中心有效性（UV 中点可能不在面上）与倾斜圆柱精确轴向验证仍为已知限制。
1. **实机验收证据为用户报告**：八项验收通过的信息来自用户对话内确认，未附截图/日志存档；复现依赖"执行者 = 用户"的信任链，与 `RELEASE_CHECKLIST.md` 记录规则一致。
2. **XUnit 等待模式限制**：测试宿主对"裸 await TCS 完成"会崩溃（根因已隔离，见 `ChatSessionControllerToolTests` 类注释与 WORK_LOG 2026-10-16）；console 复现正常，产品路径不受影响。新增工具测试必须沿用 `WaitForCompletedAsync`（WhenAny+Delay）模式。
3. **NX 退出清理**：八项验收第 ⑧ 项（NX 退出不挂起）用户报告通过；仍依赖进程拆除兜底，无显式订阅（已知限制，非缺陷）。
4. **ChatLog 非线程安全**：单线程所有权模型（UI 线程）；流式输出/后台日志消费前必须重审。
5. **历史消息上限固定 20 条**，无对话持久化；确认等待期间窗口关闭会丢弃整个回合。
6. **未签名 DLL**：跨机器部署可能被 NX 拒载，出现时再打通 SignDotNet。
7. **并行工具调用不支持**（每条 assistant 消息至多 1 个 tool call）；模型一次请求多个调用会被拒绝为 InvalidResponse。
8. **Phase 2 成果尚未同步 GitHub**：远端恢复分支仍为 `9cfd87a`（Phase 1）；2026-10-16 归档轮查询远端时 GitHub 连接失败，实际远端状态待网络恢复后确认。同步清单见 WORK_LOG 同日条目。
9. 外部 API"图片请求导致编程界面无法继续"根因未定（与本项目无直接关系，见 `reports/EXTERNAL_API_DIAGNOSTICS.md`）。
10. **UF API 教训（防止复发）**：Phase 5 曾因"元数据核实了签名但未核实 API 适用域"引入崩溃——`UFEvalsf`（UF_EVALSF_*）仅限 B-surface；选 UF API 必须同时确认签名与适用面类型，教训已写入 `NxContextReader.cs` 类头注释。

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

**Phase 7 实机验收**：Phase 7 已部署（4 DLL SHA-256 一致）。用户在 NX2512 隔离测试部件中按 Phase 7 报告 §7 的 14 项清单执行：矩形/圆草图→取消→拉伸→旋转（WCS 轴+选中轴）→三布尔→安全拒绝→模式路径→回归→无自动保存。任一项失败即停止并报告。

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
