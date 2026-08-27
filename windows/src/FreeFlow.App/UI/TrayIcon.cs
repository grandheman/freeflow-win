using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using FreeFlow.Core.Shortcuts;

namespace FreeFlow.App.UI;

/// <summary>
/// The notification-area presence: status, quick toggles, and the way into Settings.
/// </summary>
/// <remarks>
/// <para>
/// Windows counterpart to the macOS menu bar item in <c>Sources/MenuBarView.swift</c>.
/// </para>
/// <para>
/// Windows Forms provides the tray API here because WPF has no equivalent, and taking
/// a UI package dependency for one icon would not be a good trade.
/// </para>
/// <para>
/// The icon is drawn in code rather than shipped as a resource so it can be redrawn
/// in the signal color while recording and stay crisp at any DPI.
/// </para>
/// </remarks>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly AppState _state;

    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _shortcutItem;
    private readonly ToolStripMenuItem _pasteAgainItem;

    private Icon? _idleIcon;
    private Icon? _liveIcon;

    public event Action? OpenSettingsRequested;
    public event Action? OpenDebugPanelRequested;
    public event Action? QuitRequested;

    public TrayIcon(AppState state)
    {
        _state = state;

        _statusItem = new ToolStripMenuItem("Ready") { Enabled = false };
        _shortcutItem = new ToolStripMenuItem(ShortcutSummary()) { Enabled = false };
        _pasteAgainItem = new ToolStripMenuItem("Paste last transcript again", null,
            (_, _) => _ = _state.PasteAgainAsync());

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(_shortcutItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_pasteAgainItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Settings", null, (_, _) => OpenSettingsRequested?.Invoke()));
        menu.Items.Add(new ToolStripMenuItem("Pipeline history", null, (_, _) => OpenDebugPanelRequested?.Invoke()));
        menu.Items.Add(new ToolStripMenuItem("Quit FreeFlow", null, (_, _) => QuitRequested?.Invoke()));

        _idleIcon = BuildIcon(Color.FromArgb(0xB8, 0xBA, 0xC4));
        _liveIcon = BuildIcon(Color.FromArgb(0xFF, 0x6B, 0x4A));

        _notifyIcon = new NotifyIcon
        {
            Icon = _idleIcon,
            Text = "FreeFlow",
            Visible = true,
            ContextMenuStrip = menu,
        };

        // Double-click is the conventional Windows shortcut into a tray app's window.
        _notifyIcon.DoubleClick += (_, _) => OpenSettingsRequested?.Invoke();
    }

    public void UpdateStatus()
    {
        _statusItem.Text = _state.StatusMessage;
        _shortcutItem.Text = ShortcutSummary();

        var isLive = _state.Status == PipelineStatus.Recording;
        _notifyIcon.Icon = isLive ? _liveIcon : _idleIcon;

        // The tooltip is capped at 63 characters by the shell; longer text is dropped
        // entirely rather than truncated, so it is trimmed here.
        var tooltip = $"FreeFlow — {_state.StatusMessage}";
        _notifyIcon.Text = tooltip.Length > 63 ? tooltip[..63] : tooltip;
    }

    public void ShowError(string message)
    {
        UpdateStatus();
        _notifyIcon.ShowBalloonTip(5000, "FreeFlow", message, ToolTipIcon.Warning);
    }

    private string ShortcutSummary()
    {
        var hold = _state.Settings.HoldShortcut;
        return hold.IsDisabled
            ? "No shortcut set"
            : $"Hold {hold.DisplayName} to talk";
    }

    /// <summary>
    /// Draws the tray glyph: a microphone capsule, sized for a 16 pixel slot.
    /// </summary>
    /// <remarks>
    /// Kept to one solid shape with no interior detail, because tray icons are
    /// rendered small and anything finer turns to mush.
    /// </remarks>
    private static Icon BuildIcon(Color color)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            using var brush = new SolidBrush(color);
            using var pen = new Pen(color, 2.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };

            // Capsule body.
            var body = new Rectangle(11, 5, 10, 15);
            using var path = new GraphicsPath();
            path.AddArc(body.X, body.Y, body.Width, body.Width, 180, 180);
            path.AddArc(body.X, body.Bottom - body.Width, body.Width, body.Width, 0, 180);
            path.CloseFigure();
            graphics.FillPath(brush, path);

            // Cradle and stand.
            graphics.DrawArc(pen, 7, 12, 18, 14, 20, 140);
            graphics.DrawLine(pen, 16, 24, 16, 27);
        }

        return Icon.FromHandle(bitmap.GetHicon());
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _idleIcon?.Dispose();
        _liveIcon?.Dispose();
        _idleIcon = null;
        _liveIcon = null;
    }
}
