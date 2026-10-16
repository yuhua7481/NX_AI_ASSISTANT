using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace NXAI.Assistant.UI;

/// <summary>
/// Reusable WinForms toggle switch for the execution mode, drawn in the
/// project's palette: OFF = gray track, knob left, label "确认模式";
/// ON = orange track, knob right, label "自主模式". The state is never
/// conveyed by color alone — the text label always names the current mode.
///
/// The control only raises <see cref="ToggleRequested"/>; the owning form
/// decides whether the switch may actually change (in-flight request,
/// persistence failure) and calls <see cref="SetChecked"/> with the mode
/// that is really in effect. Clicking never silently flips the state.
///
/// DPI: all geometry is computed from the control's DeviceDpi in OnPaint, so
/// the switch scales with the form's font autoscaling; no absolute pixels.
/// Keyboard: Space/Enter toggle (Accessibility: role Switch, named label).
/// </summary>
public sealed class ModeToggleSwitch : Control
{
    internal static readonly Color TrackOff = ChatPalette.InputBorder;
    internal static readonly Color TrackOn = ChatPalette.PrimaryButton;
    internal static readonly Color TrackOffHover = ChatPalette.SecondaryButtonHover;
    internal static readonly Color TrackOnHover = ChatPalette.PrimaryButtonHover;
    internal static readonly Color Knob = Color.White;
    internal static readonly Color LabelOff = ChatPalette.HeaderText;
    internal static readonly Color LabelOn = ChatPalette.PrimaryButton;

    private bool _checked;
    private bool _hover;

    public ModeToggleSwitch()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        Cursor = Cursors.Hand;
        BackColor = Color.Transparent;
        AutoSize = true;
        AccessibleRole = AccessibleRole.CheckButton;
        UpdateAccessibleName();
    }

    /// <summary>Raised when the user asks to toggle. The state has NOT
    /// changed yet; the owner validates and calls <see cref="SetChecked"/>.</summary>
    public event EventHandler? ToggleRequested;

    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value)
                return;
            _checked = value;
            UpdateAccessibleName();
            Invalidate();
        }
    }

    /// <summary>Sets the switch to the mode that is actually in effect
    /// (owner-called after validation or after a failed persistence attempt).</summary>
    public void SetChecked(bool value) => Checked = value;

    /// <summary>Track+knob width in 96-dpi pixels; scaled at draw time.</summary>
    private const int TrackWidth96 = 44;
    private const int TrackHeight96 = 22;
    private const int KnobInset96 = 3;
    private const int LabelGap96 = 8;

    private string ModeText => _checked ? "自主模式" : "确认模式";

    private void UpdateAccessibleName()
    {
        AccessibleName = $"执行模式：{ModeText}。切换确认模式与自主模式。";
        AccessibleDefaultActionDescription = "切换执行模式";
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        float scale = DeviceDpi / 96f;
        using var font = BuildLabelFont();
        int textWidth = TextRenderer.MeasureText(ModeText, font).Width;
        int trackWidth = (int)Math.Round(TrackWidth96 * scale);
        int gap = (int)Math.Round(LabelGap96 * scale);
        int trackHeight = (int)Math.Round(TrackHeight96 * scale);
        return new Size(trackWidth + gap + textWidth + 2, trackHeight + 2);
    }

    private Font BuildLabelFont() => new("Microsoft YaHei UI", 9F, FontStyle.Regular);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        float scale = DeviceDpi / 96f;
        int trackWidth = (int)Math.Round(TrackWidth96 * scale);
        int trackHeight = (int)Math.Round(TrackHeight96 * scale);
        int knobInset = (int)Math.Round(KnobInset96 * scale);
        int knobSize = trackHeight - 2 * knobInset;

        // Vertical centering within the control's own height.
        int trackY = Math.Max(0, (Height - trackHeight) / 2);
        var trackRect = new RectangleF(0.5f, trackY + 0.5f, trackWidth - 1, trackHeight - 1);

        Color track = _checked
            ? (_hover ? TrackOnHover : TrackOn)
            : (_hover ? TrackOffHover : TrackOff);
        using (var brush = new SolidBrush(track))
            g.FillPath(brush, RoundedPath(trackRect, trackHeight / 2f));

        // Knob: slides from left (confirm) to right (autonomous).
        float knobX = _checked
            ? trackRect.Right - knobInset - knobSize
            : trackRect.Left + knobInset;
        using (var knobBrush = new SolidBrush(Knob))
            g.FillEllipse(knobBrush, knobX, trackY + knobInset, knobSize, knobSize);

        // The mode text is the authoritative state indicator.
        using var font = BuildLabelFont();
        var textColor = _checked ? LabelOn : LabelOff;
        TextRenderer.DrawText(g, ModeText, font,
            new Rectangle(trackWidth + (int)Math.Round(LabelGap96 * scale), 0,
                Width - trackWidth - (int)Math.Round(LabelGap96 * scale), Height),
            textColor, BackColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }

    private static GraphicsPath RoundedPath(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnClick(EventArgs e)
    {
        ToggleRequested?.Invoke(this, EventArgs.Empty);
        base.OnClick(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter)
        {
            e.Handled = true;
            ToggleRequested?.Invoke(this, EventArgs.Empty);
        }
        base.OnKeyDown(e);
    }
}
