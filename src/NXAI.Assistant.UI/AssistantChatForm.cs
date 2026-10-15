using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

using NXAI.Assistant.Core;

namespace NXAI.Assistant.UI;

/// <summary>
/// Modeless floating chat shell opened from the NX main UI entry.
/// Runs on the NX UI thread: NX's message loop pumps this form, so the
/// NX main window stays interactive while it is shown. Chat requests run
/// asynchronously through ChatSessionController — the HTTP call never
/// blocks the NX message loop, and continuations come back to this
/// thread through the WinForms SynchronizationContext.
///
/// Layout (no absolute coordinates; Dock + TableLayoutPanel only):
///   root ─ sidebar (left, 224px)        brand / 新对话 / 会话状态 / 模型设置
///        └ main ─ header (top)          会话标题 + 模型状态 + 活动部件摘要
///               ├ messages (fill)       AutoScroll 消息卡片流
///               └ input area (bottom)   多行输入 + 发送/取消 + 快捷键提示
/// </summary>
public sealed class AssistantChatForm : Form
{
    private const int SidebarWidth = 224;
    private const int MaxTextWidth = 660;
    private static readonly Font BodyFont = new("Microsoft YaHei UI", 9.75F);
    private static readonly Font SmallFont = new("Microsoft YaHei UI", 8.25F);

    private readonly IActivePartReader _partReader;
    private readonly ChatLog _chatLog = new();
    private readonly ChatSessionController _controller;
    private readonly ToolTip _toolTip = new();
    private readonly ContextMenuStrip _copyMenu = new();
    private readonly List<MessageItem> _messageItems = new();

    private readonly Label _headerTitleLabel;
    private readonly Label _headerModelLabel;
    private readonly Label _partLineLabel;
    private readonly LinkLabel _refreshLink;
    private readonly Button _newChatButton;
    private readonly Label _sessionStateLabel;
    private readonly Label _sidebarModelLabel;
    private readonly Button _settingsButton;
    private readonly Panel _messagesPanel;
    private readonly TextBox _inputBox;
    private readonly Button _sendButton;
    private readonly Button _cancelButton;
    private readonly Label _inputHintLabel;

    public AssistantChatForm(IActivePartReader partReader)
    {
        _partReader = partReader ?? throw new ArgumentNullException(nameof(partReader));

        _controller = new ChatSessionController(new ModelService(), _chatLog,
            () => ModelService.LoadConfiguration());
        _controller.RequestStarted += (_, _) => SetRequestUiState(inFlight: true);
        _controller.RequestCompleted += (_, _) => SetRequestUiState(inFlight: false);
        // Apply a saved configuration (if any) immediately — no settings dialog needed.
        _controller.ReloadConfiguration();

        Text = "NX AI Assistant";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(850, 600);
        Size = new Size(1100, 760);
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.Font;
        Font = new Font("Microsoft YaHei UI", 9F);

        // --- sidebar -----------------------------------------------------------
        _newChatButton = MakeFullWidthButton("＋ 新对话");
        _newChatButton.Margin = new Padding(0, 16, 0, 0);
        _newChatButton.Click += (_, _) => StartNewConversation();

        _sessionStateLabel = new Label
        {
            Text = "空对话，发送消息即可开始",
            AutoSize = true,
            ForeColor = ChatPalette.BodyText,
            Font = new Font("Microsoft YaHei UI", 9F),
            MaximumSize = new Size(SidebarWidth - 32, 0),
        };

        _sidebarModelLabel = new Label
        {
            Text = "未配置",
            AutoSize = true,
            ForeColor = ChatPalette.ErrorText,
            Font = new Font("Microsoft YaHei UI", 9F),
            MaximumSize = new Size(SidebarWidth - 32, 0),
        };

        _settingsButton = MakeFullWidthButton("模型设置");
        _settingsButton.Margin = new Padding(0, 8, 0, 0);
        _settingsButton.Click += (_, _) => OpenModelSettings();

        var sidebar = new Panel
        {
            Dock = DockStyle.Left,
            Width = SidebarWidth,
            BackColor = ChatPalette.Sidebar,
        };
        sidebar.Paint += (s, e) =>
        {
            using var pen = new Pen(ChatPalette.SidebarBorder);
            e.Graphics.DrawLine(pen, sidebar.Width - 1, 0, sidebar.Width - 1, sidebar.Height);
        };
        var sideTable = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16, 18, 16, 16),
            ColumnCount = 1,
            RowCount = 9,
        };
        sideTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        for (int i = 0; i < 9; i++)
            sideTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        sideTable.RowStyles[5] = new RowStyle(SizeType.Percent, 100f);

        sideTable.Controls.Add(new Label
        {
            Text = "NX AI Assistant",
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 11.25F, FontStyle.Bold),
            ForeColor = ChatPalette.BodyText,
        }, 0, 0);
        sideTable.Controls.Add(new Label
        {
            Text = "Siemens NX2512 进程内助手",
            AutoSize = true,
            Font = SmallFont,
            ForeColor = ChatPalette.HeaderText,
        }, 0, 1);
        sideTable.Controls.Add(_newChatButton, 0, 2);
        sideTable.Controls.Add(MakeSectionLabel("当前对话", new Padding(0, 24, 0, 2)), 0, 3);
        sideTable.Controls.Add(_sessionStateLabel, 0, 4);
        // row 5: elastic spacer
        sideTable.Controls.Add(MakeSectionLabel("模型", new Padding(0, 0, 0, 2)), 0, 6);
        sideTable.Controls.Add(_sidebarModelLabel, 0, 7);
        sideTable.Controls.Add(_settingsButton, 0, 8);
        sidebar.Controls.Add(sideTable);

        // --- header: session title + model status + active part summary --------
        _headerTitleLabel = new Label
        {
            Text = "新对话",
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold),
            ForeColor = ChatPalette.BodyText,
            Anchor = AnchorStyles.Left,
        };
        _headerModelLabel = new Label
        {
            Text = "模型未配置",
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 9F),
            ForeColor = ChatPalette.ErrorText,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(3, 8, 3, 0),
        };
        _partLineLabel = new Label
        {
            Text = "部件：（未读取）",
            AutoSize = false,
            AutoEllipsis = true,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Height = 20,
            Font = new Font("Microsoft YaHei UI", 8.5F),
            ForeColor = ChatPalette.HeaderText,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(3, 4, 3, 0),
        };
        _refreshLink = new LinkLabel
        {
            Text = "刷新",
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 8.5F),
            LinkColor = ChatPalette.PrimaryButton,
            ActiveLinkColor = ChatPalette.PrimaryButtonHover,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(3, 4, 3, 0),
        };
        _refreshLink.Click += (_, _) => RefreshActivePart();

        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 72,
            BackColor = ChatPalette.Card,
        };
        header.Paint += (s, e) =>
        {
            using var pen = new Pen(ChatPalette.SidebarBorder);
            e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width - 1, header.Height - 1);
        };
        var headerTable = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24, 12, 24, 10),
            ColumnCount = 2,
            RowCount = 2,
        };
        headerTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        headerTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        headerTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        headerTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        headerTable.Controls.Add(_headerTitleLabel, 0, 0);
        headerTable.Controls.Add(_headerModelLabel, 1, 0);
        headerTable.Controls.Add(_partLineLabel, 0, 1);
        headerTable.Controls.Add(_refreshLink, 1, 1);
        header.Controls.Add(headerTable);

        // --- messages: AutoScroll card flow (no ListBox selection highlight) ---
        _messagesPanel = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = ChatPalette.MainBg,
            Padding = new Padding(0, 12, 0, 8),
        };
        _messagesPanel.ClientSizeChanged += (_, _) => ApplyViewportWidthToAllItems();
        _chatLog.MessageAdded += OnChatMessageAdded;

        // --- input area: always pinned to the bottom ----------------------------
        _inputBox = new TextBox
        {
            Multiline = true,
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            BackColor = Color.White,
            Font = BodyFont,
        };
        _inputBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && !e.Shift && !ModifierKeys.HasFlag(Keys.Control))
            {
                e.SuppressKeyPress = true;
                SendCurrentInput();
            }
        };

        var inputCard = new Panel
        {
            Dock = DockStyle.Top,
            Height = 68,
            BackColor = ChatPalette.InputBorder,
            Padding = new Padding(1),
        };
        inputCard.Controls.Add(_inputBox);
        inputCard.SizeChanged += (_, _) =>
            inputCard.Region = CreateRoundedRegion(inputCard.Size, 12);

        _sendButton = new Button
        {
            Text = "发送",
            AutoSize = false,
            Size = new Size(88, 32),
            TabIndex = 1,
        };
        StylePrimaryButton(_sendButton);
        _sendButton.Click += (_, _) => SendCurrentInput();

        _cancelButton = new Button
        {
            Text = "取消",
            AutoSize = false,
            Size = new Size(72, 32),
            Enabled = false,
            TabIndex = 2,
        };
        StyleSecondaryButton(_cancelButton);
        _cancelButton.Click += (_, _) => _controller.CancelActiveRequest();

        _inputHintLabel = new Label
        {
            Text = "Enter 发送 · Shift+Enter 换行",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = ChatPalette.HeaderText,
            Font = SmallFont,
            Padding = new Padding(4, 0, 200, 0),
        };
        var inputBar = new Panel { Dock = DockStyle.Fill };
        inputBar.Controls.Add(_inputHintLabel);
        inputBar.Controls.Add(_cancelButton);
        inputBar.Controls.Add(_sendButton);
        inputBar.Layout += (_, _) => PositionInputButtons(inputBar);

        var inputRoot = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 140,
            Padding = new Padding(24, 12, 24, 14),
            BackColor = ChatPalette.MainBg,
        };
        inputRoot.Controls.Add(inputBar);
        inputRoot.Controls.Add(inputCard);

        // --- assemble ------------------------------------------------------------
        var mainArea = new Panel { Dock = DockStyle.Fill, BackColor = ChatPalette.MainBg };
        mainArea.Controls.Add(_messagesPanel);
        mainArea.Controls.Add(header);
        mainArea.Controls.Add(inputRoot);

        var root = new Panel { Dock = DockStyle.Fill, BackColor = ChatPalette.MainBg };
        root.Controls.Add(mainArea);
        root.Controls.Add(sidebar);
        Controls.Add(root);

        var copyItem = new ToolStripMenuItem { Text = "复制文本" };
        copyItem.Click += (_, _) => CopyMessageUnderMenu();
        _copyMenu.Items.Add(copyItem);

        Activated += (_, _) => RefreshActivePart();
        RefreshActivePart();
        UpdateModelStatus();
        _chatLog.Add(ChatRole.Info, _controller.IsConfigurationReady
            ? $"已加载模型配置（{_controller.CurrentModelName}），发送消息即可开始对话。"
            : "尚未配置模型 API：点击左下角“模型设置”完成配置后再发送消息。");
        UpdateSessionStatus();
    }

    /// <summary>Re-reads the active work part from the NX session (read-only).</summary>
    public void RefreshActivePart()
    {
        WorkPartSnapshot snapshot;
        try
        {
            snapshot = _partReader.ReadActivePart();
        }
        catch (Exception ex)
        {
            snapshot = WorkPartSnapshot.FromError(ex.Message);
        }

        if (snapshot.HasActivePart)
        {
            _partLineLabel.Text =
                $"部件：{snapshot.DisplayName ?? "（未知）"} · {snapshot.Units ?? "（未知）"} · " +
                $"{snapshot.StatusMessage ?? "已加载"}";
            _toolTip.SetToolTip(_partLineLabel,
                $"{snapshot.FullPath ?? ""}{Environment.NewLine}{snapshot.StatusMessage ?? ""}");
        }
        else
        {
            _partLineLabel.Text = snapshot.StatusMessage ?? "当前没有活动工作部件。";
            _toolTip.SetToolTip(_partLineLabel, snapshot.FullPath ?? "");
        }
    }

    /// <summary>
    /// Opens the settings dialog modally (owned by this window). Only an OK result
    /// reloads the persisted configuration; cancel/close leaves everything untouched.
    /// </summary>
    private void OpenModelSettings()
    {
        using var dialog = new ModelSettingsForm();
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        var configured = _controller.ReloadConfiguration();
        UpdateModelStatus();
        if (configured)
            _chatLog.Add(ChatRole.Info, $"模型配置已更新：{_controller.CurrentModelName}。");
        else
            _chatLog.Add(ChatRole.Info, "模型配置已保存，但内容不完整，请重新检查。");
    }

    /// <summary>
    /// 开始新对话：清空聊天记录的同时清空模型上下文（外发消息完全由 ChatLog
    /// 构建，Clear 之后请求只会携带新对话内容）。已有对话内容时先确认，
    /// 避免误操作丢失当前会话；请求进行中不允许清空。
    /// </summary>
    private void StartNewConversation()
    {
        if (_controller.IsRequestInFlight)
        {
            MessageBox.Show(this,
                "当前有请求正在进行。\n请先取消或等待回复完成，再开始新对话。",
                "NX AI Assistant", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (_chatLog.Messages.Any(m => m.Role is ChatRole.User or ChatRole.Assistant))
        {
            var choice = MessageBox.Show(this,
                "开始新对话将清空当前对话记录和模型上下文（不影响已保存的模型配置）。\n是否继续？",
                "NX AI Assistant", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (choice != DialogResult.Yes)
                return;
        }

        _chatLog.Clear();
        while (_messagesPanel.Controls.Count > 0)
            _messagesPanel.Controls[0].Dispose();
        _messageItems.Clear();

        _chatLog.Add(ChatRole.Info, "已开始新对话，之前的消息不会发送给模型。");
        UpdateSessionStatus();
    }

    private void SendCurrentInput()
    {
        if (_controller.IsRequestInFlight)
            return;
        var text = _inputBox.Text;
        if (string.IsNullOrWhiteSpace(text))
            return;

        if (!_controller.IsConfigurationReady)
        {
            var choice = MessageBox.Show(this,
                "尚未配置模型 API，无法发送消息。\n是否现在打开“模型设置”？",
                "NX AI Assistant",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (choice == DialogResult.Yes)
                OpenModelSettings();
            return;
        }

        if (_controller.StartSend(text))
            _inputBox.Clear();
    }

    /// <summary>Busy/idle UI state. Runs on the UI thread via controller events.</summary>
    private void SetRequestUiState(bool inFlight)
    {
        if (IsDisposed)
            return;
        _sendButton.Enabled = !inFlight;
        _cancelButton.Enabled = inFlight;
        _inputHintLabel.Text = inFlight
            ? "正在等待模型回复…"
            : "Enter 发送 · Shift+Enter 换行";
        _inputHintLabel.ForeColor = inFlight ? ChatPalette.PrimaryButton : ChatPalette.HeaderText;
        UpdateSessionStatus();
    }

    private void UpdateModelStatus()
    {
        string? modelName = _controller.CurrentModelName;
        if (string.IsNullOrEmpty(modelName))
        {
            _headerModelLabel.Text = "模型未配置";
            _headerModelLabel.ForeColor = ChatPalette.ErrorText;
            _sidebarModelLabel.Text = "未配置";
            _sidebarModelLabel.ForeColor = ChatPalette.ErrorText;
        }
        else
        {
            _headerModelLabel.Text = $"模型：{modelName}";
            _headerModelLabel.ForeColor = ChatPalette.HeaderText;
            _sidebarModelLabel.Text = modelName;
            _sidebarModelLabel.ForeColor = ChatPalette.BodyText;
        }
    }

    private void UpdateSessionStatus()
    {
        int conversationCount = _chatLog.Messages.Count(
            m => m.Role is ChatRole.User or ChatRole.Assistant);
        _headerTitleLabel.Text = conversationCount > 0 ? "对话" : "新对话";
        _sessionStateLabel.Text = _controller.IsRequestInFlight
            ? "等待模型回复…"
            : conversationCount == 0
                ? "空对话，发送消息即可开始"
                : $"共 {conversationCount} 条消息";
    }

    // ------------------------------------------------------------------ messages

    private void OnChatMessageAdded(object? sender, ChatMessage message)
    {
        var item = new MessageItem(message, _toolTip, _copyMenu);
        item.ApplyViewportWidth(_messagesPanel.ClientSize.Width);
        _messageItems.Add(item);
        _messagesPanel.Controls.Add(item);
        item.BringToFront(); // Dock=Top 子项按 z 序停靠：BringToFront 使新消息排在最底部
        UpdateSessionStatus();
        ScrollMessagesToEnd();
    }

    private void ApplyViewportWidthToAllItems()
    {
        int width = _messagesPanel.ClientSize.Width;
        foreach (var item in _messageItems)
            item.ApplyViewportWidth(width);
    }

    private void ScrollMessagesToEnd()
    {
        if (IsDisposed || !_messagesPanel.IsHandleCreated)
            return;
        _messagesPanel.BeginInvoke(() =>
        {
            if (IsDisposed || _messagesPanel.IsDisposed)
                return;
            _messagesPanel.AutoScrollPosition = new Point(0, _messagesPanel.DisplayRectangle.Height);
        });
    }

    private void CopyMessageUnderMenu()
    {
        for (Control? control = _copyMenu.SourceControl; control != null; control = control.Parent)
        {
            if (control is MessageItem item)
            {
                try
                {
                    Clipboard.SetText(item.MessageText);
                }
                catch (ExternalException)
                {
                    // 剪贴板被其他进程瞬时锁定：放弃本次复制，不弹错误。
                }
                return;
            }
        }
    }

    // ------------------------------------------------------------------ helpers

    private static Button MakeFullWidthButton(string text)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = false,
            Height = 36,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            BackColor = ChatPalette.SecondaryButton,
        };
        StyleSecondaryButton(button);
        return button;
    }

    private static Label MakeSectionLabel(string text, Padding margin)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            Font = SmallFont,
            ForeColor = ChatPalette.HeaderText,
            Margin = margin,
        };
    }

    private void PositionInputButtons(Panel inputBar)
    {
        // 取消在发送左侧：两个按钮作为一个整体右对齐、垂直居中。
        _cancelButton.Left = inputBar.ClientSize.Width - _cancelButton.Width - 4;
        _cancelButton.Top = Math.Max(0, (inputBar.ClientSize.Height - _cancelButton.Height) / 2);
        _sendButton.Left = _cancelButton.Left - _sendButton.Width - 8;
        _sendButton.Top = Math.Max(0, (inputBar.ClientSize.Height - _sendButton.Height) / 2);
    }

    private static void StylePrimaryButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = ChatPalette.PrimaryButtonHover;
        button.BackColor = ChatPalette.PrimaryButton;
        button.ForeColor = Color.White;
        button.Cursor = Cursors.Hand;
    }

    private static void StyleSecondaryButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = ChatPalette.SecondaryButtonHover;
        button.BackColor = ChatPalette.SecondaryButton;
        button.ForeColor = ChatPalette.BodyText;
        button.Cursor = Cursors.Hand;
    }

    private static Region CreateRoundedRegion(Size size, int radius)
    {
        int r = Math.Min(radius, Math.Min(size.Width, size.Height) / 2);
        if (r <= 0)
            return new Region(new Rectangle(Point.Empty, size));

        var path = new GraphicsPath();
        int d = r * 2;
        path.AddArc(0, 0, d, d, 180, 90);
        path.AddArc(size.Width - d, 0, d, d, 270, 90);
        path.AddArc(size.Width - d, size.Height - d, d, d, 0, 90);
        path.AddArc(0, size.Height - d, d, d, 90, 90);
        path.CloseFigure();
        return new Region(path);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Window is closing: drop late async results and abort any in-flight request.
            _controller.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// 单条消息卡片。用户消息为右侧圆角气泡，助手回复为左侧通栏文本，
    /// Info 居中弱化显示，错误为红色气泡。文本按视口宽度自动换行，
    /// 不使用 ListBox，因此没有系统蓝色选中高亮。
    /// </summary>
    private sealed class MessageItem : Panel
    {
        private const int BubblePadH = 14;
        private const int BubblePadV = 10;
        private const int SideMargin = 24;

        private readonly Label _textLabel;
        private readonly Panel? _bubble;

        public ChatRole Role { get; }
        public string MessageText => _textLabel.Text;

        public MessageItem(ChatMessage message, ToolTip toolTip, ContextMenuStrip copyMenu)
        {
            Role = message.Role;
            Dock = DockStyle.Top;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            BackColor = ChatPalette.MainBg;
            Padding = new Padding(0, 5, 0, 5);

            _textLabel = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(400, 0),
                Text = message.Text,
                Font = message.Role == ChatRole.Info ? SmallFont : BodyFont,
                ForeColor = ChatPalette.BodyText,
            };

            bool hasBubble = message.Role is ChatRole.User or ChatRole.Error;
            if (hasBubble)
            {
                bool isError = message.Role == ChatRole.Error;
                _bubble = new Panel
                {
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    BackColor = isError ? ChatPalette.ErrorBubble : ChatPalette.UserBubble,
                    Padding = new Padding(0, 0, BubblePadH, BubblePadV),
                    Location = new Point(SideMargin, 5),
                };
                _textLabel.ForeColor = isError ? ChatPalette.ErrorText : ChatPalette.BodyText;
                _textLabel.Location = new Point(BubblePadH, BubblePadV);
                _bubble.Controls.Add(_textLabel);
                Controls.Add(_bubble);
                _bubble.SizeChanged += (_, _) =>
                {
                    _bubble.Region?.Dispose();
                    _bubble.Region = CreateRoundedRegion(_bubble.Size, 12);
                };
                toolTip.SetToolTip(_bubble, $"时间：{message.TimestampUtc.LocalDateTime:HH:mm:ss}");
                _bubble.ContextMenuStrip = copyMenu;
            }
            else
            {
                _textLabel.Location = new Point(SideMargin, 5);
                if (message.Role == ChatRole.Info)
                {
                    _textLabel.Font = SmallFont;
                    _textLabel.ForeColor = ChatPalette.HeaderText;
                }
                Controls.Add(_textLabel);
            }

            toolTip.SetToolTip(_textLabel, $"时间：{message.TimestampUtc.LocalDateTime:HH:mm:ss}");
            _textLabel.ContextMenuStrip = copyMenu;
            ContextMenuStrip = copyMenu;

            Layout += (_, _) => RepositionByRole();
        }

        /// <summary>
        /// 视口宽度变化时重新计算换行宽度。设置 MaximumSize 触发 Label 重新测量，
        /// 气泡与卡片 AutoSize 随之重排，因此窗口缩放不会产生水平裁切。
        /// </summary>
        public void ApplyViewportWidth(int viewportWidth)
        {
            int bubbleChrome = _bubble != null ? 2 * BubblePadH : 0;
            int roleBudget = Role switch
            {
                ChatRole.User => viewportWidth - 220 - bubbleChrome, // 用户气泡不占满整行
                ChatRole.Info => viewportWidth - 120,
                _ => viewportWidth - 2 * SideMargin - bubbleChrome,
            };
            int width = Math.Max(160, Math.Min(660, roleBudget));
            if (_textLabel.MaximumSize.Width != width)
                _textLabel.MaximumSize = new Size(width, 0);
        }

        private void RepositionByRole()
        {
            switch (Role)
            {
                case ChatRole.User:
                    if (_bubble != null)
                        _bubble.Left = Math.Max(SideMargin, ClientSize.Width - SideMargin - _bubble.Width);
                    break;
                case ChatRole.Info:
                    _textLabel.Left = Math.Max(SideMargin,
                        (ClientSize.Width - _textLabel.Width) / 2);
                    break;
                case ChatRole.Error:
                    if (_bubble != null)
                        _bubble.Left = SideMargin;
                    break;
                default:
                    _textLabel.Left = SideMargin;
                    break;
            }
        }
    }
}
