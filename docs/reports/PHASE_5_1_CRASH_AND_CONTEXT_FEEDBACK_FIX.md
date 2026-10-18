# 阶段 5.1 详细报告：NX 闪退根因定位修复与选中面反馈

日期：2026-10-16。分支：`recovery/pre-review-20261014-234252`（成果未提交，基线 `99f7d95`）。
状态术语见 [../PROJECT_STATUS.md](../PROJECT_STATUS.md)。

## 1. 故障与结论

**故障**：Phase 5 部署后，在 NX 中选中块体表面后发送消息（"在我选定的面上创建一个圆柱"），NX（ugraf.exe）直接闪退；且选中面在聊天窗口无任何反馈。

**定位结论（有实证，非推测）**：

- **崩溃阶段：B —— 发送消息时采集 NX 上下文**。尚未到达模型请求（C）、工具解析（D）、确认卡（E）或建模执行（F/G/H）。
- **直接根因**：`NxContextReader.DescribeFace` 对**平面**调用了 `UFEvalsf.AskFaceUvMinmax`（包装 `UF_EVALSF_ask_face_uv_minmax`，自由曲面/B-surface 求值器）。该 API 的前置条件要求 B-surface 面；对平面调用触发原生访问冲突：
  ```
  System.AccessViolationException: Attempted to read or write protected memory.
    at NXOpen.UF.UFEvalsf._AskFaceUvMinmax(IntPtr, Double[])
    at NXOpen.UF.UFEvalsf.AskFaceUvMinmax(IntPtr, Double[])
    at NXAI.Assistant.NxHost.NX2512.NxContextReader.DescribeFace(Part, Face)
    at NXAI.Assistant.NxHost.NX2512.NxContextReader.CaptureSelection(Part, ...)
    at NXAI.Assistant.NxHost.NX2512.NxContextReader.Capture()
    at NXAI.Assistant.NxHost.NX2512.NxHostBoot.<ShowChatWindow>b__0()
    at NXAI.Assistant.UI.ChatSessionController.StartSend(String)
    at NXAI.Assistant.UI.AssistantChatForm.SendCurrentInput()
    （WinForms TextBox 键盘消息循环 → NX UI 线程）
  ```
- **证据来源**：Windows 事件日志 `Application`，`.NET Runtime` 事件 **2026-10-16 12:57:09**，进程 `ugraf.exe`，CoreCLR 8.0.3126.42015（完整栈见上）。同日 11:44:21 的 dotnet.exe 事件为开发探针崩溃（nxprobe，IndexOutOfRange），与 NX 无关。10-14 23:48 事件早于 Phase 5 部署，无关。
- **第二缺陷（潜在错误几何，被崩溃掩盖）**：`UFModl.AskFaceProps` 的调用按假设签名 `(tag, param, point, normal, du, dv, duu, dvv, uvp)` 传参，而反射核验的真实托管签名为 `(face_id, param[2], point[3], u1[3], v1[3], u2[3], v2[3], unit_norm[3], radii[2])`——代码的 `normal` 实际绑定到 **u1**（一阶 u 导数向量），末参 `uvp` 实为 `radii[2]`。因各数组长度恰好兼容，不会崩溃但**法向会是错误向量**。两处调用点（NxContextReader、BlockToolExecutor）同病。
- **线程调度无过错**：完整栈证明上下文捕获同步运行在 NX UI 线程（TextBox 按键 → StartSend → provider → Capture），阶段 A-H 未发现跨线程 NX 调用。
- **选中面无反馈的原因**：Phase 5 设计为"发送时捕获快照"，本就不存在选择变化时的实时 UI 更新；且捕获崩溃发生在任何消息展示之前，用户完全看不到系统捕获了什么。两者叠加表现为"无反馈 + 一发就崩"。

## 2. 修复内容

1. **替换崩溃 API（两处）**：`NxContextReader.DescribeFace`、`BlockToolExecutor.ExecuteCreateCylinderOnSelectedFace` 改用 `ufSession.Modl.AskFaceUvMinmax(face.Tag, uv)`（包装 `UF_MODL_ask_face_uv_minmax`，uf_modl/global 通用面 API，元数据核实存在且 Tag 型）。全库不再调用 `UFEvalsf`。
2. **修正 AskFaceProps 参数序（两处）**：按反射核验的 9 参签名传 `(tag, param, point, u1, v1, u2, v2, unit_norm, radii)`，法向从 `unit_norm` 读取；`radii = new double[2]`。
3. **类头注释更正**：NxContextReader 头部 API 事实更新为反射核验的签名，并显式记录"UFEvalsf 是 B-surface 专用求值器、对平面会 AV"的教训，防止复发。
4. **阶段日志（新）**：`Core/PhaseLog.cs`——线程安全、绝不抛异常、512KB 上限、%TEMP%\NXAI_Assistant_phase.log；不记录密钥/请求体/部件路径。覆盖：上下文采集开始/结束/失败原因、选择数量、面几何读取成功/失败（含中心法向值）、建模执行目标解析、Builder 创建/Commit/验证/回滚/销毁。
5. **选中面反馈（两处）**：
   - 发送时：捕获快照后在聊天显示一行摘要（"NX 上下文：已选择 1 个对象（1 个面（planar））+ 面中心/法向"；空选、无部件、读取失败均如实显示，失败带原因）。该 Info 消息不进入模型请求。
   - 新增"刷新上下文"按钮（输入区，UI 线程执行采集）：随时查看当前捕获内容，无需发送消息。
6. **NxHostBoot 捕获失败可观测**：provider 的裸 `catch { return null; }` 改为记录失败原因后返回 null（语义不变）。

## 3. 未伪造的边界

- `UF_MODL_ask_face_uv_minmax` 对平面的适用性：签名已核实（NXOpen.UF.xml 元数据），"UF_MODL 通用面 API + 社区标准面中心配方"为强证据；**平面上的实际读数正确性列为实机验收第一项**（只读，见 §5），只读阶段若仍异常立即停止。
- 面法向"外法向"语义未在实机核对（AskFaceProps unit_norm 对实体的朝向）；确认卡显示法向数值，用户目视核对，实机验收含轴向核对项。
- 带孔/凹面 UV 中点可能不在面上的限制保持不变（无伪验证，确认卡目视核对 + 实机验收覆盖）。
- 普通 try/catch 不能拦截 AV；本修复不依赖 catch 防崩，而是消除违规调用本身。

## 4. 测试与构建（离线，本轮执行）

- **自动化测试**：**239 项 = 238 通过 / 0 失败 / 1 跳过**（新增 13：PhaseLogTests 2 + ContextFeedbackTests 11——摘要空选/单面含中心法向/多类型分组/无部件/错误态/Unavailable 追加、发送时 Info 摘要、摘要不进模型请求、无 provider 不发摘要、CaptureContextNow 刷新、provider null 不发摘要）。既有全部回归通过（含 block/cylinder/cone/sphere、RTF、placement 拒绝规则）。
- **Release 构建**：0 警告 0 错误；`git diff --check` 通过。
- **部署**：NX 未运行，build.ps1 部署成功（未签名——本机已证实可加载，签名步骤因无 dotnet_author 许可跳过，与 Phase 0 结论一致）。4 个 DLL SHA-256 与构建产物逐一比对一致。
- **离线测试通过 ≠ NX 实机通过**。崩溃是否根除以实机只读验收为准——**2026-10-16 用户实机验收：原崩溃路径未复现闪退（见 §5）**。

## 5. NX2512 实机验收（清单与结果）

验收清单（只读先行，任一步异常立即停止）：

1. 启动 NX，插件启动正常，不改部件。
2. **选中一个简单块体的平面 → 发送任意消息 → NX 不崩**（崩溃根除的直接检验），聊天出现"NX 上下文：已选择 1 个对象（1 个面（planar））"及面中心/法向。
3. 点"刷新上下文"→ 显示一致。
4. 空选发送 → 显示"当前没有选中对象"。
5. 查询类型/中心/法向核对合理（法向目视确认朝外）。
6. 隔离测试部件：面相对圆柱请求 → 确认卡参数正确 → **先取消**（无实体）→ 再确认（中心/底面/轴向正确）。
7. 目标失效（确认前切部件/换选择）→ 中止不误建。
8. 旧 global_origin 圆柱 + block/cone/sphere 回归；聊天渲染/复制无回归。

### 实机验收结果（用户执行，2026-10-16）

**用户报告通过**：原崩溃路径（第 2 步场景：选中平面 → 发送消息）实测正常，**未复现闪退**——本修复在 NX2512 实机得到确认。同轮报告通过：当前选择读取（平面 Face 识别与几何摘要）、"刷新上下文"入口、空选择处理、面相对圆柱确认卡显示与确认后实际创建检查（第 6 步的确认+创建部分）。

**未执行/未报告（保持待验收，不得视为通过）**：第 5 步法向朝向目视核对、第 6 步取消分支、第 7 步目标失效中止、第 8 步旧 global_origin 与 block/cone/sphere 回归、聊天渲染/复制回归；边、顶点、曲线、基准对象及其他对象类型识别、切 WCS/视图的上下文反映（完整清单见 [PHASE_5_CONTEXT_AWARENESS.md](PHASE_5_CONTEXT_AWARENESS.md) §6.2）。

**限制保留**：复杂面 UV 中点可能不在实际面上；倾斜圆柱精确轴向验证——§3 所列边界不变，不因本次简单平面场景通过自动视为已验证。

## 6. Git 与备份

- 成果未提交（未授权）；基线 `99f7d95`。
- 故障版本已存档：修复前备份 `D:\AI\NX_AI_ASSISTANT_backup_20261016_crash`（15MB，含未提交成果与未跟踪文件）；故障 DLL SHA-256：Core `7d92a390…7f31d02`、Entry `b6ddcf13…d69840ca`、NxHost.NX2512 `c109e370…c3be73014`、UI `d601e221…51005397`。
- 修复后部署 DLL SHA-256：Core `2008531f…4ba7b15`、Entry `0c0d454a…530351e`、NxHost.NX2512 `7d6e4516…1e23c37c`、UI `adbc1250…2951874`。
