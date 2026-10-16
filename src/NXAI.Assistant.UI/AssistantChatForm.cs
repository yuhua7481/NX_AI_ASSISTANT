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
    private readonly INxToolExecutor? _toolExecutor;
    private readonly ExecutionModeManager _modeManager;
    private readonly ModeToggleSwitch _modeSwitch;
    private ToolConfirmationRequest? _pendingConfirmation;
    private volatile bool _detached;
    private readonly ToolTip _toolTip = new();
    private readonly ContextMenuStrip _copyMenu = new();

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
    private readonly Button _refreshContextButton;
    private readonly Button _cancelButton;
    private readonly Label _inputHintLabel;
    private readonly Label _modeBarLabel;
    private readonly Func<NxContextSnapshot?>? _contextProvider;

    public AssistantChatForm(IActivePartReader partReader, INxToolExecutor? toolExecutor = null,
        Func<NxContextSnapshot?>? contextProvider = null)
    {
        _partReader = partReader ?? throw new ArgumentNullException(nameof(partReader));
        _toolExecutor = toolExecutor;
        _contextProvider = contextProvider;
        _modeManager = new ExecutionModeManager(new JsonFileExecutionModeStore());

        ChatSessionController BuildController() => new(
            new ModelService(), _chatLog, () => ModelService.LoadConfiguration(),
            requestSender: null,
            toolExecutor: toolExecutor,
            confirmationHandler: toolExecutor != null ? ShowToolConfirmationAsync : null,
            contextProvider: contextProvider,
            modeManager: _modeManager);

        _controller = BuildController();
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

        _refreshContextButton = new Button
        {
            Text = "刷新上下文",
            AutoSize = false,
            Size = new Size(96, 32),
            TabIndex = 3,
        };
        StyleSecondaryButton(_refreshContextButton);
        _refreshContextButton.Click += (_, _) => RefreshContextDisplay();

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
        inputBar.Controls.Add(_refreshContextButton);
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

        // --- execution mode bar: sits directly above the input box -------------
        // The bar's height must be driven by the switch's own preferred size
        // (it scales with DPI), never a fixed pixel height — a fixed height
        // clips the track/knob at 125%/150% DPI scaling.
        _modeSwitch = new ModeToggleSwitch
        {
            Checked = _modeManager.CurrentMode == ExecutionMode.Autonomous,
            Margin = new Padding(0, 2, 0, 2),
            Dock = DockStyle.Fill,
            TabStop = true,
        };
        _modeSwitch.ToggleRequested += (_, _) => HandleModeToggleRequested();
        _modeBarLabel = new Label
        {
            Text = "执行模式",
            AutoSize = true,
            Margin = new Padding(0, 6, 0, 0),
            Font = SmallFont,
            ForeColor = ChatPalette.HeaderText,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        var modeBarTable = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
        };
        modeBarTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        modeBarTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        modeBarTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        modeBarTable.Controls.Add(_modeBarLabel, 0, 0);
        modeBarTable.Controls.Add(_modeSwitch, 1, 0);
        var modeBar = new Panel
        {
            Dock = DockStyle.Top,
            BackColor = ChatPalette.MainBg,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(24, 2, 24, 2),
        };
        modeBar.Controls.Add(modeBarTable);

        // --- assemble ------------------------------------------------------------
        var mainArea = new Panel { Dock = DockStyle.Fill, BackColor = ChatPalette.MainBg };
        mainArea.Controls.Add(_messagesPanel);
        mainArea.Controls.Add(modeBar);
        mainArea.Controls.Add(header);
        mainArea.Controls.Add(inputRoot);

        var root = new Panel { Dock = DockStyle.Fill, BackColor = ChatPalette.MainBg };
        root.Controls.Add(mainArea);
        root.Controls.Add(sidebar);
        Controls.Add(root);

        var copyItem = new ToolStripMenuItem { Text = "复制整条消息" };
        copyItem.Click += (_, _) => CopyMessageUnderMenu();
        _copyMenu.Items.Add(copyItem);
        var copySelectionItem = new ToolStripMenuItem { Text = "复制" };
        copySelectionItem.Click += (_, _) => CopySelectedUnderMenu();
        _copyMenu.Items.Add(copySelectionItem);
        var selectAllItem = new ToolStripMenuItem { Text = "全选" };
        selectAllItem.Click += (_, _) => SelectAllUnderMenu();
        _copyMenu.Items.Add(selectAllItem);

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

    // ------------------------------------------------------------ context feedback

    /// <summary>
    /// Re-reads the NX context on demand (NX UI thread — button click) and
    /// shows in the chat what was captured. Read failures are reported as
    /// failures with the reason; success is never fabricated.
    /// </summary>
    private void RefreshContextDisplay()
    {
        try
        {
            var snapshot = _controller.CaptureContextNow();
            if (snapshot != null)
                _chatLog.Add(ChatRole.Info, ChatSessionController.DescribeCapturedContext(snapshot));
            else
                _chatLog.Add(ChatRole.Info, "NX 上下文：暂不可用（未捕获到上下文）。");
        }
        catch (Exception ex)
        {
            _chatLog.Add(ChatRole.Error, $"NX 上下文读取失败：{ex.Message}");
        }
    }

    // ------------------------------------------------------------ execution mode

    /// <summary>
    /// Handles a toggle ask from the switch. The switch state is never
    /// flipped directly: the mode manager validates + persists first, and
    /// only a successful application updates the switch to the mode that is
    /// really in effect. A failing persistence keeps the previous mode and
    /// the switch snaps back with an error message. Toggling never confirms
    /// any pending modeling request and never triggers a tool.
    /// </summary>
    private void HandleModeToggleRequested()
    {
        if (_controller.IsRequestInFlight)
        {
            MessageBox.Show(this,
                "当前有请求正在进行，切换模式可能在执行中途改变授权策略。\n" +
                "请等待请求完成或取消后再切换。",
                "NX AI Assistant", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var target = _modeManager.CurrentMode == ExecutionMode.Autonomous
            ? ExecutionMode.Confirm
            : ExecutionMode.Autonomous;
        try
        {
            _modeManager.SetMode(target);
        }
        catch (Exception ex)
        {
            // Persistence failed: the in-memory mode is unchanged — restore
            // the switch to the mode that is actually in effect.
            SyncModeSwitchToEffectiveMode();
            _chatLog.Add(ChatRole.Error,
                $"模式切换失败（未生效）：{ex.Message}。当前仍为{_modeManager.CurrentMode.DisplayName()}。");
            return;
        }

        SyncModeSwitchToEffectiveMode();
        _chatLog.Add(ChatRole.Info,
            target == ExecutionMode.Autonomous
                ? "已切换到自主模式：允许清单中的建模工具（长方体/圆柱/圆锥/圆台/球体）在参数与上下文校验通过后将直接执行，不再逐次确认。歧义或非法请求仍会安全拒绝。"
                : "已切换到确认模式：所有建模操作执行前都会显示确认卡。");
    }

    /// <summary>Makes the switch show the backend's effective mode — the
    /// single source of truth is the manager (the switch's own label names
    /// the mode, so no separate state label is needed).</summary>
    private void SyncModeSwitchToEffectiveMode()
    {
        var effective = _modeManager.CurrentMode;
        _modeSwitch.SetChecked(effective == ExecutionMode.Autonomous);
    }

    // ------------------------------------------------------------ tool confirmation

    /// <summary>
    /// Describes the planar face selected in this turn's context snapshot (for
    /// the face-relative confirmation card). Null when no single planar face
    /// with a computed center/normal is present.
    /// </summary>
    private string? DescribeSelectedPlanarFace()
    {
        var snapshot = _controller.CurrentTurnContext;
        if (snapshot == null)
            return null;
        var faces = snapshot.Selection
            .Where(s => s.Kind == "face" && s.Face != null)
            .ToList();
        if (faces.Count != 1)
            return null;
        var face = faces[0].Face!;
        if (face.CenterX is not { } cx || face.CenterY is not { } cy || face.CenterZ is not { } cz ||
            face.NormalX is not { } nx || face.NormalY is not { } ny || face.NormalZ is not { } nz)
            return null;
        return $"目标面：平面\n面中心：({FormatMm(cx)}, {FormatMm(cy)}, {FormatMm(cz)})\n" +
               $"面法向（圆柱轴向）：({nx:0.###}, {ny:0.###}, {nz:0.###})";
    }

    /// <summary>
    /// Presents a pending modeling action for explicit user confirmation.
    /// Runs on the UI thread (controller continuations arrive via the WinForms
    /// SynchronizationContext). Returns the user's decision, or null when the
    /// flow is aborted — the window is closing or the request was cancelled.
    /// </summary>
    private async Task<ToolConfirmationDecision?> ShowToolConfirmationAsync(
        ToolConfirmationRequest request, CancellationToken cancellationToken)
    {
        if (IsDisposed || _detached)
            return null;
        // The dialog below is synchronous and must stay on the UI thread; the
        // yield keeps this method genuinely asynchronous for the controller's
        // await and preserves the SynchronizationContext hop semantics.
        await Task.Yield();

        // Read the work part identity now, on the NX UI thread, so the card
        // shows exactly what the executor will later re-verify.
        string? workPartFullPath = null;
        string workPartDisplay = "（当前无活动工作部件）";
        try
        {
            var snapshot = _partReader.ReadActivePart();
            if (snapshot.HasActivePart)
            {
                workPartFullPath = snapshot.FullPath;
                workPartDisplay = snapshot.FullPath ?? snapshot.DisplayName ?? "（未保存部件）";
            }
            else
            {
                MessageBox.Show(this,
                    "当前没有活动工作部件，无法创建几何。\n请先在 NX 中打开或新建一个零件。",
                    "NX AI Assistant", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return new ToolConfirmationDecision(false, null);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"读取活动工作部件失败：{ex.Message}",
                "NX AI Assistant", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return new ToolConfirmationDecision(false, null);
        }

        string operationText;
        string dimensionsText;
        string originText;
        switch (request.ParametersKind)
        {
            case ToolParametersKind.Cylinder:
            {
                var c = request.CylinderParameters!;
                if (c.Placement == CylinderPlacement.SelectedFaceCenter)
                {
                    operationText = "创建圆柱体（放置方式：选中面居中，轴沿面外法向）";
                    dimensionsText = $"直径：{FormatMm(c.Diameter)} 毫米\n高：{FormatMm(c.Height)} 毫米";
                    originText = DescribeSelectedPlanarFace() ?? "（当前选中面信息不可用，将无法执行）";
                }
                else
                {
                    operationText = "创建圆柱体（轴向 +Z）";
                    dimensionsText = $"直径：{FormatMm(c.Diameter)} 毫米\n高：{FormatMm(c.Height)} 毫米";
                    originText = c.OriginX == 0 && c.OriginY == 0 && c.OriginZ == 0
                        ? "(0, 0, 0)"
                        : $"({FormatMm(c.OriginX)}, {FormatMm(c.OriginY)}, {FormatMm(c.OriginZ)})（底面圆心）";
                }
                break;
            }
            case ToolParametersKind.Cone:
            {
                var c = request.ConeParameters!;
                if (c.TopDiameter > 0)
                {
                    operationText = "创建圆台（轴向 +Z，截头锥体）";
                    dimensionsText = $"底面直径：{FormatMm(c.BaseDiameter)} 毫米\n顶部直径：{FormatMm(c.TopDiameter)} 毫米\n高：{FormatMm(c.Height)} 毫米";
                }
                else
                {
                    operationText = "创建正圆锥体（轴向 +Z，锥顶朝上）";
                    dimensionsText = $"底面直径：{FormatMm(c.BaseDiameter)} 毫米\n高：{FormatMm(c.Height)} 毫米";
                }
                originText = c.OriginX == 0 && c.OriginY == 0 && c.OriginZ == 0
                    ? "(0, 0, 0)"
                    : $"({FormatMm(c.OriginX)}, {FormatMm(c.OriginY)}, {FormatMm(c.OriginZ)})（底面圆心）";
                break;
            }
            case ToolParametersKind.Sphere:
            {
                var s = request.SphereParameters!;
                operationText = "创建球体";
                dimensionsText = $"直径：{FormatMm(s.Diameter)} 毫米";
                originText = s.CenterX == 0 && s.CenterY == 0 && s.CenterZ == 0
                    ? "(0, 0, 0)"
                    : $"({FormatMm(s.CenterX)}, {FormatMm(s.CenterY)}, {FormatMm(s.CenterZ)})（球心）";
                break;
            }
            default:
            {
                var p = request.BlockParameters!;
                operationText = "创建长方体";
                dimensionsText = $"长：{FormatMm(p.Length)} 毫米\n宽：{FormatMm(p.Width)} 毫米\n高：{FormatMm(p.Height)} 毫米";
                originText = p.OriginX == 0 && p.OriginY == 0 && p.OriginZ == 0
                    ? "(0, 0, 0)"
                    : $"({FormatMm(p.OriginX)}, {FormatMm(p.OriginY)}, {FormatMm(p.OriginZ)})";
                break;
            }
            case ToolParametersKind.Sketch:
            {
                var s = request.SketchParameters!;
                string profileText = s.ProfileType == SketchProfileType.Rectangle
                    ? $"矩形：宽 {FormatMm(s.RectangleWidth)} × 高 {FormatMm(s.RectangleHeight)} 毫米"
                    : $"圆形：直径 {FormatMm(s.CircleDiameter)} 毫米";
                string planeText = s.PlaneMode switch
                {
                    SketchPlaneMode.SelectedPlane => "用户当前选中的平面/基准面（执行时校验：必须恰好一个）",
                    SketchPlaneMode.WcsXY => "绝对坐标 XY 平面",
                    SketchPlaneMode.WcsXZ => "绝对坐标 XZ 平面",
                    _ => "绝对坐标 YZ 平面",
                };
                operationText = "创建草图";
                dimensionsText = $"{profileText}";
                originText = s.CenterU == 0 && s.CenterV == 0
                    ? $"平面：{planeText}\n轮廓中心：平面原点"
                    : $"平面：{planeText}\n轮廓中心偏移（草图局部）：(u {FormatMm(s.CenterU)}, v {FormatMm(s.CenterV)})";
                break;
            }
            case ToolParametersKind.Extrude:
            {
                var e = request.ExtrudeParameters!;
                operationText = "拉伸草图";
                dimensionsText = $"距离：{FormatMm(e.Distance)} 毫米\n方向：{(e.Direction == ExtrudeDirection.Reverse ? "沿草图法向反向" : "沿草图平面法向")}";
                originText = $"草图引用：{e.SketchRef}";
                break;
            }
            case ToolParametersKind.Revolve:
            {
                var r = request.RevolveParameters!;
                string axisText = r.AxisMode switch
                {
                    RevolveAxisMode.SelectedAxis => "用户当前选中的直线/基准轴（执行时校验）",
                    RevolveAxisMode.WcsX => "绝对坐标 X 轴",
                    RevolveAxisMode.WcsY => "绝对坐标 Y 轴",
                    _ => "绝对坐标 Z 轴",
                };
                operationText = "创建旋转体（Revolve）";
                dimensionsText = $"角度：{FormatMm(r.AngleDeg)} 度\n轴：{axisText}（须在草图平面内）";
                originText = $"草图引用：{r.SketchRef}";
                break;
            }
            case ToolParametersKind.Boolean:
            {
                var b = request.BooleanParameters!;
                string opText = b.Operation switch
                {
                    BooleanOperationType.Unite => "并集（unite）",
                    BooleanOperationType.Subtract => "差集（subtract）：从目标体中减去工具体",
                    _ => "交集（intersect）",
                };
                operationText = $"布尔运算：{opText}";
                dimensionsText = $"目标体：{b.TargetBodyRef}\n工具体：{b.ToolBodyRef}";
                originText = "运算后工具体被消耗（合并入布尔特征），结果归属目标体";
                break;
            }
        }

        var choice = MessageBox.Show(this,
            "模型请求执行以下建模操作：\n\n" +
            $"操作：{operationText}\n" +
            $"{dimensionsText}\n" +
            $"原点：{originText}\n" +
            $"目标部件：{workPartDisplay}\n\n" +
            "确认后将在该部件中创建实体（可通过 NX 撤销）。",
            "确认建模操作",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);

        if (IsDisposed || _detached || cancellationToken.IsCancellationRequested)
            return null;

        if (choice != DialogResult.Yes)
            return new ToolConfirmationDecision(false, null);

        _pendingConfirmation = request;
        return new ToolConfirmationDecision(true, workPartFullPath);
    }

    private static string FormatMm(double value) =>
        value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

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
        _messagesPanel.Controls.Add(item);
        item.BringToFront(); // Dock=Top 子项按 z 序停靠：BringToFront 使新消息排在最底部
        UpdateSessionStatus();
        ScrollMessagesToEnd();
    }

    private void ApplyViewportWidthToAllItems()
    {
        // Single source of truth: the control tree. Items removed or disposed
        // by any path (new conversation, window teardown) are gone from
        // panel.Controls, so a ClientSizeChanged firing mid-disposal can
        // never reach a disposed item through this loop. The _detached gate
        // covers the form-teardown window where the panel itself is going
        // away with this handler still attached.
        if (_detached || IsDisposed || _messagesPanel.IsDisposed)
            return;
        int width = _messagesPanel.ClientSize.Width;
        foreach (var item in _messagesPanel.Controls.OfType<MessageItem>())
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

    /// <summary>复制光标所在消息内选中的文本（无选中则复制整条）。</summary>
    private void CopySelectedUnderMenu()
    {
        if (_copyMenu.SourceControl is RichTextBox box && box.SelectionLength > 0)
        {
            CopyTextToClipboard(box.SelectedText);
            return;
        }
        CopyMessageUnderMenu();
    }

    private void SelectAllUnderMenu()
    {
        if (_copyMenu.SourceControl is RichTextBox box)
            box.SelectAll();
    }

    private static void CopyTextToClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (ExternalException)
        {
            // 剪贴板被其他进程瞬时锁定：放弃本次复制，不弹错误。
        }
    }

    private void CopyMessageUnderMenu()
    {
        for (Control? control = _copyMenu.SourceControl; control != null; control = control.Parent)
        {
            if (control is MessageItem item)
            {
                CopyTextToClipboard(item.MessageText);
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
        // 取消在发送左侧、刷新上下文在取消左侧：三个按钮作为一个整体右对齐、垂直居中。
        _cancelButton.Left = inputBar.ClientSize.Width - _cancelButton.Width - 4;
        _cancelButton.Top = Math.Max(0, (inputBar.ClientSize.Height - _cancelButton.Height) / 2);
        _sendButton.Left = _cancelButton.Left - _sendButton.Width - 8;
        _sendButton.Top = Math.Max(0, (inputBar.ClientSize.Height - _sendButton.Height) / 2);
        _refreshContextButton.Left = _sendButton.Left - _refreshContextButton.Width - 8;
        _refreshContextButton.Top = Math.Max(0, (inputBar.ClientSize.Height - _refreshContextButton.Height) / 2);
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
            // Window is closing: no confirmed tool call may execute after this
            // point, and any pending confirmation card resolves as aborted.
            _detached = true;
            _controller.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// 单条消息卡片。用户消息为右侧圆角气泡，助手回复为左侧通栏文本，
    /// Info 居中弱化显示，错误为红色气泡。文本按视口宽度自动换行，
    /// 不使用 ListBox，因此没有系统蓝色选中高亮。
    /// internal 供生命周期测试使用（InternalsVisibleTo）。
    /// </summary>
    internal sealed class MessageItem : Panel
    {
        private const int BubblePadH = 14;
        private const int BubblePadV = 10;
        private const int SideMargin = 24;

        private readonly Control _bodyView;
        private readonly RichTextBox? _bodyRichText;
        private readonly Panel? _bubble;
        private readonly string _rawText;
        private readonly Font _bodyFont;
        private int _viewWidth;

        public ChatRole Role { get; }

        /// <summary>原始消息全文（复制整条/工具提示使用，不含渲染变换）。</summary>
        public string MessageText => _rawText;

        public MessageItem(ChatMessage message, ToolTip toolTip, ContextMenuStrip copyMenu)
        {
            Role = message.Role;
            _rawText = message.Text;
            Dock = DockStyle.Top;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            BackColor = ChatPalette.MainBg;
            Padding = new Padding(0, 5, 0, 5);

            bool isInfo = message.Role == ChatRole.Info;
            _bodyFont = isInfo ? SmallFont : BodyFont;

            // Info lines are single short status strings: keep the plain label
            // (no selection needed). User/assistant/error get a read-only
            // RichTextBox with markdown rendering — selectable and copyable.
            if (isInfo)
            {
                _bodyView = new Label
                {
                    AutoSize = true,
                    Text = message.Text,
                    Font = SmallFont,
                    ForeColor = ChatPalette.HeaderText,
                    BackColor = Color.Transparent,
                };
            }
            else
            {
                bool isError = message.Role == ChatRole.Error;
                _bodyRichText = CreateMessageTextBox(
                    message.Text, _bodyFont,
                    isError ? ChatPalette.ErrorText : ChatPalette.BodyText,
                    isError ? ChatPalette.ErrorBubble : ChatPalette.UserBubble);
                _bodyView = _bodyRichText;
            }
            _bodyView.Margin = Padding.Empty;

            bool hasBubble = message.Role is ChatRole.User or ChatRole.Error;
            if (hasBubble)
            {
                _bubble = new Panel
                {
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    BackColor = _bodyRichText!.BackColor,
                    Padding = new Padding(BubblePadH, BubblePadV, BubblePadH, BubblePadV),
                    Location = new Point(SideMargin, 5),
                };
                _bubble.Controls.Add(_bodyView);
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
                _bodyView.Location = new Point(SideMargin, 5);
                Controls.Add(_bodyView);
            }

            toolTip.SetToolTip(_bodyView, $"时间：{message.TimestampUtc.LocalDateTime:HH:mm:ss}");
            _bodyView.ContextMenuStrip = copyMenu;
            ContextMenuStrip = copyMenu;

            Layout += (_, _) => RepositionByRole();
        }

        /// <summary>
        /// Creates a borderless read-only RichTextBox whose RTF body carries
        /// the markdown rendering (bold, bullets, paragraphs). Selection and
        /// Ctrl+C work natively; the control auto-sizes to its wrapped text
        /// height via ContentsResized.
        /// </summary>
        private static RichTextBox CreateMessageTextBox(
            string text, Font font, Color textColor, Color backColor)
        {
            var box = new RichTextBox
            {
                BorderStyle = BorderStyle.None,
                BackColor = backColor,
                ForeColor = textColor,
                Font = font,
                ReadOnly = true,
                TabStop = false,
                Cursor = Cursors.IBeam,
                Margin = Padding.Empty,
                DetectUrls = false,
                EnableAutoDragDrop = false,
                ShortcutsEnabled = true,
                WordWrap = true,
                ScrollBars = RichTextBoxScrollBars.None,
            };
            ApplyMarkdownToBox(box, text, font.Size);
            box.ContentsResized += (_, e) =>
            {
                // RichTextBox needs the +2 padding around ContentsRectangle to
                // avoid a hidden last line; width is driven by the parent.
                box.ClientSize = new Size(
                    Math.Max(box.ClientSize.Width, e.NewRectangle.Width + 2),
                    e.NewRectangle.Height + 2);
            };
            return box;
        }

        private static void ApplyMarkdownToBox(RichTextBox box, string text, float fontSizePt)
        {
            box.Rtf = ChatRtfBuilder.Build(text, fontSizePt);
        }

        /// <summary>
        /// 视口宽度变化时重新计算换行宽度。RichTextBox 在设定宽度后按
        /// WordWrap 自然换行，ContentsResized 驱动高度自适应；气泡 AutoSize
        /// 随之重排，因此窗口缩放不会产生水平裁切。
        /// 存活检查：控件树移除是同步的，但窗体拆除时子视图（RichTextBox）
        /// 会先于本控件离开树被释放，期间仍可能收到宽度回调——此时必须
        /// 不再触碰已释放的视图。这是对"遍历只含存活项"的结构性保证之外
        /// 的拆除窗口防线。
        /// </summary>
        public void ApplyViewportWidth(int viewportWidth)
        {
            if (IsDisposed || UsedViewIsDisposed())
                return;
            int bubbleChrome = _bubble != null ? 2 * BubblePadH : 0;
            int roleBudget = Role switch
            {
                ChatRole.User => viewportWidth - 220 - bubbleChrome, // 用户气泡不占满整行
                ChatRole.Info => viewportWidth - 120,
                _ => viewportWidth - 2 * SideMargin - bubbleChrome,
            };
            int width = Math.Max(160, Math.Min(660, roleBudget));
            if (_viewWidth == width)
                return;
            _viewWidth = width;
            if (_bodyRichText != null)
            {
                _bodyRichText.Width = width;
                // Force a relayout so ContentsResized fires with the new wrap.
                ApplyMarkdownToBox(_bodyRichText, _rawText, _bodyFont.Size);
            }
            else
            {
                _bodyView.MaximumSize = new Size(width, 0);
            }
        }

        private bool UsedViewIsDisposed() =>
            _bodyRichText is { IsDisposed: true } || _bodyView.IsDisposed;

        private void RepositionByRole()
        {
            switch (Role)
            {
                case ChatRole.User:
                    if (_bubble != null)
                        _bubble.Left = Math.Max(SideMargin, ClientSize.Width - SideMargin - _bubble.Width);
                    break;
                case ChatRole.Info:
                    _bodyView.Left = Math.Max(SideMargin,
                        (ClientSize.Width - _bodyView.Width) / 2);
                    break;
                case ChatRole.Error:
                    if (_bubble != null)
                        _bubble.Left = SideMargin;
                    break;
                default:
                    _bodyView.Left = SideMargin;
                    break;
            }
        }
    }
}
