# 全功能综合验收报告（2026-10-17）

分支：`recovery/pre-review-20261014-234252`　本地 HEAD：`412b1ad`（Phase 8.2）

## 一、功能清单（以当前代码为准）

| # | 工具/功能 | 注册名 | 源码 | 离线测试 | NX 实机 | 备注 |
|---|---|---|---|---|---|---|
| 1 | 长方体 | `create_block` | ✓ | ✓ 合同+执行器 | ✓ 用户报告（八项） | — |
| 2 | 圆柱 | `create_cylinder` | ✓ | ✓ | ✓（选中面模式实机通过） | — |
| 3 | 圆锥/圆台 | `create_cone` | ✓ | ✓ | ✓（用户报告） | top_diameter=0 圆锥 / >0 圆台 |
| 4 | 球体 | `create_sphere` | ✓ | ✓ | ✓（用户报告） | — |
| 5 | 草图 | `create_sketch` | ✓ | ✓ 合同 | 部分（XY 实机 ✓；XZ 待复验） | 8.2 坐标系修复后待复验 |
| 6 | 拉伸 | `create_extrude` | ✓ | ✓ 合同 | 部分（XY 链路 ✓） | 7.2 验证盒修复+7.4 存活门禁 |
| 7 | 旋转体 | `create_revolve` | ✓ | ✓ 合同 | **待复验**（caseB 随 8.2 修复） | IsDead 门禁已加 |
| 8 | 布尔 | `boolean_operation` | ✓ | ✓ 合同 | **未实机** | 三运算 |
| 9 | 双执行模式 | — | ✓ | ✓ 22 项 | ✓ 16 项实机通过 | 确认/自主+持久化 |
| 10 | 结构化计划 | ModelingPlan | ✓ | ✓ 14 项 | **未实机**（依赖场景 A/B） | 串行协调器 |
| 11 | 协议适配 | ToolTagProtocol | ✓ | ✓ 31 项 | 部分（7.3.x 用户实测到 NS 阶段） | 别名表封闭 |
| 12 | NX 几何自检 | NxGeometrySelfCheck | ✓ | 部分（模板发现 3 项） | **部分**（首轮 3/4 ✓，caseB 修复后待复验） | 种子副本方案 |
| 13 | 上下文捕获 | NxContextReader | ✓ | ✓ 16 项 | 部分（面识别 ✓） | Sketch case 已加 |
| 14 | 聊天 UI | AssistantChatForm | ✓ | ✓ 20 项 | ✓（选择/复制/渲染用户实测） | 7.1 生命周期修复 |
| 15 | 模型服务 | ModelService | ✓ | ✓ 35 项 | ✓（实机在用） | DPAPI/URL 规范化 |

## 二、本次实际执行的验证（不得沿用旧数据）

### 2.1 构建
- 命令：`dotnet build NXAIAssistant.sln -c Release --nologo`
- 退出码 0；**0 警告 / 0 错误**

### 2.2 全量自动化测试
- 命令：`dotnet test tests/NXAI.Assistant.Tests/NXAI.Assistant.Tests.csproj -c Release --nologo`
- **373 项 = 372 通过 / 0 失败 / 1 跳过**（跳过项：需真实 NX 的 `CreateBlock_ValidParameters_SucceedsInNX`，按设计跳过）

按领域（文件 × 用例声明数，同一运行器实际结果）：
| 领域 | 测试文件（用例数） |
|---|---|
| Core 单元 | ChatLog(4)、WorkPartSnapshot(3)、NxVersionProfile(2)、PhaseLog(2) |
| 工具合同/Schema | CreateBlock(19)、CreateCylinder(16)、CreateCone(15)、FeatureTool(12) |
| 模型协议 | ModelServiceChat(11)、ModelServiceToolProtocol(15)、ModelServiceConfiguration(9)、ChatCompletionsUrl(5)、ToolTagProtocol(15)、ToolTagAlias(8)、ToolTagCompatAndChain(8) |
| 计划与协调器 | ModelingPlanTests(14) |
| 执行模式 | ExecutionMode(16)、ExecutionModeController(17) |
| 控制器编排 | ChatSessionController(15)、ChatSessionControllerTool(26) |
| 上下文 | NxContext(16)、ContextFeedback(11) |
| UI/渲染 | ChatMarkdown(10)、ChatRtfBuilder(13)、ChatMessageItemLifecycle(7) |
| 自检基础设施 | SelfCheckTemplateDiscovery(3)、PlaneFrameHandedness(2) |
| NXHost 集成 | BlockFeatureCreator(14)（含 1 跳过——需真实 NX） |

### 2.3 架构一致性
- **Core 无 NXOpen 依赖**：csproj 无 NXOpen 引用、无 `using NXOpen`（源码中仅注释提到"NXOpen-free"，合规）
- ToolRegistry 注册 8 工具与 Schema/解析器/允许清单（8 项）一致
- 协议适配层（ToolTagProtocol）与类型化解析器经 31 项测试对齐；`NormalizeCall` 为计划工厂与单发路径共用入口

### 2.4 git diff --check
- 通过（无空白错误）

### 2.5 部署一致性
- 4 DLL（Core/UI/NxHost.NX2512/Entry）部署 SHA-256 与构建产物**逐一一致**（Phase 8.2 版本）

## 三、NX2512 实机层（用户已执行的部分 + 待执行部分）

### 已由用户实测（本轮自检运行，2026-10-17）
- 种子部件方案**实机生效**（setup ✓）
- caseA XY 圆→拉伸 ✓、caseC 四基本体 ✓、caseD 失效引用拒绝 ✓
- caseB 失败 → Phase 8.2 修复（sketch.Orientation 权威坐标系）→ **已部署，待复验**

### 待用户执行（无法离线替代）
1. 点"几何自检"复验四场景（重点 caseB 环面 + XZ U45 世界坐标映射）
2. 场景 A 端到端：自主模式一句"创建直径 30 mm 的圆形草图，然后沿草图法向拉伸 50 mm"
3. 场景 B 端到端："在 XZ 平面 X=45 毫米的位置绘制直径 10 毫米的圆，再绕绝对 Z 轴旋转 360°"（核对环面形状而非仅包围盒）
4. 布尔三运算实机（unite/subtract/intersect 各一）
5. 确认模式分步确认 + 自主模式连续执行对比
6. 取消/超时路径复核

## 四、GitHub 同步

- 本地 HEAD `412b1ad`；上次确认推送成功的远端 HEAD 也是 `412b1ad`
- 本轮验收期间 `git ls-remote` 连接失败（GitHub 间歇不可达）——**无新提交需要推送**（412b1ad 已在远端），仅网络探测失败，不影响交付

## 五、结论与未完成项

**已完成**：构建/全量测试/架构/部署一致性全部通过；功能清单与实机状态边界已明确。

**未完成（均需 NX2512 实机，无法离线替代）**：
| 项 | 原因 | 下一步 |
|---|---|---|
| caseB 复验（环面） | 8.2 修复需真实 NX 几何确认 | 用户点"几何自检" |
| 场景 A/B 端到端 | 需真实模型请求+NX 执行 | 自主模式发指令 |
| 布尔三运算 | 同上 | 在测试部件执行 |
| 确认/自主对比 | 同上 | 两模式各跑一遍 |
| Phase 5 剩余上下文项 | 边/顶点/曲线/基准识别 | 用户实测 |

按第八节规则：在场景 A/B 真实通过前，不开始新的建模功能扩展。
