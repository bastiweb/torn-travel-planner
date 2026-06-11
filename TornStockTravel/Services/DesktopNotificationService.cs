using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace TornStockTravel.Services;

public sealed class DesktopNotificationService : IDisposable
{
    private const int MaximumBalloonTextLength = 255;
    private readonly Forms.NotifyIcon _notifyIcon;

    public DesktopNotificationService()
    {
        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? string.Empty) ?? Drawing.SystemIcons.Application,
            Text = "Torn Stock Travel",
            Visible = true
        };
    }

    public void Show(string title, string message)
    {
        _notifyIcon.ShowBalloonTip(8000, title, TrimForBalloon(message), Forms.ToolTipIcon.Info);
    }

    private static string TrimForBalloon(string message)
    {
        string normalized = message.Trim();
        return normalized.Length <= MaximumBalloonTextLength
            ? normalized
            : $"{normalized[..(MaximumBalloonTextLength - 3)]}...";
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
