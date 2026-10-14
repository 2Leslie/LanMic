using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows.Forms;

namespace LanMic
{
    internal static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                using (var form = new MainForm())
                {
                    if (args.Length == 2 && args[0] == "--render-preview")
                    {
                        form.ShowInTaskbar = false; form.Opacity = 0;
                        form.Show(); Application.DoEvents();
                        using (var bitmap = new Bitmap(form.Width, form.Height))
                        { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(Path.GetFullPath(args[1]), ImageFormat.Png); }
                        return 0;
                    }
                    Application.Run(form);
                }
                return 0;
            }
            catch (Exception error) { MessageBox.Show(error.Message, "LanMic 启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
        }
    }

    public sealed class MainForm : Form
    {
        static readonly Color Background = Color.FromArgb(17, 22, 30);
        static readonly Color Card = Color.FromArgb(27, 35, 46);
        static readonly Color TextColor = Color.FromArgb(233, 241, 247);
        static readonly Color MutedColor = Color.FromArgb(161, 178, 194);
        static readonly Color Accent = Color.FromArgb(82, 221, 181);
        readonly ReceiverServer server = new ReceiverServer();
        readonly ComboBox addresses = new ComboBox();
        readonly NumericUpDown port = new NumericUpDown();
        readonly Button start = new Button();
        readonly ComboBox outputs = new ComboBox();
        readonly Button refresh = new Button();
        readonly Label route = new Label();
        readonly Label status = new Label();
        readonly Label details = new Label();
        readonly Label meters = new Label();
        readonly Label fault = new Label();
        readonly Label gainLabel = new Label();
        readonly LevelMeter meter = new LevelMeter();
        readonly CheckBox mute = new CheckBox();
        readonly TrackBar gain = new TrackBar();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        bool running;

        public MainForm()
        {
            SuspendLayout();
            Text = "LanMic · Wi-Fi 麦克风";
            ClientSize = new Size(900, 684);
            MinimumSize = new Size(916, 723);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 10F);
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Background; ForeColor = TextColor;
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(26, 20, 26, 16), ColumnCount = 1, RowCount = 5 };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 83));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 148));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 155));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 202));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(root);

            var header = new Panel { Dock = DockStyle.Fill };
            header.Controls.Add(LabelAt("LanMic", 0, 0, 240, 46, 28, TextColor));
            header.Controls.Add(LabelAt("让手机的声音，来到电脑。", 2, 50, 500, 24, 11, MutedColor));
            var badge = LabelAt("ANDROID  →  WINDOWS", 568, 16, 260, 29, 10, Accent);
            badge.TextAlign = ContentAlignment.MiddleRight;
            header.Controls.Add(badge); root.Controls.Add(header, 0, 0);

            var pairing = NewCard();
            pairing.Controls.Add(LabelAt("01   连接手机", 18, 13, 180, 27, 12, TextColor));
            pairing.Controls.Add(LabelAt("同一局域网 · 在手机上填写下面的信息", 235, 16, 560, 23, 10, MutedColor));
            pairing.Controls.Add(LabelAt("电脑地址", 18, 51, 150, 23, 9, MutedColor));
            SetupCombo(addresses, 18, 76, 392); addresses.Anchor = AnchorStyles.Left | AnchorStyles.Top;
            pairing.Controls.Add(addresses);
            pairing.Controls.Add(LabelAt("端口", 426, 51, 100, 23, 9, MutedColor));
            port.SetBounds(426, 76, 112, 32); port.Minimum = 1024; port.Maximum = 65535; port.Value = 18765;
            port.BackColor = Background; port.ForeColor = TextColor; port.BorderStyle = BorderStyle.FixedSingle;
            pairing.Controls.Add(port);
            SetupButton(start, "开始监听", 562, 71, 241, 38, true);
            start.Click += delegate { ToggleListening(); }; pairing.Controls.Add(start);
            pairing.Controls.Add(LabelAt("电脑开始监听后，在手机上填写 IP 和端口，点击「开始传输」。", 18, 114, 785, 22, 9, MutedColor));
            root.Controls.Add(pairing, 0, 1);

            var routing = NewCard();
            routing.Controls.Add(LabelAt("02   音频去向", 18, 13, 180, 27, 12, TextColor));
            var driver = new LinkLabel { Text = "获取 VB-CABLE 虚拟声卡 ↗", LinkColor = Accent, ActiveLinkColor = TextColor, VisitedLinkColor = Accent };
            driver.SetBounds(549, 15, 255, 25); driver.TextAlign = ContentAlignment.MiddleRight;
            driver.LinkClicked += delegate { OpenUrl("https://vb-audio.com/Cable/"); }; routing.Controls.Add(driver);
            SetupCombo(outputs, 18, 51, 679); outputs.SelectedIndexChanged += delegate { UpdateRoute(); }; routing.Controls.Add(outputs);
            SetupButton(refresh, "刷新", 714, 49, 89, 33, false); refresh.Click += delegate { LoadDevices(); LoadAddresses(); }; routing.Controls.Add(refresh);
            route.SetBounds(18, 95, 785, 40); route.ForeColor = MutedColor; route.Font = new Font(Font.FontFamily, 9);
            routing.Controls.Add(route); root.Controls.Add(routing, 0, 2);

            var monitoring = NewCard();
            status.SetBounds(18, 13, 550, 30); status.Text = "尚未开始"; status.Font = new Font(Font.FontFamily, 14, FontStyle.Bold);
            status.ForeColor = Accent; monitoring.Controls.Add(status);
            meters.SetBounds(596, 16, 207, 24); meters.TextAlign = ContentAlignment.MiddleRight; meters.ForeColor = MutedColor;
            monitoring.Controls.Add(meters);
            details.SetBounds(18, 47, 785, 25); details.ForeColor = MutedColor; details.Font = new Font(Font.FontFamily, 9); monitoring.Controls.Add(details);
            meter.SetBounds(18, 80, 785, 18); monitoring.Controls.Add(meter);
            gainLabel.SetBounds(18, 117, 125, 24); gainLabel.Text = "输出音量 100%"; gainLabel.ForeColor = MutedColor; gainLabel.Font = new Font(Font.FontFamily, 9);
            monitoring.Controls.Add(gainLabel);
            gain.SetBounds(145, 112, 438, 40); gain.Minimum = 0; gain.Maximum = 200; gain.Value = 100; gain.TickStyle = TickStyle.None; gain.BackColor = Card;
            gain.ValueChanged += delegate { server.Volume = gain.Value; gainLabel.Text = "输出音量 " + gain.Value + "%"; };
            monitoring.Controls.Add(gain);
            mute.SetBounds(645, 116, 159, 30); mute.Text = "静音输出"; mute.ForeColor = TextColor;
            mute.CheckedChanged += delegate { server.Muted = mute.Checked; }; monitoring.Controls.Add(mute);
            fault.SetBounds(18, 155, 785, 28); fault.ForeColor = Color.FromArgb(255, 192, 123); fault.Font = new Font(Font.FontFamily, 9);
            monitoring.Controls.Add(fault); root.Controls.Add(monitoring, 0, 3);

            var footer = new Label { Dock = DockStyle.Fill, Text = "Wi-Fi  ·  48 kHz / PCM16 / 单声道  ·  局域网直连    |    LanMic 0.2.0 开发预览", ForeColor = MutedColor, Font = new Font(Font.FontFamily, 9), TextAlign = ContentAlignment.MiddleLeft };
            root.Controls.Add(footer, 0, 4);
            LoadAddresses(); LoadDevices();
            timer.Interval = 100; timer.Tick += delegate { UpdateState(); }; timer.Start(); UpdateState();
            FormClosing += delegate { timer.Stop(); server.Stop(); };
            // The interface is authored in 96-DPI pixels. Fonts are already rendered
            // at the system DPI, so scale the completed control tree once as well.
            using (Graphics graphics = CreateGraphics())
            {
                float factor = graphics.DpiX / 96F;
                if (factor != 1F) Scale(new SizeF(factor, factor));
            }
            ResumeLayout(true);
            MinimumSize = Size;
        }

        Panel NewCard() { return new Panel { Dock = DockStyle.Fill, BackColor = Card, Margin = new Padding(0, 0, 0, 12) }; }
        static Label LabelAt(string text, int x, int y, int width, int height, float size, Color color)
        { return new Label { Text = text, Bounds = new Rectangle(x, y, width, height), ForeColor = color, Font = new Font("Microsoft YaHei UI", size) }; }
        static void SetupCombo(ComboBox combo, int x, int y, int width)
        {
            combo.SetBounds(x, y, width, 32); combo.DropDownStyle = ComboBoxStyle.DropDownList;
            combo.FlatStyle = FlatStyle.Flat; combo.BackColor = Background; combo.ForeColor = TextColor;
        }
        static void SetupButton(Button button, string text, int x, int y, int width, int height, bool primary)
        {
            button.SetBounds(x, y, width, height); button.Text = text; button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = primary ? 0 : 1; button.FlatAppearance.BorderColor = Color.FromArgb(66, 85, 103);
            button.BackColor = primary ? Accent : Card; button.ForeColor = primary ? Background : TextColor;
            button.Cursor = Cursors.Hand;
        }
        void LoadAddresses()
        {
            addresses.Items.Clear();
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var address in nic.GetIPProperties().UnicastAddresses)
                {
                    string ip = address.Address.ToString();
                    if (address.Address.AddressFamily != AddressFamily.InterNetwork || ip.StartsWith("169.254.") || ip.StartsWith("198.18.")) continue;
                    addresses.Items.Add(ip + "  ·  " + nic.Name);
                }
            }
            if (addresses.Items.Count == 0) addresses.Items.Add("未找到局域网地址，请先连接网络");
            addresses.SelectedIndex = 0;
        }
        void LoadDevices()
        {
            outputs.Items.Clear(); outputs.Items.Add(new AudioDevice(ReceiverServer.LevelOnlyDevice, "仅查看接收电平（不输出声音）"));
            int preferred = 0;
            foreach (AudioDevice device in WaveOutPlayer.GetDevices()) { int index = outputs.Items.Add(device); if (device.IsCable) preferred = index; }
            outputs.SelectedIndex = preferred;
        }
        void UpdateRoute()
        {
            var device = outputs.SelectedItem as AudioDevice;
            if (device == null) return;
            route.Text = device.Id == ReceiverServer.LevelOnlyDevice ? "此模式只验证收到音频。要供微信 / 游戏使用，请安装虚拟声卡并选择 CABLE Input。" :
                device.IsCable ? "已选择虚拟声卡。请在微信 / 游戏 / 会议软件的麦克风设置中选择 CABLE Output。" :
                "此设备用于耳机 / 扬声器试听。要供其他软件作为麦克风使用，请改选 CABLE Input。";
        }
        void ToggleListening()
        {
            try
            {
                if (running) { server.Stop(); running = false; }
                else { server.Start((int)port.Value, ((AudioDevice)outputs.SelectedItem).Id); running = true; }
                start.Text = running ? "停止监听" : "开始监听";
                port.Enabled = outputs.Enabled = refresh.Enabled = !running;
                UpdateState();
            }
            catch (Exception error) { MessageBox.Show(this, error.Message, "无法开始监听", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
        void UpdateState()
        {
            ReceiverSnapshot state = server.Snapshot;
            status.Text = !running ? "尚未开始" : state.ActiveClients == 0 ? "等待手机连接" : state.DeviceName + " · 已连接";
            details.Text = !running ? "先选择音频去向，再点击开始监听。" : state.ActiveClients == 0 ? "在安卓 App 填写电脑地址和端口，然后开始传输。" :
                "收到 " + state.ReceivedFrames + " 帧  ·  输出设备完成 " + state.OutputFrames + " 帧  ·  缓冲 " + (state.QueuedFrames * 20) + " ms  ·  丢弃 " + state.DroppedFrames + " 帧";
            bool recent = state.LastFrameUtc != default(DateTime) && (DateTime.UtcNow - state.LastFrameUtc).TotalSeconds < 1;
            double rms = recent ? state.Rms : 0;
            meters.Text = rms > 0 ? (20 * Math.Log10(rms)).ToString("0.0") + " dBFS" : "−∞ dBFS";
            meter.Level = rms > 0 ? Math.Max(0, Math.Min(1, (20 * Math.Log10(rms) + 60) / 60)) : 0;
            fault.Text = state.Error;
        }
        static void OpenUrl(string url)
        { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception error) { MessageBox.Show(error.Message); } }
        protected override void Dispose(bool disposing)
        { if (disposing) { timer.Dispose(); server.Dispose(); } base.Dispose(disposing); }
    }

    internal sealed class LevelMeter : Control
    {
        double level;
        public double Level { get { return level; } set { level = value; Invalidate(); } }
        public LevelMeter() { DoubleBuffered = true; }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Color.FromArgb(17, 22, 30));
            int segments = 48;
            for (int i = 0; i < segments; i++)
            {
                Color color = i < segments * level ? (i >= 44 ? Color.FromArgb(255, 166, 104) : Color.FromArgb(82, 221, 181)) : Color.FromArgb(46, 60, 75);
                using (var brush = new SolidBrush(color)) e.Graphics.FillRectangle(brush, i * Width / segments, 0, Math.Max(1, Width / segments - 3), Height);
            }
        }
    }
}
