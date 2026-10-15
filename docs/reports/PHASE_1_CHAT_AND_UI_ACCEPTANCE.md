# 阶段 1 详细报告：真实模型对话链路与聊天 UI 验收

日期：2026-10-14 ~ 2026-10-15。分支：`recovery/pre-review-20261014-234252`（成果均为未提交工作区状态）。
状态术语定义见 [../PROJECT_STATUS.md](../PROJECT_STATUS.md)。

## 1. 本阶段范围

**做了**：可配置模型 API（OpenAI 兼容 Chat Completions）接入、多轮对话、错误分类、取消、配置安全（DPAPI）、聊天 UI 全面重构、模型设置窗口重构、新对话与上下文隔离。

**没做**（阶段边界，不是缺陷）：Tool Calling / 工具协议、任何几何修改、附件、流式输出、对话持久化。模型回复当前仅作为文本展示。

## 2. 交付内容与关键决策

### 2.1 模型服务（`ModelService.cs`，Core 层，NXOpen-free）

- **URL 规范化** `NormalizeChatCompletionsUrl`：接受裸域名/尾斜杠/任意 `/vN` 版本段/完整端点；拒绝相对路径、非 HTTP(S)、含查询/片段。决策：规范化的端点 = `{base}/chat/completions`，用户填 base 或 base+`/vN` 均可。
- **错误分类** `ModelChatResult`/`ModelChatError`：HTTP 401/403→鉴权、404→端点、400→请求、429→限流、5xx→服务端、网络异常、响应解析各自映射为可读中文错误。
- **认证**：`Authorization: Bearer` 头——修复轮发现并修复了 **P1 缺陷（头从未发送）**。
- **配置安全**：API Key 经 DPAPI（`Protect`/`Unprotect`）加密落盘 `%APPDATA%\NX AI Assistant\model-config.json`；`Save/LoadConfiguration` 支持 `configPath` 注入（测试隔离的唯一正确入口）；加载失败只清内存配置，**绝不改写磁盘文件**。
- **测试注入**：构造函数接受自定义 `HttpClient`（fake handler，不发真实网络）。

### 2.2 对话协调（`ChatSessionController.cs`，UI 层新增）

- 职责：输入校验→追加用户消息→异步请求→回写回复/错误→UI 状态事件（`RequestStarted/Completed`）。
- **线程模型**：控制器活在 UI 线程，async 延续经 WinForms `SynchronizationContext` 回 UI 线程，绝不后台线程触碰控件。
- **生命周期**：窗口关闭→`Detach()`→取消在途请求、丢弃迟到结果、不再触碰 ChatLog（`Dispose` 同路径）。
- **外发上下文**：仅 User/Assistant/System 进入请求（Info/Error 是本地状态条目）；上限 20 条（新留旧裁）。
- **防重复**：in-flight 时 `StartSend` 返回 false。

### 2.3 聊天 UI（`AssistantChatForm.cs` 全重写 + `ChatPalette.cs` 新增）

布局（纯 Dock/TableLayoutPanel，无绝对坐标，`AutoScaleMode.Font`）：

```text
root ─ sidebar 224px（品牌 / ＋新对话 / 会话状态 / 底部：模型状态+模型设置）
     └ main ─ header 72px（会话标题 + 模型状态 + 部件摘要单行 + 刷新链接）
            ├ messages AutoScroll 卡片流（Panel，非 ListBox → 无蓝色选中高亮）
            └ input 140px 底部固定（圆角输入卡片 + 发送/取消 + 快捷键提示）
```

关键实现决策：
- 消息渲染 `MessageItem`（嵌套 Panel）：用户右对齐米色圆角气泡、助手左侧通栏、Info 居中灰字、Error 红气泡；`Label.AutoSize + MaximumSize` 按视口宽度换行，`ClientSizeChanged` 触发全量重排。
- Dock=Top 子项追加到底部 = `Controls.Add` + `BringToFront`（z 序即停靠序）。
- 滚动到底：`BeginInvoke` + `AutoScrollPosition = DisplayRectangle.Height`。
- 输入区：无边框多行 TextBox + 1px 圆角卡片（Region 圆角）；Enter 发送 / Shift+Enter 换行（含 Ctrl 检查）；请求中禁发送、ReadOnly 输入、提示文案切换；失败/取消后状态恢复且原始输入已在对话记录中。
- 新对话：请求中禁止；有 User/Assistant 内容先确认；确认后 `ChatLog.Clear()`——**上下文清理即日志清空**（外发消息完全由 ChatLog 构建，二者天然同步，并有测试固化该行为）。
- 活动部件摘要：单行 + AutoEllipsis，完整路径入 Tooltip；每 5s 无轮询，`Activated` + 手动刷新。

### 2.4 模型设置窗口（`ModelSettingsForm.cs` 全重写）

- 两列 `TableLayoutPanel`（132px 标签列 + Percent 输入列），底部按钮区，620×360 初始 / 580×340 最小，可缩放——从容器布局根因修复控件裁切。
- Key 输入 `UseSystemPasswordChar`；**留空 = 保留已存密钥**（不把占位符写盘）；校验/保存失败内联红字 + 焦点落出错字段 + 窗口保留；成功才 `DialogResult.OK`；取消/关闭零写入。
- 焦点落 Base URL；Tab 序：URL→Key→模型名→保存→取消。

## 3. 验证证据

### 自动化（当前工作区，2026-10-15 归档时重新执行核实）

- Release 构建：0 警告 0 错误。
- 测试：**96 项 = 95 通过 / 0 失败 / 1 跳过**。本阶段新增（离线、不碰真实网络/配置/零件）：`ChatCompletionsUrlTests`（URL 规范化矩阵）、`ChatSessionControllerTests`（发送/取消/历史裁剪/上下文清空/Detach/配置热加载，含 fake HTTP handler 验证真实请求体 model 字段）、`ModelServiceChatTests`（错误分类、Bearer 头）、`ModelServiceConfigurationTests`（DPAPI 往返、隔离路径）。
- 部署一致性：`deploy/nxai_custom/application/` 4 DLL SHA-256 与 Release 产物一致。

### NX2512 实机（用户验收，2026-10-15，六项全部通过）

| 项 | 内容 | 结果 |
|---|---|---|
| 1 | 主窗口布局（三区完整、缩放正常） | ✅ |
| 2 | 模型设置窗口（输入框/按钮完整、保存/取消、取消不改配置） | ✅ |
| 3 | 真实模型对话（输入→发送→回复→自动滚动） | ✅ |
| 4 | 请求期间行为（取消按钮、窗口关闭、NX 可操作） | ✅ |
| 5 | 新建对话及旧上下文清理 | ✅ |
| 6 | Windows 显示缩放（100%/150%） | ✅ |

证据形式：用户实测反馈（对话内确认），未附截图/日志存档。

## 4. 遗留风险（进入 Phase 2 前须知）

1. NX 退出清理靠进程拆除兜底，无显式订阅；需一次"NX 退出时窗口不挂起"专项实测。
2. ChatLog 非线程安全（单线程所有权）；流式输出/后台消费接入前必须重审。
3. 对话无持久化，窗口关闭即丢；历史上限 20 条固定。
4. 未签名 DLL；跨机器部署可能被 NX 拒载，出现时再打通 SignDotNet。

## 5. Phase 2 接口缺口（下一任务的直接输入）

- **工具契约层缺失**：无 typed tool registry/schema；模型输出当前不解析 `tool_calls`。
- `ModelService` 无 tools 参数与工具调用响应解析。
- 无"执行前用户确认"UI 流；无执行后几何验证查询工具。
- `BlockRequestValidator`（参数校验，已离线测试）与 `BlockFeatureCreator`（NXOpen 执行，未实机验收）已就位待接入——注意 `CreateBlock_ValidParameters_SucceedsInNX` 测试当前按设计跳过，Phase 2 实机验收时执行。
