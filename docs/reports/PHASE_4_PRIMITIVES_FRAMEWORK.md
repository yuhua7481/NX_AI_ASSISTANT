# 阶段 4 详细报告：四种基本体统一协调创建框架

日期：2026-10-16。分支：`recovery/pre-review-20261014-234252`（成果为未提交工作区状态，基线 `7cf7dce`）。
状态术语见 [../PROJECT_STATUS.md](../PROJECT_STATUS.md)。

## 1. 本阶段范围

**做了**：四种基本体（block/cylinder/cone/sphere）统一协调创建框架——复用现有 ToolRegistry、参数解析、INxToolExecutor、ToolConfirmationFlow、ChatSessionController 机制（无平行框架、无 shape 字符串分发）；新增圆锥与球体；四种形状共用确认流、安全边界与回滚验证；离线测试与构建核验。

**没做**（阶段边界）：NX2512 实机验收（待用户执行，§7）；第五种形状；批量多实体创建（未来功能）；Git 提交/推送（未授权）。

## 2. NXOpen 事实核验（实现前完成）

- **ConeBuilder**（`managed/NXOpen.xml` + `managed_core` 元数据）：工厂 `CreateConeBuilder(Cone)`；类型 `DiametersAndHeight`（builder 默认）；`BaseDiameter`/`Height`/`TopDiameter` 均为 Expression，**文档默认 `TopDiameter.Value = 0`**——即“上端直径 0”是该类型受支持的合法值，产生真圆锥（非圆台）。实现采用该组合并显式 `SetFormula(0)`，从未把无效直径丢给 NX 后谎报成功。
- **放置**：ConeBuilder 无 `Origin` 属性；底面圆心由 `Axis` 决定 → `workPart.Axes.CreateAxis(Point, Direction, UpdateOption)`（签名核实），Point=`CreatePoint(Point3d)`，Direction=`CreateDirection(Point3d, Vector3d, UpdateOption)`（签名核实）。方向固定 +Z。
- **SphereBuilder**：工厂 `CreateSphereBuilder(Sphere)`；类型 `CenterPointAndDiameter`（builder 默认）；`CenterPoint` 为 `NXOpen.Point`（经 `PointCollection.CreatePoint(Point3d)` 创建）；`Diameter` 为 Expression。真实球形实体 API，非网格近似。

## 3. 统一协调架构

```text
ToolRegistry（4 工具注册）
  → ModelService tool_calls → ChatSessionController.ProcessToolCallAsync
      → 按工具名选择类型化解析器（block/cylinder/cone/sphere 各自的 parser）
      → ToolConfirmationRequest.ForXxx（ParametersKind 分型）
      → 确认卡（AssistantChatForm，按 kind 显示参数）
      → INxToolExecutor.ExecuteCreateXxx（类型化参数，switch 分派）
      → BlockToolExecutor 各形状独立实现（共享前置/回滚/日志骨架）
      → 形状专属后置验证 → 结构化结果按 tool_call_id 回填
```

- 每种形状拥有独立参数解析、NXOpen 构造与后置条件验证；类型检查不被绕过。
- 共性下沉：`GeometryToolJson`（单位/有限数/未知字段）、`TryResolveWorkPart`（会话/部件/确认一致性/毫米门禁）、`Rollback`/`LogFailure`（回滚与日志）、`VerificationResult`/包围盒验证骨架。
- 回合语义不变：每轮至多一次确认执行、2 回合上限、最终回合不广告 tools、混合工具序列被上限拒绝。

## 4. 四形状参数与验证语义（毫米归一）

| 工具 | 参数 | 原点语义 | 包围盒验证 |
|---|---|---|---|
| create_block | length/width/height/unit (+origin) | 角点 | [L, W, H]（不变） |
| create_cylinder | diameter/height/unit (+origin) | 底面圆心，轴 +Z（不变） | [d, d, h]（不变） |
| create_cone | base_diameter/height/unit (+origin) | 底面圆心，轴 +Z，锥顶朝上 | [d, d, h] |
| create_sphere | diameter/unit (+origin) | 球心 | [d, d, d] |

- 界限沿用 `BlockRequestValidator`（±1e9 原点、≤1e6 尺寸、有限、正数），未放宽；球体直径同时约束三维包围盒，圆锥底面直径约束 X/Y。
- 模型不能提供文件路径（`save_path` 等按未知字段拒绝）或自选轴向。
- **形状真实性**：包围盒无法区分“圆锥 vs 圆柱”“球 vs 立方体”。可信度来自 (a) 使用经核实的真圆锥/真球 API（TopDiameter=0 是文档默认值；SphereBuilder 是真实体 API），(b) 撤销名与确认卡明确标注形状，(c) **实机验收中用户目视核对形状**（§7 第 3/4 项）。体积校验（MeasureManager）未加——实机稳定性未验证，不引入不可靠估算。

## 5. 修改文件清单

**修改（10）**：`CreateBlockTool.cs`（注册 4 工具）、`NxToolExecutor.cs`（接口 +cone/sphere）、`BlockToolExecutor.cs`（+ExecuteCreateCone/ExecuteCreateSphere + TryResolveWorkPart 公共化 + VerifyConeCreated/VerifySphereCreated + Rollback/LogFailure 参数化）、`ToolConfirmationFlow.cs`（ForCone/ForSphere）、`ChatSessionController.cs`（四工具分派）、`AssistantChatForm.cs`（确认卡四分支）、`CreateBlockToolContractTests.cs`、`CreateCylinderToolContractTests.cs`、`ChatSessionControllerToolTests.cs`、deploy 4 DLL。

**新增（5+1 更正）**：Core `CreateConeTool.cs`、`CreateConeSchema.cs`、`CreateSphereTool.cs`、`CreateSphereSchema.cs`；本报告。**更正**：初稿声称新增了 `CreateConeToolContractTests.cs`（16 项），该文件实际未创建（登记错误）；Phase 4.1 已补建（13 项，含圆台用例）。

## 6. 自动化测试与构建

- **Release 构建**：0 警告 0 错误（2026-10-16 本轮执行）。
- **自动化测试**：**170 项 = 169 通过 / 0 失败 / 1 跳过**（2026-10-16 本轮执行；跳过项为需真实 NX 的旧验收测试）。新增：cone 契约 16 项 + 协调器 cone/sphere 用例 5 项；既有 block/cylinder 测试全部保持通过（注册表断言 2→4 已同步）。
- `git diff --check` 通过。
- **部署**：本轮部署复制失败——**NX（ugraf.exe）正在运行锁定 DLL**；构建成功，部署待用户完全退出 NX 后重跑 `build.ps1`（构建产物已就绪，仅复制未完成）。

## 7. 实机验收（待用户执行，一次性测试部件，十项）

1. 长方体创建和取消 2. 圆柱体创建和取消 3. **圆锥确认创建并目视核对真锥体（非圆台/圆柱）** 4. **球体确认创建并核对三维尺寸与球形** 5. 各形状非法参数拒绝 6. 各形状失败后再建正常 7. 确认期间切换部件中止 8. 所有形状创建后路径未变未自动保存 9. block/cylinder 无回归 10. NX 退出重开后插件正常

## 8. Git 状态

- 基线 `7cf7dce`（= 远端，Phase 2/3 检查点已推送；本轮 ls-remote 核验成功）。
- 本阶段全部成果**未提交未推送**（任务书未授权 Git 操作）；修改 10 + 新增 6 文件。
- 新增仓库外备份：`D:\AI\NX_AI_ASSISTANT_backup_20261016_092605`（含 Phase 3 未提交成果快照）。

## 9. Phase 4.1 增量（2026-10-16）：圆台支持 + Markdown 显示

1. **create_cone 可选 top_diameter**：省略/0 = 标准圆锥（原行为不变）；0 < top < base = 圆台（真实截头锥体，ConeBuilder DiametersAndHeight 路径不变，SetFormula 设实际值）；负值/≥底面/NaN/Infinity/越界拒绝并返回中文错误。确认卡与结果消息按形状区分（圆台显示底/顶/高三径）。**验证范围如实说明**：包围盒无法区分锥/台/柱，真实形状依赖 API 正确性（TopDiameter 是 Expression 直通）与实机目视核对；NX 内程序化形状校验成本高且稳定性未验证，未实现，不伪造。
2. **聊天 Markdown 显示**：`ChatMarkdownParser`（`**` 成对且内容首尾非空格才粗体——CommonMark flanking 规则；`- `/`* ` 列表；空行分段；CRLF 归一）+ `ChatMarkdownRenderer`（行内粗体真实生效：FlowLayoutPanel + 逐 run Label）。纯显示层：原始文本留在 ChatLog（复制/tooltip/模型历史不变），不解析 HTML/脚本，未改聊天 UI 布局与请求流程。
3. 测试：**197 项 = 196/0/1**（新增 cone 契约 13 + 圆台协调器 2 + markdown 解析 10）。
4. 部署：NX 未运行，部署成功，4 DLL SHA-256 一致。

## 10. Phase 4.2 增量（2026-10-16）：聊天正文改只读 RichTextBox

1. **问题**：4.1 的 FlowLayoutPanel + 多 Label 拼接在实际运行中文字片段缺失/错位/挤压（行内超宽 spacer 在限宽容器中溢出），且 Label 不可选择。
2. **修复**：正文改只读 RichTextBox + 程序 RTF（`ChatRtfBuilder`：粗体/列表/段落/中文 \u 转义/花括号转义防注入）；ContentsResized 高度自适应；右键菜单"复制/全选/复制整条消息"；Ctrl+C 原生。删除 Label 拼接渲染器。
3. 测试：**210 项 = 209/0/1**（新增 RTF 生成器 13 项：文本往返一致、转义、注入阻断、列表/粗体结构）。
4. 部署：NX 未运行，4 DLL SHA-256 一致。
5. **手动验收待执行**：RichTextBox 实际显示、选择、复制手感须在 NX2512 实测（八步清单），本轮未声称通过。
