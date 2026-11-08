using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("ARC Lobby Mute")]
[assembly: AssemblyDescription("Automatically mute ARC Raiders in the lobby and unmute in a match.")]

namespace ArcLobbyMute
{
    internal static class Program
    {
        internal const string Title = "ARC Lobby Mute";
        internal const int ShowMessage = 0x8001;
        internal const int ExitMessage = 0x8002;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string className, string windowName);
        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

        [STAThread]
        private static void Main(string[] args)
        {
            bool created;
            using (var mutex = new Mutex(true, @"Local\ArcLobbyMute.76F0B036", out created))
            {
                bool exit = Array.IndexOf(args, "--exit") >= 0;
                if (!created || exit)
                {
                    IntPtr window = FindWindow(null, Title);
                    if (window != IntPtr.Zero)
                        PostMessage(window, exit ? ExitMessage : ShowMessage, IntPtr.Zero, IntPtr.Zero);
                    return;
                }
                try
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    using (var form = new MainForm(Array.IndexOf(args, "--background") >= 0))
                        Application.Run(form);
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }

    internal static class Settings
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppKey = @"Software\ArcLobbyMute";
        public static bool Enabled
        {
            get
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(AppKey))
                    return key == null || Convert.ToInt32(key.GetValue("Enabled", 1)) != 0;
            }
            set
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(AppKey))
                    key.SetValue("Enabled", value ? 1 : 0, RegistryValueKind.DWord);
            }
        }
        public static bool StartWithWindows
        {
            get
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
                    return key != null && string.Equals(key.GetValue("ArcLobbyMute") as string,
                        StartupCommand, StringComparison.OrdinalIgnoreCase);
            }
            set
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (value) key.SetValue("ArcLobbyMute", StartupCommand);
                    else key.DeleteValue("ArcLobbyMute", false);
                }
            }
        }
        private static string StartupCommand { get { return "\"" + Application.ExecutablePath + "\" --background"; } }
    }

    internal static class ActivityLog
    {
        internal static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"ArcLobbyMute\activity.log");
        public static void Write(string message)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 131072)
                    File.WriteAllText(LogPath, "");
                File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal sealed class MainForm : Form
    {
        private readonly GameMonitor monitor = new GameMonitor();
        private readonly StateDetector detector = new StateDetector();
        private readonly AudioController audio = new AudioController();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private readonly NotifyIcon tray = new NotifyIcon();
        private readonly Label stateLabel;
        private readonly Label detailLabel;
        private readonly StatusDot indicator;
        private readonly MainView view;
        private readonly CheckBox enabledCheck;
        private readonly CheckBox startupCheck;
        private readonly ToolStripMenuItem enabledMenu;
        private readonly ToolStripMenuItem statusMenu;
        private readonly Icon appIcon;
        private bool background;
        private bool exiting;
        private bool updatingControls;
        private bool disposedResources;
        private bool enabled;
        private string lastReport;
        private AppTheme theme;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        public MainForm(bool background)
        {
            SuspendLayout();
            this.background = background;
            Text = Program.Title;
            appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Icon = appIcon;
            Font = new Font("Segoe UI", 10F);
            // This system-DPI-aware window scales layout metrics explicitly; text retains native point sizes.
            AutoScaleMode = AutoScaleMode.None;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            ClientSize = new Size(480, 360);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            view = new MainView();
            stateLabel = view.StateLabel;
            detailLabel = view.DetailLabel;
            indicator = view.Indicator;
            enabledCheck = view.EnabledCheck;
            startupCheck = view.StartupCheck;
            view.HideButton.Click += delegate { Hide(); };
            Controls.Add(view);
            MinimumSize = SizeFromClientSize(new Size(view.LayoutWidth, 0));
            ResumeLayout(true);

            var menu = new ContextMenuStrip();
            statusMenu = new ToolStripMenuItem("Checking game...") { Enabled = false };
            var show = new ToolStripMenuItem("Open ARC Lobby Mute");
            show.Click += delegate { ShowWindow(); };
            enabledMenu = new ToolStripMenuItem("Automatically mute lobby") { CheckOnClick = true };
            var quit = new ToolStripMenuItem("Quit (restore game audio)");
            quit.Click += delegate { Quit(); };
            menu.Items.AddRange(new ToolStripItem[] { statusMenu, new ToolStripSeparator(), show, enabledMenu, new ToolStripSeparator(), quit });
            tray.Icon = appIcon;
            tray.Text = Program.Title;
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { ShowWindow(); };
            tray.Visible = true;

            enabled = true;
            try { enabled = Settings.Enabled; startupCheck.Checked = Settings.StartWithWindows; }
            catch (Exception ex) { ActivityLog.Write("Could not read preferences: " + ex.Message); }
            enabledCheck.Checked = enabledMenu.Checked = enabled;
            enabledCheck.CheckedChanged += delegate { SetEnabled(enabledCheck.Checked); };
            enabledMenu.CheckedChanged += delegate { SetEnabled(enabledMenu.Checked); };
            startupCheck.CheckedChanged += delegate
            {
                if (updatingControls) return;
                try { Settings.StartWithWindows = startupCheck.Checked; }
                catch (Exception ex)
                {
                    updatingControls = true;
                    startupCheck.Checked = !startupCheck.Checked;
                    updatingControls = false;
                    MessageBox.Show(this, "Could not update startup preference.\n" + ex.Message, Program.Title);
                }
            };
            timer.Interval = 1000;
            timer.Tick += delegate { Tick(); };
            // Ensure the single-instance activation window exists even when launched in the tray.
            IntPtr handle = Handle;
            ApplyWindowsTheme();
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            ActivityLog.Write("Started. Automation " + (enabled ? "enabled." : "paused."));
            Tick();
            timer.Start();
        }

        private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (IsDisposed || Disposing || !IsHandleCreated) return;
            try { BeginInvoke((MethodInvoker)ApplyWindowsTheme); }
            catch (InvalidOperationException) { }
        }

        private void ApplyWindowsTheme()
        {
            if (IsDisposed || Disposing) return;
            theme = AppTheme.ReadWindows();
            BackColor = theme.Background;
            ForeColor = theme.Text;
            view.ApplyTheme(theme);
            tray.ContextMenuStrip.BackColor = theme.Surface;
            tray.ContextMenuStrip.ForeColor = theme.Text;
            tray.ContextMenuStrip.Renderer = theme.HighContrast ? (ToolStripRenderer)new ToolStripSystemRenderer() : new ThemeMenuRenderer(theme);
            int dark = theme.Dark ? 1 : 0;
            if (DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int)) != 0)
                DwmSetWindowAttribute(Handle, 19, ref dark, sizeof(int));
            Tick();
        }

        protected override void SetVisibleCore(bool value)
        {
            if (background && value) { background = false; value = false; }
            base.SetVisibleCore(value);
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == Program.ShowMessage) { ShowWindow(); return; }
            if (message.Msg == Program.ExitMessage) { Quit(); return; }
            base.WndProc(ref message);
            // Windows broadcasts these for app-mode, color and accessibility changes.
            if ((message.Msg == 0x001A || message.Msg == 0x031A) && theme != null && !Disposing && !IsDisposed)
                BeginInvoke((MethodInvoker)ApplyWindowsTheme);
        }

        private void ShowWindow()
        {
            background = false;
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        private void SetEnabled(bool value)
        {
            if (updatingControls) return;
            updatingControls = true;
            enabled = value;
            enabledCheck.Checked = enabledMenu.Checked = value;
            updatingControls = false;
            try { Settings.Enabled = value; }
            catch (Exception ex) { ActivityLog.Write("Could not save preference: " + ex.Message); }
            Tick();
        }

        private void Tick()
        {
            try
            {
                GameSnapshot snapshot = monitor.Read();
                GameState state = detector.Update(snapshot, clock.Elapsed);
                string title;
                string detail;
                Color color;
                if (!enabled)
                {
                    bool restored = audio.Restore();
                    title = "Paused";
                    detail = restored ? "Game audio is back under your control." : "Retrying audio restore...";
                    color = theme.Secondary;
                }
                else if (state == GameState.Lobby || state == GameState.Match)
                {
                    bool mute = state == GameState.Lobby;
                    audio.Apply(snapshot.ProcessIds, mute);
                    title = mute ? "Lobby / menu" : "In match";
                    color = mute ? theme.Lobby : theme.Match;
                    detail = audio.LastError != null ? "Audio unavailable; retrying automatically." :
                        audio.SessionCount == 0 ? "Waiting for the game's audio session..." :
                        mute ? "Game muted. It will unmute when you deploy." : "Game sound on. Your volume level is unchanged.";
                }
                else
                {
                    audio.Restore();
                    title = state == GameState.Offline ? "Game closed" : state == GameState.Unknown ? "Checking connection" : "Checking game...";
                    detail = state == GameState.Offline ? "Ready for the next time you launch ARC Raiders." :
                        state == GameState.Unknown ? "Status unavailable. Leaving game audio unchanged." : "Waiting for a stable lobby or match state.";
                    color = theme.Secondary;
                }
                stateLabel.Text = title;
                detailLabel.Text = detail;
                indicator.ForeColor = color;
                statusMenu.Text = title + (enabled && audio.SessionCount > 0 && audio.LastError == null ?
                    (state == GameState.Lobby ? " - muted" : state == GameState.Match ? " - sound on" : "") : "");
                tray.Text = Program.Title + ": " + statusMenu.Text;
                string report = statusMenu.Text + "; sessions=" + audio.SessionCount + (audio.LastError == null ? "" : "; " + audio.LastError);
                if (report != lastReport) { ActivityLog.Write(report); lastReport = report; }
            }
            catch (Exception ex)
            {
                try { audio.Restore(); } catch (Exception) { }
                stateLabel.Text = "Retrying...";
                detailLabel.Text = "Waiting for game audio to become available.";
                string report = "Monitor error: " + ex.Message;
                if (report != lastReport) { ActivityLog.Write(report); lastReport = report; }
            }
        }

        private void Quit() { exiting = true; Close(); }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!exiting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
            else
            {
                timer.Stop();
                try { ActivityLog.Write(audio.Restore() ? "Stopped; original audio restored." : "Stopped; audio restore incomplete."); }
                catch (Exception ex) { ActivityLog.Write("Audio restore error: " + ex.Message); }
                tray.Visible = false;
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposedResources)
            {
                disposedResources = true;
                SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
                timer.Dispose();
                try { audio.Restore(); } catch (Exception) { }
                tray.Visible = false;
                if (tray.ContextMenuStrip != null) tray.ContextMenuStrip.Dispose();
                tray.Dispose();
                appIcon.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
