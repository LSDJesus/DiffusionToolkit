using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Diffusion.Common;

namespace Diffusion.Watcher;

/// <summary>
/// System tray application context for the background watcher service
/// </summary>
public class WatcherApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly WatcherService _watcherService;
    private readonly ContextMenuStrip _contextMenu;
    
    private ToolStripMenuItem _statusMenuItem;
    private ToolStripMenuItem _pauseResumeMenuItem;
    private ToolStripMenuItem _scanNowMenuItem;
    private ToolStripMenuItem _metadataScanMenuItem;

    public WatcherApplicationContext()
    {
        // Create context menu
        _contextMenu = new ContextMenuStrip();
        
        _statusMenuItem = new ToolStripMenuItem("Status: Starting...");
        _statusMenuItem.Enabled = false;
        _contextMenu.Items.Add(_statusMenuItem);
        
        _contextMenu.Items.Add(new ToolStripSeparator());
        
        _pauseResumeMenuItem = new ToolStripMenuItem("Pause Watching", null, OnPauseResume);
        _contextMenu.Items.Add(_pauseResumeMenuItem);
        
        _scanNowMenuItem = new ToolStripMenuItem("Scan Now", null, OnScanNow);
        _contextMenu.Items.Add(_scanNowMenuItem);
        
        _metadataScanMenuItem = new ToolStripMenuItem("Background Metadata: Enabled", null, OnToggleMetadataScan);
        _metadataScanMenuItem.Checked = true;
        _contextMenu.Items.Add(_metadataScanMenuItem);
        
        _contextMenu.Items.Add(new ToolStripSeparator());
        
        _contextMenu.Items.Add(new ToolStripMenuItem("Settings...", null, OnSettings));
        _contextMenu.Items.Add(new ToolStripMenuItem("View Logs...", null, OnViewLogs));
        
        _contextMenu.Items.Add(new ToolStripSeparator());
        
        _contextMenu.Items.Add(new ToolStripMenuItem("Exit", null, OnExit));

        // Create system tray icon
        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application, // TODO: Create custom icon
            ContextMenuStrip = _contextMenu,
            Visible = true,
            Text = "Diffusion Watcher - Initializing..."
        };
        
        _notifyIcon.DoubleClick += (s, e) => ShowStatusWindow();

        // Initialize watcher service
        _watcherService = new WatcherService();
        _watcherService.StatusChanged += OnWatcherStatusChanged;
        _watcherService.Start();
        
        Logger.Log("Diffusion Watcher started");
    }

    private void OnWatcherStatusChanged(object? sender, WatcherStatusEventArgs e)
    {
        // Update tray icon tooltip on UI thread
        // NotifyIcon doesn't have InvokeRequired, just set the property directly
        // WinForms will marshal automatically
        UpdateStatus(e);
    }

    private void UpdateStatus(WatcherStatusEventArgs e)
    {
        _notifyIcon.Text = $"Diffusion Watcher\n{e.Status}";
        _statusMenuItem.Text = $"Status: {e.Status}";
        
        if (e.FilesQueued > 0)
        {
            _statusMenuItem.Text += $" ({e.FilesQueued} queued)";
        }
    }

    private void OnPauseResume(object? sender, EventArgs e)
    {
        if (_watcherService.IsPaused)
        {
            _watcherService.Resume();
            _pauseResumeMenuItem.Text = "Pause Watching";
            ShowNotification("Watching Resumed", "File monitoring has been resumed.");
        }
        else
        {
            _watcherService.Pause();
            _pauseResumeMenuItem.Text = "Resume Watching";
            ShowNotification("Watching Paused", "File monitoring has been paused.");
        }
    }

    private void OnScanNow(object? sender, EventArgs e)
    {
        _watcherService.TriggerFullScan();
        ShowNotification("Scan Started", "Full quick-scan of all watched folders started.");
    }

    private void OnToggleMetadataScan(object? sender, EventArgs e)
    {
        _watcherService.BackgroundMetadataEnabled = !_watcherService.BackgroundMetadataEnabled;
        _metadataScanMenuItem.Checked = _watcherService.BackgroundMetadataEnabled;
        _metadataScanMenuItem.Text = _watcherService.BackgroundMetadataEnabled 
            ? "Background Metadata: Enabled" 
            : "Background Metadata: Disabled";
    }

    private void OnSettings(object? sender, EventArgs e)
    {
        // TODO: Show settings dialog
        MessageBox.Show("Settings dialog will be implemented here.\n\nFor now, configure watched folders in Diffusion Toolkit.", 
            "Settings", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void OnViewLogs(object? sender, EventArgs e)
    {
        try
        {
            // Build log path manually
            var logPath = Path.Combine(AppInfo.AppDataPath, "logs", "log.txt");
            if (File.Exists(logPath))
            {
                Process.Start(new ProcessStartInfo("notepad.exe", logPath) { UseShellExecute = true });
            }
            else
            {
                MessageBox.Show("Log file not found.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error opening log file: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnExit(object? sender, EventArgs e)
    {
        var result = MessageBox.Show(
            "Stop watching folders?\n\nNew images won't be indexed until you start the watcher again.",
            "Exit Diffusion Watcher",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (result == DialogResult.Yes)
        {
            _watcherService.Stop();
            _notifyIcon.Visible = false;
            Logger.Log("Diffusion Watcher stopped by user");
            Application.Exit();
        }
    }

    private void ShowStatusWindow()
    {
        // TODO: Show detailed status window
        var stats = _watcherService.GetStatistics();
        MessageBox.Show(
            $"Diffusion Watcher Status\n\n" +
            $"Watching: {stats.WatchedFolders} folders\n" +
            $"Indexed: {stats.TotalImages:N0} images\n" +
            $"Quick-scanned: {stats.QuickScannedImages:N0}\n" +
            $"Metadata extracted: {stats.MetadataScannedImages:N0}\n" +
            $"Files queued: {stats.FilesInQueue}\n\n" +
            $"Background metadata: {(stats.MetadataScanEnabled ? "Enabled" : "Disabled")}",
            "Diffusion Watcher",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private void ShowNotification(string title, string message)
    {
        _notifyIcon.ShowBalloonTip(3000, title, message, ToolTipIcon.Info);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _watcherService?.Dispose();
            _notifyIcon?.Dispose();
            _contextMenu?.Dispose();
        }
        base.Dispose(disposing);
    }
}
