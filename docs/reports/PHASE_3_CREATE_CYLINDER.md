# 阶段 3 详细报告：create_cylinder 圆柱体建模工具

日期：2026-10-16。分支：`recovery/pre-review-20261014-234252`（成果为未提交工作区状态，基线为 Phase 2 检查点 `7cf7dce`）。
状态术语定义见 [../PROJECT_STATUS.md](../PROJECT_STATUS.md)。

## 1. 本阶段范围

**做了**：新增第二个受控建模工具 `create_cylinder`——Core 契约（复用现有注册表/解析/单位换算机制，无平行框架）、NxHost 圆柱执行与执行后验证、协调器与确认卡扩展、离线测试、构建部署核验。

**没做**（阶段边界）：真实 NX2512 实机验收（待用户执行，见 §7）、第三个建模工具、本地模型接入、聊天 UI 可视化重构、Git 提交/推送（任务书未授权）。

## 2. NXOpen API 事实核验（实现前完成，非凭方法名猜测）

- `NXOpen.Features.CylinderBuilder`（XML 文档 `NXBIN\managed\NXOpen.xml` + `managed_core/NXOpen.dll` 元数据探测）：
  - 工厂 `FeatureCollection.CreateCylinderBuilder(Feature)`（NX5.0.0+，license solid_modeling）。
  - `Type = AxisDiameterAndHeight`（枚举成员，另一选项 ArcAndHeight 未用）。
  - `Diameter`/`Height` 为 `Expression`（文档默认值：毫米部件 50/100）→ 用 `SetFormula("R" 格式不变文化)` 设值，与 block 的表达式字符串约定一致。
  - `Origin` 为 `Point3d`（setter 实测存在）→ 底面圆心。
  - `Direction` 为 `Vector3d`（builder 默认 +Z，`ReverseDirection=false`）→ **实现保持 builder 默认，模型不能自选轴向**。
- 结论：默认轴向 +Z、origin 为底面圆心的坐标约定与任务书一致，按此实现。

## 3. 工具契约（Core 层，复用现有机制）

| 文件 | 内容 |
|---|---|
| `CreateCylinderSchema.cs`（新增） | `create_cylinder` schema（required: diameter/height/unit；可选 origin_x/y/z；`additionalProperties:false`）与描述 |
| `CreateCylinderTool.cs`（新增） | `CreateCylinderParameters` record + `CreateCylinderRequestParser`（复用 `GeometryToolJson` 共享助手；几何界限复用 `BlockRequestValidator.ValidateWorkingPartRequest`，直径同时约束 X/Y 包围盒，未放宽任何边界） |
| `GeometryToolJson.cs`（新增） | 从 block 解析器抽出的共享助手：单位表/换算、有限数字读取、未知字段拒绝（block 与 cylinder 共用，无第二套框架） |
| `CreateBlockTool.cs`（修改） | 注册表现在注册两个工具（block + cylinder） |
| `NxToolExecutor.cs`（修改） | 接口增加 `ExecuteCreateCylinder` |

**参数约定**：`diameter`/`height` 有限正数，`unit` 必填（mm/cm/m/inch → 换算毫米），`origin` 可选默认 (0,0,0)（毫米）。模型不能提供文件路径（`save_path`/`file_path` 等一律按未知字段拒绝）或任意代码。

## 4. NXOpen 执行与验证（NxHost 层）

`BlockToolExecutor.ExecuteCreateCylinder`（新增，同一执行器类，同一安全骨架）：

- 前置：参数验证 → 无工作部件拒绝 → **部件与确认时不符拒绝** → 非毫米部件拒绝（与 block 完全一致）。
- 执行：UndoMark "NXAI 创建圆柱体" → `CylinderBuilder`（AxisDiameterAndHeight + SetFormula + Origin）→ `CommitFeature` + `DoUpdate` → finally `Destroy()`。
- 回滚：异常与验证不过两条路径都 `UndoToMark`（回滚名"NXAI 创建圆柱体"）并报告恢复结果。
- **执行后验证**（真实 NXOpen，不用模型自述）：实体计数差 =1 → JournalIdentifier 差集定位新实体（避免把已有实体误判为本次结果）→ `UF_MODL_ask_bounding_box` 读包围盒：**X/Y 尺寸 = 直径、Z 尺寸 = 高度**（+Z 轴圆柱的轴对齐包围盒恰为 [d,d,h]），容差 0.01 mm（与 block 同一 `DimensionToleranceMm`）。体积校验未加（`MeasureManager` 路径在 NX 内的稳定性未经实机验证，不引入不确定的估算——按任务书要求宁可少验不引入不可靠数据）。
- 不修改 `BlockFeatureCreator.CreateBlock`；不调用 SaveAs/自动保存/重命名（grep 复核）。

## 5. 确认 UI 与协调（UI 层，复用现有机制）

- `ToolConfirmationRequest` 重构为多工具参数容器（`ParametersKind` + `ForBlock`/`ForCylinder` 工厂）——确认卡显示逻辑按类型分支，未重构整个界面。
- 确认卡（`ShowToolConfirmationAsync`）：操作名"创建圆柱体（轴向 +Z）"、直径、高、原点（底面圆心）、目标部件（现场读取），默认焦点"否"；block 显示保持不变。
- `ProcessToolCallAsync`：注册表校验 → 按工具名选择解析器 → 类型化确认 → 单次执行（block 或 cylinder）→ 结构化结果回填。回合上限、tool_call_id 关联、Detach 防线全部复用，未改协议层。
- 一轮请求仍至多一次建模执行；混合工具序列（圆柱确认执行后最终回合再请求 block）由回合上限拒绝（测试固化）。

## 6. 测试与构建证据

- **Release 构建**：0 警告 0 错误（2026-10-16 本轮执行）。
- **自动化测试**：**165 项 = 164 通过 / 0 失败 / 1 跳过**（2026-10-16 本轮执行）。跳过项：需真实 NX 的 `CreateBlock_ValidParameters_SucceedsInNX`（按设计跳过）。
- 新增测试 21 项：`CreateCylinderToolContractTests` 16 项（注册双工具/schema/单位换算/各类拒绝/路径字段拒绝）+ 协调器圆柱用例 5 项（确认执行一次/取消零执行/非法参数拒绝/file_path 拒绝/混合工具不双执行）。
- 旧测试适配 2 处断言（注册表 1→2 工具；纯文本回合广告 1→2 tools），`create_block` 全部既有测试保持通过；纯文本聊天/取消/窗口关闭/新建对话测试保持通过。
- `git diff --check` 通过；部署 4 DLL SHA-256 与 Release 一致；`build.ps1` 不触碰用户配置。
- NX 外无法验证的 `CylinderBuilder` 真实行为（CommitFeature、SetFormula、包围盒读数）在离线测试中不覆盖，列为实机验收项，未伪造。

## 7. 尚需 NX2512 实机验证（九步验收，待用户执行一次性测试部件）

1. 自然语言请求创建圆柱 → 确认卡显示直径/高/单位/原点(+Z 轴)。
2. 取消 → 无新增实体。
3. 重新请求并确认 → 实体出现。
4. 实测几何：直径（X/Y 包围盒）与高（Z）一致。
5. 部件路径未变、未自动保存。
6. 非法尺寸请求执行前被拒绝。
7. 失败后再做有效圆柱，系统正常。
8. 重试 `create_block`，旧工具无回归。
9. （如可能）验证 `CylinderBuilder.SetFormula` 表达式与撤销列表行为正常。

## 8. Git 状态（未提交，任务书未授权 Git 操作）

- 基线：`7cf7dce`（Phase 2 检查点，已推送）。
- 本阶段修改：`CreateBlockTool.cs`、`NxToolExecutor.cs`、`BlockToolExecutor.cs`、`ChatSessionController.cs`、`AssistantChatForm.cs`、`CreateBlockToolContractTests.cs`、`ChatSessionControllerToolTests.cs`。
- 新增：`CreateCylinderSchema.cs`、`CreateCylinderTool.cs`、`GeometryToolJson.cs`（Core）、`CreateCylinderToolContractTests.cs`、本报告。
- 远端：`git ls-remote` 多次尝试均连接失败（GitHub 不可达）；本地 HEAD = `7cf7dce` 与最后已知远端一致。**未同步远端，不虚称已同步。**
