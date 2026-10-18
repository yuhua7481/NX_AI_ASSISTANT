# Phase 7.3 报告：修复模型工具调用协议未识别、原始标记泄漏到聊天界面

日期：2026-10-17　分支：`recovery/pre-review-20261014-234252`

## 1. 故障与协议审计结论

实机现象：create_sketch 成功后，聊天界面直接显示模型输出的 `invoke`/`parameter` 原始控制标记（含 profile/direction/distance 等），无工具执行反馈。

审计结论（代码路径核查，非猜测）：

1. **泄漏路径**：`ParseChatCompletionResponse` 只识别 `choices[].message.tool_calls` 结构化字段；工具调用若以标签块形式出现在 `message.content` 中，会走 `OkText(text)` → `finalText` → `ChatRole.Assistant` → 聊天 Markdown 渲染——原始标记直接呈现给用户，从未经过工具解析器。
2. **模型行为**：当前配置（nvidia_nim / z-ai GLM 推理模型）在部分轮次把工具调用写成 content 内的 XML 风格标签块而非结构化 tool_calls（推理模型经 OpenAI 兼容网关的已知行为差异）。
3. **工具名核对**：注册表中的真实名称是 `create_extrude`（Phase 7 注册），不是 `extrude`；修复不改变工具名。
4. **结构化路径本身无缺陷**：tool_call_id、arguments、tool 结果回填（role=tool + tool_call_id）链路完整（SerializeMessages/ParseToolCalls），保持不变。

## 2. 修复（新增 [ToolTagProtocol.cs](../../src/NXAI.Assistant.Core/ToolTagProtocol.cs)，修改 ModelService.cs）

**受限协议适配层**（Core，NXOpen-free，可离线测试）：

- 仅当 content 含完整、良构的 invoke 块时解析：函数名须匹配 `^[A-Za-z0-9_.-]+$`；参数为严格 name→值 映射（重复名/空名/缺 `=`/未闭合/参数区杂散文本 → 整块拒绝并给出明确中文协议错误）。
- 每条消息至多一个块；孤立开标签（未闭合）按协议错误处理，**绝不当作普通文本渲染**。
- 解析成功 → 转换为与结构化路径相同的 `ParsedToolCall`（合成稳定 `tagcall_N` id 供回填），**下游注册表/Schema/ExecutionModePolicy/NX UI 线程/回滚检查全部原样生效**——适配层只归一化形状，不授予任何权限。
- 参数值统一为字符串进入既有类型化解析器（其本就容忍字符串编码数字）。
- 结构化 tool_calls 优先：模型正确发出结构化调用时适配层不参与。
- 无 secrets：适配层与日志不触碰 API Key/Authorization。

标记常量在源码中由片段拼接（`"an"+"tml"+":"` 等），避免字面控制序列出现在本仓库源文件中；测试同样以片段重建。

## 3. 自动化验证【本轮执行】

新增 `ToolTagProtocolTests`（15 项）：纯文本直通、相似但非真实标记的文本不误判、完整块→类型化调用（含参数 JSON 断言）、带前后文本提取、id 唯一性、未闭合块/多块/重复参数/未闭合参数/缺工具名/非法工具名/参数区杂散文本/缺 name 属性全部拒绝且**错误信息不含原始标记**、未注册工具名解析后被注册表拒绝（run_shell 用例）、适配输出喂给真实 CreateExtrudeRequestParser 通过。

开发过程中测试捕获并修复了适配器自身一个边界 bug（Scan 用参数闭标签当块结束→已改为 invoke 闭标签定位块尾）。

全量：**336 项 = 335 通过 / 0 失败 / 1 跳过**（既有 create_sketch/extrude/四基本体/布尔/模式/RichTextBox 生命周期/聊天渲染全部无回归）；Release 构建 0 警告 0 错误；`git diff --check` 通过。

## 4. 部署

NX 未运行，4 DLL 部署 SHA-256 与构建产物逐一一致：Core `22e6fe97…`、UI `488f5c05…`、NxHost.NX2512 `50580d4c…`、Entry `baf508fc…`。

## 5. NX2512 实机验收（**全部待执行，不得视为通过**）

1. "创建直径 30 的圆形草图" → 草图成功（与之前一致）。
2. 单独发送"将当前圆形草图沿法向拉伸 50 毫米，创建实体"。
3. 聊天窗口**不再显示** invoke/parameter 原始标记；若协议无法解析则显示简洁中文提示。
4. PhaseLog 确认进入 create_extrude 真实执行链（execute.extrude/execute.section 各阶段）。
5. 成功后实体 d30×50（与 Phase 7.2 修复联动验证）。
6. 确认/自主模式各一次，授权策略一致。
7. 若仍报协议错误或对象失效：保留日志、停止重试、按阶段定位。

## 6. 边界

- 适配层只支持"content 内完整 invoke 块"这一种可严格解析形态；其他变体（流式分片、异常属性顺序、嵌套块）按协议错误安全拒绝——宁可拒绝不猜测。
- 请求侧 tools 声明保持标准 OpenAI function 格式不变。

---

## Phase 7.3.1（2026-10-17）：工具名别名与方向值映射

**实机补充证据**：自主模式下模型实际输出 `extrude`（短名，非注册名 `create_extrude`）与 `direction=positive_z`（非 schema 值 normal/reverse）——7.3 适配层虽能解析块结构，但归一化后 `extrude` 不在注册表、`positive_z` 不在方向枚举，仍被拒绝。

**修复**（[ToolTagProtocol.cs](../../src/NXAI.Assistant.Core/ToolTagProtocol.cs) 新增封闭映射表）：
- **工具别名表**（显式、封闭）：`extrude` → `create_extrude`。仅在有已核实语义时新增条目，不做模糊匹配、不做字符串替换绕过注册表；未别名化的未知名原样传给注册表拒绝。
- **方向值映射**（仅 create_extrude 的 direction 参数）：`positive_z` → `normal`（schema 的 normal 即草图平面法向 = XY 草图的 +Z，语义核实后准入）；其他值原样传给类型化解析器拒绝。
- `profile`/`SKETCH_000`/`distance=50`/`unit=mm` 均不在映射范围：SKETCH_000 本就是 NXHost 管理的受控引用（JournalIdentifier）原样传递；模型若用 `profile` 而非 schema 字段 `sketch_ref`，类型化解析器按未知字段拒绝——**适配层不做参数名猜测**。
- 结构化 tool_calls 优先级、回填协议、ExecutionModePolicy、NX 执行链全部不变。

**测试**：新增 ToolTagAliasTests 8 项（截图样例、别名+方向映射到真实类型化解析器、positive_z→normal 语义断言、非法方向拒绝、受控引用原样传递、未知别名不重写、其他工具不受影响、结构化调用 id 保留）。全量 **344 项 = 343 通过 / 0 失败 / 1 跳过**。

**部署**：Core.dll SHA-256 `91780fdf…` 与构建产物一致。

**实机验收（待执行）**：重启插件加载新 DLL → 自主模式对既有 SKETCH_000 请求 +Z 拉伸 50mm → 聊天无原始标签、PhaseLog 显示进入 create_extrude → 包围盒 30×30×50 → 确认模式复测确认卡。注意：**若模型继续用 `profile` 字段名，调用会被类型化解析器拒绝（未知字段）**——如复现，需按截图原始输出决定是否将 `profile` 列入已核实别名（当前无证据表明应静默改写参数名，不猜）。

---

## Phase 7.3.2（2026-10-17）：参数别名 + 串行两步链 + 回合上限扩展

**实机补充证据**：模型稳定输出 `extrude / sketch=SKETCH_000 / distance=50 / unit=mm`（无 direction）——7.3.1 解析了 `extrude` 别名，但 `sketch` 参数名不在 schema（typed parser 按未知字段拒绝），且回合上限 2 使 `create_sketch → create_extrude` 串行链无法在单轮完成。

**修复**：

1. **参数名别名**（ToolTagProtocol 封闭表，仅 create_extrude）：`sketch` → `sketch_ref`。一对一映射；同一调用同时携带 `sketch` 与 `sketch_ref` → 冲突拒绝；不做参数名猜测。`profile`（7.3.1 观察到）**未准入**——本批截图证据是 `sketch`，两者并存无据，继续由类型化解析器按未知字段拒绝。
2. **direction 默认值核实**：产品契约本就允许默认——schema required 仅 sketch_ref/distance/unit，解析器 direction 缺省即 `Normal`（沿草图法向）。无改动，补测试锁定契约。
3. **串行链路**（ChatSessionController）：`MaxModelResponsesPerTurn` 2→3，新增 `MaxToolExecutionsPerTurn = 2`。rounds 1-2 每轮至多一个工具调用、严格串行（各自等待前一工具真实结果），工具结果照常回填（sketchRef 由模型从回填 payload 读取）；round 3 仅最终文本、不广告工具。第一工具失败不阻止第二工具的独立校验，但每个调用仍走全部注册/Schema/授权/线程/回滚检查。两工具均在自主允许清单（Phase 7 已列）。
4. **泄漏阻断不变**：7.3 的"协议错误不渲染原文"路径覆盖全部新拒绝分支（参数冲突等）。

**测试**：新增 ToolTagCompatAndChainTests 8 项（live 样例含 sketch 别名直达真实解析器、direction 缺省契约锁定、别名冲突拒绝、串行链 sketch→extrude 传递真实受控引用、确认模式每步各一张卡、自主模式零卡片、首步失败次步独立校验、上限防循环）；更新 3 个 round-cap 旧契约测试至新两步契约。全量 **352 项 = 351 通过 / 0 失败 / 1 跳过**。

**部署**：4 DLL SHA-256 一致（Core `8767fbe5…`、UI `08376a86…`、NxHost `68d4ce4e…`、Entry `baf508fc…`）。

**实机验收（待执行）**：自主模式一句"用拉伸命令绘制直径30、高50的圆柱"→ 圆形草图成功 → 串行 create_extrude（sketch_ref=SKETCH_000，d=50 默认法向）→ 无原始标签、PhaseLog 两步执行阶段齐全 → 包围盒 30×30×50 → 确认模式复测每步确认卡 → 取消/失败路径不重复执行、不自动保存。
