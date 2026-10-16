# Phase 6 报告：确认模式 / 自主模式双模式交互与建模执行

日期：2026-10-16　分支：`recovery/pre-review-20261014-234252`（未提交，未授权）

## 1. 交付范围

在现有确认/执行框架上增加可切换的双交互模式，无平行执行框架、无聊天 UI 重写：

- **确认模式（Confirm）**：默认；所有建模工具执行前显示确认卡，行为与 Phase 5.1 完全一致。
- **自主模式（Autonomous）**：对允许清单内（create_block / create_cylinder / create_cone / create_sphere）且参数、上下文、安全校验全部通过的工具调用直接执行，不再逐次弹确认卡；歧义、非法输入、无效目标、未授权工具仍然安全拒绝。

## 2. 开始前保护（已执行）

- 分支/HEAD/工作区检查：`recovery/pre-review-20261014-234252`，Phase 5/5.1 未提交修改与新文件原样保留。
- 仓库外完整备份：`D:\AI\NX_AI_ASSISTANT_backup_20261016_phase6_start`（15 MB，含未跟踪文件；旧备份未动）。
- 无 git reset/clean/commit/push；未触碰用户模型配置、API Key 与 .bak 文件。

## 3. 架构与实现

### Core（`src/NXAI.Assistant.Core/ExecutionMode.cs`，新增）

| 类型 | 职责 |
|---|---|
| `ExecutionMode` 枚举 | `Confirm` / `Autonomous`；扩展方法提供稳定文件名（`confirm`/`autonomous`，未知值不回解析）与中文显示名。 |
| `AutonomousToolAllowlist` | 显式、封闭的四个已注册基本体创建工具名；`Contains` 精确匹配（大小写敏感）。未来破坏性操作不自动纳入，需单独定义权限。 |
| `IExecutionModeStore` / `JsonFileExecutionModeStore` | `%APPDATA%\NX AI Assistant\execution-mode.json`（无密钥）。缺失/损坏 JSON/未知值/非对象 → Load 返回 null → 默认确认模式；Save 失败向调用方抛出。 |
| `ExecutionModePolicy` | 纯函数授权规则：`RequiresConfirmation(mode, tool)` = 确认模式全部需确认；自主模式仅允许清单跳过。**后端分派阶段执行此规则，UI 开关只改模式，不改规则。** |
| `ExecutionModeManager` | 线程安全持有当前模式；构造时应用存储值或默认 Confirm（后端与 UI 启动态必然一致）；SetMode 先持久化后发布，持久化失败内存模式不变，`ModeChanged` 事件通知。 |

Core 不引用 NXOpen 或 WinForms（遵循项目分层约束）。

### Controller（`ChatSessionController.cs`）

- 新增可选 `ExecutionModeManager` 参数；**不传时行为与 Phase 5.1 完全一致**（全部走确认流程）。
- `ProcessToolCallAsync` 决策点读取 `_modeManager.CurrentMode` 并经 `ExecutionModePolicy` 判定——**授权在后端分派阶段检查，不依赖 UI 开关，也不依赖提示词**。
- 工具请求描述文本从确认日志中拆出：
  - 确认路径：信息文本仍为 `模型请求创建…等待你的确认。`（现有测试断言保持不变）。
  - 自主路径：`自主模式：正在创建…`（开始状态行，不谎称成功；真实结果仍由执行结果消息呈现）。
- **执行前重查模式**：自主路径在实际执行前再次读取当前模式；已切回确认模式则转回确认流程（不直接执行）。
- 自主路径执行调用 `expectedWorkPartFullPath: null`——执行器内部的工作部件校验、毫米单位门禁、UndoMark/回滚、Builder Destroy、后置条件几何验证**全部原样保留**；模型/调用方无法绕过。
- 工具请求的 `ToolConfirmationRequest` 构造逻辑不变，两路径共用同一 `ExecuteConfirmedTool`。
- 回合上限（每轮至多 2 次模型响应、至多 1 次执行）与既有取消/Detach 语义未变。

### UI（`ModeToggleSwitch.cs` 新增，`AssistantChatForm.cs` 接入）

- `ModeToggleSwitch`：自绘开关。OFF 灰轨+左滑块+文字“确认模式”；ON 橙轨（项目主色）+右滑块+文字“自主模式”。状态始终由文字标签表达（不只靠颜色）。按 DeviceDpi 缩放绘制；支持点击与空格/回车键盘切换；Accessibility（CheckButton 角色 + 模式名称）。控件只发 `ToggleRequested`，是否真正改变由窗体决定并回写 `SetChecked`。
- 位置：输入框上方模式条（`执行模式` 标签 + 开关）。
- 切换规则（`HandleModeToggleRequested`）：
  - 请求进行中 → 拒绝切换并提示（避免在途请求授权策略竞态）。
  - `SetMode` 持久化失败 → 开关恢复到仍生效的模式并给出错误信息（内存模式未变）。
  - 切换成功 → 聊天记录显示模式说明；切换动作本身不触发任何建模、不等价于对任何待执行操作的确认。
- 启动时从持久化文件加载模式，开关与状态文字准确反映实际生效模式（管理器构造即决定，后端与 UI 不可能不一致）。

## 4. 在途请求与模式切换竞态

- 每次工具调用的授权决策都在分派时读取当前生效模式；自主路径在执行前再重查一次。
- 确认卡已生成后切换模式：卡片仍等待用户明确决定，不因切换而自动执行（测试覆盖）。
- 模式切换只改变后续请求的授权策略，从不当作确认。
- 执行期间 UI 禁止切换（请求进行中拦截），消除“NX 已开始修改时切换”的线程竞态面。

## 5. 测试结果【本轮执行】

- 新增 `tests/ExecutionModeTests.cs`（22 项）：默认模式、持久化往返、损坏/未知/非对象文件 → null、启动恢复存储模式、SetMode 持久化失败内存不变、允许清单封闭性（非清单名/空/大小写变体全拒）、策略矩阵、命名稳定性。
- 新增 `ExecutionModeControllerTests.cs`（12 项）：无 manager 回归、确认模式保持确认卡、自主模式跳过确认且恰好执行一次、圆柱自主路径、状态行不谎报成功、非法参数/未知工具/未知字段不执行、确认卡 pending 期间切自主不自动执行、用户取消无几何、在途期间模式切回确认走确认流程、无重复提交（在途 StartSend 拒绝 + 执行计数=1）。
- **全量：274 项 = 273 通过 / 0 失败 / 1 跳过**（跳过：需真实 NX 的 `CreateBlock_ValidParameters_SucceedsInNX`，按设计跳过）；既有 226 项无回归。
- Release 构建 0 警告 0 错误；`git diff --check` 通过。

## 6. 部署与 SHA-256

NX 未运行（tasklist 无 ugraf.exe），4 DLL 复制至 `deploy/nxai_custom/application/`，与构建产物逐一比对一致：

| DLL | SHA-256（部署 = 构建） |
|---|---|
| NXAI.Assistant.Core.dll | `eee89cb28fc42a493c4e1e5333523eeaa147bc5d989819c039ef14346aaaf2a2` |
| NXAI.Assistant.UI.dll | `42a6919017cc2df18ca45ebb574ba6f3366045d8f52fe364835c246f406e4b86` |
| NXAI.Assistant.NxHost.NX2512.dll | `5db568355d7fc3a76c708654dfd36f47f4d4f90557a0d3745bfda7cac046aa93` |
| NXAI.Assistant.Entry.dll | `25d48833e5f70cb6e52484e95b530cf9cedf2cad751849d5bd5a90ccce551770` |

## 7. NX2512 实机验收清单（**待执行，未测项不得视为通过**）

1. 初次使用为确认模式（无 execution-mode.json 时）。
2. 开关外观：OFF 灰轨左滑块“确认模式”，ON 橙轨右滑块“自主模式”；模式文字正确。
3. 确认模式创建圆柱仍显示确认卡。
4. 自主模式明确简单圆柱请求：无确认卡、创建成功。
5. 自主模式选中面创建圆柱：中心与法向正确。
6. 缺少选择/非法尺寸/歧义目标不产生几何。
7. 取消请求不产生几何。
8. 切回确认模式后恢复确认流程。
9. 自主模式不自动保存、不 SaveAs、不修改部件路径。
10. 聊天文本选择/复制、取消请求、刷新上下文、其他基本体无回归。
11. （持久化）切换模式后重启 NX，开关与实际模式一致。

## 8. 边界与明确不做

- 允许清单封闭：删除/修改/抑制/改名/保存/SaveAs/导出等破坏性操作永远不在自主模式自动纳入。
- 模型不能修改模式、授权或允许清单；模式只在后端分派阶段生效。
- 不自动保存/另存为/改名/改路径；保留全部既有校验、UndoMark、回滚、错误日志。
- 模式持久化文件无敏感信息（只有 `confirm`/`autonomous`）。

## 9. 遗留

1. §5 实机验收清单全部待执行；通过前不得标记“实机通过”。
2. 工作区未提交（未授权）。
3. 执行模式持久化文件与模型配置同目录；如需迁移/清理，与 model-config.json 同策略处理。

---

## Phase 6.1（2026-10-16）：修复开关显示不完整

**现象**（用户实机验收报告）：模式切换功能正常，但"执行模式"区域的开关显示不完整，轨道/滑块疑似被裁剪。

**根因**（代码检查确认，非推测）：
1. **固定高度容器裁剪自绘内容**：`modeBar` 固定 `Height = 30`、`Padding(24, 2, 24, 2)` → 内部可用高度仅 26px。而 `ModeToggleSwitch` 是自绘控件，轨道+文字高度按 `DeviceDpi` 缩放：96dpi 需 24px（余量仅 2px），125% 需 30px、150% 需 35px——超过 26px 的部分被 TableLayoutPanel 底边裁掉。窗体 `AutoScaleMode.Font` 与 DPI 缩放系数不一致时（YaHei 字体缩放常见）该问题必然出现。
2. **死控件**：`_modeBarStateLabel`（"确认模式/自主模式"状态文字）被创建并持续更新，但**从未加入任何容器**——用户看不到，纯死代码。

**修复**（[AssistantChatForm.cs](../../src/NXAI.Assistant.UI/AssistantChatForm.cs) 模式条区域）：
- `modeBar` 高度改为内容驱动：`AutoSize = true` + TableLayoutPanel `RowStyle(AutoSize)`，行高随开关 `GetPreferredSize`（内部按 DPI 缩放计算）自适应——任何 DPI 下轨道、滑块、文字都不会再被裁剪。
- 开关 `Dock = Fill` 于 TLP 单元格，垂直居中；标签 `执行模式` 显式 `RowStyle` + 顶部对齐边距。
- 删除从未显示的 `_modeBarStateLabel`；模式状态由开关自身的文字标签表达（本就是权威指示，符合"不只靠颜色"要求）。
- 未改动：`ModeToggleSwitch` 绘制代码、点击/键盘/无障碍/`ToggleRequested` 语义、模式持久化、授权策略、允许清单、工具调用与 NX 执行链路。

**验证**【本轮执行】：Release 构建 0 警告 0 错误；全量测试 **274 项 = 273 通过 / 0 失败 / 1 跳过**（无回归）；`git diff --check` 通过；NX 未运行，仅 `NXAI.Assistant.UI.dll` 有变更，部署后 SHA-256 与构建产物一致：`191589a4931e374fe2e06a5dfad4bf9d1ea35416ad3c9b547e150525b60e1dbe`。

**NX2512 实机复验（待执行，不得视为通过）**：
1. OFF：灰色轨道完整可见，滑块在左侧，文字"确认模式"清晰；
2. ON：橙色轨道完整可见，滑块在右侧，文字"自主模式"清晰；
3. 轨道/滑块圆角边缘无裁剪，与"执行模式"标签、消息区无遮挡重叠；
4. 窗口缩放、消息增多时开关仍完整；
5. 点击与空格切换、无障碍标签、持久化行为与 6.0 一致；其他功能无回归。
