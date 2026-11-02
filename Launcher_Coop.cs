using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace TagForce3Coop
{
    // ========================================================================
    // JANELA DE JOGO AO VIVO DO PARCEIRO (ESPELHAMENTO EM TEMPO REAL + INPUT)
    // ========================================================================
    public class LiveCoopViewForm : Form
    {
        private PictureBox pbScreen;
        private Label lblOverlay;
        private LauncherForm parentLauncher;
        private readonly object imgLock = new object();

        public LiveCoopViewForm(LauncherForm parent)
        {
            this.parentLauncher = parent;
            this.Text = "Yu-Gi-Oh! GX Tag Force 3 - Campanha Co-op Ao Vivo (Tela do Host em Tempo Real)";
            this.ClientSize = new Size(960, 580);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.FromArgb(10, 12, 16);
            this.ForeColor = Color.White;
            this.DoubleBuffered = true;
            this.KeyPreview = true;

            Panel pnlTop = new Panel
            {
                Dock = DockStyle.Top,
                Height = 36,
                BackColor = Color.FromArgb(20, 24, 32)
            };

            lblOverlay = new Label
            {
                Text = "AO VIVO: Conectado na tela do Host! | Controles no Duelo: Setas, Z (Confirmar), X (Cancelar), A, S, Q, W, Espaco ou Controle USB",
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                ForeColor = Color.FromArgb(74, 222, 128),
                Location = new Point(12, 8),
                AutoSize = true
            };

            Button btnSyncDeck = new Button
            {
                Text = "Atualizar Meu Deck",
                Size = new Size(135, 24),
                Location = new Point(815, 6),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                BackColor = Color.FromArgb(245, 158, 11),
                ForeColor = Color.Black,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8, FontStyle.Bold),
                Cursor = Cursors.Hand,
                TabStop = false
            };
            btnSyncDeck.FlatAppearance.BorderSize = 0;
            btnSyncDeck.Click += (s, e) =>
            {
                parentLauncher.TriggerSendDeckFromLiveWindow();
                this.Focus();
            };

            pnlTop.Controls.Add(lblOverlay);
            pnlTop.Controls.Add(btnSyncDeck);

            pbScreen = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Black
            };
            pbScreen.Click += (s, e) => this.Focus();

            this.Controls.Add(pbScreen);
            this.Controls.Add(pnlTop);
        }

        public void UpdateFrame(byte[] jpegBytes)
        {
            if (this.IsDisposed || !this.IsHandleCreated) return;
            try
            {
                Bitmap bmp;
                using (MemoryStream ms = new MemoryStream(jpegBytes))
                {
                    bmp = new Bitmap(Image.FromStream(ms));
                }
                this.BeginInvoke(new Action(() =>
                {
                    if (this.IsDisposed)
                    {
                        bmp.Dispose();
                        return;
                    }
                    lock (imgLock)
                    {
                        Image old = pbScreen.Image;
                        pbScreen.Image = bmp;
                        if (old != null) old.Dispose();
                    }
                }));
            }
            catch { }
        }
    }

    // ========================================================================
    // LAUNCHER PRINCIPAL CO-OP v1.3.0
    // ========================================================================
    public class LauncherForm : Form
    {
        private const int COOP_PORT = 27315;
        private const int WS_PORT = 45450;
        private const string DEFAULT_RADMIN_IP = "26.196.166.86";
        private const string GITHUB_REPO = "Jugo26/YuGiOh-GX-TagForce3-Coop";
        private const string GITHUB_BRANCH = "main";

        private const int DATA0_SLOT0_OFFSET = 0x48D4;
        private const int DATA0_PLAYER_DECK_OFFSET = 0x4A40;
        private const int DATA0_PARTNER_DECK_START = 0x4BAC;
        private const int DATA0_DECK_SIZE = 0x16C; // 364 bytes (stride entre slots)
        private const int DATA0_DECK_PAYLOAD_SIZE = 0x160; // 352 bytes (64 Nome + 192 Main/Counts + 96 Side/Extra)
        private const int DATA0_PARTNER_COUNT = 125; // Slots 2..126 em 0x4BAC..0xFBFC
        private const int DATA0_PLAYER_NAME_OFFSET = 0xFD5C;
        private const uint PSP_PARTNER_AI_FLAG = 0x09E02998;
        private const uint PSP_DECK_SLOT0_ADDR = 0x08AAA078;
        private const uint PSP_DECK_SLOT1_PLAYER_ADDR = 0x08AAA1E4;
        private const uint PSP_DECK_SLOT2_PARTNER_ADDR = 0x08AAA350;
        private const uint PSP_DECK_SLOT3_PARTNER_ADDR = 0x08AAA4BC;
        private const uint PSP_PLAYER_NAME_ADDR = 0x08AB5500;
        private const uint PSP_DUEL_PARTNER_NAME_ADDR = 0x08AC1AB6;
        private const uint PSP_DUEL_PARTNER_MAIN_ADDR = 0x08AC1AF8;
        private const uint PSP_DUEL_PARTNER_EXTRA_ADDR = 0x08AC1BB8;

        // Win32 Menu Commands nativos do PPSSPP (Menu ID 101)
        private const uint WM_COMMAND = 0x0111;
        private const int ID_FILE_QUICKSAVESTATE = 40034;
        private const int ID_FILE_QUICKLOADSTATE = 40035;

        // Win32 APIs
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X, Y;
        }

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint uCode, uint uMapType);

        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        // XInput (Suporte a Controle Xbox/PS4/USB para o Amigo)
        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_GAMEPAD
        {
            public ushort wButtons;
            public byte bLeftTrigger;
            public byte bRightTrigger;
            public short sThumbLX;
            public short sThumbLY;
            public short sThumbRX;
            public short sThumbRY;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_STATE
        {
            public uint dwPacketNumber;
            public XINPUT_GAMEPAD Gamepad;
        }

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        private static extern uint XInputGetState14(uint dwUserIndex, ref XINPUT_STATE pState);

        [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
        private static extern uint XInputGetState910(uint dwUserIndex, ref XINPUT_STATE pState);

        private const int VK_F5 = 0x74;
        private const int VK_F6 = 0x75;
        private const int VK_F7 = 0x76;

        private static readonly Dictionary<string, int> VK_MAP = new Dictionary<string, int>
        {
            { "up", 0x26 },
            { "down", 0x28 },
            { "left", 0x25 },
            { "right", 0x27 },
            { "cross", 0x5A },    // Z
            { "circle", 0x58 },   // X
            { "square", 0x41 },   // A
            { "triangle", 0x53 }, // S
            { "ltrigger", 0x51 }, // Q
            { "rtrigger", 0x57 }, // W
            { "start", 0x20 },    // Espaco
            { "select", 0x56 }    // V
        };

        private string baseDir;
        private string gameCsoPath;
        private string bundledPpssppPath;
        private string systemPpssppPath = @"C:\Program Files\PPSSPP\PPSSPPWindows64.exe";
        private string ppssppDocsDir;

        // UI Controls
        private Label lblVersion;
        private TextBox txtIp;
        private TextBox txtName;
        private Label lblDeckInfo;
        private Button btnHost;
        private Button btnPartner;
        private Label lblNetStatus;
        private TextBox txtLog;
        private LiveCoopViewForm liveViewForm;

        // Estado Co-op
        private volatile bool running = false;
        private volatile bool sendingSavestate = false;
        private string role = ""; // "host" ou "partner"
        private string activeTurn = "host";
        private TcpListener serverListener;
        private TcpClient peerClient;
        private NetworkStream peerStream;
        private readonly object sendLock = new object();
        private Socket wsSocket;
        private readonly object wsLock = new object();
        private const string BINARY_VERSION = "1.5.0";
        private string localVersion = BINARY_VERSION;
        private Dictionary<string, bool> hostLastPartnerBtns = new Dictionary<string, bool>();
        private byte[] lastPartnerDeckBytes = null;
        private string lastSentDeckHash = "";

        public LauncherForm()
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // TLS 1.2
            baseDir = AppDomain.CurrentDomain.BaseDirectory;
            gameCsoPath = Path.Combine(baseDir, "Yu-Gi-Oh! GX Tag Force 3 PTBR.cso");
            bundledPpssppPath = Path.Combine(baseDir, "PPSSPP", "PPSSPPWindows64.exe");
            ppssppDocsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PPSSPP", "PSP");

            CleanupOldExe();
            localVersion = BINARY_VERSION;
            LoadCachedPartnerDeck();
            ConfigurePpssppForCoop(true, DEFAULT_RADMIN_IP);
            InitUI();

            ThreadPool.QueueUserWorkItem(_ => CheckGitHubUpdates(false));
        }

        private void LoadCachedPartnerDeck()
        {
            try
            {
                string cachePath = Path.Combine(baseDir, "PARTNER_DECK_CACHE.bin");
                if (File.Exists(cachePath))
                {
                    byte[] raw = File.ReadAllBytes(cachePath);
                    if (IsValidDeckBuffer(raw))
                    {
                        lastPartnerDeckBytes = raw;
                    }
                }
            }
            catch { }
        }

        private void CleanupOldExe()
        {
            try
            {
                string oldExe = Path.Combine(baseDir, "Launcher_Coop.exe.old");
                if (File.Exists(oldExe)) File.Delete(oldExe);
            }
            catch { }
        }

        private void ReadLocalVersion()
        {
            try
            {
                string vPath = Path.Combine(baseDir, "VERSION.json");
                if (File.Exists(vPath))
                {
                    string json = File.ReadAllText(vPath, Encoding.UTF8);
                    Match m = Regex.Match(json, "\"version\"\\s*:\\s*\"([^\"]+)\"");
                    if (m.Success) localVersion = m.Groups[1].Value;
                }
            }
            catch { }
        }

        private void InitUI()
        {
            this.Text = "Yu-Gi-Oh! GX Tag Force 3 - Launcher Co-op v" + localVersion;
            this.ClientSize = new Size(640, 680);
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.FromArgb(17, 19, 24);
            this.ForeColor = Color.White;

            string iconPath = Path.Combine(baseDir, "game_icon.ico");
            if (File.Exists(iconPath))
            {
                try { this.Icon = new Icon(iconPath); } catch { }
            }

            // Header
            Panel pnlHeader = new Panel { Dock = DockStyle.Top, Height = 68, BackColor = Color.FromArgb(24, 27, 34) };
            Label lblTitle = new Label
            {
                Text = "YU-GI-OH! GX TAG FORCE 3 [PT-BR]",
                Font = new Font("Segoe UI", 15, FontStyle.Bold),
                ForeColor = Color.FromArgb(245, 158, 11),
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Dock = DockStyle.Top,
                Height = 38
            };
            lblVersion = new Label
            {
                Text = "Campanha Co-op Ao Vivo (Transmissao de Tela em Tempo Real + Controle Remoto) | v" + localVersion,
                Font = new Font("Segoe UI", 9, FontStyle.Regular),
                ForeColor = Color.FromArgb(56, 189, 248),
                AutoSize = false,
                TextAlign = ContentAlignment.TopCenter,
                Dock = DockStyle.Fill
            };
            pnlHeader.Controls.Add(lblVersion);
            pnlHeader.Controls.Add(lblTitle);
            this.Controls.Add(pnlHeader);

            // 1. Painel Radmin e Deck
            GroupBox grpCfg = new GroupBox
            {
                Text = " 1. Conexao Radmin VPN & Seu Deck Proprio ",
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                ForeColor = Color.FromArgb(56, 189, 248),
                BackColor = Color.FromArgb(24, 27, 34),
                Location = new Point(20, 78),
                Size = new Size(600, 95)
            };

            Label lblIp = new Label
            {
                Text = "IP do Host no Radmin VPN:",
                Font = new Font("Segoe UI", 9),
                ForeColor = Color.FromArgb(226, 232, 240),
                Location = new Point(14, 28),
                AutoSize = true
            };
            txtIp = new TextBox
            {
                Text = DEFAULT_RADMIN_IP,
                Font = new Font("Consolas", 10, FontStyle.Bold),
                BackColor = Color.FromArgb(15, 17, 21),
                ForeColor = Color.FromArgb(56, 189, 248),
                BorderStyle = BorderStyle.FixedSingle,
                Location = new Point(180, 25),
                Size = new Size(145, 24)
            };

            Button btnCopyIp = CreateButton("Copiar IP", new Point(335, 24), new Size(95, 26), Color.FromArgb(38, 43, 54), Color.FromArgb(56, 189, 248));
            btnCopyIp.Click += (s, e) =>
            {
                Clipboard.SetText(txtIp.Text.Trim());
                AppendLog("[Sistema] IP " + txtIp.Text.Trim() + " copiado!");
            };

            Button btnCheckGit = CreateButton("Verificar Att (Git)", new Point(440, 24), new Size(145, 26), Color.FromArgb(30, 41, 59), Color.FromArgb(74, 222, 128));
            btnCheckGit.Click += (s, e) => ThreadPool.QueueUserWorkItem(_ => CheckGitHubUpdates(true));

            Label lblName = new Label
            {
                Text = "Seu Nome / Apelido:",
                Font = new Font("Segoe UI", 9),
                ForeColor = Color.FromArgb(226, 232, 240),
                Location = new Point(14, 60),
                AutoSize = true
            };

            string pName, dName;
            int cCount;
            byte[] dBytes = ReadLocalPlayerDeck(out pName, out dName, out cCount);

            txtName = new TextBox
            {
                Text = string.IsNullOrEmpty(pName) ? "Jogador" : pName,
                Font = new Font("Segoe UI", 9),
                BackColor = Color.FromArgb(15, 17, 21),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                Location = new Point(180, 58),
                Size = new Size(145, 24)
            };

            lblDeckInfo = new Label
            {
                Text = dBytes != null ? string.Format("Deck no Save: {0} ({1} cartas)", dName, cCount) : "Deck no Save: Padrao",
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(34, 197, 94),
                Location = new Point(335, 61),
                Size = new Size(255, 20),
                TextAlign = ContentAlignment.MiddleRight
            };

            grpCfg.Controls.AddRange(new Control[] { lblIp, txtIp, btnCopyIp, btnCheckGit, lblName, txtName, lblDeckInfo });
            this.Controls.Add(grpCfg);

            // 2. Painel Modos Host / Parceiro
            GroupBox grpModes = new GroupBox
            {
                Text = " 2. Iniciar Campanha Co-op (Tela Ao Vivo + Deck do Amigo) ",
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                ForeColor = Color.FromArgb(245, 158, 11),
                BackColor = Color.FromArgb(24, 27, 34),
                Location = new Point(20, 180),
                Size = new Size(600, 155)
            };

            btnHost = CreateButton("CRIAR SALA CO-OP E ABRIR JOGO (SOU O HOST)", new Point(16, 24), new Size(568, 40), Color.FromArgb(185, 28, 28), Color.White);
            btnHost.Font = new Font("Segoe UI", 10.5f, FontStyle.Bold);
            btnHost.Click += (s, e) => StartAsHost();

            btnPartner = CreateButton("ENTRAR NA CAMPANHA DO HOST AO VIVO (SOU O AMIGO / PARCEIRO)", new Point(16, 70), new Size(568, 40), Color.FromArgb(2, 132, 199), Color.White);
            btnPartner.Font = new Font("Segoe UI", 10.5f, FontStyle.Bold);
            btnPartner.Click += (s, e) => StartAsPartner();

            Button btnOpenLiveView = CreateButton("Abrir Tela Ao Vivo do Host", new Point(16, 116), new Size(278, 30), Color.FromArgb(20, 83, 45), Color.FromArgb(74, 222, 128));
            btnOpenLiveView.Click += (s, e) => ShowLiveViewWindow();

            Button btnOpenLocalPpsspp = CreateButton("Abrir Meu PPSSPP (Editar Meu Deck / Save)", new Point(306, 116), new Size(278, 30), Color.FromArgb(38, 43, 54), Color.FromArgb(245, 158, 11));
            btnOpenLocalPpsspp.Click += (s, e) => LaunchPpsspp();

            grpModes.Controls.AddRange(new Control[] { btnHost, btnPartner, btnOpenLiveView, btnOpenLocalPpsspp });
            this.Controls.Add(grpModes);

            // 3. Painel Sincronizacao
            GroupBox grpSync = new GroupBox
            {
                Text = " 3. Sincronizacao de Deck e Estado (Atalhos Opcionais: F5, F6, F7) ",
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                ForeColor = Color.FromArgb(34, 197, 94),
                BackColor = Color.FromArgb(24, 27, 34),
                Location = new Point(20, 342),
                Size = new Size(600, 125)
            };

            lblNetStatus = new Label
            {
                Text = "Status: Pronto para iniciar (Escolha Host ou Parceiro acima)",
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                ForeColor = Color.FromArgb(234, 179, 8),
                Location = new Point(16, 22),
                Size = new Size(568, 20)
            };

            Button btnF5 = CreateButton("[F5] Sincronizar PPSSPP", new Point(16, 48), new Size(184, 30), Color.FromArgb(38, 43, 54), Color.White);
            btnF5.Click += (s, e) => ThreadPool.QueueUserWorkItem(_ =>
            {
                if (role == "partner") SendFrame(4, Encoding.UTF8.GetBytes("host"));
                else CaptureAndSendSavestate("host");
            });

            Button btnF6 = CreateButton("[F6] Vez do Amigo", new Point(208, 48), new Size(184, 30), Color.FromArgb(30, 58, 138), Color.FromArgb(56, 189, 248));
            btnF6.Click += (s, e) => ThreadPool.QueueUserWorkItem(_ =>
            {
                activeTurn = "partner";
                SetStatus("Vez Ativa: PARCEIRO (Amigo controlando o jogo)");
                AppendLog("[Turno] Controle liberado para o Amigo!");
                if (role == "host" && FindPpssppWindow() != IntPtr.Zero)
                {
                    SendWebSocketJson("{\"event\":\"memory.write_u32\",\"address\":" + PSP_PARTNER_AI_FLAG + ",\"value\":0}");
                }
            });

            Button btnF7 = CreateButton("[F7] Vez do Host", new Point(400, 48), new Size(184, 30), Color.FromArgb(20, 83, 45), Color.FromArgb(74, 222, 128));
            btnF7.Click += (s, e) => ThreadPool.QueueUserWorkItem(_ =>
            {
                activeTurn = "host";
                SetStatus("Vez Ativa: HOST");
                AppendLog("[Turno] Controle principal com o Host.");
            });

            Button btnSendDeck = CreateButton("Enviar / Atualizar Meu Deck Proprio no Parceiro da Campanha", new Point(16, 84), new Size(410, 30), Color.FromArgb(38, 43, 54), Color.FromArgb(245, 158, 11));
            btnSendDeck.Click += (s, e) => ThreadPool.QueueUserWorkItem(_ => SendOrApplyMyDeck());

            Button btnCheats = CreateButton("Cheats", new Point(434, 84), new Size(72, 30), Color.FromArgb(38, 43, 54), Color.LightGray);
            btnCheats.Click += (s, e) =>
            {
                string ch = Path.Combine(ppssppDocsDir, "Cheats", "ULES01183.ini");
                if (File.Exists(ch)) Process.Start("notepad.exe", ch);
            };

            Button btnGuide = CreateButton("Guia", new Point(512, 84), new Size(72, 30), Color.FromArgb(38, 43, 54), Color.LightGray);
            btnGuide.Click += (s, e) =>
            {
                string gd = Path.Combine(baseDir, "COMO_JOGAR_COOP.txt");
                if (File.Exists(gd)) Process.Start("notepad.exe", gd);
            };

            grpSync.Controls.AddRange(new Control[] { lblNetStatus, btnF5, btnF6, btnF7, btnSendDeck, btnCheats, btnGuide });
            this.Controls.Add(grpSync);

            // 4. Log
            GroupBox grpLog = new GroupBox
            {
                Text = " Registro de Conexao, Tela Ao Vivo e Auto-Updater Git ",
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(148, 163, 184),
                BackColor = Color.FromArgb(24, 27, 34),
                Location = new Point(20, 474),
                Size = new Size(600, 190)
            };

            txtLog = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.FromArgb(11, 13, 17),
                ForeColor = Color.FromArgb(167, 243, 208),
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 8.5f),
                Location = new Point(12, 20),
                Size = new Size(576, 158)
            };
            grpLog.Controls.Add(txtLog);
            this.Controls.Add(grpLog);

            AppendLog("[Sistema] Launcher Co-op v" + localVersion + " pronto! (Transmissao Ao Vivo Ativa)");
            AppendLog("[Dica] Tudo o que aparece na tela do Host agora vai em tempo real para o Amigo!");
        }

        private Button CreateButton(string text, Point loc, Size sz, Color bg, Color fg)
        {
            Button b = new Button
            {
                Text = text,
                Location = loc,
                Size = sz,
                BackColor = bg,
                ForeColor = fg,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderSize = 0;
            return b;
        }

        private void AppendLog(string msg)
        {
            if (this.IsDisposed) return;
            if (this.InvokeRequired)
            {
                try { this.BeginInvoke(new Action<string>(AppendLog), msg); } catch { }
                return;
            }
            txtLog.AppendText(msg + Environment.NewLine);
        }

        private void SetStatus(string msg)
        {
            if (this.IsDisposed) return;
            if (this.InvokeRequired)
            {
                try { this.BeginInvoke(new Action<string>(SetStatus), msg); } catch { }
                return;
            }
            lblNetStatus.Text = "Status: " + msg;
        }

        public void ShowLiveViewWindow()
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(ShowLiveViewWindow));
                return;
            }
            if (liveViewForm == null || liveViewForm.IsDisposed)
            {
                liveViewForm = new LiveCoopViewForm(this);
                liveViewForm.Icon = this.Icon;
                liveViewForm.Show();
            }
            else
            {
                if (!liveViewForm.Visible) liveViewForm.Show();
                liveViewForm.BringToFront();
            }
        }

        public void TriggerSendDeckFromLiveWindow()
        {
            ThreadPool.QueueUserWorkItem(_ => SendOrApplyMyDeck());
        }

        // ====================================================================
        // CONFIGURACAO AUTOMATICA DO PPSSPP E SAVE (DATA0)
        // ====================================================================
        private void CopyDirRecursive(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (string f in Directory.GetFiles(src))
            {
                string destFile = Path.Combine(dst, Path.GetFileName(f));
                if (!File.Exists(destFile)) File.Copy(f, destFile, false);
            }
            foreach (string d in Directory.GetDirectories(src))
            {
                CopyDirRecursive(d, Path.Combine(dst, Path.GetFileName(d)));
            }
        }

        private bool IsValidDeckBuffer(byte[] deckBytes)
        {
            if (deckBytes == null || deckBytes.Length < DATA0_DECK_PAYLOAD_SIZE) return false;
            int mainCount = BitConverter.ToInt32(deckBytes, 64);
            int sideCount = BitConverter.ToInt32(deckBytes, 68);
            int extraCount = BitConverter.ToInt32(deckBytes, 72);
            if (mainCount < 40 || mainCount > 80) return false;
            if (sideCount < 0 || sideCount > 15) return false;
            if (extraCount < 0 || extraCount > 30) return false;
            ushort firstCard = BitConverter.ToUInt16(deckBytes, 76);
            if (firstCard < 3000 || firstCard > 9500) return false;
            return true;
        }

        private bool IsCleanPlayerName(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char ch in s)
            {
                if (ch < 0x20 || ch > 0x024F) return false;
            }
            return true;
        }

        private void ConfigurePpssppForCoop(bool isHost, string hostIp)
        {
            try
            {
                string sysDir = Path.Combine(ppssppDocsDir, "SYSTEM");
                string cheatsDir = Path.Combine(ppssppDocsDir, "Cheats");
                string stateDir = Path.Combine(ppssppDocsDir, "PPSSPP_STATE");
                string saveDir = Path.Combine(ppssppDocsDir, "SAVEDATA", "ULES011830001");

                Directory.CreateDirectory(sysDir);
                Directory.CreateDirectory(cheatsDir);
                Directory.CreateDirectory(stateDir);
                Directory.CreateDirectory(saveDir);

                string saveBase = Path.Combine(baseDir, "SAVEDATA_BASE", "ULES011830001");
                string data0Path = Path.Combine(saveDir, "DATA0");
                if (!File.Exists(data0Path) && Directory.Exists(saveBase))
                {
                    CopyDirRecursive(saveBase, saveDir);
                }

                string sysBase = Path.Combine(baseDir, "SYSTEM_BASE");
                string ppssppIni = Path.Combine(sysDir, "ppsspp.ini");
                string controlsIni = Path.Combine(sysDir, "controls.ini");

                if (!File.Exists(ppssppIni) && File.Exists(Path.Combine(sysBase, "ppsspp.ini")))
                    File.Copy(Path.Combine(sysBase, "ppsspp.ini"), ppssppIni, true);
                if (!File.Exists(controlsIni) && File.Exists(Path.Combine(sysBase, "controls.ini")))
                    File.Copy(Path.Combine(sysBase, "controls.ini"), controlsIni, true);

                if (File.Exists(ppssppIni))
                {
                    string c = File.ReadAllText(ppssppIni, Encoding.UTF8);
                    c = c.Replace("EncryptSave = True", "EncryptSave = False")
                         .Replace("EnableCheats = False", "EnableCheats = True")
                         .Replace("RemoteISOManualConfig = False", "RemoteISOManualConfig = True")
                         .Replace("RemoteDebuggerOnStartup = False", "RemoteDebuggerOnStartup = True")
                         .Replace("RemoteDebuggerLocal = False", "RemoteDebuggerLocal = True")
                         .Replace("PauseOnLostFocus = True", "PauseOnLostFocus = False")
                         .Replace("EnableWlan = False", "EnableWlan = True");

                    if (!isHost && c.Contains("58:87:b8:25:4f:2d"))
                    {
                        Random rnd = new Random();
                        string mac = string.Format("{0:x2}:{1:x2}:{2:x2}:{3:x2}:{4:x2}:{5:x2}",
                            rnd.Next(256), rnd.Next(256), rnd.Next(256), rnd.Next(256), rnd.Next(256), rnd.Next(256));
                        c = c.Replace("58:87:b8:25:4f:2d", mac);
                    }

                    c = Regex.Replace(c, @"EncryptSave\s*=\s*\w+", "EncryptSave = False");
                    c = Regex.Replace(c, @"EnableAdhocServer\s*=\s*\w+", "EnableAdhocServer = " + (isHost ? "True" : "False"));
                    c = Regex.Replace(c, @"proAdhocServer\s*=\s*[^\r\n]+", "proAdhocServer = " + hostIp);
                    c = Regex.Replace(c, @"RemoteISOPort\s*=\s*\d+", "RemoteISOPort = " + WS_PORT);
                    c = Regex.Replace(c, @"StateSlot\s*=\s*\d+", "StateSlot = 0");
                    File.WriteAllText(ppssppIni, c, Encoding.UTF8);
                }

                string cheatFile = Path.Combine(cheatsDir, "ULES01183.ini");
                string localCheat = Path.Combine(baseDir, "ULES01183.ini");
                if (File.Exists(localCheat))
                {
                    File.Copy(localCheat, cheatFile, true);
                }

                // Verifica e repara DATA0 caso tenha sido criptografado ou corrompido anteriormente
                if (File.Exists(data0Path))
                {
                    byte[] save = File.ReadAllBytes(data0Path);
                    if (save.Length >= 0x10000)
                    {
                        byte[] slot1 = new byte[DATA0_DECK_SIZE];
                        Buffer.BlockCopy(save, DATA0_PLAYER_DECK_OFFSET, slot1, 0, DATA0_DECK_SIZE);
                        if (!IsValidDeckBuffer(slot1))
                        {
                            string baseData0 = Path.Combine(saveBase, "DATA0");
                            if (File.Exists(baseData0))
                            {
                                try { File.Copy(data0Path, data0Path + ".encrypted_bak", true); } catch { }
                                File.Copy(baseData0, data0Path, true);
                                save = File.ReadAllBytes(data0Path);
                                Buffer.BlockCopy(save, DATA0_PLAYER_DECK_OFFSET, slot1, 0, DATA0_DECK_SIZE);
                            }
                        }

                        bool modified = false;
                        string curPName = Encoding.Unicode.GetString(save, DATA0_PLAYER_NAME_OFFSET, 32).Split('\0')[0].Trim();
                        if (!IsCleanPlayerName(curPName))
                        {
                            byte[] nameBytes = Encoding.Unicode.GetBytes("lbn");
                            Array.Clear(save, DATA0_PLAYER_NAME_OFFSET, 50);
                            Buffer.BlockCopy(nameBytes, 0, save, DATA0_PLAYER_NAME_OFFSET, nameBytes.Length);
                            modified = true;
                        }

                        byte[] slot2 = new byte[DATA0_DECK_SIZE];
                        Buffer.BlockCopy(save, DATA0_PARTNER_DECK_START, slot2, 0, DATA0_DECK_SIZE);
                        byte[] fillDeck = IsValidDeckBuffer(lastPartnerDeckBytes) ? lastPartnerDeckBytes : (!IsValidDeckBuffer(slot2) ? slot1 : null);
                        if (fillDeck != null && IsValidDeckBuffer(fillDeck))
                        {
                            Buffer.BlockCopy(fillDeck, 0, save, DATA0_SLOT0_OFFSET, DATA0_DECK_PAYLOAD_SIZE);
                            for (int i = 0; i < DATA0_PARTNER_COUNT; i++)
                            {
                                int off = DATA0_PARTNER_DECK_START + (i * DATA0_DECK_SIZE);
                                if (off + DATA0_DECK_PAYLOAD_SIZE <= DATA0_PLAYER_NAME_OFFSET)
                                {
                                    Buffer.BlockCopy(fillDeck, 0, save, off, DATA0_DECK_PAYLOAD_SIZE);
                                }
                            }
                            modified = true;
                        }

                        if (modified)
                        {
                            File.WriteAllBytes(data0Path, save);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppendLog("[Aviso Config] " + ex.Message);
            }
        }

        private byte[] ReadRamBytesViaWebSocket(uint address, int size)
        {
            if (FindPpssppWindow() == IntPtr.Zero) return null;
            try
            {
                using (Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                {
                    s.ReceiveTimeout = 450;
                    s.SendTimeout = 450;
                    IAsyncResult ar = s.BeginConnect("127.0.0.1", WS_PORT, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(300)) return null;
                    s.EndConnect(ar);

                    string req = "GET /debugger HTTP/1.1\r\n" +
                                 "Host: 127.0.0.1:" + WS_PORT + "\r\n" +
                                 "Upgrade: websocket\r\n" +
                                 "Connection: Upgrade\r\n" +
                                 "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n" +
                                 "Sec-WebSocket-Version: 13\r\n\r\n";
                    s.Send(Encoding.ASCII.GetBytes(req));
                    byte[] buf = new byte[4096];
                    int r = s.Receive(buf);
                    if (r <= 0 || !Encoding.ASCII.GetString(buf, 0, r).Contains("101")) return null;

                    string json = "{\"event\":\"memory.read\",\"address\":" + address + ",\"size\":" + size + "}";
                    byte[] raw = Encoding.UTF8.GetBytes(json);
                    byte[] mask = new byte[] { 0x12, 0x34, 0x56, 0x78 };
                    List<byte> frame = new List<byte> { 0x81, (byte)(0x80 | raw.Length) };
                    frame.AddRange(mask);
                    for (int i = 0; i < raw.Length; i++) frame.Add((byte)(raw[i] ^ mask[i & 3]));
                    s.Send(frame.ToArray());

                    StringBuilder sb = new StringBuilder();
                    DateTime deadline = DateTime.UtcNow.AddMilliseconds(400);
                    while (DateTime.UtcNow < deadline)
                    {
                        int n = s.Receive(buf);
                        if (n <= 0) break;
                        sb.Append(Encoding.ASCII.GetString(buf, 0, n));
                        Match m = Regex.Match(sb.ToString(), "\"base64\"\\s*:\\s*\"([A-Za-z0-9+/=]+)\"");
                        if (m.Success)
                        {
                            return Convert.FromBase64String(m.Groups[1].Value);
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private void WriteRamBytesViaWebSocket(uint address, byte[] data, int offset, int count)
        {
            if (data == null || offset + count > data.Length) return;
            byte[] slice = new byte[count];
            Buffer.BlockCopy(data, offset, slice, 0, count);
            string b64 = Convert.ToBase64String(slice);
            SendWebSocketJson("{\"event\":\"memory.write\",\"address\":" + address + ",\"base64\":\"" + b64 + "\"}");
        }

        private void ApplyPartnerDeckToHostRam(byte[] deckBytes)
        {
            if (FindPpssppWindow() == IntPtr.Zero) return;
            try
            {
                // 1. Garante Controle Manual do Parceiro (Co-op) e Hooks MIPS para sempre usar o Deck do Amigo (Slot 2) em qualquer Duelo de Dupla!
                SendWebSocketJson("{\"event\":\"memory.write_u32\",\"address\":" + PSP_PARTNER_AI_FLAG + ",\"value\":0}");
                SendWebSocketJson("{\"event\":\"memory.write_u32\",\"address\":142815208,\"value\":604110848}");  // 0x08832FE8: li $v0, 0
                SendWebSocketJson("{\"event\":\"memory.write_u32\",\"address\":142828624,\"value\":1384120390}"); // 0x08836450: beql $s4, $zero, 0x0883656c
                SendWebSocketJson("{\"event\":\"memory.write_u32\",\"address\":142828628,\"value\":604241922}");  // 0x08836454: li $a0, 2
                SendWebSocketJson("{\"event\":\"memory.write_u32\",\"address\":142828916,\"value\":604241922}");  // 0x08836574: li $a0, 2
                SendWebSocketJson("{\"event\":\"memory.write_u32\",\"address\":142828928,\"value\":604241922}");  // 0x08836580: li $a0, 2

                // 2. Restaura o Nome do Host em RAM caso um Savestate antigo estivesse carregado
                string hostName = (txtName != null && !string.IsNullOrEmpty(txtName.Text.Trim())) ? txtName.Text.Trim() : "lbn";
                byte[] hostNameBuf = new byte[50];
                byte[] hostNameRaw = Encoding.Unicode.GetBytes(hostName);
                Buffer.BlockCopy(hostNameRaw, 0, hostNameBuf, 0, Math.Min(48, hostNameRaw.Length));
                WriteRamBytesViaWebSocket(PSP_PLAYER_NAME_ADDR, hostNameBuf, 0, 50);

                // 3. Injeta os 352 bytes do Deck do Amigo diretamente nos Slots de Parceiro na RAM do PPSSPP
                if (IsValidDeckBuffer(deckBytes))
                {
                    WriteRamBytesViaWebSocket(PSP_DECK_SLOT0_ADDR, deckBytes, 0, DATA0_DECK_PAYLOAD_SIZE);
                    WriteRamBytesViaWebSocket(PSP_DECK_SLOT2_PARTNER_ADDR, deckBytes, 0, DATA0_DECK_PAYLOAD_SIZE);
                    WriteRamBytesViaWebSocket(PSP_DECK_SLOT3_PARTNER_ADDR, deckBytes, 0, DATA0_DECK_PAYLOAD_SIZE);

                    // Tambem atualiza a tabela de Setup de Duelo em RAM para o Duelista 0 (Parceiro)
                    WriteRamBytesViaWebSocket(PSP_DUEL_PARTNER_NAME_ADDR, deckBytes, 0, 64);
                    WriteRamBytesViaWebSocket(PSP_DUEL_PARTNER_MAIN_ADDR, deckBytes, 64, 192);
                    WriteRamBytesViaWebSocket(PSP_DUEL_PARTNER_EXTRA_ADDR, deckBytes, 256, 96);
                }
            }
            catch { }
        }

        private byte[] ReadLocalPlayerDeck(out string playerName, out string deckName, out int cardCount)
        {
            playerName = "Parceiro";
            deckName = "Deck Customizado";
            cardCount = 0;
            try
            {
                string data0 = Path.Combine(ppssppDocsDir, "SAVEDATA", "ULES011830001", "DATA0");
                byte[] all = File.Exists(data0) ? File.ReadAllBytes(data0) : null;
                byte[] deckBytes = null;

                // 1. Tenta primeiro ler AO VIVO da RAM do PPSSPP local (0x08AAA1E4), onde o Deck esta sempre descriptografado e atualizado na hora!
                byte[] ramDeck = ReadRamBytesViaWebSocket(PSP_DECK_SLOT1_PLAYER_ADDR, DATA0_DECK_SIZE);
                if (IsValidDeckBuffer(ramDeck))
                {
                    deckBytes = ramDeck;
                    if (all != null && all.Length >= 0x10000)
                    {
                        Buffer.BlockCopy(deckBytes, 0, all, DATA0_PLAYER_DECK_OFFSET, DATA0_DECK_PAYLOAD_SIZE);
                        try { File.WriteAllBytes(data0, all); } catch { }
                    }
                }
                else if (all != null && all.Length >= 0x10000)
                {
                    deckBytes = new byte[DATA0_DECK_SIZE];
                    Buffer.BlockCopy(all, DATA0_PLAYER_DECK_OFFSET, deckBytes, 0, DATA0_DECK_SIZE);
                    if (!IsValidDeckBuffer(deckBytes))
                    {
                        // Save no disco estava criptografado pelo PPSSPP antigo; restaura base limpa
                        string baseData0 = Path.Combine(baseDir, "SAVEDATA_BASE", "ULES011830001", "DATA0");
                        if (File.Exists(baseData0))
                        {
                            try { File.Copy(data0, data0 + ".encrypted_bak", true); } catch { }
                            File.Copy(baseData0, data0, true);
                            all = File.ReadAllBytes(data0);
                            Buffer.BlockCopy(all, DATA0_PLAYER_DECK_OFFSET, deckBytes, 0, DATA0_DECK_SIZE);
                        }
                    }
                }

                if (!IsValidDeckBuffer(deckBytes)) return null;

                deckName = Encoding.Unicode.GetString(deckBytes, 0, 64).Split('\0')[0].Trim();
                if (all != null && all.Length >= 0x10000)
                {
                    string p = Encoding.Unicode.GetString(all, DATA0_PLAYER_NAME_OFFSET, 32).Split('\0')[0].Trim();
                    if (IsCleanPlayerName(p)) playerName = p;
                }

                for (int i = 76; i + 1 < DATA0_DECK_PAYLOAD_SIZE; i += 2)
                {
                    ushort cid = BitConverter.ToUInt16(deckBytes, i);
                    if (cid != 0) cardCount++;
                }
                return deckBytes;
            }
            catch
            {
                return null;
            }
        }

        private bool InjectPartnerDeckIntoHostSave(byte[] deckBytes, string friendName)
        {
            try
            {
                if (!IsValidDeckBuffer(deckBytes))
                {
                    AppendLog("[Aviso Deck] O pacote de deck recebido era invalido ou estava criptografado; ignorado para proteger o Save.");
                    return false;
                }

                lastPartnerDeckBytes = new byte[DATA0_DECK_SIZE];
                Buffer.BlockCopy(deckBytes, 0, lastPartnerDeckBytes, 0, Math.Min(deckBytes.Length, DATA0_DECK_SIZE));
                try
                {
                    File.WriteAllBytes(Path.Combine(baseDir, "PARTNER_DECK_CACHE.bin"), lastPartnerDeckBytes);
                }
                catch { }

                string data0 = Path.Combine(ppssppDocsDir, "SAVEDATA", "ULES011830001", "DATA0");
                if (File.Exists(data0))
                {
                    byte[] save = File.ReadAllBytes(data0);
                    if (save.Length >= 0x10000)
                    {
                        // Copia os 352 bytes (Nome + Contagens + Main + Side/Extra) para Slot 0 e Slots 2..126, sem tocar em 0xFD5C!
                        Buffer.BlockCopy(deckBytes, 0, save, DATA0_SLOT0_OFFSET, DATA0_DECK_PAYLOAD_SIZE);
                        for (int i = 0; i < DATA0_PARTNER_COUNT; i++)
                        {
                            int off = DATA0_PARTNER_DECK_START + (i * DATA0_DECK_SIZE);
                            if (off + DATA0_DECK_PAYLOAD_SIZE <= DATA0_PLAYER_NAME_OFFSET)
                            {
                                Buffer.BlockCopy(deckBytes, 0, save, off, DATA0_DECK_PAYLOAD_SIZE);
                            }
                        }
                        File.WriteAllBytes(data0, save);
                    }
                }

                // Injeta AO VIVO na memoria RAM do PPSSPP do Host imediatamente!
                ApplyPartnerDeckToHostRam(deckBytes);

                AppendLog(string.Format("[Deck Automatico OK] Deck de {0} carregado no Parceiro (Save DATA0 + Memoria RAM Ao Vivo)!", friendName));
                return true;
            }
            catch (Exception ex)
            {
                AppendLog("[Erro Deck] " + ex.Message);
                return false;
            }
        }

        // ====================================================================
        // CONTROLE E CAPTURA DE TELA DO PPSSPP
        // ====================================================================
        private IntPtr FindPpssppWindow()
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows((hWnd, lParam) =>
            {
                if (IsWindowVisible(hWnd))
                {
                    StringBuilder sbCls = new StringBuilder(128);
                    GetClassName(hWnd, sbCls, 128);
                    if (sbCls.ToString() == "PPSSPPWnd")
                    {
                        found = hWnd;
                        return false;
                    }
                    StringBuilder sbTitle = new StringBuilder(256);
                    GetWindowText(hWnd, sbTitle, 256);
                    string title = sbTitle.ToString();
                    if ((title.Contains("PPSSPP") || title.Contains("ULES01183")) && !title.Contains("Launcher") && !title.Contains("Ao Vivo"))
                    {
                        found = hWnd;
                        return false;
                    }
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        private bool IsLocalGameWindowFocused()
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            if (liveViewForm != null && !liveViewForm.IsDisposed && fg == liveViewForm.Handle)
                return true;
            IntPtr ppsspp = FindPpssppWindow();
            return ppsspp != IntPtr.Zero && fg == ppsspp;
        }

        private bool TriggerPpssppSaveState()
        {
            IntPtr hwnd = FindPpssppWindow();
            if (hwnd == IntPtr.Zero) return false;
            PostMessage(hwnd, WM_COMMAND, (IntPtr)ID_FILE_QUICKSAVESTATE, IntPtr.Zero);
            return true;
        }

        private bool TriggerPpssppLoadState()
        {
            IntPtr hwnd = FindPpssppWindow();
            if (hwnd == IntPtr.Zero) return false;
            PostMessage(hwnd, WM_COMMAND, (IntPtr)ID_FILE_QUICKLOADSTATE, IntPtr.Zero);
            return true;
        }

        private string GetPrimaryStatePath()
        {
            return Path.Combine(ppssppDocsDir, "PPSSPP_STATE", "ULES01183_1.02_0.ppst");
        }

        private string FindNewestStateFile(DateTime afterUtc)
        {
            string stateDir = Path.Combine(ppssppDocsDir, "PPSSPP_STATE");
            if (!Directory.Exists(stateDir)) return null;
            string bestFile = null;
            DateTime bestTime = afterUtc;

            foreach (string f in Directory.GetFiles(stateDir, "ULES01183*.ppst"))
            {
                if (f.EndsWith("undo.ppst", StringComparison.OrdinalIgnoreCase)) continue;
                DateTime wt = File.GetLastWriteTimeUtc(f);
                if (wt > bestTime)
                {
                    bestTime = wt;
                    bestFile = f;
                }
            }
            return bestFile;
        }

        private void LaunchPpsspp()
        {
            string exe = File.Exists(bundledPpssppPath) ? bundledPpssppPath : systemPpssppPath;
            if (!File.Exists(exe))
            {
                MessageBox.Show("PPSSPP nao encontrado em:\n" + exe, "Erro", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (!File.Exists(gameCsoPath))
            {
                MessageBox.Show("Jogo nao encontrado em:\n" + gameCsoPath, "Erro", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (FindPpssppWindow() == IntPtr.Zero)
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "\"" + gameCsoPath + "\"",
                    WorkingDirectory = Path.GetDirectoryName(exe)
                };
                Process.Start(psi);
            }
        }

        // Captura e transmite a tela do PPSSPP do Host em tempo real (~18 FPS JPEG)
        private ImageCodecInfo GetJpegEncoder()
        {
            foreach (ImageCodecInfo codec in ImageCodecInfo.GetImageEncoders())
            {
                if (codec.MimeType == "image/jpeg") return codec;
            }
            return null;
        }

        private void HostScreenStreamThread()
        {
            ImageCodecInfo jpegCodec = GetJpegEncoder();
            EncoderParameters encParams = new EncoderParameters(1);
            encParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 58L);

            const int targetW = 640;
            const int targetH = 362;

            using (Bitmap scaledBmp = new Bitmap(targetW, targetH, PixelFormat.Format24bppRgb))
            using (Graphics gScaled = Graphics.FromImage(scaledBmp))
            {
                gScaled.InterpolationMode = InterpolationMode.Bilinear;
                gScaled.CompositingQuality = CompositingQuality.HighSpeed;
                gScaled.SmoothingMode = SmoothingMode.None;

                int frameTick = 0;
                while (running && role == "host")
                {
                    try
                    {
                        frameTick++;
                        if (frameTick % 45 == 1 && !sendingSavestate)
                        {
                            ApplyPartnerDeckToHostRam(lastPartnerDeckBytes);
                        }

                        if (peerStream != null && !sendingSavestate)
                        {
                            IntPtr hwnd = FindPpssppWindow();
                            if (hwnd != IntPtr.Zero && !IsIconic(hwnd))
                            {
                                RECT rc;
                                if (GetClientRect(hwnd, out rc))
                                {
                                    int w = rc.Right - rc.Left;
                                    int h = rc.Bottom - rc.Top;
                                    if (w > 64 && h > 64)
                                    {
                                        POINT pt = new POINT { X = 0, Y = 0 };
                                        ClientToScreen(hwnd, ref pt);

                                        using (Bitmap srcBmp = new Bitmap(w, h, PixelFormat.Format24bppRgb))
                                        {
                                            using (Graphics gSrc = Graphics.FromImage(srcBmp))
                                            {
                                                gSrc.CopyFromScreen(pt.X, pt.Y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
                                            }
                                            gScaled.DrawImage(srcBmp, new Rectangle(0, 0, targetW, targetH), new Rectangle(0, 0, w, h), GraphicsUnit.Pixel);
                                        }

                                        using (MemoryStream ms = new MemoryStream())
                                        {
                                            if (jpegCodec != null)
                                                scaledBmp.Save(ms, jpegCodec, encParams);
                                            else
                                                scaledBmp.Save(ms, ImageFormat.Jpeg);

                                            SendFrame(5, ms.ToArray());
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                    Thread.Sleep(55); // ~18 FPS fluido e leve na Radmin VPN
                }
            }
        }

        // ====================================================================
        // WEBSOCKET CLIENT NATIVO PARA O PPSSPP (ws://127.0.0.1:45450/debugger)
        // ====================================================================
        private bool ConnectWebSocket()
        {
            lock (wsLock)
            {
                if (wsSocket != null && wsSocket.Connected) return true;
                try
                {
                    Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    s.ReceiveTimeout = 1000;
                    s.SendTimeout = 1000;
                    s.Connect("127.0.0.1", WS_PORT);
                    string req = "GET /debugger HTTP/1.1\r\n" +
                                 "Host: 127.0.0.1:" + WS_PORT + "\r\n" +
                                 "Upgrade: websocket\r\n" +
                                 "Connection: Upgrade\r\n" +
                                 "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n" +
                                 "Sec-WebSocket-Version: 13\r\n\r\n";
                    s.Send(Encoding.ASCII.GetBytes(req));
                    byte[] buf = new byte[512];
                    int r = s.Receive(buf);
                    string resp = Encoding.ASCII.GetString(buf, 0, r);
                    if (resp.Contains("101"))
                    {
                        wsSocket = s;
                        return true;
                    }
                    s.Close();
                }
                catch { }
                wsSocket = null;
                return false;
            }
        }

        private void SendWebSocketJson(string jsonPayload)
        {
            if (!ConnectWebSocket()) return;
            byte[] raw = Encoding.UTF8.GetBytes(jsonPayload);
            byte[] mask = new byte[] { 0x12, 0x34, 0x56, 0x78 };
            List<byte> frame = new List<byte> { 0x81 };
            if (raw.Length < 126)
            {
                frame.Add((byte)(0x80 | raw.Length));
            }
            else if (raw.Length < 65536)
            {
                frame.Add(0x80 | 126);
                frame.Add((byte)((raw.Length >> 8) & 0xFF));
                frame.Add((byte)(raw.Length & 0xFF));
            }
            frame.AddRange(mask);
            for (int i = 0; i < raw.Length; i++)
            {
                frame.Add((byte)(raw[i] ^ mask[i & 3]));
            }
            lock (wsLock)
            {
                try
                {
                    wsSocket.Send(frame.ToArray());
                }
                catch
                {
                    try { wsSocket.Close(); } catch { }
                    wsSocket = null;
                }
            }
        }

        // Executa os comandos de controle enviados pelo Amigo no PPSSPP do Host
        private void ApplyRemoteButtonsOnHost(string wsJson)
        {
            SendWebSocketJson(wsJson);
            SendWebSocketJson("{\"event\":\"memory.write_u32\",\"address\":" + PSP_PARTNER_AI_FLAG + ",\"value\":0}");

            if (FindPpssppWindow() == IntPtr.Zero) return;

            foreach (var kv in VK_MAP)
            {
                bool isDown = wsJson.Contains("\"" + kv.Key + "\":true");
                bool wasDown = false;
                hostLastPartnerBtns.TryGetValue(kv.Key, out wasDown);
                if (isDown != wasDown)
                {
                    hostLastPartnerBtns[kv.Key] = isDown;
                    byte vk = (byte)kv.Value;
                    byte scan = (byte)MapVirtualKey(vk, 0);
                    uint flags = isDown ? 0u : KEYEVENTF_KEYUP;
                    if (vk >= 0x25 && vk <= 0x28) flags |= KEYEVENTF_EXTENDEDKEY;
                    keybd_event(vk, scan, flags, UIntPtr.Zero);
                }
            }
        }

        // ====================================================================
        // REDE CO-OP VIA RADMIN VPN (HOST E PARCEIRO)
        // ====================================================================
        private void StartAsHost()
        {
            string ip = txtIp.Text.Trim();
            ConfigurePpssppForCoop(true, ip);
            if (!running)
            {
                running = true;
                role = "host";
                activeTurn = "host";
                new Thread(HostListenThread) { IsBackground = true }.Start();
                new Thread(HostScreenStreamThread) { IsBackground = true }.Start();
                new Thread(InputAndHotkeyThread) { IsBackground = true }.Start();
            }
            LaunchPpsspp();
            btnHost.Text = "SALA HOST ATIVA (TRANSMITINDO TELA AO VIVO)";
            btnHost.BackColor = Color.FromArgb(21, 128, 61);
            btnPartner.Enabled = false;
        }

        private void StartAsPartner()
        {
            string ip = txtIp.Text.Trim();
            ConfigurePpssppForCoop(false, ip);
            if (!running)
            {
                running = true;
                role = "partner";
                activeTurn = "partner";
                new Thread(() => PartnerConnectThread(ip)) { IsBackground = true }.Start();
                new Thread(InputAndHotkeyThread) { IsBackground = true }.Start();
            }
            ShowLiveViewWindow();
            btnPartner.Text = "CONECTADO A CAMPANHA DO HOST (TELA AO VIVO ABERTA)";
            btnPartner.BackColor = Color.FromArgb(3, 105, 161);
            btnHost.Enabled = false;
        }

        private void SendFrame(byte type, byte[] payload)
        {
            NetworkStream ns = peerStream;
            if (ns == null) return;
            lock (sendLock)
            {
                try
                {
                    int len = payload != null ? payload.Length : 0;
                    byte[] hdr = new byte[5];
                    hdr[0] = type;
                    hdr[1] = (byte)((len >> 24) & 0xFF);
                    hdr[2] = (byte)((len >> 16) & 0xFF);
                    hdr[3] = (byte)((len >> 8) & 0xFF);
                    hdr[4] = (byte)(len & 0xFF);
                    ns.Write(hdr, 0, 5);
                    if (len > 0) ns.Write(payload, 0, len);
                    ns.Flush();
                }
                catch { }
            }
        }

        private byte[] ReadExact(NetworkStream ns, int count)
        {
            byte[] buf = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int r = ns.Read(buf, offset, count - offset);
                if (r <= 0) return null;
                offset += r;
            }
            return buf;
        }

        private void HostListenThread()
        {
            try
            {
                serverListener = new TcpListener(IPAddress.Any, COOP_PORT);
                serverListener.Start();
                SetStatus("Aguardando Amigo conectar na porta " + COOP_PORT + "...");
                AppendLog("[Servidor Radmin] Sala Co-op aberta na porta " + COOP_PORT + "! Aguardando seu amigo...");

                while (running)
                {
                    if (!serverListener.Pending())
                    {
                        Thread.Sleep(200);
                        continue;
                    }
                    TcpClient client = serverListener.AcceptTcpClient();
                    client.NoDelay = true;
                    peerClient = client;
                    peerStream = client.GetStream();
                    string remoteIp = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();
                    AppendLog("[Conectado] Seu amigo conectou pelo Radmin VPN (" + remoteIp + ")!");
                    AppendLog("[Ao Vivo] Transmitindo sua tela do jogo em tempo real para o seu amigo!");
                    SetStatus("Amigo Conectado (" + remoteIp + ") | Transmitindo Tela Ao Vivo!");

                    ReceiveLoop(peerStream);
                }
            }
            catch (Exception ex)
            {
                if (running) AppendLog("[Erro Servidor] " + ex.Message);
            }
        }

        private void PartnerConnectThread(string hostIp)
        {
            while (running)
            {
                try
                {
                    SetStatus("Conectando ao Host (" + hostIp + ":" + COOP_PORT + ")...");
                    TcpClient client = new TcpClient();
                    client.NoDelay = true;
                    client.Connect(hostIp, COOP_PORT);
                    peerClient = client;
                    peerStream = client.GetStream();
                    AppendLog("[Conectado] Conectado a Campanha do Host (" + hostIp + ") via Radmin VPN!");
                    AppendLog("[Ao Vivo] Recebendo a tela da partida do Host em tempo real!");
                    SetStatus("Conectado ao Host (" + hostIp + ") | Tela Ao Vivo Ativa!");

                    SendOrApplyMyDeck();
                    ReceiveLoop(peerStream);
                }
                catch
                {
                    if (running)
                    {
                        SetStatus("Aguardando Host (" + hostIp + ")... Tentando novamente em 3s");
                        Thread.Sleep(3000);
                    }
                }
            }
        }

        private string ComputeDeckHash(byte[] deckBytes)
        {
            if (deckBytes == null || deckBytes.Length < DATA0_DECK_PAYLOAD_SIZE) return "";
            uint h = 2166136261u;
            for (int i = 0; i < DATA0_DECK_PAYLOAD_SIZE; i++)
            {
                h ^= deckBytes[i];
                h *= 16777619u;
            }
            return h.ToString("X8");
        }

        private void UpdateDeckInfoLabel(string dName, int cCount)
        {
            if (this.IsDisposed || lblDeckInfo == null) return;
            if (this.InvokeRequired)
            {
                try { this.BeginInvoke(new Action<string, int>(UpdateDeckInfoLabel), dName, cCount); } catch { }
                return;
            }
            lblDeckInfo.Text = string.Format("Deck no Save: {0} ({1} cartas)", dName, cCount);
        }

        private void SendOrApplyMyDeck()
        {
            string pName, dName;
            int cCount;
            byte[] deckBytes = ReadLocalPlayerDeck(out pName, out dName, out cCount);
            if (deckBytes == null)
            {
                AppendLog("[Aviso] Save DATA0 ainda nao encontrado.");
                return;
            }
            UpdateDeckInfoLabel(dName, cCount);
            lastSentDeckHash = ComputeDeckHash(deckBytes);

            string customName = txtName.Text.Trim();
            if (string.IsNullOrEmpty(customName)) customName = pName;

            if (role == "partner" && peerStream != null)
            {
                byte[] nameBytes = Encoding.UTF8.GetBytes(customName + "|" + dName + "|" + cCount);
                byte[] packet = new byte[DATA0_DECK_SIZE + nameBytes.Length];
                Buffer.BlockCopy(deckBytes, 0, packet, 0, Math.Min(deckBytes.Length, DATA0_DECK_SIZE));
                Buffer.BlockCopy(nameBytes, 0, packet, DATA0_DECK_SIZE, nameBytes.Length);
                SendFrame(1, packet);
                AppendLog(string.Format("[Deck Enviado Automaticamente] Seu deck '{0}' ({1} cartas) foi carregado no Parceiro do Host!", dName, cCount));
            }
            else
            {
                InjectPartnerDeckIntoHostSave(deckBytes, customName + " (" + dName + " - " + cCount + " cartas)");
            }
        }

        private void CaptureAndSendSavestate(string newTurn)
        {
            if (peerStream == null)
            {
                AppendLog("[Aviso] Nenhum amigo conectado na sala ainda.");
                return;
            }

            sendingSavestate = true;
            try
            {
                DateTime beforeSave = DateTime.UtcNow.AddMilliseconds(-100);
                if (!TriggerPpssppSaveState())
                {
                    AppendLog("[Aviso] Janela do PPSSPP nao encontrada. Abra o jogo primeiro.");
                    return;
                }

                string foundState = null;
                DateTime deadline = DateTime.UtcNow.AddMilliseconds(2500);
                while (DateTime.UtcNow < deadline)
                {
                    foundState = FindNewestStateFile(beforeSave);
                    if (foundState != null)
                    {
                        Thread.Sleep(120);
                        break;
                    }
                    Thread.Sleep(40);
                }

                if (foundState == null)
                {
                    string primary = GetPrimaryStatePath();
                    if (File.Exists(primary)) foundState = primary;
                }

                if (foundState == null || !File.Exists(foundState))
                {
                    AppendLog("[Aviso] Certifique-se de que o jogo ja iniciou dentro do PPSSPP antes de sincronizar.");
                    return;
                }

                byte[] rawState = File.ReadAllBytes(foundState);
                activeTurn = newTurn;
                using (MemoryStream ms = new MemoryStream())
                {
                    ms.WriteByte(newTurn == "partner" ? (byte)1 : (byte)0);
                    using (GZipStream gz = new GZipStream(ms, CompressionMode.Compress, true))
                    {
                        gz.Write(rawState, 0, rawState.Length);
                    }
                    byte[] payload = ms.ToArray();
                    SendFrame(2, payload);
                }
                string tLabel = activeTurn == "partner" ? "SEU AMIGO (Parceiro)" : "HOST";
                AppendLog(string.Format("[Sync OK] Estado sincronizado ({0} KB)! Vez ativa: {1}", rawState.Length / 1024, tLabel));
                SetStatus("Sincronizado | Vez Ativa: " + tLabel);
            }
            catch (Exception ex)
            {
                AppendLog("[Erro Sync] " + ex.Message);
            }
            finally
            {
                sendingSavestate = false;
            }
        }

        private void ApplyReceivedSavestate(byte[] payload)
        {
            try
            {
                if (payload == null || payload.Length < 2) return;
                activeTurn = payload[0] == 1 ? "partner" : "host";
                string stateDir = Path.Combine(ppssppDocsDir, "PPSSPP_STATE");
                Directory.CreateDirectory(stateDir);

                string path102 = Path.Combine(stateDir, "ULES01183_1.02_0.ppst");
                string path100 = Path.Combine(stateDir, "ULES01183_1.00_0.ppst");

                byte[] decompressed;
                using (MemoryStream msIn = new MemoryStream(payload, 1, payload.Length - 1))
                using (GZipStream gz = new GZipStream(msIn, CompressionMode.Decompress))
                using (MemoryStream msOut = new MemoryStream())
                {
                    gz.CopyTo(msOut);
                    decompressed = msOut.ToArray();
                }

                File.WriteAllBytes(path102, decompressed);
                File.WriteAllBytes(path100, decompressed);

                if (FindPpssppWindow() != IntPtr.Zero)
                {
                    Thread.Sleep(50);
                    TriggerPpssppLoadState();
                }

                string tLabel = activeTurn == "partner" ? "PARCEIRO (Sua Vez!)" : "HOST";
                AppendLog("[Sync OK] Estado da Campanha salvo/carregado no PPSSPP! Vez ativa: " + tLabel);
                SetStatus("Sincronizado | Vez Ativa: " + tLabel);
            }
            catch (Exception ex)
            {
                AppendLog("[Erro LoadState] " + ex.Message);
            }
        }

        private void ReceiveLoop(NetworkStream ns)
        {
            try
            {
                while (running)
                {
                    byte[] hdr = ReadExact(ns, 5);
                    if (hdr == null) break;
                    byte type = hdr[0];
                    int len = (hdr[1] << 24) | (hdr[2] << 16) | (hdr[3] << 8) | hdr[4];
                    if (len < 0 || len > 50 * 1024 * 1024) break;
                    byte[] payload = len > 0 ? ReadExact(ns, len) : new byte[0];
                    if (len > 0 && payload == null) break;

                    if (type == 1 && payload.Length >= DATA0_DECK_SIZE)
                    {
                        byte[] deckBytes = new byte[DATA0_DECK_SIZE];
                        Buffer.BlockCopy(payload, 0, deckBytes, 0, DATA0_DECK_SIZE);
                        string meta = Encoding.UTF8.GetString(payload, DATA0_DECK_SIZE, payload.Length - DATA0_DECK_SIZE);
                        string[] parts = meta.Split('|');
                        string fName = parts.Length > 0 ? parts[0] : "Amigo";
                        string dName = parts.Length > 1 ? parts[1] : "Deck";
                        string cCount = parts.Length > 2 ? parts[2] : "?";
                        InjectPartnerDeckIntoHostSave(deckBytes, fName + " (" + dName + " - " + cCount + " cartas)");
                        SendWebSocketJson("{\"event\":\"memory.write_u32\",\"address\":" + PSP_PARTNER_AI_FLAG + ",\"value\":0}");
                    }
                    else if (type == 2)
                    {
                        ApplyReceivedSavestate(payload);
                    }
                    else if (type == 3)
                    {
                        string wsJson = Encoding.UTF8.GetString(payload);
                        if (role == "host")
                            ApplyRemoteButtonsOnHost(wsJson);
                        else
                            SendWebSocketJson(wsJson);
                    }
                    else if (type == 4 && role == "host")
                    {
                        string reqTurn = Encoding.UTF8.GetString(payload);
                        CaptureAndSendSavestate(reqTurn);
                    }
                    else if (type == 5 && role == "partner")
                    {
                        if (liveViewForm != null && !liveViewForm.IsDisposed)
                        {
                            liveViewForm.UpdateFrame(payload);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppendLog("[Desconectado] Conexao encerrada: " + ex.Message);
            }
            finally
            {
                peerStream = null;
                if (running) SetStatus("Amigo desconectado. Aguardando reconexao...");
            }
        }

        private Dictionary<string, bool> ReadLocalButtons()
        {
            Dictionary<string, bool> btns = new Dictionary<string, bool>();
            foreach (var kv in VK_MAP)
            {
                btns[kv.Key] = (GetAsyncKeyState(kv.Value) & 0x8000) != 0;
            }

            // Leitura opcional de Controle XInput (Xbox/PS4/USB)
            try
            {
                XINPUT_STATE st = new XINPUT_STATE();
                uint res = 1;
                try { res = XInputGetState14(0, ref st); }
                catch { try { res = XInputGetState910(0, ref st); } catch { } }

                if (res == 0)
                {
                    ushort w = st.Gamepad.wButtons;
                    short lx = st.Gamepad.sThumbLX;
                    short ly = st.Gamepad.sThumbLY;
                    if ((w & 0x0001) != 0 || ly > 16000) btns["up"] = true;
                    if ((w & 0x0002) != 0 || ly < -16000) btns["down"] = true;
                    if ((w & 0x0004) != 0 || lx < -16000) btns["left"] = true;
                    if ((w & 0x0008) != 0 || lx > 16000) btns["right"] = true;
                    if ((w & 0x0010) != 0) btns["start"] = true;
                    if ((w & 0x0020) != 0) btns["select"] = true;
                    if ((w & 0x0100) != 0) btns["ltrigger"] = true;
                    if ((w & 0x0200) != 0) btns["rtrigger"] = true;
                    if ((w & 0x1000) != 0) btns["cross"] = true;
                    if ((w & 0x2000) != 0) btns["circle"] = true;
                    if ((w & 0x4000) != 0) btns["square"] = true;
                    if ((w & 0x8000) != 0) btns["triangle"] = true;
                }
            }
            catch { }

            return btns;
        }

        private void InputAndHotkeyThread()
        {
            bool f5Prev = false, f6Prev = false, f7Prev = false;
            string lastBtnJson = "";
            int autoDeckTick = 0;

            while (running)
            {
                try
                {
                    autoDeckTick++;
                    if (autoDeckTick % 220 == 0 && peerStream != null && role == "partner")
                    {
                        string pName, dName;
                        int cCount;
                        byte[] curDeck = ReadLocalPlayerDeck(out pName, out dName, out cCount);
                        if (curDeck != null)
                        {
                            string curHash = ComputeDeckHash(curDeck);
                            if (!string.IsNullOrEmpty(curHash) && curHash != lastSentDeckHash)
                            {
                                SendOrApplyMyDeck();
                            }
                        }
                    }

                    bool f5 = (GetAsyncKeyState(VK_F5) & 0x8000) != 0;
                    bool f6 = (GetAsyncKeyState(VK_F6) & 0x8000) != 0;
                    bool f7 = (GetAsyncKeyState(VK_F7) & 0x8000) != 0;

                    if (f5 && !f5Prev)
                    {
                        if (role == "host") CaptureAndSendSavestate("host");
                        else SendFrame(4, Encoding.UTF8.GetBytes("host"));
                    }
                    if (f6 && !f6Prev)
                    {
                        activeTurn = "partner";
                        SetStatus("Vez Ativa: PARCEIRO (Amigo controlando)");
                    }
                    if (f7 && !f7Prev)
                    {
                        activeTurn = "host";
                        SetStatus("Vez Ativa: HOST");
                    }
                    f5Prev = f5; f6Prev = f6; f7Prev = f7;

                    // O Parceiro envia seus controles em tempo real sempre que a Janela Ao Vivo (ou PPSSPP) estiver focada!
                    if (peerStream != null && role == "partner" && IsLocalGameWindowFocused())
                    {
                        Dictionary<string, bool> btns = ReadLocalButtons();
                        StringBuilder sb = new StringBuilder("{\"event\":\"input.buttons.send\",\"buttons\":{");
                        bool first = true;
                        foreach (var kv in btns)
                        {
                            if (!first) sb.Append(",");
                            sb.Append("\"").Append(kv.Key).Append("\":").Append(kv.Value ? "true" : "false");
                            first = false;
                        }
                        sb.Append("}}");
                        string curJson = sb.ToString();
                        if (curJson != lastBtnJson)
                        {
                            lastBtnJson = curJson;
                            SendFrame(3, Encoding.UTF8.GetBytes(curJson));
                        }
                    }
                }
                catch { }
                Thread.Sleep(16);
            }
        }

        // ====================================================================
        // AUTO-UPDATER PELO GITHUB COM AUTO-RECOMPILACAO E AUTO-RESTART
        // ====================================================================
        private int CompareVersions(string v1, string v2)
        {
            string[] p1 = v1.Trim().TrimStart('v', 'V').Split('.');
            string[] p2 = v2.Trim().TrimStart('v', 'V').Split('.');
            int len = Math.Max(p1.Length, p2.Length);
            for (int i = 0; i < len; i++)
            {
                int n1 = 0, n2 = 0;
                if (i < p1.Length) int.TryParse(p1[i], out n1);
                if (i < p2.Length) int.TryParse(p2[i], out n2);
                if (n1 != n2) return n1.CompareTo(n2);
            }
            return 0;
        }

        private string DownloadGitHubText(WebClient wc, string fileName)
        {
            try
            {
                wc.Headers.Clear();
                wc.Headers.Add("User-Agent", "YuGiOh-GX-TagForce3-Coop-Updater");
                wc.Headers.Add("Accept", "application/vnd.github.v3.raw");
                wc.Headers.Add("Cache-Control", "no-cache");
                string apiUrl = string.Format("https://api.github.com/repos/{0}/contents/{1}?ref={2}&t={3}",
                    GITHUB_REPO, fileName, GITHUB_BRANCH, DateTime.UtcNow.Ticks);
                return wc.DownloadString(apiUrl);
            }
            catch
            {
                wc.Headers.Clear();
                wc.Headers.Add("User-Agent", "YuGiOh-GX-TagForce3-Coop-Updater");
                wc.Headers.Add("Cache-Control", "no-cache");
                string rawUrl = string.Format("https://raw.githubusercontent.com/{0}/{1}/{2}?t={3}",
                    GITHUB_REPO, GITHUB_BRANCH, fileName, DateTime.UtcNow.Ticks);
                return wc.DownloadString(rawUrl);
            }
        }

        private void CheckGitHubUpdates(bool force)
        {
            try
            {
                AppendLog("[Auto-Update] Verificando atualizacoes no GitHub (" + GITHUB_REPO + ")...");
                using (WebClient wc = new WebClient())
                {
                    wc.Encoding = Encoding.UTF8;
                    string json = DownloadGitHubText(wc, "VERSION.json");

                    Match mVer = Regex.Match(json, "\"version\"\\s*:\\s*\"([^\"]+)\"");
                    if (!mVer.Success) return;
                    string remoteVer = mVer.Groups[1].Value;

                    if (!force && CompareVersions(remoteVer, localVersion) <= 0)
                    {
                        AppendLog("[Auto-Update] Voce ja esta na versao mais recente (v" + localVersion + ").");
                        return;
                    }

                    AppendLog("[Auto-Update] Nova versao encontrada no GitHub: v" + remoteVer + "! Atualizando...");
                    string[] filesToUpdate = new string[]
                    {
                        "coop_engine.py",
                        "auto_updater.py",
                        "Launcher.pyw",
                        "Launcher_Coop.cs",
                        "Abrir_Launcher.bat",
                        "JOGAR.bat",
                        "ULES01183.ini",
                        "COMO_JOGAR_COOP.txt",
                        "VERSION.json"
                    };

                    foreach (string f in filesToUpdate)
                    {
                        try
                        {
                            string content = DownloadGitHubText(wc, f);
                            string dest = Path.Combine(baseDir, f);
                            File.WriteAllText(dest, content, Encoding.UTF8);
                            AppendLog("[Auto-Update] Atualizado: " + f);
                        }
                        catch { }
                    }

                    localVersion = remoteVer;
                    AppendLog("[Auto-Update] Recompilando executavel para v" + remoteVer + "...");

                    string csc = @"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe";
                    if (!File.Exists(csc)) csc = @"C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe";
                    string csPath = Path.Combine(baseDir, "Launcher_Coop.cs");
                    string newExePath = Path.Combine(baseDir, "Launcher_Coop_new.exe");
                    string curExePath = Path.Combine(baseDir, "Launcher_Coop.exe");
                    string oldExePath = Path.Combine(baseDir, "Launcher_Coop.exe.old");

                    if (File.Exists(csc) && File.Exists(csPath))
                    {
                        ProcessStartInfo psi = new ProcessStartInfo
                        {
                            FileName = csc,
                            Arguments = "/nologo /target:winexe /out:\"" + newExePath + "\" \"" + csPath + "\"",
                            CreateNoWindow = true,
                            UseShellExecute = false
                        };
                        Process p = Process.Start(psi);
                        p.WaitForExit(8000);

                        if (File.Exists(newExePath))
                        {
                            if (File.Exists(oldExePath)) File.Delete(oldExePath);
                            if (File.Exists(curExePath)) File.Move(curExePath, oldExePath);
                            File.Move(newExePath, curExePath);

                            AppendLog("[Auto-Update] Reiniciando Launcher na nova versao v" + remoteVer + "...");
                            Thread.Sleep(600);
                            Process.Start(curExePath);
                            Environment.Exit(0);
                        }
                    }
                }
            }
            catch
            {
                AppendLog("[Auto-Update] Modo Offline ativo (v" + localVersion + ").");
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            running = false;
            try { if (liveViewForm != null && !liveViewForm.IsDisposed) liveViewForm.Close(); } catch { }
            try { if (peerStream != null) peerStream.Close(); } catch { }
            try { if (peerClient != null) peerClient.Close(); } catch { }
            try { if (serverListener != null) serverListener.Stop(); } catch { }
            base.OnFormClosing(e);
        }

        [STAThread]
        public static void Main()
        {
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new LauncherForm());
        }
    }
}
