using System;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ArcLobbyMute
{
    internal sealed class AppTheme
    {
        public bool Dark;
        public bool HighContrast;
        public Color Background, Surface, Text, Secondary, Border, Button, Hover, Accent, Match, Lobby;

        public static AppTheme ReadWindows()
        {
            if (SystemInformation.HighContrast)
                return new AppTheme {
                    HighContrast = true, Background = SystemColors.Window, Surface = SystemColors.Window,
                    Text = SystemColors.WindowText, Secondary = SystemColors.WindowText, Border = SystemColors.WindowText,
                    Button = SystemColors.Control, Hover = SystemColors.Highlight, Accent = SystemColors.Highlight,
                    Match = SystemColors.WindowText, Lobby = SystemColors.WindowText
                };
            bool dark = false;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    dark = key != null && Convert.ToInt32(key.GetValue("AppsUseLightTheme", 1)) == 0;
            }
            catch (System.Security.SecurityException) { }
            catch (UnauthorizedAccessException) { }
            catch (System.IO.IOException) { }
            return Create(dark);
        }

        public static AppTheme Create(bool dark)
        {
            return dark ? new AppTheme {
                Dark = true, Background = Color.FromArgb(32, 32, 32), Surface = Color.FromArgb(43, 43, 43),
                Text = Color.FromArgb(245, 245, 245), Secondary = Color.FromArgb(190, 190, 190),
                Border = Color.FromArgb(67, 67, 67), Button = Color.FromArgb(55, 55, 55), Hover = Color.FromArgb(70, 70, 70),
                Accent = Color.FromArgb(96, 205, 255), Match = Color.FromArgb(88, 214, 173), Lobby = Color.FromArgb(244, 190, 92)
            } : new AppTheme {
                Background = Color.FromArgb(243, 243, 243), Surface = Color.White,
                Text = Color.FromArgb(26, 26, 26), Secondary = Color.FromArgb(96, 96, 96),
                Border = Color.FromArgb(213, 213, 213), Button = Color.FromArgb(253, 253, 253), Hover = Color.FromArgb(232, 232, 232),
                Accent = Color.FromArgb(0, 103, 192), Match = Color.FromArgb(0, 128, 99), Lobby = Color.FromArgb(151, 99, 0)
            };
        }
    }

    internal sealed class StatusDot : Control
    {
        public StatusDot() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            int diameter = Math.Max(4, Math.Min(Width, Height) - 4);
            using (var brush = new SolidBrush(ForeColor))
                e.Graphics.FillEllipse(brush, (Width - diameter) / 2F, (Height - diameter) / 2F, diameter, diameter);
        }
    }

    internal sealed class StatusCard : TableLayoutPanel
    {
        public Color BorderColor = Color.Gray;
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(BorderColor)) e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }

    // Every vertical position comes from preferred text height and margins. No text shares a fixed-height row.
    internal sealed class MainView : TableLayoutPanel
    {
        public readonly Label Heading;
        public readonly Label Subtitle;
        public readonly Label StateLabel;
        public readonly Label DetailLabel;
        public readonly StatusDot Indicator;
        public readonly CheckBox EnabledCheck;
        public readonly CheckBox StartupCheck;
        public readonly Button HideButton;
        private readonly StatusCard card;
        private readonly Label footer;
        private readonly float scale;
        public int LayoutWidth { get { return Px(480); } }

        public MainView()
        {
            using (Graphics graphics = Graphics.FromHwnd(IntPtr.Zero)) scale = graphics.DpiX / 96F;
            SuspendLayout();
            Name = "MainView";
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Dock = DockStyle.Top;
            MinimumSize = new Size(LayoutWidth, 0);
            MaximumSize = new Size(LayoutWidth, 0);
            Padding = Pad(26, 24, 26, 24);
            Margin = Padding.Empty;
            ColumnCount = 1;
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            RowCount = 6;
            for (int i = 0; i < RowCount; i++) RowStyles.Add(new RowStyle(SizeType.AutoSize));

            Heading = MakeLabel("ARC Lobby Mute", "Heading", new Font("Segoe UI", 20F, FontStyle.Bold));
            Heading.Margin = Pad(0, 0, 0, 4);
            Subtitle = MakeLabel("Quiet in the lobby. Ready for the raid.", "Subtitle", null);
            Subtitle.Margin = Pad(0, 0, 0, 24);

            card = new StatusCard {
                Name = "StatusCard", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill, Padding = new Padding(Px(18)), Margin = Pad(0, 0, 0, 18),
                ColumnCount = 2, RowCount = 2
            };
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Px(30)));
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Indicator = new StatusDot { Name = "Indicator", Size = new Size(Px(16), Px(16)), Margin = Pad(0, 6, 10, 0) };
            StateLabel = MakeLabel("Checking game...", "StateLabel", new Font("Segoe UI", 12F, FontStyle.Bold));
            StateLabel.Margin = Pad(0, 0, 0, 5);
            DetailLabel = MakeLabel("Waiting for ARC Raiders", "DetailLabel", null);
            card.Controls.Add(Indicator, 0, 0);
            card.Controls.Add(StateLabel, 1, 0);
            card.Controls.Add(DetailLabel, 1, 1);

            EnabledCheck = new CheckBox {
                Name = "EnabledCheck", Text = "Automatically mute in the lobby", AutoSize = true,
                Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, Margin = Pad(0, 3, 0, 14),
                UseVisualStyleBackColor = false, TabIndex = 0
            };
            StartupCheck = new CheckBox {
                Name = "StartupCheck", Text = "Start with Windows", AutoSize = true,
                Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, Margin = Pad(0, 0, 0, 24),
                UseVisualStyleBackColor = false, TabIndex = 1
            };
            var bottom = new TableLayoutPanel {
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill,
                Margin = Padding.Empty, ColumnCount = 2, RowCount = 1
            };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            footer = MakeLabel("Closing this window keeps the app in the tray.", "Footer", new Font("Segoe UI", 9F));
            footer.Margin = Pad(0, 0, 18, 0);
            footer.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            HideButton = new Button {
                Name = "HideButton", Text = "Hide to tray", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(Px(112), Px(36)), Padding = Pad(12, 6, 12, 6), Margin = Padding.Empty,
                Anchor = AnchorStyles.Right, FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false, TabIndex = 2
            };
            bottom.Controls.Add(footer, 0, 0);
            bottom.Controls.Add(HideButton, 1, 0);

            Controls.Add(Heading, 0, 0);
            Controls.Add(Subtitle, 0, 1);
            Controls.Add(card, 0, 2);
            Controls.Add(EnabledCheck, 0, 3);
            Controls.Add(StartupCheck, 0, 4);
            Controls.Add(bottom, 0, 5);
            ResumeLayout(false);
        }

        private int Px(int value) { return (int)Math.Round(value * scale); }
        private Padding Pad(int left, int top, int right, int bottom) { return new Padding(Px(left), Px(top), Px(right), Px(bottom)); }

        private static Label MakeLabel(string text, string name, Font font)
        {
            var label = new Label {
                Name = name, Text = text, AutoSize = true, Dock = DockStyle.Fill, Margin = Padding.Empty,
                UseMnemonic = false
            };
            if (font != null) label.Font = font;
            return label;
        }

        public void ApplyTheme(AppTheme theme)
        {
            SuspendLayout();
            SetColors(this, theme.Background, theme.Text);
            SetColors(card, theme.Surface, theme.Text);
            Subtitle.ForeColor = DetailLabel.ForeColor = footer.ForeColor = theme.Secondary;
            Indicator.ForeColor = theme.Secondary;
            card.BorderColor = theme.Border;
            HideButton.BackColor = theme.Button;
            HideButton.FlatAppearance.BorderColor = theme.Border;
            HideButton.FlatAppearance.MouseOverBackColor = theme.Hover;
            HideButton.FlatAppearance.MouseDownBackColor = theme.Hover;
            EnabledCheck.FlatAppearance.CheckedBackColor = StartupCheck.FlatAppearance.CheckedBackColor = theme.Accent;
            ResumeLayout(true);
            Invalidate(true);
        }

        private static void SetColors(Control control, Color background, Color foreground)
        {
            control.BackColor = background;
            control.ForeColor = foreground;
            foreach (Control child in control.Controls) SetColors(child, background, foreground);
        }
    }

    internal sealed class ThemeMenuColors : ProfessionalColorTable
    {
        private readonly AppTheme theme;
        public ThemeMenuColors(AppTheme theme) { this.theme = theme; UseSystemColors = false; }
        public override Color ToolStripDropDownBackground { get { return theme.Surface; } }
        public override Color MenuBorder { get { return theme.Border; } }
        public override Color MenuItemBorder { get { return theme.Border; } }
        public override Color MenuItemSelected { get { return theme.Hover; } }
        public override Color MenuItemSelectedGradientBegin { get { return theme.Hover; } }
        public override Color MenuItemSelectedGradientEnd { get { return theme.Hover; } }
        public override Color ImageMarginGradientBegin { get { return theme.Surface; } }
        public override Color ImageMarginGradientMiddle { get { return theme.Surface; } }
        public override Color ImageMarginGradientEnd { get { return theme.Surface; } }
        public override Color CheckBackground { get { return theme.Hover; } }
        public override Color CheckSelectedBackground { get { return theme.Hover; } }
        public override Color CheckPressedBackground { get { return theme.Hover; } }
        public override Color SeparatorDark { get { return theme.Border; } }
        public override Color SeparatorLight { get { return theme.Surface; } }
    }

    internal sealed class ThemeMenuRenderer : ToolStripProfessionalRenderer
    {
        private readonly AppTheme theme;
        public ThemeMenuRenderer(AppTheme theme) : base(new ThemeMenuColors(theme)) { this.theme = theme; RoundedEdges = false; }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? theme.Text : theme.Secondary;
            base.OnRenderItemText(e);
        }
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = theme.Text;
            base.OnRenderArrow(e);
        }
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            Rectangle r = e.ImageRectangle;
            using (var pen = new Pen(theme.Text, 2))
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                e.Graphics.DrawLines(pen, new[] {
                    new Point(r.Left + r.Width / 5, r.Top + r.Height / 2),
                    new Point(r.Left + r.Width * 2 / 5, r.Top + r.Height * 3 / 4),
                    new Point(r.Left + r.Width * 4 / 5, r.Top + r.Height / 4)
                });
            }
        }
    }
}
