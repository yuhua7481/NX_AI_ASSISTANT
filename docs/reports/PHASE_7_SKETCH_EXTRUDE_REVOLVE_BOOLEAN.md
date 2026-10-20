# Phase 7 报告：草图、拉伸、旋转体与布尔运算

日期：2026-10-16　分支：`recovery/pre-review-20261014-234252`

## 1. 交付范围

在现有类型化工具协议上新增四个参数化建模工具（复用 ToolRegistry、ChatSessionController、上下文捕获、执行模式、确认卡、NX UI 线程执行、UndoMark/回滚/Builder Destroy/后置验证全链路）：

| 工具 | 功能 | 结果引用（DetailsJson） |
|---|---|---|
| `create_sketch` | 真实 NX 参数化草图 + 单个封闭轮廓（矩形/圆）；平面=选中平面/基准面或绝对坐标 XY/XZ/YZ | `sketchRef`（JournalIdentifier） |
| `create_extrude` | 草图沿其平面法向（或反向）拉伸为实体 | `sketchRef` + `newBodyRef` |
| `create_revolve` | 草图绕显式轴旋转成体（选中直线/基准轴或绝对坐标轴），角度 (0,360] | `sketchRef` + `newBodyRef` + `axis` |
| `boolean_operation` | unite/subtract/intersect 两个受控引用的实体 | `resultBodyRef` |

自主模式允许清单从 4 个扩展到 8 个工具（同一封闭授权策略，未绕过）。

## 2. API 核验（全部针对本机 NX2512，非网络示例）

核验方式：`NXOpen.dll`/`NXOpen.UF.dll` 程序集元数据反射（临时工具，不入库）+ `NXBIN\managed\NXOpen.xml` 文档抽取。关键已核实项：

- `SketchCollection.CreateSketchInPlaceBuilder2(Sketch)`（NX11+，null=新建）；`SketchInPlaceBuilder.PlaneOption`（`Sketch.PlaneOption.ExistingPlane`）+ `PlaneReference`（类型 `Plane`；`PlaneOrFace` 已弃用）
- `PlaneCollection.CreatePlane(Point3d, Vector3d, UpdateOption)`（WCS 平面与选中面/基准面的构造）
- `Sketch.Activate(ViewReorient)` / `Deactivate(ViewReorient, UpdateLevel)`；`Sketch.AddGeometry(Curve, InferConstraintsOption.InferNoConstraints)`（不加约束，符合"不过约束"要求）；`Sketch.GetAllGeometry()`；`Sketch.Feature`；`Sketch.Orientation.Element`（Matrix3x3，列=局部轴）
- `CurveCollection.CreateLine(Point3d, Point3d)`、`CreateArc(center, xDir, yDir, r, 0, 2π)`（全圆）
- `Part.Sections.CreateSection()` + `ScRuleFactory.CreateRuleCurveFeature(Feature[])` + `Section.AddToSection(rules, seed, null, null, helpPoint, Mode.Create)`
- `ExtrudeBuilder`：Section/Direction(`NXOpen.Direction`)/Limits（`GeometricUtilities.Limits.StartExtend/EndExtend` → `Extend.Value.SetFormula(string)`）
- `RevolveBuilder.Axis`（`NXOpen.Axis`）；`AxisCollection.CreateAxis(Point3d, Vector3d, UpdateOption)`。**DatumAxis 不是 Axis**（继承 DisplayableObject）——从其 Direction/Origin 构造自由 Axis
- `BooleanBuilder.Operation`（`Feature+BooleanType.Unite/Subtract/Intersect`）、`Targets`（SelectBodyList）、`Tools`（SelectDisplayableObjectList；二者在 NX2512 标注过时但受支持，以 pragma 显式声明使用；ScCollector 变体需选择规则替换流程，无额外安全性）
- `Expression.SetFormula(string)`；`UFModl.AskBoundingBox(Tag, double[]&)`（包围盒验证）

**不可核实项（如实记录，未使用）**：`UFModl.AskMassProps3d`（`UF_MODL_ask_mass_props_3d` 包装）签名存在，但 47 元素结果数组中体积索引无法从本机文档核实——**未采用**，后置验证改为解析几何包围盒比对（见 §4）。

## 3. 设计要点

- **受控引用**：草图/实体引用 = NXHost 生成的 `JournalIdentifier`，经工具结果（DetailsJson 合并进 payload）返回给模型；执行器在当前工作部件中按引用解析，伪造/过期引用一律 `PreconditionsChanged` 拒绝。上下文选中的实体现在携带 `BodyRef`（NxBodyInfo 新增字段）。
- **局部坐标**：草图轮廓 `center_u/center_v` 沿执行器确定的平面局部 u/v 轴（选中面：法向+世界 X 投影；WCS 平面：右手系 XY(+Z)/XZ(−Y)/YZ(+X)），在确认卡中明确标注"草图局部坐标"。
- **旋转轴约束**：轴点必须位于草图平面内（≤0.01mm）、轴方向必须与平面平行，否则拒绝（不回退全局轴）。
- **确认卡**：四种操作都有专属确认卡文案（平面/轮廓/尺寸/局部中心/引用/轴/角度/布尔语义与工具体消耗说明）。
- **自主模式**：四工具进入封闭允许清单；分派阶段策略判定 + 执行前重查模式沿用 Phase 6 机制，无新授权路径。

## 4. 后置验证

- **草图**：草图集合计数+引用 diff、`GetAllGeometry` 曲线数（矩形 4 线 / 圆 1 弧）
- **拉伸**：体计数+1、新体身份 diff、世界包围盒与解析期望盒逐分量比对（从实际草图曲线顶点沿方向扫掠计算——草图内容与请求不符会被抓住；容差 0.01mm）
- **旋转**：体计数+1、新体 diff、旋转包络解析包围盒（轴向范围 + maxR·√(1−u²) 径向投影）。360° 为精确包络；**部分角度时为保守上界**（如实记录，实机验收核对形状）
- **布尔**：体计数 n→n−1、结果体定位、必要条件包含关系（unite ⊇ 目标 / subtract ⊆ 目标 / intersect ⊆ 两者）。**体积级精确比对未实现**（依赖未核实的 mass-props 索引）——必要条件验证强度如实声明，体积核对列入实机验收
- 全部操作：毫米门禁、UndoMark、失败回滚（含验证失败）、Builder Destroy、`%TEMP%` 错误日志、PhaseLog 阶段日志

## 5. 已知限制（v1 范围内，如实声明）

1. 草图轮廓：仅矩形/圆；无圆弧轮廓、约束求解、多轮廓、参考尺寸。
2. 选中面草图平面为**非关联**构造（面的法向+代表点），面参数化变更后草图不随动。
3. 拉伸：仅定距离、法向/反向；无对称/到面/偏置/薄壁。
4. 旋转：轴必须在草图平面内；部分角度验证为包络上界（非精确）。
5. 布尔：工具体默认消耗（NX 默认行为）；无 RetainTool/CopyTools 选项。
6. 每请求仍至多一个修改工具（现有回合上限不变）；多步骤需用户分步驱动。
7. `AskMassProps3d` 数组布局未核实而未用（体积级验证缺口，见 §4）。

## 6. 验证结果【本轮执行】

- Release 构建 0 警告 0 错误；`git diff --check` 通过。
- 全量测试 **314 项 = 313 通过 / 0 失败 / 1 跳过**（新增 40 项：四个解析器契约 25 + 控制器分派/模式 5 + 注册表/允许清单/广告更新既有断言；跳过项为需真实 NX 的既有验收测试）。
- 部署：NX 未运行，4 DLL SHA-256 与构建产物逐一一致（Core `5eddb99407a1bfe1…`、UI `fc1e608071d6239f…`、NxHost.NX2512 `e4bcaf9b7a06e07a…`、Entry `adbdb66c123cc661…`）。

## 7. NX2512 实机验收清单（**全部待执行，未测项不得视为通过**）

按隔离测试部件执行：
1. 矩形草图（WCS XY）：尺寸、封闭性、特征树中为真实草图。
2. 圆形草图（选中平面）：直径、定位（center_u/v 局部偏移）。
3. 取消草图确认：不新增特征。
4. 拉伸矩形草图：高度、方向（normal/reverse）、实体有效、AABB 与实际一致。
5. 旋转体：WCS 轴与选中直边轴各一次，核对轴、角度、形状。
6. 并集：核对目标/工具管理与结果体引用。
7. 差集：核对从目标体实际减去工具体体积。
8. 交集：核对结果正确。
9. 安全拒绝：非法轮廓尺寸、无效/过期草图引用、轴不在草图平面、目标=工具、片体输入、非相交实体。
10. 确认模式确认/取消路径；自主模式明确合法请求直接执行；歧义请求拒绝。
11. 失败路径无残留几何、无 NX 闪退。
12. 既有四基本体、圆台、选中面圆柱、上下文刷新、聊天渲染/复制、模式开关回归。
13. 无自动保存、部件路径未变。
14. （对照 §4/§5）布尔体积核对、部分角度旋转形状核对。

## 8. 遗留

1. §7 清单大部分仍待实机执行。**部分关闭（2026-10-20，证据来源：用户报告）**：§7 第 5 项中的"倾斜轴 360° 旋转"具体失败案例已实机验收通过——自主模式，WCS XY 平面 d20 圆（局部 U=45/V=0），用户实际选中的关联直线特征为轴，`REVOLVED(2)` 成功、无 verify FAIL、无回滚（修复记录见 WORK_LOG 2026-10-20 条目与 commit `12e39ea`；包络计算已替换为 `RevolveEnvelopeMath` 精确数学）。此项通过**不代表 §7 其余清单项已验收**。WCS 轴旋转、部分角度旋转（60°/270° 事实驱动验证）等其余旋转场景仍以待验收论。
2. 体积级后置验证（mass-props 索引核实后补充）。
3. 上一轮推送时 GitHub 连接失败；本轮提交后推送（若仍失败，保留本地提交并报告）。
