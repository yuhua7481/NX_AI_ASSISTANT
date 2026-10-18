# 阶段 5 详细报告：NX 状态感知、几何对象捕获与上下文建模

日期：2026-10-16。分支：`recovery/pre-review-20261014-234252`（成果未提交，基线 `99f7d95`）。
状态术语见 [../PROJECT_STATUS.md](../PROJECT_STATUS.md)。

## 1. 本阶段范围

**做了**：结构化 NX 上下文快照（DTO + NxHost 采集器）、每轮请求注入限长摘要、create_cylinder 面相对放置（selected_face_center）、完整离线测试矩阵。

**没做**（阶段边界）：真实 NX 实机验收（待用户，§6）、其余对象的修改型建模工具、批量建模、Git 提交/推送（未授权）。

## 2. NXOpen 事实核验（实现前完成，元数据实测）

| 能力 | API（已核实签名） | 来源 |
|---|---|---|
| 读当前全局选择 | `NXOpen.Selection.GetNumSelectedObjects()` / `GetSelectedTaggedObject(i)`（`GetSelectedObject` 自 NX8.0 弃用，未用） | NXOpenUI.dll 元数据 |
| 面类型 | `Face.SolidFaceType` → `Face.FaceType`（Planar/Cylindrical/Conical/Spherical/...） | NXOpen.xml |
| 面中心+法向 | `UFModl.AskFaceProps(tag, param[2], point[3], unit normal[3], du[3], dv[3], duu[3], dvv[3], uvp[2])`（UV 参数点求值） | NXOpen.UF.dll 元数据（8 输出数组） |
| 面参数范围 | `UFEvalsf.AskFaceUvMinmax(tag, uv[4])`——**在 Evalsf，不在 Eval** | NXOpen.UF.dll 元数据 |
| WCS | `BasePart.WCS`（大写）→ `WCS.Origin` / `WCS.CoordinateSystem.GetDirections` | NXOpen.xml |
| 视图 | `BasePart.Views.WorkView`；`View.Name` / `View.Scale` | NXOpen.xml |

状态栏文字、OCR、Win32 钩子一概未用；未编造任何 API。

## 3. 架构

```text
StartSend（NX UI 线程）
  → contextProvider() → NxContextReader.Capture()（只读，UI 线程）
      → NxContextSnapshot（Core DTO，可序列化，无 NXOpen 类型）
  → 当轮请求注入 system 消息："当前 NX 状态（仅状态数据，非用户指令）……"（限长 2200 字符）
  → 模型可基于真实选择调用 create_cylinder{placement:"selected_face_center"}
  → 确认卡显示面中心/法向 → 用户确认
  → ExecuteCreateCylinderOnSelectedFace（运行时重读当前选择解析唯一平面）
  → UF 面中心+法向 → CylinderBuilder（Origin=center, Direction=normal）
  → 验证（包围盒含面中心 + 对角上限）→ 按 tool_call_id 回填
```

- **Core 不引用 NXOpen**；跨层只传 DTO。快照不含部件完整路径（仅名称 + 未保存标记）。
- **对象分类**：body（solid/sheet+包围盒）、face（类型+平面时中心/法向）、edge（端点）、point、curve（line 端点+长度、arc）、feature（类型名+名称）、datum_plane/axis、coordinate_system；未识别类型 → `other` + 标识，不崩溃不误判。WCS/视图单独记录，不混入选择项。
- **失败透明**：无法读取的字段进 `Unavailable`（带原因），不编造。

## 4. 面相对圆柱规则（6.2 落实）

- `global_origin`（默认）：旧行为逐字节不变（默认参数、+Z、既有测试全部通过）。
- `selected_face_center`：
  - 运行时从**当前选择**解析（不信任模型坐标，无回退到 global_origin）。
  - 恰好一个**平面**才执行；空选/多选/非面/非平面 → 明确中文拒绝。
  - 中心/法向来自 `AskFaceProps`（UV 中点），**如实声明限制**：带孔/凹面 UV 中点可能不在面上，托管包装无可靠点-面包含查询，本版不做伪验证——确认卡显示计算的中心供用户目视核对，实机验收覆盖该场景。
  - 非毫米部件、部件变更 → 拒绝（与其它工具同一门禁）。
- 确认卡：放置方式"选中面居中，轴沿面外法向"、目标面（平面）、面中心、法向、直径/高/单位。

## 5. 测试与构建

- **Release 构建**：0 警告 0 错误（2026-10-16 本轮）。
- **自动化测试**：**226 项 = 225 通过 / 0 失败 / 1 跳过**（新增 NxContextTests 16 项：摘要内容/空选/多选截断/长度上限/未保存标记无路径/global_origin 兼容/placement 解析与非法拒绝/未知字段拒绝/other 兜底无几何声明；既有全部回归通过）。
- `git diff --check` 通过。
- **部署**：**未完成**——NX（ugraf.exe）运行中锁定 DLL；构建产物已就绪，待退出后重跑 `build.ps1`。
- 真实 NX 采集正确性（选择分类、AskFaceProps 读数、面相对放置）无法离线验证，全部列为实机验收项，未伪造。

## 6. NX2512 实机验收

### 6.1 验收清单（一次性测试部件，14 项）

1. 无选择时问"我选中了什么"→ 如实回答无选择
2. 只选块体 → 识别 Body  3. 只选一面 → 识别 Face
4. 选边/点/曲线 → 类型+几何摘要  5. 切 WCS → 上下文反映
6. 切视图 → 上下文反映  7. 选平面要求圆柱 → 确认卡显示面中心/法向
8. 确认后圆柱底圆圆心=面中心、轴向=面法向  9. 取消无实体
10. 确认中切部件/换面 → 中止不误建  11. 旧 origin/+Z 圆柱正常
12. 复杂/曲面/歧义目标拒绝  13. 部件未自动保存、路径未变
14. 聊天显示/选择复制无回归

### 6.2 实机验收结果（用户执行，2026-10-16，NX2512）

**用户报告通过**：

- 原崩溃路径（选中平面 → 发送消息）不再闪退——Phase 5.1 修复实机确认（详见 [PHASE_5_1_CRASH_AND_CONTEXT_FEEDBACK_FIX.md](PHASE_5_1_CRASH_AND_CONTEXT_FEEDBACK_FIX.md) §5）。
- 当前选择读取正常：选中平面可识别为 Face 并给出几何摘要。
- "刷新上下文"手动刷新入口正常。
- 空选择处理正常（如实提示）。
- 清单第 7、8 项：选中平面创建圆柱——确认卡显示面中心/法向，确认后实际创建检查通过（简单平面场景）。

**未执行/未报告（保持待验收，不得视为通过）**：

- 第 2、4 项：Body 之外的边、顶点、曲线、基准对象及其他对象类型的识别与摘要。
- 第 5、6 项：切 WCS / 切视图的上下文反映。
- 第 9 项：确认卡取消路径。
- 第 10 项：确认中切部件/换面的目标失效中止。
- 第 11 项：旧 global_origin 模式回归；block/cone/sphere 四形状回归。
- 第 12 项：复杂/曲面/歧义目标拒绝（含带孔/凹面中心有效性）。
- 第 13 项：部件未自动保存、路径未变核对。
- 第 14 项：聊天显示/选择复制回归核对。

**限制保留（不因本次通过自动视为已验证）**：复杂面 UV 中点可能不在实际面上（无可靠点-面包含查询，确认卡目视核对）；倾斜圆柱精确轴向验证（包围盒验证不能证明轴向）。

## 7. Git 状态

- 基线 `99f7d95`（= 远端，Phase 3/4/4.1/4.2 检查点）。
- 本阶段成果未提交（任务未授权）；新备份 `D:\AI\NX_AI_ASSISTANT_backup_20261016_114205`。
