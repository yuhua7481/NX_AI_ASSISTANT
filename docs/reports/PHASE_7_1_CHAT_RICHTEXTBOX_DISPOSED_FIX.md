# Phase 7.1 报告：修复聊天 RichTextBox 已释放异常

日期：2026-10-16　分支：`recovery/pre-review-20261014-234252`

## 1. 故障与根因

实机异常：`System.ObjectDisposedException`（`RichTextBox.set_Rtf` ← `MessageItem.ApplyViewportWidth` ← `ApplyViewportWidthToAllItems` ← WndProc/UpdateBounds），发生在聊天消息视口宽度更新期间。

生命周期根因（代码检查确认，三处叠加）：

1. **新对话竞态（直接崩溃路径）**：`StartNewConversation()` 先循环 `Controls[0].Dispose()`、循环结束后才 `_messageItems.Clear()`。每个消息 Dispose 都会改变面板内容高度→滚动条出现/消失→`ClientSizeChanged` 同步触发 `ApplyViewportWidthToAllItems()`→遍历**仍含刚释放项**的 `_messageItems`→对已释放 RichTextBox 设置 Rtf。
2. **平行列表与控件树双轨**：`_messageItems` 列表与 `_messagesPanel.Controls` 各自维护，任何 Dispose 路径都不会同步该列表——结构上必然出现"列表引用已释放项"。
3. **窗体拆除窗口**：WinForms Dispose 顺序中子控件（RichTextBox）先于父级 MessageItem 离开控件树，期间布局回调仍可触达子视图；窗体关闭时 `ClientSizeChanged` 处理器仍挂接在面板上。

## 2. 修复（仅 [AssistantChatForm.cs](../../src/NXAI.Assistant.UI/AssistantChatForm.cs) 聊天消息生命周期/布局）

结构修复（非 try/catch 吞异常、非单点 IsDisposed）：

1. **删除 `_messageItems` 平行列表**——`ApplyViewportWidthToAllItems` 改为遍历 `_messagesPanel.Controls.OfType<MessageItem>()`，控件树即唯一事实来源：任何路径移除/释放的消息自动不再参与宽度更新，双轨失同步问题按构造消除。
2. **拆除窗口门禁**：`ApplyViewportWidthToAllItems` 在 `_detached || IsDisposed || _messagesPanel.IsDisposed` 时早退（窗体 `Dispose` 先置 `_detached` 再释放子控件，覆盖整个拆除过程）。
3. **子视图存活防线**：`MessageItem.ApplyViewportWidth` 在自身 `IsDisposed` 或子视图（RichTextBox/Label）`IsDisposed` 时早退——覆盖"子控件已释放但 MessageItem 尚未离开控件树"的拆除间隙。

未改动：Markdown 渲染（ChatRtfBuilder）、文本选择/复制、聊天滚动、建模执行、授权策略、模式持久化。

## 3. 自动化验证【本轮执行】

新增 `ChatMessageItemLifecycleTests`（7 项，STA 线程运行 WinForms 控件）：

1. 正常宽度更新（助手消息 RTB 宽度按预算钳制 660）✓
2. 长多段 Markdown 两次重排（窄视口收窄、高度不回退）✓
3. 用户气泡路径与 Info 标签路径宽度更新 ✓
4. **MessageItem Dispose 后再触发布局更新：不抛异常**（回归覆盖崩溃路径）✓
5. **子视图（RTB/Label）先释放后再更新：不抛异常**（覆盖拆除窗口）✓
6. 重复 Dispose 安全 ✓

全量：**321 项 = 320 通过 / 0 失败 / 1 跳过**（既有无回归）；Release 构建 0 警告 0 错误；`git diff --check` 通过。

**测试边界声明**：离线测试覆盖 MessageItem 级生命周期；面板级 ClientSizeChanged 接线与滚动条交互无法在离线测试中可靠覆盖，列入下方实机手动验收。

## 4. NX2512 实机手动验收（**待执行，不得视为通过**）

1. 多条长消息 + Markdown 混排 → 改变窗口宽度：无异常，重排正常。
2. 消息较多出现滚动条 → 新对话清空：无异常（原崩溃路径）。
3. 清空后立即缩放窗口：无异常。
4. 消息文本选择/复制、滚动位置行为与之前一致。
5. 打开聊天窗体后直接关闭 NX/窗体：无 ObjectDisposedException（%TEMP% 日志无新增）。
6. 建模功能（四种基本体 + Phase 7 四工具任一）抽查无回归。

## 5. 部署状态

**已部署（2026-10-16，用户确认 NX 关闭后）**：UI.dll 复制至 `deploy/nxai_custom/application/`，部署后 SHA-256 与构建产物核对一致：`f46eaa96eafddde3f9697b0045f8e4c59976bc7dee106ede594878849309babd`。（此前曾因 NX 运行锁定而按规则停止部署，未强制覆盖。）
