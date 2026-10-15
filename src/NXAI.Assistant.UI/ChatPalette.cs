using System.Drawing;

namespace NXAI.Assistant.UI;

/// <summary>
/// Shared neutral, light color palette for the chat shell and settings dialog.
/// Kept in one place so the sidebar, bubbles and buttons stay consistent.
/// </summary>
internal static class ChatPalette
{
    internal static readonly Color Sidebar = Color.FromArgb(0xF2, 0xEF, 0xE6);
    internal static readonly Color SidebarBorder = Color.FromArgb(0xE3, 0xDF, 0xD2);
    internal static readonly Color MainBg = Color.FromArgb(0xFA, 0xF9, 0xF5);
    internal static readonly Color Card = Color.White;
    internal static readonly Color UserBubble = Color.FromArgb(0xEA, 0xE5, 0xD9);
    internal static readonly Color ErrorBubble = Color.FromArgb(0xFB, 0xEC, 0xEB);
    internal static readonly Color ErrorText = Color.FromArgb(0xA3, 0x3B, 0x36);
    internal static readonly Color HeaderText = Color.FromArgb(0x6E, 0x69, 0x60);
    internal static readonly Color BodyText = Color.FromArgb(0x30, 0x2E, 0x2B);
    internal static readonly Color PrimaryButton = Color.FromArgb(0xD9, 0x77, 0x57);
    internal static readonly Color PrimaryButtonHover = Color.FromArgb(0xC4, 0x66, 0x47);
    internal static readonly Color SecondaryButton = Color.FromArgb(0xE7, 0xE3, 0xD7);
    internal static readonly Color SecondaryButtonHover = Color.FromArgb(0xDD, 0xD8, 0xC9);
    internal static readonly Color InputMuted = Color.FromArgb(0xF2, 0xF0, 0xEA);
    internal static readonly Color InputBorder = Color.FromArgb(0xD5, 0xD0, 0xC4);
}
