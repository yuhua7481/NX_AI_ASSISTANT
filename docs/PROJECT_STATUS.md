# 项目当前状态（PROJECT_STATUS）

> 每次打开新编码会话时**首先阅读本文件**。每次任务结束后必须更新本文件（见文末"更新规则"）。
> 最后核验时间：2026-10-24（Phase 10.4 闭环：临时 datum 删除时机修复——移至 DoUpdate+后置验证成功后（原 Commit 后立即删除致未更新镜像失引用），1018 项测试通过、已部署（NxHost `697d1c6a…`，v2：临时 datum 保留为镜像支撑定义（layer 250+blank），DeleteFeature 级联毁镜像已证实并移除；菜单未变）；**实机自检复验未执行**。核验方式见各条目"证据"。)

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

**Phase 9.9 环形阵列 create_circular_pattern 已实现并部署，NX2512 实机验收通过（T1–T6，用户 2026-10-24 确认）**：feature/geometry 双模式（与线性阵列同语义）；axis 必填显式（plus_x/y/z 过原点绝对轴 / datum_axis=选中基准轴 / line_edge=选中直线边，Body 永不作轴、歧义结构化拒绝）；count=总实例含源（2–100）；完整圆周 pitch=360/count（首尾不重复）、部分圆弧 SpanAngle（末实例落在结束角）；种子解析复用 R1 选区优先；schema 无任何对象引用字段；后置判定纯函数。API 经 NXOpen.xml 直读+**编译验证**（RotationAxis=NXOpen.Axis 智能对象、UsePitchOptions 枚举、DatumAxis.Direction/.Origin 为标量）。全量 **939 项 = 938 通过 / 0 失败 / 1 跳过**（后经 9.9.1–9.9.3 三轮实机修复，最终 962 项通过、4 DLL 哈希一致——见 WORK_LOG）。**实机验收：T1–T6 全部通过（2026-10-24 用户确认）；T5/T6 逐项执行日志未单独记录。**

**Phase 9.7.1 实机问题修复 R1/R2 已部署并实机验收通过（2026-10-23）**：R1——线性阵列**几何体模式选区优先**：当前选区恰一个有效 Body 直接作种子（其他实体不否决），混选/多选/无可用 Body 结构化拒绝，空选区走既有记忆引用+唯一候选路径；复用 `CollectSelectionObjects`/`FindLiveBodyByRef`。R2——`target` 等未知字段严格拒绝（schema 未放宽）；本轮 (tool, 参数 JSON) 重复提交闸门（参数类失败字节相同即拒，目标解析失败允许前置变化后重试）；`SanitizeModelTextForDisplay` 清洗 finalText/错误卡（完整块剔除、未闭合剥除、纯协议换提示）。全量 914 项通过；实机验收：用户确认 R1/R2 通过。

**Phase 9.7 通用目标重定位 + Phase 9.8 双向原生矩形阵列已部署（2026-10-23）**：①新对话清空记忆后从特征树/实体清单重新定位目标——`ResolveBodyFromInventory`（单实体直用/唯一标准长方体自动解析/歧义拒绝）、`ResolveFeatureFromInventory`（按托管类型定位，**孔特征托管类名=NXOpen.Features.Hole**，已核实）、上下文快照新增 `FeatureTree` 清单（≤50 行引用/类型/显示名）；②`direction2`/`count2`/`pitch2`（同提供或同缺省、第二轴禁平行）→ `UseYDirectionToggle`+`YDirection`+`YSpacing` **一次 CommitFeature** 生成 count×count2 实例。全量 901 项通过。**双向阵列实机验收：用户报告已通过（含 4×3=12 场景）。**

**Phase 9.6 线性阵列双模式已部署（2026-10-23）**：`create_linear_pattern` 显式区分**阵列特征**（PatternFeatureBuilder：FeatureList=最近验证特征，实例证据=GetAllContainedFeatures）与**阵列几何体**（PatternGeometryBuilder：GeometryToPattern=最近验证 Body，验证=部件 body 数增长恰为副本数、源保留）；mode 必填显式（不默认 Feature）、direction 六轴向必填（永不猜测）、count 2–100、pitch≤1000mm；目标自动解析（记忆引用+活部件重验）；后置判定纯函数化。API 经**真实程序集反射**核实（PatternDefinition→RectangularDefinition→DistancePatternSpacing NCopies/PitchDistance）。

**Phase 9.5 自主几何感知（空选区自动目标解析）已部署（2026-10-23）**：空选区时圆角/倒角自动解析目标——"周边"仅对标准长方体解析为 12 条外棱边，非标准形状拒绝。

**Phase 9.4.1 圆角后置验证修复已部署（2026-10-23，待实机复验）**：`is EdgeBlend/is Chamfer` 托管类型判定 + `Feature.GetFaces()` 非空几何证据 + 先 Destroy 后回滚。**Phase 9.4 圆角/倒斜角、9.3 选点放置、9.2.x 四孔型均已部署，实机验收项见 WORK_LOG 各条目。**

**Phase 9.4 倒圆角与倒斜角（2026-10-23）**：create_fillet（EdgeBlendBuilder+链集半径表达式+读回门）/create_chamfer（ChamferBuilder SymmetricOffsets+FirstOffsetExp+读回门+Option 读回门）；目标边来自当前选区（EdgeDumb 规则、混入非边即拒、确认后重验证）。

**Phase 9.3 选定点放置（2026-10-23，待实机验收）**：四种标准孔支持 selected_point——宿主从当前选区解析唯一点（PointFeature/独立点/单点草图），孔心=点坐标不回退，放置面=唯一 UV 域包含的实体平面，多面交界拒绝。

**Phase 9.2.x 四种标准孔 + 中文映射定案（2026-10-23，沉头孔=Counterbored 圆柱、埋头孔=Countersink 锥形，待实机复验）**；Phase 9.2.1 Schema 模板修复已部署。

**Phase 9.2.3 中文孔型映射定案（2026-10-23）**：沉头孔=Counterbored（圆柱平底，ISO 4762）、埋头孔=Countersink（锥形，ISO 10642）、螺纹间隙孔=Simple、螺纹孔=ThreadedHole；四描述互斥认领。

**Phase 9.2.2 埋头孔语义修正（2026-10-23，已被 9.2.3 定案修正）**：执行路径正确、提交前 TypeOfHole 读回门保留；描述文案以 9.2.3 正式映射为准。

**Phase 9.2.1 Schema 实机错误已修复并部署（2026-10-23）**：用户实机报"工具 create_threaded_hole 的 JSON Schema 不是合法 JSON（内部错误）"——根因为四孔型 Schema 模板占位符双引号叠加（`[""size", …]` 非法 JSON），`ModelService` 每次请求统一解析全部 13 工具 Schema，任一失败即整体拒绝（与提示词无关）。已修复模板并新增 `ToolSchemaValidityTests` 5 项防回归。**待实机复验：重试"创建100*60*40块几何中心位于原点"确认错误消失、长方体真实创建。**

**Phase 9.2 四种标准孔（螺纹孔 / 普通螺钉间隙孔 / 埋头孔 / 沉头孔）已完成正式代码集成与离线验证（2026-10-22）**：四工具（create_threaded_hole / create_clearance_hole / create_counterbored_hole / create_countersunk_hole）注册+路由+确认卡+执行器全链路接入，标准数据取自 NX2512 安装的真实 XML（36 组合中 34 组唯一匹配；**Countersunk 沉头 M2/M1.6 在 ISO 10642 标准中不存在 → 结构化拒绝**——用户 2026-10-23 确认此为正确行为，无需扩展标准系列）。整合自已实机验证的探针（螺纹孔 9/9、三族 M6）。待实机验收：四工具×规格抽样在隔离部件中执行。**实机未测项一律不视为通过。**

**Phase 7.3 工具协议适配已完成离线验证（2026-10-17）：GLM 推理模型把工具调用写成 content 内标签块导致原始标记泄漏聊天的问题已修复（受限适配层，335 项测试通过，4 DLL 部署一致）。待实机验收：Phase 7.3 协议 7 项（报告 §5）+ Phase 7.2 拉伸 8 项 + Phase 7 四工具 14 项。Phase 6/6.1 已实机验收通过（16 项）。实机未测项一律不视为通过。**

**2026-10-20 追记**：具体失败案例"倾斜轴（用户选中的关联直线特征）+ XY 圆形草图 + 360° 旋转包络误拒回滚"已**实机验收通过**（自主模式，d20 圆 U45/V0，`REVOLVED(2)` 成功、无 verify FAIL、无回滚；证据来源为用户报告，详见 WORK_LOG 2026-10-20 条目）。该修复引入了 Core 级精确包络数学（`RevolveEnvelopeMath`，逐轴 Lipschitz 分支定界，替代四点采样与圆柱界）。**此项通过不改变 Phase 7 其余实机清单的待验收状态。**

**2026-10-20 第二次追记（布尔未知状态恢复）**：布尔未知状态曾被探针映射缺口永久阻塞（`boolean_operation` 无探针映射 → 全部"无法判断"硬停），已修复（commit `f55a574`）；随后发现映射修复本身存在**归因缺陷**（任意已有布尔特征都会被误当作本次未知操作的提交证据），已补充**归因快照**修复——探针按 `ProbeFeatureSnapshot`（BeginAttempt 前的只读特征身份快照）区分操作前已有特征与本次尝试新增特征，无法归因时安全停止。实机复验分两类：①普通差集（重启 NX 后隔离部件验证参数/执行/实体）；②未知状态恢复（同一会话内确实制造待核条目并触发宿主探针，未安全复现前不宣称通过）。详见 WORK_LOG 2026-10-20 两条目。

**2026-10-20 第三次追记（探针二次补充）**：归因快照修复仍有两个遗漏已修正——①**空快照语义**：非 null 空集合是有效基线（操作前无匹配特征），不再误判为"无法判断"；②**按操作类型归因**：待核布尔操作按结构化 `BooleanOperationType` 只匹配对应 NX 特征类型（unite→UNITE / subtract→SUBTRACT / intersect→INTERSECT，不再三合一），类型缺失时安全停止。实机验收分类（普通差集 vs 未知状态恢复两类）同前次追记。该轮全量 Release 测试实际为 **556 项 = 555 通过 / 0 失败 / 1 跳过**（布尔归因专项 21/21；TRX 归档于 `tests/NXAI.Assistant.Tests/TestResults/verify_eefd752_20261020/`，退出码 0），此前 WORK_LOG 记录的 555 项（554/0/1）系少计 1 项，已更正。

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
| **Phase 9.6 全量自动化测试** | **876 项 = 875 通过 / 0 失败 / 1 跳过**（新增 LinearPatternToolTests 26 项：双模式解析、13 类非法输入、注册 16 工具、后置判定 6 子条件、路由） | 当前工作区（Phase 9.2.x–9.6），2026-10-23 本轮执行 | `dotnet test tests\NXAI.Assistant.Tests -c Release`（7 分钟，退出码 0） |
| **Phase 9.6 Release 构建与部署** | **通过**：build.ps1 成功（0 警告 0 错误，NX 未运行）；4 DLL SHA-256 一致（Core `cd9afa12…87ab`、UI `858b74c7…f5a`、NxHost `99fa05d4…eed0`、Entry `6bcc7e97…69de`）；菜单未变（`319ee927…`） | 同上 | build.ps1 输出 + sha256sum 比对 |
| NX2512 实机验收（9.6 线性阵列两模式 + 前序各轮） | **未执行**：特征模式与几何体模式**分别**验收（不得互相代表） | 当前部署产物 | 待用户实测 |
| **Phase 9.5 全量自动化测试** | **850 项 = 849 通过 / 0 失败 / 1 跳过**（新增 EdgeTargetResolutionTests 12 项 + 控制器记忆生命周期 4 项：标准长方体→12 边、六类非标准形状拒绝、多实体歧义、失效引用拒用、成功才记录） | 当前工作区（Phase 9.2.x–9.5），2026-10-23 本轮执行 | `dotnet test tests\NXAI.Assistant.Tests -c Release`（7 分钟，退出码 0） |
| **Phase 9.5 Release 构建** | **通过**：build.ps1 构建段 0 警告 0 错误 | 同上 | build.ps1 输出 |
| **Phase 9.5 部署** | **完成**（NX 退出后重跑）：4 DLL SHA-256 与构建产物一致（Core `00915c81…db1a`、UI `6993df03…34d`、NxHost `33484dc5…c35b`、Entry `f4e794a2…370c`）；菜单字节级未变（`319ee927…`）；目录仅 4 DLL + selfcheck 种子 | 同上 | build.ps1 输出 + sha256sum 比对 |
| NX2512 实机验收（9.5 自动目标 + 前序各轮） | **未执行** | 待部署 | 待用户实测 |
| **Phase 9.4.1 全量自动化测试** | **834 项 = 833 通过 / 0 失败 / 1 跳过**（新增 EdgeFinishPostconditionTests 9 项：实机失败案例重现必须 PASS、五个失败子条件原因互异、源码含托管类型检查无字符串比较残留） | 当前工作区（Phase 9.2.x–9.4.1），2026-10-23 本轮执行 | `dotnet test tests\NXAI.Assistant.Tests -c Release`（6 分 40 秒，退出码 0） |
| **Phase 9.4.1 Release 构建与部署** | **通过**：build.ps1 成功（0 警告 0 错误，NX 未运行）；NxHost DLL 更新 `a7dc829b…c3ecc` 与构建产物一致，其余三 DLL 未变（Core `76d86b68…`、UI `4ed0497d…`、Entry `d884958f…`）；菜单字节级未变 | 同上 | build.ps1 输出 + sha256sum 比对 |
| **Phase 9.4 全量自动化测试** | **825 项 = 824 通过 / 0 失败 / 1 跳过**（新增 EdgeFinishToolContractTests 25 项：R2/C2 解析与换算、非法输入拒绝、注册 15 工具、Schema required 正确、描述互斥、双路由） | 当前工作区（Phase 9.2.x–9.4），2026-10-23 本轮执行 | `dotnet test tests\NXAI.Assistant.Tests -c Release`（6 分 40 秒，退出码 0） |
| **Phase 9.4 Release 构建与部署** | **通过**：build.ps1 成功（0 警告 0 错误，NX 未运行）；4 DLL SHA-256 一致（Core `76d86b68…`、UI `4ed0497d…`、NxHost `5fd3c6c1…`、Entry `e8ae1245…`）；菜单字节级未变（`319ee927…`） | 同上 | build.ps1 输出 + sha256sum 比对 |
| NX2512 实机验收（9.4 圆角/倒角 + 9.3 选点 + 9.2.x 孔型） | **未执行**：①选边"倒 R2 圆角"→确认卡 R2、特征 EDGE_BLEND、真实圆角；②选边"倒 C2 斜角"→特征 CHAMFER、对称 2mm；③取消/空选/选面等无效目标清晰拒绝；④失败回滚；⑤9.3 选点放置六项；⑥9.2.3 沉头/埋头两词分测 | 当前部署产物 | 待用户实测 |
| **Phase 9.3 全量自动化测试** | **800 项 = 799 通过 / 0 失败 / 1 跳过**（新增 12 项点放置：四族点模式解析、非法输入拒绝、Schema 广告、点→面几何规则离线数学、多面歧义；孔相关 119 项全通过） | 当前工作区（Phase 9.2.x + 9.3），2026-10-23 本轮执行 | `dotnet test tests\NXAI.Assistant.Tests -c Release`（6 分 40 秒，退出码 0） |
| **Phase 9.3 Release 构建与部署** | **通过**：build.ps1 成功（0 警告 0 错误，NX 未运行）；4 DLL SHA-256 一致（Core `66d7ef49…`、UI `070d8be6…`、NxHost `066cc0bb…`、Entry `37f8ec7f…`）；菜单字节级未变（`319ee927…`） | 同上 | build.ps1 输出 + sha256sum 比对 |
| NX2512 实机验收（Phase 9.3 选点放置 + 9.2.x 孔型） | **未执行**：①选三维点/草图点 →"创建M6螺纹孔"→ 孔心=点坐标、无螺纹表警告；②多点草图 → 拒绝不擅自挑选；③点在面外/边界 → 拒绝不回退面中心；④四孔型点路径抽查；⑤面中心模式回归；⑥"沉头孔/埋头孔"两词分测 | 当前部署产物 | 待用户实测 |
| **Phase 9.2.3 映射定案后全量测试** | **788 项 = 787 通过 / 0 失败 / 1 跳过**（语义守卫重写 53/53：Counterbored 独占"沉头孔"、Countersunk 独占"埋头孔"并拒绝仅"沉头孔"、Clearance/Threaded 认领、工具名→family→NX 孔型确定映射、无混合歧义文案） | 当前工作区（Phase 9.2 + 9.2.1–9.2.3），2026-10-23 本轮执行 | `dotnet test tests\NXAI.Assistant.Tests -c Release`（6 分 40 秒，退出码 0） |
| **Phase 9.2.3 Release 构建与部署** | **通过**：build.ps1 成功（0 警告 0 错误，NX 未运行）；4 DLL SHA-256 一致（Core `cca9aa12…`、UI `eb029638…`、NxHost `7adae5fa…`、Entry `c47438c0…`）；菜单文件字节级未变（`319ee927…`） | 同上 | build.ps1 输出 + sha256sum 比对 |
| **Phase 9.2.2 Release 构建与部署** | **通过**：build.ps1 成功（0 警告 0 错误，NX 未运行）；4 DLL SHA-256 与构建产物一致（Core `da79fa27…`、UI `d5b00b28…`、NxHost `844d35b5…`、Entry `e7f9f17c…`）；目录仅 4 DLL + selfcheck 种子 + startup 菜单 | 同上 | build.ps1 输出 + sha256sum 比对 |
| NX2512 实机验收（9.2.1+9.2.2 复验 + 四孔型） | **未执行**：①"创建100*60*40块…"长方体（Schema 复验）；②"创建一个 M8 埋头孔"→真实几何为 90° 锥面沉孔（CSinkDia 18.25）、成功消息报"锥形沉孔（Countersink）"、无螺纹表警告；③圆柱台阶孔路径独立抽查；④沉头 M2/M1.6 结构化拒绝；⑤取消/回滚/自主模式 | 当前部署产物 | 待用户实测 |
| **Phase 9.2.1 Release 构建与部署** | **通过**：build.ps1 成功（0 警告 0 错误）；4 DLL 部署 `deploy/nxai_custom/application`，SHA-256 与构建产物逐一一致（Core `227affa5…`、UI `51e0d823…`、NxHost `55363e15…`、Entry `47a4eaa5…`；NX 未运行）；目录仅 4 DLL + startup 菜单，无杂物 | 同上 | build.ps1 输出 + sha256sum 比对 |
| NX2512 实机验收（Phase 9.2.1 复验 + 9.2 四工具） | **未执行**：①重试"创建100*60*40块几何中心位于原点"确认 Schema 错误消失、长方体真实创建；②四工具×规格抽样（C2–C7，含沉头 M2/M1.6 结构化拒绝）；③确认卡/取消/回滚/自主模式 | 当前部署产物 | 待用户实测 |
| Phase 9.2 标准数据核查 | 34/36 组合唯一匹配；Countersunk M2/M1.6 无 ISO 记录（实现为拒绝）；螺纹 9/9、Simple 9/9、Counterbored 9/9 | 同上 | python 解析 NX 安装 XML（NX_Thread_Standard.xml 3279 行、NX_Screw_Clearance_Hole_Standard.xml 2690 行） |
| NX2512 实机验收（Phase 9.2 四工具） | **未执行**：四工具×规格抽样、沉头 M2/M1.6 拒绝提示、特征树核查（无"螺纹表未找到"警告）、确认卡、回滚、自主模式路径 | 待部署 | 待用户实测 |
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

**圆形阵列已完成收尾（2026-10-24）**：create_circular_pattern NX2512 实机验收 T1–T6 全部通过（用户确认）。无待办复测。

**Phase 10 镜像 create_mirror 已实现（2026-10-24，未部署、实机验收待执行）**：geometry（MirrorBodyBuilder，N 实体一个特征、原体保留）/feature（MirrorFeatureBuilder，历史保留）双模式；镜像面=选中的基准面**或普通平面面**（Face 引用直接进选择器，Plane 属性为 SelectDatumPlane/SelectISurface——编译验证）；选区角色分离（镜像面永不计入种子，歧义/非平面/空选区结构化拒绝，无默认面兜底）；schema 无对象引用字段；后置判定纯函数。全量 **981 项 = 980 通过 / 0 失败 / 1 跳过**。**实机验收方案 G1–G4/F1–F4/N1–N2 见 WORK_LOG Phase 10，未执行。已部署（2026-10-24，4 DLL 哈希一致）；自检几何体新增对象识别用例（caseE.recognition）辅助实机验证。**

**Phase 10.2 自检全覆盖（2026-10-24，已部署、实机自检未执行）**：NxGeometrySelfCheck 扩至 13 用例覆盖全部 18 注册工具（布尔三运算/四标准孔族/边缘 R2C2 真实选区/线性圆形阵列双模式/镜像三场景/负向回滚扫描）；真实 NX 选区驱动（RequestSelections/ClearGlobalSelectionList，反射验证）；报告首页新增注册工具覆盖数/负向计数/清理结果。全量 997 项=996/0/1。**待执行：NX 内完整自检 + 镜像 G/F/N 组。**

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
