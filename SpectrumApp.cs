using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SpectrumApp
{
    // ── 音频解码 ─────────────────────────────────
    class AudioDecoder
    {
        public static float[] Decode(string filePath, out int sampleRate, out int channels)
        {
            sampleRate = 44100;
            channels = 1;
            string ffmpeg = FindFfmpeg();
            if (ffmpeg == null) throw new Exception("找不到 ffmpeg.exe");

            string args = string.Format("-i \"{0}\" -f f32le -ac 1 -ar 44100 -acodec pcm_f32le -y pipe:1", filePath);

            using (Process proc = new Process())
            {
                proc.StartInfo.FileName = ffmpeg;
                proc.StartInfo.Arguments = args;
                proc.StartInfo.UseShellExecute = false;
                proc.StartInfo.RedirectStandardOutput = true;
                proc.StartInfo.RedirectStandardError = true;
                proc.StartInfo.CreateNoWindow = true;

                List<byte> allData = new List<byte>(1024 * 1024);
                proc.Start();

                // 必须异步读取 stderr，否则缓冲区满后 ffmpeg 会卡死
                proc.ErrorDataReceived += (snd, e) => { };
                proc.BeginErrorReadLine();

                byte[] buffer = new byte[65536];
                int read;
                while ((read = proc.StandardOutput.BaseStream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    byte[] chunk = new byte[read];
                    Array.Copy(buffer, chunk, read);
                    allData.AddRange(chunk);
                }

                proc.WaitForExit(10000);
                sampleRate = 44100;
                channels = 1;

                byte[] rawBytes = allData.ToArray();
                float[] samples = new float[rawBytes.Length / 4];
                for (int i = 0; i < samples.Length; i++)
                {
                    samples[i] = BitConverter.ToSingle(rawBytes, i * 4);
                }
                return samples;
            }
        }

        static string FindFfmpeg()
        {
            string[] paths = {
                @"C:\ffmpeg\ffmpeg-8.0-full_build\bin\ffmpeg.exe",
                @"C:\ffmpeg\bin\ffmpeg.exe",
            };
            foreach (string p in paths)
                if (File.Exists(p)) return p;

            foreach (string dir in Environment.GetEnvironmentVariable("PATH").Split(';'))
            {
                try
                {
                    string full = Path.Combine(dir.Trim(), "ffmpeg.exe");
                    if (File.Exists(full)) return full;
                }
                catch { }
            }
            return null;
        }
    }

    // ── FFT & STFT ──────────────────────────────
    class SpectrogramEngine
    {
        public float[] Data;          // [frames * bins]
        public int Frames;
        public int Bins;
        public float MaxPower;
        public int SampleRate;
        public int MaxFreq;
        public double Duration;
        public int FftSize;

        public void Compute(float[] samples, int sampleRate, int fftSize, int maxFreqSel)
        {
            SampleRate = sampleRate;
            FftSize = fftSize;
            MaxFreq = maxFreqSel;
            Duration = (double)samples.Length / sampleRate;

            int hop = fftSize >> 2;
            int numFrames = Math.Max(0, (samples.Length - fftSize) / hop) + 1;
            int maxBin = (int)Math.Round((double)maxFreqSel / (sampleRate / (double)fftSize));
            int useBins = Math.Min(maxBin, fftSize >> 1);

            // Hann window
            float[] win = new float[fftSize];
            for (int i = 0; i < fftSize; i++)
                win[i] = 0.5f * (1 - (float)Math.Cos(2 * Math.PI * i / (fftSize - 1)));

            Data = new float[numFrames * useBins];
            Frames = numFrames;
            Bins = useBins;
            MaxPower = float.NegativeInfinity;

            for (int f = 0; f < numFrames; f++)
            {
                int off = f * hop;
                float[] re = new float[fftSize];
                float[] im = new float[fftSize];
                for (int i = 0; i < fftSize; i++)
                    re[i] = (off + i < samples.Length ? samples[off + i] : 0) * win[i];

                FFT(re, im, fftSize);

                for (int b = 0; b < useBins; b++)
                {
                    float p = 10 * (float)Math.Log10(re[b] * re[b] + im[b] * im[b] + 1e-10f);
                    Data[f * useBins + b] = p;
                    if (p > MaxPower) MaxPower = p;
                }
            }
        }

        static void FFT(float[] re, float[] im, int n)
        {
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j)
                {
                    float tr = re[i]; re[i] = re[j]; re[j] = tr;
                    float ti = im[i]; im[i] = im[j]; im[j] = ti;
                }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                float ang = -2 * (float)Math.PI / len;
                float wR = (float)Math.Cos(ang), wI = (float)Math.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    float cR = 1, cI = 0;
                    for (int j = 0; j < len >> 1; j++)
                    {
                        float uR = re[i + j], uI = im[i + j];
                        float vR = re[i + j + (len >> 1)] * cR - im[i + j + (len >> 1)] * cI;
                        float vI = re[i + j + (len >> 1)] * cI + im[i + j + (len >> 1)] * cR;
                        re[i + j] = uR + vR; im[i + j] = uI + vI;
                        re[i + j + (len >> 1)] = uR - vR; im[i + j + (len >> 1)] = uI - vI;
                        float nR = cR * wR - cI * wI; cI = cR * wI + cI * wR; cR = nR;
                    }
                }
            }
        }
    }

    // ── 色映射表 ────────────────────────────────
    static class ColorMaps
    {
        public static Color[] Viridis = Gen(0, 0, 4, 72, 40, 120, 62, 83, 160, 49, 120, 188, 38, 155, 205, 53, 183, 201, 91, 207, 178, 140, 225, 150, 190, 236, 121, 242, 240, 97, 253, 231, 37);
        public static Color[] Magma = Gen(0, 0, 4, 28, 16, 68, 79, 18, 123, 129, 37, 129, 181, 54, 122, 229, 80, 100, 251, 135, 97, 254, 194, 135, 252, 253, 191);
        public static Color[] Inferno = Gen(0, 0, 4, 40, 11, 84, 101, 21, 110, 159, 42, 99, 212, 72, 66, 245, 125, 21, 252, 193, 44, 252, 255, 164);
        public static Color[] Plasma = Gen(13, 8, 135, 84, 2, 163, 139, 10, 165, 185, 50, 137, 219, 92, 104, 244, 136, 73, 254, 188, 43, 240, 249, 33);
        public static Color[] Hot;
        public static Color[] Cool;
        public static Color[] Ocean;
        public static Color[] Gray;

        public static Dictionary<string, Color[]> Map = new Dictionary<string, Color[]>();

        static ColorMaps()
        {
            Hot = new Color[256];
            for (int i = 0; i < 256; i++)
            {
                float t = i / 255f;
                if (t < 0.333f) Hot[i] = Color.FromArgb((int)(t * 3 * 255), 0, 0);
                else if (t < 0.667f) Hot[i] = Color.FromArgb(255, (int)((t - 0.333f) * 3 * 255), 0);
                else Hot[i] = Color.FromArgb(255, 255, (int)((t - 0.667f) * 3 * 255));
            }

            Cool = new Color[256];
            for (int i = 0; i < 256; i++)
                Cool[i] = Color.FromArgb(i, 255 - i, 255);

            Ocean = Gen(0, 10, 30, 0, 30, 80, 0, 80, 150, 0, 130, 180, 20, 180, 200, 100, 220, 220, 200, 250, 240);

            Gray = new Color[256];
            for (int i = 0; i < 256; i++)
                Gray[i] = Color.FromArgb(i, i, i);

            Map["viridis"] = Viridis;
            Map["magma"] = Magma;
            Map["inferno"] = Inferno;
            Map["plasma"] = Plasma;
            Map["hot"] = Hot;
            Map["cool"] = Cool;
            Map["ocean"] = Ocean;
            Map["grayscale"] = Gray;
        }

        static Color[] Gen(params int[] stops)
        {
            Color[] result = new Color[256];
            int segs = stops.Length / 3 - 1;
            for (int i = 0; i < 256; i++)
            {
                float t = i / 255f;
                int idx = (int)Math.Min(t * segs, segs - 1);
                float f = t * segs - idx;
                int i1 = idx * 3, i2 = (idx + 1) * 3;
                int r = (int)(stops[i1] + (stops[i2] - stops[i1]) * f);
                int g = (int)(stops[i1 + 1] + (stops[i2 + 1] - stops[i1 + 1]) * f);
                int b = (int)(stops[i1 + 2] + (stops[i2 + 2] - stops[i1 + 2]) * f);
                result[i] = Color.FromArgb(Math.Min(255, Math.Max(0, r)), Math.Min(255, Math.Max(0, g)), Math.Min(255, Math.Max(0, b)));
            }
            return result;
        }

        public static Color[] Get(string name)
        {
            if (Map.ContainsKey(name)) return Map[name];
            return Viridis;
        }
    }

    // ── 主窗口 ──────────────────────────────────
    public class MainForm : Form
    {
        // 模式
        enum AppMode { Audio, Image }
        AppMode currentMode = AppMode.Audio;

        // 状态
        float[] samples;
        int sampleRate;
        SpectrogramEngine engine;
        ImageEngine imgEngine;
        string fileName = "";

        // 视图（横向）
        double viewZoom = 1;
        int viewStart = 0;
        bool autoSetMaxFreqDone = false;

        // UI 控件
        Panel pnlToolbar;
        ComboBox cmbColormap;
        TrackBar trkBrightness, trkGamma, trkDynRange;
        Label lblBrightness, lblGamma, lblDynRange;
        ComboBox cmbFftSize, cmbMaxFreq, cmbFreqScale;
        PictureBox pbSpectrogram;
        // 图转音频参数控件
        Panel pnlImgParams;
        ComboBox cmbImgChannels, cmbImgFFT;
        NumericUpDown nudImgSR, nudImgIter;
        Button btnGenAudio;
        ProgressBar pbImgProgress;
        HScrollBar sbScroll;
        Button btnZoomIn, btnZoomOut, btnZoomReset, btnSaveSel;
        Label lblZoom, lblTime, lblStatus;
        Panel pnlCanvas;
        bool isProcessing = false;

        public MainForm()
        {
            this.WindowState = FormWindowState.Maximized;
            this.Text = "\u97F3\u9891\u9891\u8C31\u56FE\u5DE5\u5177";
            this.Width = 1100;
            this.Height = 920;
            this.MinimumSize = new Size(800, 600);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.FromArgb(0xF0, 0xF0, 0xF0);
            this.Font = new Font("\u5FAE\u8F6F\u96C5\u9ED1", 9);

            BuildUI();
        }

        void BuildUI()
        {
            // ── 菜单栏 ──
            MenuStrip menu = new MenuStrip();
            menu.BackColor = Color.FromArgb(0xE8, 0xE8, 0xE8);
            ToolStripMenuItem fileMenu = new ToolStripMenuItem("\u6587\u4EF6(&F)");
            ToolStripMenuItem openItem = new ToolStripMenuItem("\u6253\u5F00\u97F3\u9891\u6587\u4EF6...(&O)");
            openItem.Click += OnOpenFile;
            openItem.ShortcutKeys = Keys.Control | Keys.O;
            fileMenu.DropDownItems.Add(openItem);
            menu.Items.Add(fileMenu);
            this.MainMenuStrip = menu;
            this.Controls.Add(menu);

            // ── 工具栏 ──
            pnlToolbar = new Panel();
            pnlToolbar.Dock = DockStyle.Top;
            pnlToolbar.Height = 65;
            pnlToolbar.BackColor = Color.FromArgb(0xF5, 0xF5, 0xF5);
            pnlToolbar.BorderStyle = BorderStyle.FixedSingle;
            pnlToolbar.AutoScroll = true;
            BuildToolbar();
            this.Controls.Add(pnlToolbar);

            // ── 图转音频参数面板（默认隐藏） ──
            pnlImgParams = new Panel();
            pnlImgParams.Dock = DockStyle.Top;
            pnlImgParams.Height = 48;
            pnlImgParams.BackColor = Color.FromArgb(0xEA, 0xEA, 0xEA);
            pnlImgParams.BorderStyle = BorderStyle.FixedSingle;
            pnlImgParams.Visible = false;
            Label lblImgP = new Label(); lblImgP.Text = "\u56FE\u7247\u6A21\u5F0F"; lblImgP.Location = new Point(8, 3); lblImgP.Size = new Size(60, 18);
            // 第一行：声道 + 采样率 + 迭代 + 精度 + 生成按钮
            Label lblCh = new Label(); lblCh.Text = "\u58F0\u9053"; lblCh.Location = new Point(70, 3); lblCh.Size = new Size(30, 18);
            cmbImgChannels = new ComboBox(); cmbImgChannels.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbImgChannels.Items.AddRange(new string[] { "\u53CC\u58F0\u9053", "\u53F3\u58F0\u9053", "\u5DE6\u58F0\u9053" }); cmbImgChannels.SelectedIndex = 0;
            cmbImgChannels.Location = new Point(100, 1); cmbImgChannels.Size = new Size(70, 22);
            Label lblSR = new Label(); lblSR.Text = "\u91C7\u6837\u7387"; lblSR.Location = new Point(175, 3); lblSR.Size = new Size(40, 18);
            nudImgSR = new NumericUpDown(); nudImgSR.Minimum = 8000; nudImgSR.Maximum = 48000; nudImgSR.Value = 44100; nudImgSR.Increment = 100;
            nudImgSR.Location = new Point(215, 1); nudImgSR.Width = 60;
            Label lblPrec = new Label(); lblPrec.Text = "FFT\u7CBE\u5EA6"; lblPrec.Location = new Point(278, 3); lblPrec.Size = new Size(50, 18);
            cmbImgFFT = new ComboBox(); cmbImgFFT.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbImgFFT.Items.AddRange(new string[] { "1024\u5FEB\u901F", "2048\u6807\u51C6", "4096\u9AD8", "8192\u6700\u9AD8" }); cmbImgFFT.SelectedIndex = 3;
            cmbImgFFT.Location = new Point(328, 1); cmbImgFFT.Size = new Size(80, 22);
            Label lblIter = new Label(); lblIter.Text = "\u8FED\u4EE3"; lblIter.Location = new Point(415, 3); lblIter.Size = new Size(30, 18);
            nudImgIter = new NumericUpDown(); nudImgIter.Minimum = 10; nudImgIter.Maximum = 200; nudImgIter.Value = 60; nudImgIter.Increment = 10;
            nudImgIter.Location = new Point(445, 1); nudImgIter.Width = 50;
            btnGenAudio = new Button(); btnGenAudio.Text = "\u751F\u6210\u97F3\u9891";
            btnGenAudio.Location = new Point(500, 0); btnGenAudio.Size = new Size(80, 24);
            btnGenAudio.Enabled = false;
            btnGenAudio.Click += (s, e) => GenerateAudio();
            pnlImgParams.Controls.AddRange(new Control[] { lblImgP, lblCh, cmbImgChannels, lblSR, nudImgSR, lblPrec, cmbImgFFT, lblIter, nudImgIter, btnGenAudio });
            // 第二行：进度条
            pbImgProgress = new ProgressBar();
            pbImgProgress.Visible = false;
            pbImgProgress.Location = new Point(8, 26); pbImgProgress.Size = new Size(pnlImgParams.Width - 16, 16);
            pbImgProgress.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            pbImgProgress.Minimum = 0; pbImgProgress.Maximum = 100; pbImgProgress.Value = 0;
            pnlImgParams.Controls.Add(pbImgProgress);
            this.Controls.Add(pnlImgParams);

            // ── 状态栏 ──
            lblStatus = new Label();
            lblStatus.Dock = DockStyle.Bottom;
            lblStatus.Height = 22;
            lblStatus.BackColor = Color.FromArgb(0xC0, 0xC0, 0xC0);
            lblStatus.ForeColor = Color.FromArgb(0x33, 0x33, 0x33);
            lblStatus.TextAlign = ContentAlignment.MiddleLeft;
            lblStatus.Padding = new Padding(8, 0, 0, 0);
            lblStatus.Font = new Font("\u5FAE\u8F6F\u96C5\u9ED1", 9);
            lblStatus.Text = "\u5C31\u7EEA";
            this.Controls.Add(lblStatus);

            // ── 缩放栏 ──
            Panel pnlZoom = new Panel();
            pnlZoom.Height = 32;
            pnlZoom.Dock = DockStyle.Bottom;
            pnlZoom.BackColor = Color.FromArgb(0xE8, 0xE8, 0xE8);
            pnlZoom.BorderStyle = BorderStyle.FixedSingle;

            btnZoomOut = new Button(); btnZoomOut.Text = "\u2212"; btnZoomOut.Size = new Size(28, 22); btnZoomOut.Location = new Point(8, 4); btnZoomOut.Click += (s, e) => ChangeZoom(1 / 1.3);
            btnZoomIn = new Button(); btnZoomIn.Text = "+"; btnZoomIn.Size = new Size(28, 22); btnZoomIn.Location = new Point(70, 4); btnZoomIn.Click += (s, e) => ChangeZoom(1.3);
            btnZoomReset = new Button(); btnZoomReset.Text = "\u590D\u4F4D"; btnZoomReset.Size = new Size(40, 22); btnZoomReset.Location = new Point(102, 4); btnZoomReset.Click += (s, e) => { viewZoom = 1; viewStart = 0; Render(); };
            lblZoom = new Label(); lblZoom.Text = "1\u00D7"; lblZoom.Size = new Size(40, 22); lblZoom.Location = new Point(42, 4); lblZoom.TextAlign = ContentAlignment.MiddleCenter; lblZoom.ForeColor = Color.FromArgb(0x2C, 0x5F, 0x9A);
            lblTime = new Label(); lblTime.Text = ""; lblTime.Size = new Size(200, 22); lblTime.Location = new Point(150, 4); lblTime.TextAlign = ContentAlignment.MiddleLeft; lblTime.ForeColor = Color.FromArgb(0x66, 0x66, 0x66);
            Label lblHint = new Label(); lblHint.Text = "\u6EDA\u8F6E\u7F29\u653E \u00B7 \u62D6\u52A8\u5E73\u79FB"; lblHint.Size = new Size(140, 22); lblHint.Location = new Point(pnlZoom.Width - 150, 4); lblHint.TextAlign = ContentAlignment.MiddleRight; lblHint.ForeColor = Color.FromArgb(0x99, 0x99, 0x99); lblHint.Anchor = AnchorStyles.Right;

            pnlZoom.Controls.AddRange(new Control[] { btnZoomOut, btnZoomIn, btnZoomReset, lblZoom, lblTime, lblHint });
            this.Controls.Add(pnlZoom);

            // ── 滚动条 ──
            sbScroll = new HScrollBar();
            sbScroll.Dock = DockStyle.Bottom;
            sbScroll.Height = 16;
            sbScroll.ValueChanged += sbScroll_ValueChanged;
            this.Controls.Add(sbScroll);

            // ── 频谱图画布（可纵向滚动，两端留白边） ──
            pnlCanvas = new NoScrollPanel();
            pnlCanvas.Dock = DockStyle.Fill;
            pnlCanvas.BackColor = Color.White;
            pnlCanvas.BorderStyle = BorderStyle.FixedSingle;
            pnlCanvas.AutoScroll = true;
            pnlCanvas.Resize += (s, e) => Render();

            pbSpectrogram = new PictureBox();
            pbSpectrogram.SizeMode = PictureBoxSizeMode.Normal;
            pbSpectrogram.Location = new Point(0, 0);
            pbSpectrogram.MouseDown += OnCanvasMouseDown;
            pbSpectrogram.MouseMove += OnCanvasMouseMove;
            pbSpectrogram.MouseUp += OnCanvasMouseUp;
            pbSpectrogram.Cursor = Cursors.Cross;

            // 核心：让 PictureBox 能接收鼠标滚轮事件（设为可聚焦 + 手动拦截）
            pbSpectrogram.TabStop = true;
            // 父容器滚轮不处理（留给 AutoScroll 纵向滚动），PictureBox 滚轮用于横向缩放
            pbSpectrogram.MouseWheel += OnMouseWheel;
            // 触控板/触摸滚动兼容
            pbSpectrogram.MouseEnter += (s, e) => pbSpectrogram.Focus();
            pbSpectrogram.Click += (s, e) => pbSpectrogram.Focus();

            // Ctrl 键状态跟踪
            pbSpectrogram.KeyDown += (s, e) => { if (e.Control) ctrlDown = true; };
            pbSpectrogram.KeyUp += (s, e) => { if (!e.Control) { ctrlDown = false; SetStatus(""); } };
            this.KeyDown += (s, e) => { if (e.Control) ctrlDown = true; };
            this.KeyUp += (s, e) => { if (!e.Control) { ctrlDown = false; if (!dragging) SetStatus(""); } };

            // Shift+左右键平移
            pbSpectrogram.KeyDown += OnCanvasKeyDown;
            this.KeyPreview = true; // 窗体先接收按键
            this.KeyDown += OnFormKeyDown;

            pnlCanvas.Controls.Add(pbSpectrogram);
            this.Controls.Add(pnlCanvas);

            // 拖拽
            this.AllowDrop = true;
            this.DragEnter += (s, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            this.DragDrop += (s, e) =>
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files != null && files.Length > 0) LoadFile(files[0]);
            };
        }

        void BuildToolbar()
        {
            int x = 10, y = 8;

            // ── 导入文件按钮 ──
            Button btnOpen = new Button();
            btnOpen.Text = "\u5BFC\u5165\u97F3\u9891\u6587\u4EF6";
            btnOpen.Location = new Point(x, y + 8);
            btnOpen.Size = new Size(90, 26);
            btnOpen.FlatStyle = FlatStyle.System;
            btnOpen.TabStop = false;
            btnOpen.Click += (s, e) => OnOpenFile(null, null);
            pnlToolbar.Controls.Add(btnOpen);
            x += 100;

            // ── 图片转音频（新功能，独立区域） ──
            AddDivider(x); x += 14;
            Button btnImgLoad = new Button();
            btnImgLoad.Text = "\u52A0\u8F7D\u56FE\u7247";
            btnImgLoad.Location = new Point(x, y + 8);
            btnImgLoad.Size = new Size(75, 26);
            btnImgLoad.FlatStyle = FlatStyle.System;
            btnImgLoad.TabStop = false;
            btnImgLoad.Click += (s, e) => OnLoadImage();
            pnlToolbar.Controls.Add(btnImgLoad);
            x += 85;

            // 模式切换按钮
            Button btnSwitchMode = new Button();
            btnSwitchMode.Text = "\u2192 \u56FE\u7247\u6A21\u5F0F";
            btnSwitchMode.Location = new Point(x, y + 8);
            btnSwitchMode.Size = new Size(95, 26);
            btnSwitchMode.FlatStyle = FlatStyle.System;
            btnSwitchMode.TabStop = false;
            btnSwitchMode.Click += (s, e) =>
            {
                if (currentMode == AppMode.Audio && imgEngine == null) { MessageBox.Show("\u8BF7\u5148\u52A0\u8F7D\u56FE\u7247"); return; }
                currentMode = (currentMode == AppMode.Audio) ? AppMode.Image : AppMode.Audio;
                btnSwitchMode.Text = (currentMode == AppMode.Image) ? "\u2190 \u97F3\u9891\u6A21\u5F0F" : "\u2192 \u56FE\u7247\u6A21\u5F0F";
                pnlImgParams.Visible = (currentMode == AppMode.Image);
                viewZoom = 1; viewStart = 0; Render();
            };
            pnlToolbar.Controls.Add(btnSwitchMode);
            x += 105;
            AddDivider(x); x += 14;
            AddLabel(pnlToolbar, "\u8272\u5F69\u65B9\u6848", x, y);
            cmbColormap = new ComboBox();
            cmbColormap.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbColormap.Items.AddRange(new string[] { "Viridis", "Magma", "Inferno", "Plasma", "Hot", "Cool", "Ocean", "\u7070\u5EA6" });
            cmbColormap.SelectedIndex = 7;
            cmbColormap.Location = new Point(x, y + 14);
            cmbColormap.Size = new Size(120, 22);
            cmbColormap.SelectedIndexChanged += (s, e) => Render();
            pnlToolbar.Controls.Add(cmbColormap);
            x += 130;

            // 分隔线
            x += 6; AddDivider(x); x += 14;

            // 亮度
            AddLabel(pnlToolbar, "\u4EAE\u5EA6", x, y);
            trkBrightness = new TrackBar(); trkBrightness.Minimum = 30; trkBrightness.Maximum = 300; trkBrightness.Value = 100; trkBrightness.Width = 100; trkBrightness.TickStyle = TickStyle.None; trkBrightness.Location = new Point(x, y + 12);
            lblBrightness = new Label(); lblBrightness.Text = "1.0"; lblBrightness.Location = new Point(x + 104, y + 14); lblBrightness.Size = new Size(30, 18); lblBrightness.ForeColor = Color.FromArgb(0x2C, 0x5F, 0x9A);
            trkBrightness.ValueChanged += (s, e) => { lblBrightness.Text = (trkBrightness.Value / 100.0).ToString("F1"); Render(); };
            pnlToolbar.Controls.AddRange(new Control[] { trkBrightness, lblBrightness });
            x += 140;

            // Gamma
            AddLabel(pnlToolbar, "Gamma", x, y);
            trkGamma = new TrackBar(); trkGamma.Minimum = 30; trkGamma.Maximum = 300; trkGamma.Value = 100; trkGamma.Width = 100; trkGamma.TickStyle = TickStyle.None; trkGamma.Location = new Point(x, y + 12);
            lblGamma = new Label(); lblGamma.Text = "1.0"; lblGamma.Location = new Point(x + 104, y + 14); lblGamma.Size = new Size(30, 18); lblGamma.ForeColor = Color.FromArgb(0x2C, 0x5F, 0x9A);
            trkGamma.ValueChanged += (s, e) => { lblGamma.Text = (trkGamma.Value / 100.0).ToString("F1"); Render(); };
            pnlToolbar.Controls.AddRange(new Control[] { trkGamma, lblGamma });
            x += 140;

            // 分隔线
            x += 6; AddDivider(x); x += 14;

            // FFT 窗口
            AddLabel(pnlToolbar, "FFT\u7A97\u53E3", x, y);
            cmbFftSize = new ComboBox();
            cmbFftSize.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbFftSize.Items.AddRange(new string[] { "1024\u5FEB\u901F", "2048\u5747\u8861", "4096\u7CBE\u7EC6", "8192\u8D85\u7CBE\u7EC6" });
            cmbFftSize.SelectedIndex = 3;
            cmbFftSize.Location = new Point(x, y + 14);
            cmbFftSize.Size = new Size(90, 22);
            cmbFftSize.SelectedIndexChanged += (s, e) => Recompute();
            pnlToolbar.Controls.Add(cmbFftSize);
            x += 98;

            // 最大频率
            AddLabel(pnlToolbar, "\u6700\u5927\u9891\u7387", x, y);
            cmbMaxFreq = new ComboBox();
            cmbMaxFreq.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbMaxFreq.Items.AddRange(new string[] { "8000", "12000", "16000", "22050" });
            cmbMaxFreq.SelectedIndex = 3;
            cmbMaxFreq.Location = new Point(x, y + 14);
            cmbMaxFreq.Size = new Size(70, 22);
            cmbMaxFreq.SelectedIndexChanged += cmbMaxFreq_SelectedIndexChanged;
            pnlToolbar.Controls.Add(cmbMaxFreq);
            x += 78;

            // 纵轴刻度
            AddLabel(pnlToolbar, "\u7EB5\u8F74\u523B\u5EA6", x, y);
            cmbFreqScale = new ComboBox();
            cmbFreqScale.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbFreqScale.Items.AddRange(new string[] { "\u7EBF\u6027", "\u5BF9\u6570" });
            cmbFreqScale.SelectedIndex = 0;
            cmbFreqScale.Location = new Point(x, y + 14);
            cmbFreqScale.Size = new Size(60, 22);
            cmbFreqScale.SelectedIndexChanged += (s, e) => Render();
            pnlToolbar.Controls.Add(cmbFreqScale);
            x += 68;

            // 动态范围
            AddLabel(pnlToolbar, "\u52A8\u6001\u8303\u56F4(dB)", x, y);
            trkDynRange = new TrackBar(); trkDynRange.Minimum = 40; trkDynRange.Maximum = 120; trkDynRange.Value = 80; trkDynRange.Width = 80; trkDynRange.TickStyle = TickStyle.None; trkDynRange.Location = new Point(x, y + 12);
            lblDynRange = new Label(); lblDynRange.Text = "80"; lblDynRange.Location = new Point(x + 84, y + 14); lblDynRange.Size = new Size(30, 18); lblDynRange.ForeColor = Color.FromArgb(0x2C, 0x5F, 0x9A);
            trkDynRange.ValueChanged += (s, e) => { lblDynRange.Text = trkDynRange.Value.ToString(); Render(); };
            pnlToolbar.Controls.AddRange(new Control[] { trkDynRange, lblDynRange });
            x += 120;

            // 保存选区按钮
            btnSaveSel = new Button();
            btnSaveSel.Text = "\u4FDD\u5B58\u9009\u533A";
            btnSaveSel.Location = new Point(x + 10, y + 8);
            btnSaveSel.Size = new Size(75, 26);
            btnSaveSel.FlatStyle = FlatStyle.System;
            btnSaveSel.TabStop = false;
            btnSaveSel.Enabled = false;
            btnSaveSel.Click += (s, e) => OpenFrameSelector();
            pnlToolbar.Controls.Add(btnSaveSel);
        }

        void AddLabel(Panel parent, string text, int x, int y)
        {
            Label lbl = new Label();
            lbl.Text = text;
            lbl.Location = new Point(x, y);
            lbl.Size = new Size(80, 13);
            lbl.ForeColor = Color.FromArgb(0x55, 0x55, 0x55);
            lbl.Font = new Font("\u5FAE\u8F6F\u96C5\u9ED1", 8);
            parent.Controls.Add(lbl);
        }

        void AddDivider(int x)
        {
            Panel div = new Panel();
            div.Size = new Size(1, 56);
            div.Location = new Point(x, 12);
            div.BackColor = Color.FromArgb(0xC0, 0xC0, 0xC0);
            pnlToolbar.Controls.Add(div);
        }

        // ── 文件操作 ──────────────────────────────
        void OnOpenFile(object sender, EventArgs e)
        {
            // 清空图片模式 + 释放旧引擎数据
            if (imgEngine != null) { imgEngine.Data = null; imgEngine = null; }
            if (engine != null) { engine.Data = null; engine = null; }
            currentMode = AppMode.Audio;
            OpenFileDialog dlg = new OpenFileDialog();
            dlg.Title = "\u9009\u62E9\u97F3\u9891\u6587\u4EF6";
            dlg.Filter = "\u97F3\u9891\u6587\u4EF6|*.mp3;*.wav;*.ogg;*.flac;*.m4a;*.aac;*.opus;*.wma;*.aiff|\u6240\u6709\u6587\u4EF6|*.*";
            if (dlg.ShowDialog() == DialogResult.OK)
                LoadFile(dlg.FileName);
        }

        void LoadFile(string path)
        {
            if (isProcessing) return;
            isProcessing = true;
            fileName = Path.GetFileName(path);

            SetStatus("\u6B63\u5728\u89E3\u7801\u97F3\u9891...");

            // 在后台线程解码
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                float[] decoded = null;
                int sr = 0;
                try
                {
                    int ch;
                    decoded = AudioDecoder.Decode(path, out sr, out ch);
                }
                catch (Exception ex)
                {
                    this.Invoke((MethodInvoker)(() => SetStatus("\u89E3\u7801\u5931\u8D25\uFF1A" + ex.Message)));
                    isProcessing = false;
                    return;
                }

                if (decoded == null || decoded.Length == 0)
                {
                    this.Invoke((MethodInvoker)(() => SetStatus("\u89E3\u7801\u5931\u8D25")));
                    isProcessing = false;
                    return;
                }

                this.Invoke((MethodInvoker)(() =>
                {
                    samples = decoded;
                    sampleRate = sr;
                    SetStatus(string.Format("\u89E3\u7801\u5B8C\u6210\uFF0C{0:F2}\u79D2 {1}Hz", (double)samples.Length / sampleRate, sampleRate));
                    Application.DoEvents();
                    isProcessing = false;
                    Recompute();
                }));
            });
        }

        void cmbMaxFreq_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (isProcessing) return;
            if (currentMode == AppMode.Image && imgEngine != null)
            {
                int[] freqOpts = { 8000, 12000, 16000, 22050 };
                imgEngine.MaxFreq = freqOpts[cmbMaxFreq.SelectedIndex];
                Render();
                return;
            }
            Recompute();
        }

        void Recompute()
        {
            if (samples == null || samples.Length == 0) return;
            if (isProcessing) return;
            isProcessing = true;
            SetStatus("正在计算频谱...");

            int fftSize = int.Parse(new string[] { "1024", "2048", "4096", "8192" }[cmbFftSize.SelectedIndex]);

            int nyquist = sampleRate / 2;
            int[] freqOpts = { 8000, 12000, 16000, 22050 };
            int maxFreq = freqOpts[cmbMaxFreq.SelectedIndex];
            if (!autoSetMaxFreqDone && maxFreq < nyquist && nyquist <= freqOpts[freqOpts.Length - 1])
            {
                cmbMaxFreq.SelectedIndexChanged -= cmbMaxFreq_SelectedIndexChanged;
                for (int i = 0; i < freqOpts.Length; i++)
                {
                    if (freqOpts[i] >= nyquist)
                    {
                        cmbMaxFreq.SelectedIndex = i;
                        maxFreq = freqOpts[i];
                        break;
                    }
                }
                cmbMaxFreq.SelectedIndexChanged += cmbMaxFreq_SelectedIndexChanged;
            }
            autoSetMaxFreqDone = true;

            btnSaveSel.Enabled = false;

            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                var newEngine = new SpectrogramEngine();
                newEngine.Compute(samples, sampleRate, fftSize, maxFreq);

                this.BeginInvoke((MethodInvoker)(() =>
                {
                    engine = newEngine;
                    viewZoom = 1;
                    viewStart = 0;
                    btnSaveSel.Enabled = true;
                    isProcessing = false;
                    Render();
                    SetStatus(string.Format("完成！{0}帧 · {1:F2}s", engine.Frames, engine.Duration));
                }));
            });
        }

        // ── 图片加载 ──
        void OnLoadImage()
        {
            // 清空音频模式 + 释放旧图片数据
            if (imgEngine != null) { imgEngine.Data = null; imgEngine = null; }
            engine = null; samples = null; isProcessing = false;
            OpenFileDialog dlg = new OpenFileDialog();
            dlg.Title = "\u9009\u62E9\u56FE\u7247";
            dlg.Filter = "\u56FE\u7247\u6587\u4EF6|*.png;*.jpg;*.jpeg;*.bmp";
            if (dlg.ShowDialog() != DialogResult.OK) return;
            try
            {
                int maxFreq = 22050;
                if (cmbMaxFreq != null)
                    maxFreq = int.Parse(new string[] { "8000", "12000", "16000", "22050" }[cmbMaxFreq.SelectedIndex]);
                imgEngine = new ImageEngine();
                imgEngine.Load(dlg.FileName, maxFreq);

                int dr = 80;
                if (trkDynRange != null) dr = trkDynRange.Value;
                imgEngine.Remap(dr);

                currentMode = AppMode.Image;
                viewZoom = 1; viewStart = 0;
                if (btnGenAudio != null) btnGenAudio.Enabled = true;
                SetStatus("\u56FE\u7247\u5DF2\u52A0\u8F7D\uFF1A" + Path.GetFileName(dlg.FileName));
                Render();
            }
            catch (Exception ex)
            {
                MessageBox.Show("\u52A0\u8F7D\u5931\u8D25\uFF1A" + ex.Message + "\n" + ex.GetType().Name + "\n" + ex.StackTrace);
            }
        }

        // ── 生成音频 ──
        void GenerateAudio()
        {
            if (imgEngine == null) return;
            btnGenAudio.Enabled = false;
            pbImgProgress.Visible = true;
            pbImgProgress.Value = 0;
            int sampleRate = (int)nudImgSR.Value;
            int W = imgEngine.Frames;
            int srcH = imgEngine.Bins;

            float maxP = imgEngine.MaxPower;
            float floor = maxP - trkDynRange.Value;

            int fftSize = int.Parse(new string[] { "1024", "2048", "4096", "8192" }[cmbImgFFT.SelectedIndex]);
            int hop = fftSize >> 2;
            int numBins = fftSize / 2;

            // 超大图片保护：防止 totalSamples 溢出和 OOM
            int maxSafeW = (int.MaxValue - fftSize) / hop;
            if (W > maxSafeW)
            {
                MessageBox.Show(string.Format("图片宽度 {0} 过大（上限 {1}），请先裁剪或缩小", W, maxSafeW));
                btnGenAudio.Enabled = true;
                pbImgProgress.Visible = false;
                return;
            }
            int totalSamples = (W - 1) * hop + fftSize;
            float[] output = new float[totalSamples];
            float[] winSum = new float[totalSamples];
            float maxAmp = 0;

            // Hann 窗
            float[] win = new float[fftSize];
            for (int i = 0; i < fftSize; i++)
                win[i] = 0.5f * (1 - (float)Math.Cos(2 * Math.PI * i / (fftSize - 1)));

            // 锁相：每个 bin 的相位跨帧连续
            float[] phaseAcc = new float[numBins];
            double phaseStepPerBin = 2.0 * Math.PI * hop / fftSize;

            SetStatus("正在合成音频...");

            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                // 复用 FFT 缓冲区，避免每帧分配 2048*2*4=16KB
                float[] re = new float[fftSize];
                float[] im = new float[fftSize];
                for (int x = 0; x < W; x++)
                {
                    Array.Clear(re, 0, fftSize);
                    Array.Clear(im, 0, fftSize);
                    for (int b = 0; b < numBins; b++)
                    {
                        int srcY = b * srcH / numBins;
                        if (srcY >= srcH) srcY = srcH - 1;
                        if (srcY < 0) srcY = 0;
                        float norm = (imgEngine.Data[x * srcH + srcY] - floor) / (maxP - floor);
                        float mag = Math.Max(0, Math.Min(1, norm));

                        re[b] = mag * (float)Math.Cos(phaseAcc[b]);
                        im[b] = mag * (float)Math.Sin(phaseAcc[b]);
                        phaseAcc[b] += (float)(phaseStepPerBin * b);
                        if (b > 0 && b < fftSize - b)
                        { re[fftSize - b] = re[b]; im[fftSize - b] = -im[b]; }
                    }

                    // IFFT
                    FFT(re, im, fftSize, false);

                    // 加窗 + 重叠相加
                    int off = x * hop;
                    for (int i = 0; i < fftSize && off + i < totalSamples; i++)
                    {
                        float s = re[i] * win[i];
                        output[off + i] += s;
                        winSum[off + i] += win[i];
                    }

                    if (x % 20 == 0)
                    {
                        int pct = (x + 1) * 100 / W;
                        try { this.BeginInvoke((MethodInvoker)(() => { SetStatus("\u5408\u6210\u4E2D " + pct + "%"); pbImgProgress.Value = pct; })); } catch { }
                    }
                }

                // 窗归一化 + IFFT 缩放
                for (int i = 0; i < totalSamples; i++)
                {
                    if (winSum[i] > 0.001f)
                        output[i] /= winSum[i] * fftSize;
                    float a = Math.Abs(output[i]);
                    if (a > maxAmp) maxAmp = a;
                }

                // 归一化
                if (maxAmp > 0.001f)
                    for (int i = 0; i < totalSamples; i++)
                        output[i] /= maxAmp;

                this.BeginInvoke((MethodInvoker)(() =>
                {
                    pbImgProgress.Value = 100;
                    pbImgProgress.Visible = false;
                    btnGenAudio.Enabled = true;

                    SaveFileDialog sdlg = new SaveFileDialog();
                    sdlg.Title = "\u4FDD\u5B58\u97F3\u9891";
                    sdlg.Filter = "WAV\u97F3\u9891|*.wav";
                    sdlg.FileName = "output.wav";
                    if (sdlg.ShowDialog() == DialogResult.OK)
                    {
                        // 写入单声道以避免立体声交错导致的镜像伪影
                        WavWriter.Write(sdlg.FileName, output, sampleRate, 1);
                        SetStatus("\u5DF2\u4FDD\u5B58\uFF1A" + Path.GetFileName(sdlg.FileName));
                    }
                }));
            });
        }

        static void FFT(float[] re, float[] im, int n, bool forward)
        {
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j)
                {
                    float tr = re[i]; re[i] = re[j]; re[j] = tr;
                    float ti = im[i]; im[i] = im[j]; im[j] = ti;
                }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                float ang = (float)((forward ? -2 : 2) * Math.PI / len);
                float wR = (float)Math.Cos(ang), wI = (float)Math.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    float cR = 1, cI = 0;
                    for (int j = 0; j < len >> 1; j++)
                    {
                        int u = i + j, v = i + j + (len >> 1);
                        float vR = re[v] * cR - im[v] * cI;
                        float vI = re[v] * cI + im[v] * cR;
                        re[v] = re[u] - vR; im[v] = im[u] - vI;
                        re[u] = re[u] + vR; im[u] = im[u] + vI;
                        float nR = cR * wR - cI * wI; cI = cR * wI + cI * wR; cR = nR;
                    }
                }
            }
        }

        const int MARGIN_TOP = 180;    // 顶部白边
        const int MARGIN_BOTTOM = 60; // 底部白边

        // ── 渲染（高性能）──────────────────────────
        void Render()
        {
            if (isRendering) return;
            if (pnlCanvas.Width <= 1 || pnlCanvas.Height <= 1) return;
            if (currentMode == AppMode.Audio && engine == null) return;
            if (currentMode == AppMode.Image && (imgEngine == null || imgEngine.Data == null)) return;
            isRendering = true;

            int W = Math.Min(Math.Max(pnlCanvas.Width, 100), 10000);
            int contentH = pnlCanvas.Height;
            int H = contentH + MARGIN_TOP + MARGIN_BOTTOM;
            int B = (currentMode == AppMode.Audio) ? engine.Bins : imgEngine.Bins;
            int totalFrames = (currentMode == AppMode.Audio) ? engine.Frames : imgEngine.Frames;
            float maxPow = (currentMode == AppMode.Audio) ? engine.MaxPower : imgEngine.MaxPower;
            double imgDuration = (currentMode == AppMode.Audio) ? engine.Duration : imgEngine.Duration;
            double freqEnd = (currentMode == AppMode.Audio) ? engine.MaxFreq : imgEngine.MaxFreq;

            double visFrames = totalFrames / viewZoom;
            int startF = Math.Max(0, Math.Min(viewStart, totalFrames - 1));
            int dynRangeDB = trkDynRange.Value;
            float dbFloor = maxPow - dynRangeDB;
            Color[] cmap = ColorMaps.Get(new string[] { "viridis", "magma", "inferno", "plasma", "hot", "cool", "ocean", "grayscale" }[cmbColormap.SelectedIndex]);
            double brightnessVal = trkBrightness.Value / 100.0;
            double gammaVal = trkGamma.Value / 100.0;
            bool logScale = cmbFreqScale.SelectedIndex == 1;

            // 音频模式：频率与刻度严格对应（log 时拉伸）；图片模式：始终线性
            int[] rowBins = new int[H];
            if (currentMode == AppMode.Audio && logScale)
            {
                double logAxisMin = Math.Log10(20.0);
                double logAxisMax = Math.Log10(freqEnd);
                double logRange   = logAxisMax - logAxisMin;
                for (int py = 0; py < contentH; py++)
                {
                    double frac = (double)(contentH - 1 - py) / (contentH - 1);
                    double freq = Math.Pow(10, logAxisMin + frac * logRange);
                    int b = (int)Math.Round(freq / freqEnd * (B - 1));
                    rowBins[MARGIN_TOP + py] = Math.Max(0, Math.Min(B - 1, b));
                }
            }
            else
            {
                for (int py = 0; py < contentH; py++)
                {
                    int b = (int)Math.Round((double)(B - 1) * (contentH - 1 - py) / (contentH - 1));
                    rowBins[MARGIN_TOP + py] = Math.Max(0, Math.Min(B - 1, b));
                }
            }

            // 2. gamma+亮度查找表（256级输入 → 0~255 colormap索引）
            byte[] cmapLookup = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                double t2 = Math.Pow(i / 255.0, gammaVal) * brightnessVal;
                int ci = (int)(Math.Max(0, Math.Min(1, t2)) * 255);
                cmapLookup[i] = (byte)Math.Max(0, Math.Min(255, ci));
            }

            // 3. colormap 预转 ARGB int
            int[] cmapArgb = new int[256];
            for (int i = 0; i < 256; i++)
                cmapArgb[i] = cmap[i].B | (cmap[i].G << 8) | (cmap[i].R << 16) | (255 << 24);

            // ── 渲染 ──
            Bitmap bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb);
            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            int[] pixels = new int[W * H];
            float dynRangeInv = 1f / dynRangeDB;
            float[] srcData = (currentMode == AppMode.Audio) ? engine.Data : imgEngine.Data;

            for (int x = 0; x < W; x++)
            {
                int frame = (int)(startF + (double)x / W * visFrames);
                if (frame < 0 || frame >= totalFrames) continue;
                int frameOff = frame * B;
                int colOff = x;

                for (int py = 0; py < H; py++)
                {
                    if (py < MARGIN_TOP || py >= MARGIN_TOP + contentH) { colOff += W; continue; }
                    float p = srcData[frameOff + rowBins[py]];
                    int rawI;
                    if (p <= dbFloor) rawI = 0;
                    else if (p >= maxPow) rawI = 255;
                    else rawI = (int)((p - dbFloor) * dynRangeInv * 255);

                    pixels[colOff] = cmapArgb[cmapLookup[rawI]];
                    colOff += W;
                }
            }

            Marshal.Copy(pixels, 0, bd.Scan0, pixels.Length);
            bmp.UnlockBits(bd);

            // 画频率轴和时间轴
            using (Graphics g = Graphics.FromImage(bmp))
            {
                DrawAxes(g, W, H, startF, visFrames, logScale);
            }

            if (pbSpectrogram.Image != null)
                pbSpectrogram.Image.Dispose();
            // 记住当前纵向滚动位置
            int oldScrollY = -pnlCanvas.AutoScrollPosition.Y;
            pbSpectrogram.Image = bmp;
            pbSpectrogram.Size = bmp.Size;
            // 还原纵向滚动位置
            int maxScroll = Math.Max(0, bmp.Height - pnlCanvas.Height);
            pnlCanvas.AutoScrollPosition = new Point(0, Math.Max(0, Math.Min(oldScrollY, maxScroll)));

            // 更新滚动条
            sbScroll.ValueChanged -= sbScroll_ValueChanged;
            sbScroll.Minimum = 0;
            sbScroll.Maximum = 10000;
            int maxStart2 = Math.Max(0, totalFrames - (int)(totalFrames / viewZoom));
            sbScroll.Value = maxStart2 > 0 ? Math.Min(10000, (int)((double)viewStart / maxStart2 * 10000)) : 0;
            sbScroll.ValueChanged += sbScroll_ValueChanged;

            // 更新时间标签
            double t0 = startF / (double)totalFrames * imgDuration;
            double t1 = (startF + visFrames) / (double)totalFrames * imgDuration;
            lblTime.Text = string.Format("{0:F2}s \u2014 {1:F2}s", t0, t1);
            lblZoom.Text = viewZoom.ToString("F2") + "\u00D7";
            isRendering = false;
        }

        void sbScroll_ValueChanged(object sender, EventArgs e)
        {
            int totalFrames2 = (currentMode == AppMode.Audio) ? (engine != null ? engine.Frames : 0) : (imgEngine != null ? imgEngine.Frames : 0);
            if (totalFrames2 > 0 && viewZoom > 0)
            {
                int maxStart = totalFrames2 - (int)(totalFrames2 / viewZoom);
                if (maxStart > 0)
                {
                    viewStart = (int)((double)sbScroll.Value / 10000.0 * maxStart);
                    Render();
                }
            }
        }

        void DrawAxes(Graphics g, int W, int H, int startF, double visFrames, bool logScale)
        {
            if (currentMode == AppMode.Audio && engine == null) return;
            if (currentMode == AppMode.Image && (imgEngine == null || imgEngine.Data == null)) return;
            int contentH = H - MARGIN_TOP - MARGIN_BOTTOM;
            if (contentH < 10) return;
            using (Brush bgBrush = new SolidBrush(Color.FromArgb(80, 0, 0, 0)))
                g.FillRectangle(bgBrush, Math.Max(0, W - 62), MARGIN_TOP, 62, contentH);

            // 轴标签覆盖完整频率范围 0~MaxFreq
            double freqEnd = (currentMode == AppMode.Audio) ? engine.MaxFreq : imgEngine.MaxFreq;
            double logAxisMin = Math.Log10(20.0);
            double logAxisMax = Math.Log10(freqEnd);

            using (Pen linePen = new Pen(Color.FromArgb(30, 200, 200, 200)))
            using (Brush textBrush = new SolidBrush(Color.FromArgb(180, 180, 180)))
            {
                Font font = new Font("Arial", 8);

                int maxFreqInt = (int)Math.Round(freqEnd);
                List<int> tickList;
                if (logScale)
                {
                    tickList = new List<int>(new int[] { 20, 30, 50, 70, 100, 150, 200, 300, 500, 700, 1000, 1500, 2000, 3000, 5000, 7000, 10000, 15000, 20000 });
                    if (!tickList.Contains(maxFreqInt) && maxFreqInt > 0) tickList.Add(maxFreqInt);
                    tickList.Sort(); tickList.Reverse();
                }
                else
                {
                    tickList = new List<int>(new int[] { 0, 20, 50, 100, 200, 500, 1000, 2000, 4000, 6000, 8000, 10000, 12000, 16000, 20000 });
                    if (!tickList.Contains(maxFreqInt) && maxFreqInt > 0) tickList.Add(maxFreqInt);
                    tickList.Sort(); tickList.Reverse();
                }

                int lastLabelY = -999;
                const int minLabelGap = 4;

                foreach (int f in tickList)
                {
                    if (f < 0 || f > maxFreqInt) continue;
                    int y;
                    if (logScale)
                    {
                        if (f <= 0) continue;
                        double frac = (Math.Log10(Math.Max(20, f)) - logAxisMin) / (logAxisMax - logAxisMin);
                        y = MARGIN_TOP + (int)((contentH - 1) * (1 - frac));
                    }
                    else
                    {
                        y = MARGIN_TOP + (contentH - 1) - (int)((double)f / freqEnd * (contentH - 1));
                    }
                    if (y < MARGIN_TOP || y >= MARGIN_TOP + contentH) continue;
                    g.DrawLine(linePen, 0, y, W - 62, y);
                    bool isMax = (f == maxFreqInt);
                    if (isMax || lastLabelY == -999 || Math.Abs(y - lastLabelY) >= minLabelGap)
                    {
                        string label = f >= 1000 ? (f / 1000) + "k" : f.ToString();
                        if (isMax) label += "↑";
                        int labelY = Math.Max(14, Math.Min(H - 20, y - 6));
                        g.DrawString(label, font, textBrush, W - 58, labelY);
                        lastLabelY = y;
                    }
                }

                // 底部白边内的 0Hz 红色边界
                using (Pen p = new Pen(Color.Red, 2))
                    g.DrawLine(p, 10, H - MARGIN_BOTTOM, W - 70, H - MARGIN_BOTTOM);
                g.DrawString("0Hz", new Font("Arial", 9, FontStyle.Bold), Brushes.Red, W - 60, H - MARGIN_BOTTOM + 4);

                // 顶部白边内的最大频率红色边界
                using (Pen p = new Pen(Color.Red, 2))
                    g.DrawLine(p, 10, MARGIN_TOP - 1, W - 70, MARGIN_TOP - 1);
                g.DrawString(maxFreqInt + "Hz↑", new Font("Arial", 9, FontStyle.Bold), Brushes.Red, W - 60, 2);

                int frameCount = (currentMode == AppMode.Audio) ? engine.Frames : imgEngine.Frames;
                double totalDur = (currentMode == AppMode.Audio) ? engine.Duration : imgEngine.Duration;
                double t0 = startF / (double)frameCount * totalDur;
                double t1 = (startF + visFrames) / (double)frameCount * totalDur;
                double span = t1 - t0;
                double step = NiceStep(span / 8);
                for (double t = Math.Ceiling(t0 / step) * step; t <= t1; t += step)
                {
                    int x = (int)((t - t0) / span * (W - 62));
                    if (x < 0 || x > W - 62) continue;
                    g.DrawLine(linePen, x, MARGIN_TOP, x, MARGIN_TOP + contentH - 1);
                    g.DrawString(t.ToString(span < 2 ? "F2" : "F1") + "s", font, textBrush, x, H - 12);
                }
            }
        }

        double NiceStep(double approx)
        {
            double e = Math.Pow(10, Math.Floor(Math.Log10(approx)));
            foreach (double m in new double[] { 1, 2, 5, 10 })
                if (m * e >= approx) return m * e;
            return e * 10;
        }

        // ── 缩放 ──────────────────────────────────
        void ChangeZoom(double factor)
        {
            int totalF = (currentMode == AppMode.Audio) ? (engine != null ? engine.Frames : 0) : (imgEngine != null ? imgEngine.Frames : 0);
            if (totalF <= 0) return;
            double newZoom = Math.Max(0.1, Math.Min(32, viewZoom * factor));
            double newVis = totalF / newZoom;
            double anchor = 0.5;
            double frameAnchor = viewStart + anchor * (totalF / viewZoom);
            int newStart = Math.Max(0, Math.Min(totalF - (int)newVis, (int)(frameAnchor - anchor * newVis)));
            viewZoom = newZoom;
            viewStart = newStart;
            Render();
        }

        // ── 鼠标交互 ──
        bool dragging = false;
        int dragX, dragStart;
        bool ctrlDown = false;
        bool isRendering = false; // 防重入

        void OnCanvasMouseMove(object sender, MouseEventArgs e)
        {
            if (dragging)
            {
                int tf_drag = (currentMode == AppMode.Audio) ? (engine != null ? engine.Frames : 0) : (imgEngine != null ? imgEngine.Frames : 0);
                if (tf_drag <= 0) return;
                int dx = e.X - dragX;
                double visFrames_drag = tf_drag / viewZoom;
                double framesPerPx_drag = visFrames_drag / pbSpectrogram.Width;
                int maxStart_drag = Math.Max(0, tf_drag - (int)visFrames_drag);
                viewStart = Math.Max(0, Math.Min(maxStart_drag, dragStart - (int)(dx * framesPerPx_drag)));
                Render();
                return;
            }

            // Ctrl 按下时在状态栏显示当前帧号和频率
            int tf = (currentMode == AppMode.Audio) ? (engine != null ? engine.Frames : 0) : (imgEngine != null ? imgEngine.Frames : 0);
            if (ctrlDown && tf > 0 && pbSpectrogram.Width > 0 && pbSpectrogram.Height > 0)
            {
                double visFrames = tf / viewZoom;
                int startF = Math.Max(0, Math.Min(viewStart, tf - 1));
                int frame = Math.Min((int)(startF + (double)e.X / pbSpectrogram.Width * visFrames), tf - 1);

                if (frame >= 0)
                {
                    double freqAtCursor;
                    int totalF_c;
                    double dur_c;
                    if (currentMode == AppMode.Audio)
                    { freqAtCursor = (double)engine.MaxFreq * (pbSpectrogram.Height - 1 - e.Y) / (pbSpectrogram.Height - 1); totalF_c = engine.Frames; dur_c = engine.Duration; }
                    else
                    { freqAtCursor = (double)imgEngine.MaxFreq * (pbSpectrogram.Height - 1 - e.Y) / (pbSpectrogram.Height - 1); totalF_c = imgEngine.Frames; dur_c = imgEngine.Duration; }
                    SetStatus(string.Format("\u5E27\uFF1A{0}/{1}  \u2022 {2:F2}s  \u2022 {3:F0}Hz", frame, totalF_c, (double)frame / totalF_c * dur_c, freqAtCursor));
                }
            }
        }

        void OnMouseWheel(object sender, MouseEventArgs e)
        {
            int totalF_w = (currentMode == AppMode.Audio) ? (engine != null ? engine.Frames : 0) : (imgEngine != null ? imgEngine.Frames : 0);
            if (totalF_w <= 0) return;
            double fracX = (double)e.X / pbSpectrogram.Width;
            double visFrames = totalF_w / viewZoom;
            double frameUnder = viewStart + fracX * visFrames;
            double factor = 1 + Math.Abs(e.Delta) / 120.0 * 0.15;
            if (e.Delta < 0) factor = 1.0 / factor;
            double newZoom = Math.Max(0.1, Math.Min(32, viewZoom * factor));
            double newVis = totalF_w / newZoom;
            int newStart = Math.Max(0, Math.Min(totalF_w - (int)newVis, (int)(frameUnder - fracX * newVis)));
            viewZoom = newZoom;
            viewStart = newStart;
            Render();
        }

        void OnCanvasMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && engine != null)
            {
                dragging = true;
                dragX = e.X;
                dragStart = viewStart;
                pbSpectrogram.Cursor = Cursors.SizeAll;
            }
        }

        void OnCanvasMouseUp(object sender, MouseEventArgs e)
        {
            if (dragging)
            {
                dragging = false;
                pbSpectrogram.Cursor = Cursors.Cross;
            }
        }

        // ── 键盘平移 ──
        bool IsDataReady()
        {
            return (currentMode == AppMode.Audio && engine != null) ||
                   (currentMode == AppMode.Image && imgEngine != null && imgEngine.Data != null);
        }

        void OnCanvasKeyDown(object sender, KeyEventArgs e)
        {
            if (!IsDataReady()) return;
            HandlePanKeys(e);
        }

        void OnFormKeyDown(object sender, KeyEventArgs e)
        {
            if (!IsDataReady()) return;
            HandlePanKeys(e);
        }

        void HandlePanKeys(KeyEventArgs e)
        {
            if (!e.Shift) return;

            // 左右 = 横向平移
            if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Right)
            {
                int totalF = (currentMode == AppMode.Audio) ? engine.Frames : (imgEngine != null ? imgEngine.Frames : 0);
                if (totalF <= 0) return;
                double visFrames = totalF / viewZoom;
                double panAmount = visFrames * 0.08;
                int maxStart = Math.Max(0, totalF - (int)visFrames);
                if (e.KeyCode == Keys.Left)
                    viewStart = Math.Max(0, viewStart - (int)panAmount);
                else
                    viewStart = Math.Min(maxStart, viewStart + (int)panAmount);
                Render();
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }

            // 上下 = 纵向滚动图片
            if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down)
            {
                int scroll = e.KeyCode == Keys.Up ? -40 : 40;
                int cur = -pnlCanvas.AutoScrollPosition.Y;
                pnlCanvas.AutoScrollPosition = new Point(0, cur + scroll);
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        // ── 帧选择器打开 ──
        void OpenFrameSelector()
        {
            if (engine == null) return;
            using (FrameSelectorForm frm = new FrameSelectorForm(engine, fileName,
                cmbFreqScale.SelectedIndex == 1, trkDynRange.Value,
                trkBrightness.Value / 100.0, trkGamma.Value / 100.0,
                new string[] { "viridis", "magma", "inferno", "plasma", "hot", "cool", "ocean", "grayscale" }[cmbColormap.SelectedIndex]))
            {
                frm.ShowDialog(this);
                if (frm.savedPath != null)
                    SetStatus(string.Format("\u5DF2\u4FDD\u5B58\uFF1A{0}", Path.GetFileName(frm.savedPath)));
            }
        }

        void SetStatus(string msg)
        {
            lblStatus.Text = msg;
        }
    }

    // ── 自定义 Panel：滚轮不触发 AutoScroll ──
    class NoScrollPanel : Panel
    {
        protected override void WndProc(ref Message m)
        {
            const int WM_MOUSEWHEEL = 0x020A;
            if (m.Msg == WM_MOUSEWHEEL) return;
            base.WndProc(ref m);
        }
    }

    // ── 帧范围选择 & 保存窗口 ─────────────────────
    class FrameSelectorForm : Form
    {
        SpectrogramEngine engine;
        string audioFileName;
        bool logScale;
        int dynRangeDB;
        double brightness, gammaVal;
        string cmapName;

        int startFrame, endFrame;
        int dragLine = -1;      // 0=起始线  1=结束线
        int dragStartX, dragStartFrame;
        bool updatingNud = false;

        PictureBox pbPreview;
        NumericUpDown nudStart, nudEnd, nudZoom, nudVZoom;
        Label lblInfo;
        public string savedPath;

        public FrameSelectorForm(SpectrogramEngine eng, string audioName, bool log, int dyn, double bright, double gamma, string cmap)
        {
            engine        = eng;
            audioFileName = audioName;
            logScale      = log;
            dynRangeDB    = dyn;
            brightness    = bright;
            gammaVal      = gamma;
            cmapName      = cmap;
            startFrame    = 0;
            endFrame      = engine.Frames;

            Text            = "保存频谱图";
            Width           = 960; Height = 560;
            MinimumSize     = new Size(600, 440);
            StartPosition   = FormStartPosition.CenterParent;
            MinimizeBox     = false;
            FormBorderStyle = FormBorderStyle.Sizable;
            BackColor       = Color.FromArgb(0xF0, 0xF0, 0xF0);
            Font            = new Font("微软雅黑", 9);

            BuildUI();
            this.Shown  += (s, e) => { RenderPreview(); pbPreview.Invalidate(); };
            this.Resize += (s, e) => { RenderPreview(); pbPreview.Invalidate(); };
        }

        void BuildUI()
        {
            // 底部控制条
            Panel bottom = new Panel();
            bottom.Height      = 88;
            bottom.Dock        = DockStyle.Bottom;
            bottom.BackColor   = Color.FromArgb(0xE8, 0xE8, 0xE8);
            bottom.BorderStyle = BorderStyle.FixedSingle;
            this.Controls.Add(bottom);

            // 预览画布
            pbPreview           = new PictureBox();
            pbPreview.Dock      = DockStyle.Fill;
            pbPreview.SizeMode  = PictureBoxSizeMode.StretchImage;
            pbPreview.BackColor = Color.Black;
            pbPreview.Cursor    = Cursors.Default;
            pbPreview.MouseDown += OnMouseDown;
            pbPreview.MouseMove += OnMouseMove;
            pbPreview.MouseUp   += OnMouseUp;
            pbPreview.Paint     += OnPaint;
            this.Controls.Add(pbPreview);

            // 行1：起始帧 / 结束帧
            int bx = 10, by = 10;
            bottom.Controls.Add(MakeLbl("起始帧:", bx, by + 2)); bx += 52;
            nudStart = MakeNud(bx, by, 0, engine.Frames, 0, 90);
            nudStart.ValueChanged += (s, e) =>
            {
                if (updatingNud) return;
                startFrame = (int)nudStart.Value;
                if (startFrame >= endFrame) { endFrame = Math.Min(engine.Frames, startFrame + 1); SetNud(nudEnd, endFrame); }
                pbPreview.Invalidate(); UpdateInfo();
            };
            bottom.Controls.Add(nudStart); bx += 100;

            bottom.Controls.Add(MakeLbl("结束帧:", bx, by + 2)); bx += 52;
            nudEnd = MakeNud(bx, by, 0, engine.Frames, engine.Frames, 90);
            nudEnd.ValueChanged += (s, e) =>
            {
                if (updatingNud) return;
                endFrame = (int)nudEnd.Value;
                if (endFrame <= startFrame) { startFrame = Math.Max(0, endFrame - 1); SetNud(nudStart, startFrame); }
                pbPreview.Invalidate(); UpdateInfo();
            };
            bottom.Controls.Add(nudEnd); bx += 100;

            lblInfo = new Label();
            lblInfo.Location  = new Point(bx, by + 2);
            lblInfo.Size      = new Size(260, 18);
            lblInfo.ForeColor = Color.FromArgb(0x55, 0x55, 0x55);
            bottom.Controls.Add(lblInfo);

            // 行2：缩放倍率 + 保存/关闭
            bx = 10; by = 50;
            bottom.Controls.Add(MakeLbl("缩放倍率:", bx, by + 2)); bx += 58;
            nudZoom = MakeNud(bx, by, (decimal)0.1, 200, 1, 72);
            nudZoom.DecimalPlaces = 1;
            nudZoom.Increment     = (decimal)0.5;
            nudZoom.ValueChanged += (s, e) => UpdateInfo();
            bottom.Controls.Add(nudZoom); bx += 82;
            bottom.Controls.Add(MakeLbl("x横  ", bx, by + 2)); bx += 42;

            bottom.Controls.Add(MakeLbl("纵向:", bx, by + 2)); bx += 38;
            nudVZoom = MakeNud(bx, by, (decimal)0.1, 50, 1, 72);
            nudVZoom.DecimalPlaces = 1;
            nudVZoom.Increment     = (decimal)0.5;
            nudVZoom.ValueChanged += (s, e) => UpdateInfo();
            bottom.Controls.Add(nudVZoom); bx += 82;
            bottom.Controls.Add(MakeLbl("x纵  （控制输出图片尺寸）", bx, by + 2));

            Button btnSave = new Button();
            btnSave.Text     = "保存图片";
            btnSave.Size     = new Size(80, 26);
            btnSave.Location = new Point(bottom.Width - 172, 30);
            btnSave.Anchor   = AnchorStyles.Right | AnchorStyles.Top;
            btnSave.Click   += (s, e) => DoSave();
            bottom.Controls.Add(btnSave);

            Button btnClose = new Button();
            btnClose.Text     = "关闭";
            btnClose.Size     = new Size(80, 26);
            btnClose.Location = new Point(bottom.Width - 86, 30);
            btnClose.Anchor   = AnchorStyles.Right | AnchorStyles.Top;
            btnClose.Click   += (s, e) => Close();
            bottom.Controls.Add(btnClose);

            UpdateInfo();
        }

        Label MakeLbl(string t, int x, int y) { var l = new Label(); l.Text = t; l.Location = new Point(x, y); l.AutoSize = true; return l; }

        NumericUpDown MakeNud(int x, int y, decimal min, decimal max, decimal val, int w)
        {
            var n = new NumericUpDown();
            n.Location = new Point(x, y); n.Width = w;
            n.Minimum = min; n.Maximum = max; n.Value = val;
            return n;
        }

        void SetNud(NumericUpDown n, int v) { updatingNud = true; n.Value = v; updatingNud = false; }

        void UpdateInfo()
        {
            int frames = Math.Max(1, endFrame - startFrame);
            int outW   = Math.Max(1, (int)Math.Round(frames * (double)nudZoom.Value));
            int outH   = Math.Max(1, (int)Math.Round(pbPreview.Height * (double)nudVZoom.Value));
            double dur = (double)frames / engine.Frames * engine.Duration;
            lblInfo.Text = string.Format("{0} 帧  {1:F2}s  →  {2}×{3}px", frames, dur, outW, outH);
        }

        // ── 预览渲染（全谱）──
        void RenderPreview()
        {
            if (engine == null || pbPreview.Width <= 1 || pbPreview.Height <= 1) return;
            int W = pbPreview.Width, H = pbPreview.Height, B = engine.Bins;
            float dbFloor = engine.MaxPower - dynRangeDB;
            Color[] cmap  = ColorMaps.Get(cmapName);
            int[] rowBins = CalcRowBins(H, B);
            byte[] lut    = CalcLut();
            int[]  cArgb  = CalcCmapArgb(cmap);

            Bitmap bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb);
            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            int[] px  = new int[W * H];
            float inv = 1f / dynRangeDB;

            for (int x = 0; x < W; x++)
            {
                int frame = Math.Min((int)((double)x / W * engine.Frames), engine.Frames - 1);
                int fOff  = frame * B;
                for (int py = 0; py < H; py++)
                {
                    float p = engine.Data[fOff + rowBins[py]];
                    int ri  = p <= dbFloor ? 0 : p >= engine.MaxPower ? 255 : (int)((p - dbFloor) * inv * 255);
                    px[py * W + x] = cArgb[lut[ri]];
                }
            }
            Marshal.Copy(px, 0, bd.Scan0, px.Length);
            bmp.UnlockBits(bd);
            if (pbPreview.Image != null) pbPreview.Image.Dispose();
            pbPreview.Image = bmp;
        }

        // ── 画竖线标记 ──
        void OnPaint(object sender, PaintEventArgs e)
        {
            if (engine == null || pbPreview.Width <= 1) return;
            int W = pbPreview.Width, H = pbPreview.Height;
            int x1 = (int)((double)startFrame / engine.Frames * W);
            int x2 = (int)((double)endFrame   / engine.Frames * W);

            using (Brush sel = new SolidBrush(Color.FromArgb(35, 80, 160, 255)))
                e.Graphics.FillRectangle(sel, x1, 0, x2 - x1, H);

            using (Pen p = new Pen(Color.FromArgb(220, 80, 220, 80), 2))
                e.Graphics.DrawLine(p, x1, 0, x1, H);
            using (Pen p = new Pen(Color.FromArgb(220, 220, 80, 80), 2))
                e.Graphics.DrawLine(p, x2, 0, x2, H);

            Font  f   = new Font("Arial", 8, FontStyle.Bold);
            using (Brush bg  = new SolidBrush(Color.FromArgb(160, 0, 0, 0)))
            using (Brush fg1 = new SolidBrush(Color.FromArgb(220, 80, 255, 80)))
            using (Brush fg2 = new SolidBrush(Color.FromArgb(220, 255, 100, 80)))
            {
                string s1 = "◀ " + startFrame;
                string s2 = endFrame + " ▶";
                SizeF sz1 = e.Graphics.MeasureString(s1, f);
                SizeF sz2 = e.Graphics.MeasureString(s2, f);
                int ly = H - 24;
                e.Graphics.FillRectangle(bg,  x1 + 3,           ly, sz1.Width + 2, sz1.Height);
                e.Graphics.DrawString(s1, f, fg1, x1 + 4,       ly);
                e.Graphics.FillRectangle(bg,  x2 - sz2.Width - 5, ly, sz2.Width + 2, sz2.Height);
                e.Graphics.DrawString(s2, f, fg2, x2 - sz2.Width - 4, ly);
            }
        }

        // ── 拖拽 ──
        int HitLine(int mouseX)
        {
            int W  = pbPreview.Width;
            int x1 = (int)((double)startFrame / engine.Frames * W);
            int x2 = (int)((double)endFrame   / engine.Frames * W);
            if (Math.Abs(mouseX - x1) <= 8) return 0;
            if (Math.Abs(mouseX - x2) <= 8) return 1;
            return -1;
        }

        void OnMouseDown(object sender, MouseEventArgs e)
        {
            if (engine == null) return;
            int hit = HitLine(e.X);
            if (hit < 0) return;
            dragLine       = hit;
            dragStartX     = e.X;
            dragStartFrame = hit == 0 ? startFrame : endFrame;
            pbPreview.Cursor = Cursors.SizeWE;
        }

        void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (engine == null) return;
            if (dragLine < 0) { pbPreview.Cursor = HitLine(e.X) >= 0 ? Cursors.SizeWE : Cursors.Default; return; }
            int dx       = e.X - dragStartX;
            double fpp   = (double)engine.Frames / pbPreview.Width;
            int newFrame = Math.Max(0, Math.Min(engine.Frames, dragStartFrame + (int)(dx * fpp)));
            if (dragLine == 0) { startFrame = Math.Min(newFrame, endFrame - 1); SetNud(nudStart, startFrame); }
            else               { endFrame   = Math.Max(newFrame, startFrame + 1); SetNud(nudEnd, endFrame); }
            pbPreview.Invalidate(); UpdateInfo();
        }

        void OnMouseUp(object sender, MouseEventArgs e) { dragLine = -1; }

        // ── 保存 ──
        void DoSave()
        {
            if (startFrame >= endFrame) { MessageBox.Show(this, "起始帧必须小于结束帧"); return; }
            int B          = engine.Bins;
            int totalFrames= endFrame - startFrame;
            int W          = Math.Max(1, (int)Math.Round(totalFrames * (double)nudZoom.Value));
            int H          = Math.Max(1, (int)Math.Round(pbPreview.Height * (double)nudVZoom.Value));
            if (W > 30000 && MessageBox.Show(this, string.Format("输出宽度 {0}px，文件可能很大，继续？", W), "确认", MessageBoxButtons.YesNo) != DialogResult.Yes) return;

            SaveFileDialog dlg = new SaveFileDialog();
            dlg.Title    = "保存频谱图";
            dlg.Filter   = "PNG图片|*.png";
            dlg.FileName = Path.GetFileNameWithoutExtension(audioFileName) +
                           string.Format("_f{0}-{1}_z{2}.png", startFrame, endFrame, nudZoom.Value.ToString("F1"));
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            try
            {
                float  dbFloor = engine.MaxPower - dynRangeDB;
                Color[] cmap   = ColorMaps.Get(cmapName);
                int[]  rowBins = CalcRowBins(H, B);
                byte[] lut     = CalcLut();
                int[]  cArgb   = CalcCmapArgb(cmap);

                Bitmap bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb);
                BitmapData bd = bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                int[]  px  = new int[W * H];
                float  inv = 1f / dynRangeDB;

                for (int x = 0; x < W; x++)
                {
                    int frame = startFrame + Math.Min((int)((double)x / W * totalFrames), totalFrames - 1);
                    int fOff  = frame * B;
                    for (int py = 0; py < H; py++)
                    {
                        float p = engine.Data[fOff + rowBins[py]];
                        int ri  = p <= dbFloor ? 0 : p >= engine.MaxPower ? 255 : (int)((p - dbFloor) * inv * 255);
                        px[py * W + x] = cArgb[lut[ri]];
                    }
                }
                Marshal.Copy(px, 0, bd.Scan0, px.Length);
                bmp.UnlockBits(bd);
                bmp.Save(dlg.FileName, ImageFormat.Png);
                bmp.Dispose();

                savedPath = dlg.FileName;
                MessageBox.Show(this, "已保存：" + dlg.FileName, "成功");
            }
            catch (Exception ex) { MessageBox.Show(this, "保存失败：" + ex.Message, "错误"); }
        }

        // ── 工具函数 ──
        int[] CalcRowBins(int H, int B)
        {
            int[] r = new int[H];
            if (logScale)
            {
                double lmin = Math.Log10(20.0), lmax = Math.Log10(engine.MaxFreq), lr = lmax - lmin;
                for (int py = 0; py < H; py++)
                {
                    double frac = (double)(H - 1 - py) / (H - 1);
                    double freq = Math.Pow(10, lmin + frac * lr);
                    r[py] = Math.Max(0, Math.Min(B - 1, (int)Math.Round(freq / engine.MaxFreq * (B - 1))));
                }
            }
            else
            {
                double bpp = (double)(B - 1) / (H - 1);
                for (int py = 0; py < H; py++) r[py] = Math.Max(0, Math.Min(B - 1, (int)Math.Round(bpp * (H - 1 - py))));
            }
            return r;
        }

        byte[] CalcLut()
        {
            byte[] lut = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                double t = Math.Pow(i / 255.0, gammaVal) * brightness;
                lut[i] = (byte)Math.Max(0, Math.Min(255, (int)(Math.Max(0, Math.Min(1, t)) * 255)));
            }
            return lut;
        }

        int[] CalcCmapArgb(Color[] cmap)
        {
            int[] a = new int[256];
            for (int i = 0; i < 256; i++) a[i] = cmap[i].B | (cmap[i].G << 8) | (cmap[i].R << 16) | (255 << 24);
            return a;
        }
    }

    // ── 图片转音频引擎 ──────────────────────────
    class ImageEngine
    {
        public int Frames, Bins;
        public float[] Data; // dB 值
        public float MaxPower;
        public int MaxFreq = 22050;
        public double Duration = 10;

        public void Load(string path, int maxFreq)
        {
            using (var bmp = new Bitmap(path))
            {
                Frames = bmp.Width;
                Bins = bmp.Height;
                MaxFreq = maxFreq;
                Data = new float[Frames * Bins];
                float maxP = float.MinValue;

                // 统一转为 32bppArgb，确保 stride = width * 4
                using (var bmp32 = new Bitmap(Frames, Bins, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(bmp32))
                        g.DrawImage(bmp, 0, 0, Frames, Bins);

                    Rectangle rect = new Rectangle(0, 0, Frames, Bins);
                    BitmapData bd = bmp32.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    int[] pixels = new int[Frames * Bins];
                    Marshal.Copy(bd.Scan0, pixels, 0, pixels.Length);
                    bmp32.UnlockBits(bd);

                    for (int y = 0; y < Bins; y++)
                    {
                        int rowOff = y * Frames;
                        int dstRow = Bins - 1 - y;
                        for (int x = 0; x < Frames; x++)
                        {
                            int argb = pixels[rowOff + x];
                            int r = (argb >> 16) & 0xFF;
                            int g = (argb >> 8) & 0xFF;
                            int b = argb & 0xFF;
                            float lum = (r + g + b) / 3f;
                            float dB = (lum / 255f) * 120f - 60f;
                            Data[x * Bins + dstRow] = dB;
                            if (dB > maxP) maxP = dB;
                        }
                    }
                }
                MaxPower = maxP;
                Duration = (double)Frames / (MaxFreq / (double)Bins) * 2;
            }
        }

        // 用当前参数重新映射亮度
        public void Remap(double dynRange)
        {
            if (Data == null) return;
            float floor = MaxPower - (float)dynRange;
            for (int i = 0; i < Data.Length; i++)
                Data[i] = Math.Max(floor, Math.Min(MaxPower, Data[i]));
        }
    }

    // ── WAV 写入 ────────────────────────────────
    static class WavWriter
    {
        public static void Write(string path, float[] samples, int sampleRate, int channels = 1)
        {
            int bits = 16;
            int dataLen = samples.Length * (bits / 8);
            using (var fs = new FileStream(path, FileMode.Create))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write(new char[] { 'R', 'I', 'F', 'F' });
                bw.Write(36 + dataLen);
                bw.Write(new char[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' });
                bw.Write(16);
                bw.Write((short)1);
                bw.Write((short)channels);
                bw.Write(sampleRate);
                bw.Write(sampleRate * channels * (bits / 8));
                bw.Write((short)(channels * (bits / 8)));
                bw.Write((short)bits);
                bw.Write(new char[] { 'd', 'a', 't', 'a' });
                bw.Write(dataLen);
                for (int i = 0; i < samples.Length; i++)
                {
                    short s = (short)Math.Max(-32768, Math.Min(32767, (int)(samples[i] * 32767)));
                    bw.Write(s);
                }
            }
        }
    }

    // ── 启动 ────────────────────────────────────
    static class Program
    {
        [STAThread]
        static void Main()
        {
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                File.WriteAllText(@"D:\音频频谱图工具\crash.log",
                    DateTime.Now + "\r\n" + ex.ToString());
                MessageBox.Show(ex.ToString(), "\u9519\u8BEF");
            }
        }
    }
}
