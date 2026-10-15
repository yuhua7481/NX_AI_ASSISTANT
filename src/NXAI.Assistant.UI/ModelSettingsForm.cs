using System;
using System.Drawing;
using System.Windows.Forms;

using NXAI.Assistant.Core;

namespace NXAI.Assistant.UI;

/// <summary>
/// 模型配置对话窗口。编辑 API Base URL、API Key（密码掩码）和模型名称，
/// 点击“保存”时立即通过 ModelService 持久化（API Key 经 DPAPI 加密落盘）。
/// Key 输入框留空表示保留已保存的密钥（不会把占位符写进配置）。
/// 布局采用两列 TableLayoutPanel：标签列固定宽度、输入列随窗口伸展，
/// 按钮区固定在底部，窗口可缩放（MinimumSize 兜底），任何宽度下控件均完整可见。
/// </summary>
public sealed class ModelSettingsForm : Form
{
    private const int LabelColumnWidth = 132;

    private readonly TextBox _txtApiBaseUrl = new();
    private readonly TextBox _txtApiKey = new();
    private readonly TextBox _txtModelName = new();
    private readonly Button _btnSave = new();
    private readonly Button _btnCancel = new();
    private readonly Label _lblHint = new();
    private readonly Label _lblStatus = new();

    public ModelSettingsForm()
    {
        BuildLayout();
        LoadSavedValues();
        // 打开时焦点落在 Base URL 输入框；WinForms TextBox 获得焦点不会自动
        // 全选或修改内容，已预填的值保持原样。
        ActiveControl = _txtApiBaseUrl;
    }

    /// <summary>
    /// 初始化控件。Key 输入框留空 + 占位提示 = 保留已保存密钥。
    /// </summary>
    private void BuildLayout()
    {
        SuspendLayout();

        Text = "模型 API 配置";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Font;
        AcceptButton = _btnSave;
        CancelButton = _btnCancel;
        MinimumSize = new Size(580, 340);
        Size = new Size(620, 360);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20, 20, 20, 16),
            ColumnCount = 2,
            RowCount = 7,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LabelColumnWidth));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // API Base URL
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // API Key
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // Key 留空提示
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 模型名称
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 状态（校验/保存错误）
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f)); // 弹性留白
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 按钮区

        // --- 输入行：标签列 + 伸展的输入框 -----------------------------------
        root.Controls.Add(MakeLabel("API Base URL:"), 0, 0);
        _txtApiBaseUrl.Dock = DockStyle.Fill;
        _txtApiBaseUrl.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _txtApiBaseUrl.TabIndex = 0;
        root.Controls.Add(_txtApiBaseUrl, 1, 0);

        root.Controls.Add(MakeLabel("API Key:"), 0, 1);
        _txtApiKey.Dock = DockStyle.Fill;
        _txtApiKey.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _txtApiKey.UseSystemPasswordChar = true;
        _txtApiKey.TabIndex = 1;
        root.Controls.Add(_txtApiKey, 1, 1);

        _lblHint.Text = "API Key 留空表示保留已保存的密钥；密钥经 DPAPI 加密存储，不会回显。";
        _lblHint.AutoSize = true;
        _lblHint.MaximumSize = new Size(400, 0);
        _lblHint.ForeColor = ChatPalette.HeaderText;
        _lblHint.Font = new Font("Microsoft YaHei UI", 8.25F);
        _lblHint.Margin = new Padding(LabelColumnWidth + 6, 2, 3, 6);
        root.Controls.Add(_lblHint, 0, 2);
        root.SetColumnSpan(_lblHint, 2);

        root.Controls.Add(MakeLabel("模型名称:"), 0, 3);
        _txtModelName.Dock = DockStyle.Fill;
        _txtModelName.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _txtModelName.TabIndex = 2;
        root.Controls.Add(_txtModelName, 1, 3);

        _lblStatus.AutoSize = true;
        _lblStatus.MaximumSize = new Size(400, 0);
        _lblStatus.ForeColor = ChatPalette.ErrorText;
        _lblStatus.Font = new Font("Microsoft YaHei UI", 8.25F);
        _lblStatus.Margin = new Padding(LabelColumnWidth + 6, 6, 3, 3);
        _lblStatus.Visible = false;
        root.Controls.Add(_lblStatus, 0, 4);
        root.SetColumnSpan(_lblStatus, 2);

        // --- 底部按钮区：始终完整显示，与输入区之间有弹性留白 -------------------
        var buttonStrip = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Margin = new Padding(0),
        };
        _btnSave.Text = "保存";
        _btnSave.AutoSize = true;
        _btnSave.MinimumSize = new Size(104, 32);
        _btnSave.TabIndex = 3;
        StylePrimaryButton(_btnSave);
        _btnSave.Click += OnSaveClick;

        _btnCancel.Text = "取消";
        _btnCancel.AutoSize = true;
        _btnCancel.MinimumSize = new Size(88, 32);
        _btnCancel.TabIndex = 4;
        StyleSecondaryButton(_btnCancel);
        _btnCancel.DialogResult = DialogResult.Cancel;

        buttonStrip.Controls.Add(_btnSave);
        buttonStrip.Controls.Add(_btnCancel);
        root.Controls.Add(buttonStrip, 1, 6);

        Controls.Add(root);
        ResumeLayout(false);
        PerformLayout();
    }

    /// <summary>预加载已保存的配置。密钥不回显，防止泄露。</summary>
    private void LoadSavedValues()
    {
        var cfg = ModelService.LoadConfiguration();
        if (cfg.HasValue)
        {
            _txtApiBaseUrl.Text = cfg.Value.apiBaseUrl;
            _txtModelName.Text = cfg.Value.modelName;
        }
        else if (File.Exists(ModelService.DefaultConfigPath))
        {
            // 文件存在但读不出来：旧版本格式或已损坏。解释原因，绝不改写原文件。
            ShowStatus(
                "检测到现有配置文件无法读取（旧版本格式或已损坏）。\n" +
                "请重新填写并保存；原文件不会被修改。");
        }
    }

    /// <summary>保存按钮：校验输入并立即持久化；失败时窗口保持打开、输入保留。</summary>
    private void OnSaveClick(object? sender, EventArgs e)
    {
        string apiBaseUrl = _txtApiBaseUrl.Text.Trim();
        string modelName = _txtModelName.Text.Trim();
        string typedKey = _txtApiKey.Text.Trim();

        if (apiBaseUrl.Length == 0)
        {
            ShowFieldError(_txtApiBaseUrl, "请填写 API Base URL。");
            return;
        }
        if (modelName.Length == 0)
        {
            ShowFieldError(_txtModelName, "请填写模型名称。");
            return;
        }

        // Key 留空：沿用已保存（DPAPI 保护）的密钥，而不是把空串写进配置。
        string apiKey = typedKey;
        if (apiKey.Length == 0)
        {
            var existing = ModelService.LoadConfiguration();
            if (!existing.HasValue || existing.Value.apiKey.Length == 0)
            {
                ShowFieldError(_txtApiKey, "没有已保存的密钥，请填写 API Key。");
                return;
            }
            apiKey = existing.Value.apiKey;
        }

        try
        {
            ModelService.SaveConfiguration(apiBaseUrl, apiKey, modelName);
        }
        catch (Exception ex)
        {
            // 磁盘/权限等保存失败必须反馈给用户，不能让异常逃逸到 WinForms 全局处理器。
            // 提示中只包含异常消息，绝不含密钥内容。
            ShowStatus($"配置保存失败：{ex.Message}");
            return;
        }

        DialogResult = DialogResult.OK;
    }

    private void ShowFieldError(TextBox field, string message)
    {
        ShowStatus(message);
        field.Focus();
        DialogResult = DialogResult.None;
    }

    private void ShowStatus(string message)
    {
        _lblStatus.Text = message;
        _lblStatus.Visible = true;
        DialogResult = DialogResult.None;
    }

    private static Label MakeLabel(string text)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 9, 3, 3),
            Font = new Font("Microsoft YaHei UI", 9F),
        };
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
}
