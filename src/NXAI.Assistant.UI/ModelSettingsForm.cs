using System;
using System.Windows.Forms;

using NXAI.Assistant.Core;

namespace NXAI.Assistant.UI;

/// <summary>
/// 模型配置对话窗口。提供 API Base URL、API Key（密码掩码）和模型名称的编辑。
/// 读取/保存配置会通过 ModelService 的持久化方法完成。
/// </summary>
public partial class ModelSettingsForm : Form
{
    private readonly ModelService _modelService;
    private TextBox _txtApiBaseUrl;
    private TextBox _txtApiKey;
    private TextBox _txtModelName;
    private Button _btnSave;
    private Button _btnCancel;

    /// <summary>
    /// 构造函数。预先加载已保存的配置（如果存在）。
    /// </summary>
    public ModelSettingsForm()
    {
        _modelService = new ModelService();

        Initialize();

        // 尝试加载已保存的配置
        var cfg = ModelService.LoadConfiguration();
        if (cfg.HasValue)
        {
            // 显式展开值，避免编译器将 cfg.Value 成员视为可能为 null
            string apiBaseUrl = cfg.Value.apiBaseUrl ?? "";
            string modelName = cfg.Value.modelName ?? "";

            // 在 UI 中显示已保存的 API Base URL 和模型名称
            _txtApiBaseUrl.Text = apiBaseUrl;
            _txtModelName.Text = modelName;
            // API Key：从已保存的加密文件加载，此处仅显示占位符，
            // 实际密钥已在 DPAPI 保护的文件中，此处不解密显示，防止泄露。
            _txtApiKey.Text = "<已保存的密钥>";
            _txtApiKey.ReadOnly = true;
        }
        else
        {
            // 无保存的配置，显示空白
            _txtApiBaseUrl.Text = "";
            _txtApiKey.Text = "";
            _txtModelName.Text = "";
        }

        // 设置窗口属性
        Text = "模型 API 配置";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        AcceptButton = _btnSave;
        CancelButton = _btnCancel;
    }

    /// <summary>
    /// 获取用户确认后的配置值。
    /// </summary>
    /// <returns>(ApiBaseUrl, ApiKey, ModelName)</returns>
    public (string apiBaseUrl, string apiKey, string modelName) GetConfig()
    {
        return (_txtApiBaseUrl.Text.Trim(), _txtApiKey.Text.Trim(), _txtModelName.Text.Trim());
    }

    /// <summary>
    /// 初始化控件。
    /// </summary>
    private void Initialize()
    {
        this.SuspendLayout();
        this.Text = "模型 API 配置";
        this.StartPosition = FormStartPosition.CenterParent;
        this.FormBorderStyle = FormBorderStyle.FixedDialog;
        this.MinimizeBox = false;
        this.MaximizeBox = false;
        this.ShowInTaskbar = false;

        // API Base URL
        Label lblApiBase = new Label { Text = "API Base URL:", Left = 20, Top = 20, AutoSize = true };
        _txtApiBaseUrl = new TextBox { Left = 130, Top = 19, Width = 260, Text = "" };

        // API Key（密码掩码）
        Label lblApiKey = new Label { Text = "API Key:", Left = 20, Top = 50, AutoSize = true };
        _txtApiKey = new TextBox { Left = 130, Top = 49, Width = 260, PasswordChar = '●', Text = "" };

        // 模型名称
        Label lblModelName = new Label { Text = "模型名称:", Left = 20, Top = 80, AutoSize = true };
        _txtModelName = new TextBox { Left = 130, Top = 79, Width = 260, Text = "" };

        // 按钮
        _btnSave = new Button { Text = "保存", Left = 20, Top = 120, Width = 100, DialogResult = DialogResult.OK };
        _btnCancel = new Button { Text = "取消", Left = 140, Top = 120, Width = 100, DialogResult = DialogResult.Cancel };

        // 添加控件
        this.Controls.Add(_txtApiBaseUrl);
        this.Controls.Add(lblApiBase);
        this.Controls.Add(_txtApiKey);
        this.Controls.Add(lblApiKey);
        this.Controls.Add(_txtModelName);
        this.Controls.Add(lblModelName);
        this.Controls.Add(_btnSave);
        this.Controls.Add(_btnCancel);

        this.ResumeLayout(false);
        this.PerformLayout();
    }
}