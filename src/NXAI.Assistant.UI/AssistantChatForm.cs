using NXAI.Assistant.Core;

namespace NXAI.Assistant.UI;

/// <summary>
/// Modeless floating chat shell opened from the NX main UI entry.
/// Runs on the NX UI thread: NX's message loop pumps this form, so the
/// NX main window stays interactive while it is shown.
/// Phase 0 scope: show active work part info + local chat shell.
/// </summary>
public sealed class AssistantChatForm : Form
{
    private readonly IActivePartReader _partReader;
    private readonly ChatLog _chatLog = new();

    private readonly ListBox _messageList;
    private readonly TextBox _inputBox;
    private readonly Label _partNameValue;
    private readonly Label _partPathValue;
    private readonly Label _partUnitsValue;
    private readonly Label _partStatusLabel;
    private int _partRowCount;

    public AssistantChatForm(IActivePartReader partReader)
    {
        _partReader = partReader ?? throw new ArgumentNullException(nameof(partReader));

        Text = "NX AI Assistant";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(560, 520);
        Size = new Size(640, 640);
        KeyPreview = true;

        // --- active part panel -------------------------------------------------
        var partPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(8),
            ColumnCount = 2,
        };
        _partNameValue = AddPartRow(partPanel, "名称：");
        _partPathValue = AddPartRow(partPanel, "路径：");
        _partUnitsValue = AddPartRow(partPanel, "单位：");
        _partStatusLabel = AddPartRow(partPanel, "状态：");

        var refreshButton = new Button { Text = "刷新", AutoSize = true, Anchor = AnchorStyles.Left };
        partPanel.Controls.Add(new Label { AutoSize = true }, 0, _partRowCount);
        partPanel.Controls.Add(refreshButton, 1, _partRowCount);
        refreshButton.Click += (_, _) => RefreshActivePart();

        var partGroup = new Panel { Dock = DockStyle.Top, AutoSize = true };
        partGroup.Controls.Add(partPanel);

        // --- chat log ----------------------------------------------------------
        _messageList = new ListBox
        {
            Dock = DockStyle.Fill,
            IntegralHeight = false,
            HorizontalScrollbar = true,
            Font = new Font("Microsoft YaHei UI", 9F),
        };
        _chatLog.MessageAdded += (_, message) =>
        {
            _messageList.Items.Add($"[{message.TimestampUtc.LocalDateTime:HH:mm:ss}] {RoleLabel(message.Role)}: {message.Text}");
            _messageList.TopIndex = _messageList.Items.Count - 1;
        };

        var chatGroup = new GroupBox
        {
            Text = "对话",
            Dock = DockStyle.Fill,
            Padding = new Padding(8),
        };
        chatGroup.Controls.Add(_messageList);

        // --- input -------------------------------------------------------------
        var inputPanel = new Panel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8) };
        _inputBox = new TextBox
        {
            Multiline = true,
            Height = 60,
            Dock = DockStyle.Fill,
        };
        var sendButton = new Button
        {
            Text = "发送",
            Dock = DockStyle.Right,
            Width = 80,
        };
        inputPanel.Controls.Add(_inputBox);
        inputPanel.Controls.Add(sendButton);
        _inputBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && !e.Shift && !ModifierKeys.HasFlag(Keys.Control))
            {
                e.SuppressKeyPress = true;
                SendCurrentInput();
            }
        };
        sendButton.Click += (_, _) => SendCurrentInput();

        Controls.Add(chatGroup);
        Controls.Add(partGroup);
        Controls.Add(inputPanel);

        Activated += (_, _) => RefreshActivePart();
        RefreshActivePart();
        _chatLog.Add(ChatRole.Info, "阶段 0 壳：模型 API 将在阶段 1 接入。当前窗口可读取活动部件信息。");
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

        _partNameValue.Text = snapshot.DisplayName ?? "（未知）";
        _partPathValue.Text = snapshot.FullPath ?? "（未知）";
        _partUnitsValue.Text = snapshot.Units ?? "（未知）";
        _partStatusLabel.Text = snapshot.HasActivePart
            ? snapshot.StatusMessage ?? "已读取当前活动工作部件。"
            : snapshot.StatusMessage ?? "当前没有活动工作部件。";
    }

    private void SendCurrentInput()
    {
        var text = _inputBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        _chatLog.Add(ChatRole.User, text);
        _inputBox.Clear();
        _chatLog.Add(ChatRole.Info, "阶段 0 尚未接入模型 API，此消息仅保留在本地对话列表。");
    }

    private Label AddPartRow(TableLayoutPanel panel, string labelText)
    {
        var label = new Label { Text = labelText, AutoSize = true, Anchor = AnchorStyles.Left };
        var value = new Label { Text = "（未读取）", AutoSize = true, MaximumSize = new Size(560, 0) };
        panel.Controls.Add(label, 0, _partRowCount);
        panel.Controls.Add(value, 1, _partRowCount);
        _partRowCount++;
        return value;
    }

    private static string RoleLabel(ChatRole role) => role switch
    {
        ChatRole.User => "我",
        ChatRole.Assistant => "助手",
        ChatRole.System => "系统",
        ChatRole.Error => "错误",
        _ => "提示",
    };
}
