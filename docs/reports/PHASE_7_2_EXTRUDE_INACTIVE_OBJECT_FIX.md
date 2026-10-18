# Phase 7.2 报告：修复 create_extrude"试图使用不活动的对象"错误

日期：2026-10-16　分支：`recovery/pre-review-20261014-234252`

## 1. 故障与诊断证据

用户请求"用拉伸命令绘制直径30高50的圆柱"：create_sketch 成功（SKETCH_000，圆 d30），create_extrude 失败。聊天显示"请求发生意外错误：试图使用不活动的对象"，未产生预期圆柱。

**诊断证据**（%TEMP% 日志，非推测）：

- `NXAI_Assistant_phase.log`：19h 会话 51:33 草图提交后**无任何 execute.extrude 行**（19h 拉伸在进入 UndoMark 后、commit 前失败）；20h 会话 06:23 草图提交、07:39 `execute.extrude commit done` → **随即 `execute.rollback ok`**。
- `NXAI_Assistant_operation_error.log`（20:07）：`Extrude postcondition failed: 实际包围盒 (-15,-15,0)–(15,15,50) 与预期 (-15,-15,-15)–(15,15,65) 不符`。
- NX 错误串溯源：`LOCALIZATION/simpl_chinese/libjam…txt` → `libjam: 'Attempt to use an object that is not alive' = '试图使用不活动的对象'`（JAM 层对象存活错误）。

## 2. 根因（两个独立缺陷）

**缺陷 A（20h 会话，实锤）：解析期望盒对圆算错——验证误判回滚了成功的拉伸。**
`ComputeProfileExtrudeAabb` 的 Arc 分支取"圆心±r 的三维盒角点"（z 也有 ±r），再沿拉伸方向扫掠：d30 圆在 XY 平面拉伸 50 得到期望盒 z∈[-15,65]——比真实圆柱（z∈[0,50]）两端各多出 ±15 的错误余量。NX 实际拉伸**成功且几何正确**（实测盒正是 -15,-15,0–15,15,50），被验证误判 → 回滚 → 报失败。极值点必须在**圆自身平面内**取（圆心±r·平面轴）。

**缺陷 B（19h 会话，日志时序推断 + API 语义核实）：Section 种子曲线存活问题。**
`CreateSectionFromSketch` 把草图曲线（`GetAllGeometry()[0]`）直接作为 `AddToSection` 的 seed。草图 Deactivate 后其内部曲线是被吸收（consumed）的对象；JAM "not alive" 错误即把**非存活包装对象**传给 API 所致——状态依赖（20h 相同代码路径 commit 成功、19h 失败），符合会话状态差异特征。19h 无 extrude 日志行正说明失败在 section/builder 阶段（当时该阶段无日志，属诊断盲区）。

## 3. 修复（仅 [FeatureToolExecutor.cs](../../src/NXAI.Assistant.NxHost.NX2512/FeatureToolExecutor.cs)）

1. **期望盒修正**：Arc 极值点改为圆平面内四点（圆心±r·xDir、±r·yDir；平面轴取自 `Conic.Matrix.Element` 的列——与 `TryGetSketchFrame` 相同的已核实 NXMatrix 映射）。矩形路径不变。
2. **Section 生命周期加固**：
   - 新增 `IsDead(NXObject)` 存活检查（Tag/IsOccurrence 探测），对所有传入对象先验；死对象 → 明确拒绝"草图曲线已失效，请重新创建"，不再把 NX 异常翻译成"意外错误"。
   - Section 构建前 `sketch.Activate`（曲线存活），完成后 `Deactivate(UpdateLevel.Model)` 还原；仅在草图原本非活动时激活。
3. **诊断日志补全**（PhaseLog）：`execute.extrude` 的 resolve begin/ok/FAIL（含 sketch id/name/IsActive/workPart）、frame、section FAIL、builder created、commit、verify ok/FAILED、rollback、builder destroyed；`execute.section` 的 begin/sketchFeature/curves+seed/liveness/activated/section created/addToSection ok/deactivated/FAIL（含类型+消息+首行栈）。异常仍记录到 operation_error.log，不再丢失阶段信息。
4. 未改动：确认/自主模式、授权清单、create_sketch、撤销回滚机制、其余工具。

## 4. 验证【本轮执行】

- Release 构建 0 警告 0 错误；`git diff --check` 通过。
- 全量测试 **321 项 = 320 通过 / 0 失败 / 1 跳过**（无回归；既有 create_sketch/create_cylinder/模式/聊天生命周期测试全部保持）。
- 部署：NX 未运行，NxHost.NX2512.dll 部署，SHA-256 与构建产物一致 `35769ba8ae5beca0…`（完整值经 sha256sum 核对）。

**测试边界声明**：`ComputeProfileExtrudeAabb`/`CreateSectionFromSketch` 需要 NX 会话，离线无法直接单测其几何/生命周期行为（已列入下方实机验收）；离线覆盖的是控制器层授权/取消/解析契约（既有 321 项）。

## 5. NX2512 实机验收（**全部待执行，不得视为通过**）

隔离测试部件：
1. 创建 d30 圆形草图（WCS XY）成功。
2. **下一条独立请求**拉伸该草图 50mm → 成功生成真实参数化 Extrude 特征。
3. 核验实体包围盒 ≈ 30×30×50、轴向沿草图法向（+Z）。
4. 复测"先创建草图，再立即拉伸"自然语言工作流（自主模式一次请求两步工具）。
5. 分别在确认模式与自主模式各验一次。
6. 失败/取消路径：无效草图引用、用户取消——不残留几何、不闪退。
7. %TEMP%\NXAI_Assistant_phase.log 中 `execute.section` 各阶段日志完整可查。
8. 不自动保存、部件路径未变。

## 6. 遗留与不确定性

- 缺陷 B 的修复（激活后再建 Section + 存活门禁）基于日志时序、JAM 错误语义与 NX 对象生命周期规则推断；**其实机有效性以 §5 第 2/4 项验收为准**。若复现，新增的 execute.section 阶段日志将直接定位失败阶段。
- 若 19h 失败另有他因（如部件切换），阶段日志会在下次复现时给出实证。
