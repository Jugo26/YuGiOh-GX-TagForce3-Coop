using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
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
    public class LauncherForm : Form
    {
        // ====================================================================
        // CONSTANTES DO JOGO E REDE
        // ====================================================================
        private const int COOP_PORT = 27315;
        private const int WS_PORT = 45450;
        private const string DEFAULT_RADMIN_IP = "26.196.166.86";
        private const string GITHUB_REPO = "Jugo26/YuGiOh-GX-TagForce3-Coop";
        private const string GITHUB_BRANCH = "main";

        private const int DATA0_PLAYER_DECK_OFFSET = 0x4A40;
        private const int DATA0_PARTNER_DECK_START = 0x4BAC;
        private const int DATA0_DECK_SIZE = 0x16C; // 364 bytes
        private const int DATA0_PARTNER_COUNT = 125;
        private const int DATA0_PLAYER_NAME_OFFSET = 0xFD5C;
        private const uint PSP_PARTNER_AI_FLAG = 0x09E02998;

        // Win32 Menu Commands nativos do PPSSPP (Menu ID 101)
        private const uint WM_COMMAND = 0x0111;
        private const int ID_FILE_QUICKSAVESTATE = 40034; // Save State direto no PPSSPP
        private const int ID_FILE_QUICKLOADSTATE = 40035; // Load State direto no PPSSPP

        // Win32 API
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

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

        // Caminhos
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

        // Estado Co-op
        private volatile bool running = false;
        private string role = ""; // "host" ou "partner"
        private string activeTurn = "host";
        private TcpListener serverListener;
        private TcpClient peerClient;
        private NetworkStream peerStream;
        private readonly object sendLock = new object();
        private Socket wsSocket;
        private readonly object wsLock = new object();
        private string localVersion = "1.2.0";

        public LauncherForm()
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // TLS 1.2
            baseDir = AppDomain.CurrentDomain.BaseDirectory;
            gameCsoPath = Path.Combine(baseDir, "Yu-Gi-Oh! GX Tag Force 3 PTBR.cso");
            bundledPpssppPath = Path.Combine(baseDir, "PPSSPP", "PPSSPPWindows64.exe");
            ppssppDocsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PPSSPP", "PSP");

            ReadLocalVersion();
            ConfigurePpssppForCoop(true, DEFAULT_RADMIN_IP);
            InitUI();

            ThreadPool.QueueUserWorkItem(_ => CheckGitHubUpdates(false));
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
            this.ClientSize = new Size(640, 670);
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
                Text = "Campanha Co-op via Radmin VPN (Sincronizacao Direta WM_COMMAND) | v" + localVersion,
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
                Location = new Point(20, 80),
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
            btnCheckGit.Click += (s, e) => ThreadPool.QueueUserWorkItem(_ => CheckGitHubUpdates(false));

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
                Text = " 2. Iniciar Jogo (PPSSPP Incluso na Pasta - 1 Clique!) ",
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                ForeColor = Color.FromArgb(245, 158, 11),
                BackColor = Color.FromArgb(24, 27, 34),
                Location = new Point(20, 185),
                Size = new Size(600, 125)
            };

            btnHost = CreateButton("CRIAR SALA CO-OP E ABRIR JOGO (SOU O HOST)", new Point(16, 26), new Size(568, 42), Color.FromArgb(185, 28, 28), Color.White);
            btnHost.Font = new Font("Segoe UI", 11, FontStyle.Bold);
            btnHost.Click += (s, e) => StartAsHost();

            btnPartner = CreateButton("ENTRAR NA CAMPANHA DO HOST (SOU O AMIGO / PARCEIRO)", new Point(16, 74), new Size(568, 42), Color.FromArgb(2, 132, 199), Color.White);
            btnPartner.Font = new Font("Segoe UI", 11, FontStyle.Bold);
            btnPartner.Click += (s, e) => StartAsPartner();

            grpModes.Controls.AddRange(new Control[] { btnHost, btnPartner });
            this.Controls.Add(grpModes);

            // 3. Painel Sincronizacao
            GroupBox grpSync = new GroupBox
            {
                Text = " 3. Sincronizacao de Campanha, Turnos e Deck (Atalhos no Jogo: F5, F6, F7) ",
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                ForeColor = Color.FromArgb(34, 197, 94),
                BackColor = Color.FromArgb(24, 27, 34),
                Location = new Point(20, 320),
                Size = new Size(600, 130)
            };

            lblNetStatus = new Label
            {
                Text = "Status: Pronto para iniciar (Escolha Host ou Parceiro acima)",
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                ForeColor = Color.FromArgb(234, 179, 8),
                Location = new Point(16, 24),
                Size = new Size(568, 20)
            };

            Button btnF5 = CreateButton("[F5] Sincronizar Tela/Estado", new Point(16, 50), new Size(184, 32), Color.FromArgb(38, 43, 54), Color.White);
            btnF5.Click += (s, e) => ThreadPool.QueueUserWorkItem(_ =>
            {
                if (role == "partner") SendFrame(4, Encoding.UTF8.GetBytes("host"));
                else CaptureAndSendSavestate("host");
            });

            Button btnF6 = CreateButton("[F6] Passar Vez p/ Amigo", new Point(208, 50), new Size(184, 32), Color.FromArgb(30, 58, 138), Color.FromArgb(56, 189, 248));
            btnF6.Click += (s, e) => ThreadPool.QueueUserWorkItem(_ =>
            {
                if (role == "partner") SendFrame(4, Encoding.UTF8.GetBytes("partner"));
                else CaptureAndSendSavestate("partner");
            });

            Button btnF7 = CreateButton("[F7] Fim do Turno do Amigo", new Point(400, 50), new Size(184, 32), Color.FromArgb(20, 83, 45), Color.FromArgb(74, 222, 128));
            btnF7.Click += (s, e) => ThreadPool.QueueUserWorkItem(_ => CaptureAndSendSavestate("host"));

            Button btnSendDeck = CreateButton("Enviar / Atualizar Meu Deck Proprio no Parceiro da Campanha", new Point(16, 88), new Size(410, 30), Color.FromArgb(38, 43, 54), Color.FromArgb(245, 158, 11));
            btnSendDeck.Click += (s, e) => ThreadPool.QueueUserWorkItem(_ => SendOrApplyMyDeck());

            Button btnCheats = CreateButton("Cheats", new Point(434, 88), new Size(72, 30), Color.FromArgb(38, 43, 54), Color.LightGray);
            btnCheats.Click += (s, e) =>
            {
                string ch = Path.Combine(ppssppDocsDir, "Cheats", "ULES01183.ini");
                if (File.Exists(ch)) Process.Start("notepad.exe", ch);
            };

            Button btnGuide = CreateButton("Guia", new Point(512, 88), new Size(72, 30), Color.FromArgb(38, 43, 54), Color.LightGray);
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
                Text = " Registro de Conexao, Sincronizacao e Auto-Updater Git ",
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(148, 163, 184),
                BackColor = Color.FromArgb(24, 27, 34),
                Location = new Point(20, 460),
                Size = new Size(600, 195)
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
                Location = new Point(12, 22),
                Size = new Size(576, 160)
            };
            grpLog.Controls.Add(txtLog);
            this.Controls.Add(grpLog);

            AppendLog("[Sistema] Launcher Co-op v" + localVersion + " iniciado!");
            AppendLog("[Dica] Assim que o amigo conectar, sua tela sera sincronizada com o PPSSPP dele!");
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

        // ====================================================================
        // CONFIGURACAO AUTOMATICA DO PPSSPP E LEITURA/INJECAO DE SAVE (DATA0)
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
                if (!File.Exists(Path.Combine(saveDir, "DATA0")) && Directory.Exists(saveBase))
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
                    c = c.Replace("EnableCheats = False", "EnableCheats = True")
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
            }
            catch (Exception ex)
            {
                AppendLog("[Aviso Config] " + ex.Message);
            }
        }

        private byte[] ReadLocalPlayerDeck(out string playerName, out string deckName, out int cardCount)
        {
            playerName = "Parceiro";
            deckName = "Deck Customizado";
            cardCount = 0;
            try
            {
                string data0 = Path.Combine(ppssppDocsDir, "SAVEDATA", "ULES011830001", "DATA0");
                if (!File.Exists(data0)) return null;
                byte[] all = File.ReadAllBytes(data0);
                if (all.Length < 0x10000) return null;

                byte[] deckBytes = new byte[DATA0_DECK_SIZE];
                Buffer.BlockCopy(all, DATA0_PLAYER_DECK_OFFSET, deckBytes, 0, DATA0_DECK_SIZE);

                deckName = Encoding.Unicode.GetString(deckBytes, 0, 64).Split('\0')[0].Trim();
                playerName = Encoding.Unicode.GetString(all, DATA0_PLAYER_NAME_OFFSET, 32).Split('\0')[0].Trim();

                for (int i = 76; i + 1 < DATA0_DECK_SIZE; i += 2)
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
                if (deckBytes == null || deckBytes.Length != DATA0_DECK_SIZE) return false;
                string data0 = Path.Combine(ppssppDocsDir, "SAVEDATA", "ULES011830001", "DATA0");
                if (!File.Exists(data0)) return false;

                byte[] save = File.ReadAllBytes(data0);
                for (int i = 0; i < DATA0_PARTNER_COUNT; i++)
                {
                    int off = DATA0_PARTNER_DECK_START + (i * DATA0_DECK_SIZE);
                    if (off + DATA0_DECK_SIZE <= save.Length)
                    {
                        Buffer.BlockCopy(deckBytes, 0, save, off, 64);
                        Buffer.BlockCopy(deckBytes, 76, save, off + 76, DATA0_DECK_SIZE - 76);
                    }
                }
                File.WriteAllBytes(data0, save);
                AppendLog(string.Format("[Deck do Amigo] Deck de {0} injetado em todos os parceiros da campanha!", friendName));
                return true;
            }
            catch (Exception ex)
            {
                AppendLog("[Erro Deck] " + ex.Message);
                return false;
            }
        }

        // ====================================================================
        // EXECUCAO E CONTROLE DA JANELA DO PPSSPP (WM_COMMAND 40034 / 40035)
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
                    if ((title.Contains("PPSSPP") || title.Contains("ULES01183")) && !title.Contains("Launcher"))
                    {
                        found = hWnd;
                        return false;
                    }
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        private bool IsPpssppFocused()
        {
            IntPtr hwnd = FindPpssppWindow();
            if (hwnd == IntPtr.Zero) return false;
            return GetForegroundWindow() == hwnd;
        }

        private bool TriggerPpssppSaveState()
        {
            IntPtr hwnd = FindPpssppWindow();
            if (hwnd == IntPtr.Zero) return false;
            // Envia o comando de menu nativo ID_FILE_QUICKSAVESTATE (40034) direto para o PPSSPPWnd
            PostMessage(hwnd, WM_COMMAND, (IntPtr)ID_FILE_QUICKSAVESTATE, IntPtr.Zero);
            return true;
        }

        private bool TriggerPpssppLoadState()
        {
            IntPtr hwnd = FindPpssppWindow();
            if (hwnd == IntPtr.Zero) return false;
            // Envia o comando de menu nativo ID_FILE_QUICKLOADSTATE (40035) direto para o PPSSPPWnd
            PostMessage(hwnd, WM_COMMAND, (IntPtr)ID_FILE_QUICKLOADSTATE, IntPtr.Zero);
            return true;
        }

        private string GetPrimaryStatePath()
        {
            // DISC_ID = ULES01183, DISC_VERSION = 1.02 no PARAM.SFO do jogo PT-BR
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
                new Thread(InputAndHotkeyThread) { IsBackground = true }.Start();
            }
            LaunchPpsspp();
            btnHost.Text = "SALA HOST ATIVA (PPSSPP ABERTO)";
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
                activeTurn = "host";
                new Thread(() => PartnerConnectThread(ip)) { IsBackground = true }.Start();
                new Thread(InputAndHotkeyThread) { IsBackground = true }.Start();
            }
            LaunchPpsspp();
            btnPartner.Text = "CONECTADO COMO PARCEIRO";
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
                    SetStatus("Amigo Conectado (" + remoteIp + ") | Sincronizando...");

                    // Sincroniza automaticamente a tela do Host com o Amigo 1.5s apos conectar!
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        Thread.Sleep(1500);
                        CaptureAndSendSavestate("host");
                    });

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
                    SetStatus("Conectado ao Host (" + hostIp + ")!");

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
            string customName = txtName.Text.Trim();
            if (string.IsNullOrEmpty(customName)) customName = pName;

            if (role == "partner" && peerStream != null)
            {
                byte[] nameBytes = Encoding.UTF8.GetBytes(customName + "|" + dName + "|" + cCount);
                byte[] packet = new byte[DATA0_DECK_SIZE + nameBytes.Length];
                Buffer.BlockCopy(deckBytes, 0, packet, 0, DATA0_DECK_SIZE);
                Buffer.BlockCopy(nameBytes, 0, packet, DATA0_DECK_SIZE, nameBytes.Length);
                SendFrame(1, packet);
                AppendLog(string.Format("[Deck Enviado] Seu deck '{0}' ({1} cartas) foi enviado para o Parceiro da Campanha!", dName, cCount));
            }
            else
            {
                InjectPartnerDeckIntoHostSave(deckBytes, customName);
            }
        }

        private void CaptureAndSendSavestate(string newTurn)
        {
            if (peerStream == null)
            {
                AppendLog("[Aviso] Nenhum amigo conectado na sala ainda.");
                return;
            }

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
                    Thread.Sleep(120); // Aguarda PPSSPP finalizar a gravacao no disco
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

            try
            {
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
                AppendLog(string.Format("[Sync OK] Campanha sincronizada ({0} KB)! Vez ativa: {1}", rawState.Length / 1024, tLabel));
                SetStatus("Sincronizado | Vez Ativa: " + tLabel);
            }
            catch (Exception ex)
            {
                AppendLog("[Erro Sync] " + ex.Message);
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

                Thread.Sleep(50);
                TriggerPpssppLoadState();

                string tLabel = activeTurn == "partner" ? "PARCEIRO (Sua Vez!)" : "HOST";
                AppendLog("[Sync OK] Campanha do Host carregada no seu PPSSPP! Vez ativa: " + tLabel);
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
                        SendWebSocketJson(wsJson);
                    }
                    else if (type == 4 && role == "host")
                    {
                        string reqTurn = Encoding.UTF8.GetString(payload);
                        CaptureAndSendSavestate(reqTurn);
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

        private void InputAndHotkeyThread()
        {
            bool f5Prev = false, f6Prev = false, f7Prev = false;
            string lastBtnJson = "";

            while (running)
            {
                try
                {
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
                        if (role == "host") CaptureAndSendSavestate("partner");
                        else SendFrame(4, Encoding.UTF8.GetBytes("partner"));
                    }
                    if (f7 && !f7Prev)
                    {
                        CaptureAndSendSavestate("host");
                    }
                    f5Prev = f5; f6Prev = f6; f7Prev = f7;

                    if (peerStream != null && activeTurn == role && IsPpssppFocused())
                    {
                        StringBuilder sb = new StringBuilder("{\"event\":\"input.buttons.send\",\"buttons\":{");
                        bool first = true;
                        foreach (var kv in VK_MAP)
                        {
                            if (!first) sb.Append(",");
                            bool pressed = (GetAsyncKeyState(kv.Value) & 0x8000) != 0;
                            sb.Append("\"").Append(kv.Key).Append("\":").Append(pressed ? "true" : "false");
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
        // AUTO-UPDATER PELO GITHUB (Jugo26/YuGiOh-GX-TagForce3-Coop)
        // ====================================================================
        private int CompareVersions(string v1, string v2)
        {
            string[] p1 = v1.Trim().TrimStart('v', 'V').Split('.');
            string[] p2 = v2.Trim().TrimStart('v', 'V').Split('.');
            int len = Math.Max(p1.Length, p2.Length);
            for (int i = 0; i < len; i++)
            {
                int n1 = i < p1.Length ? int.Parse(p1[i]) : 0;
                int n2 = i < p2.Length ? int.Parse(p2[i]) : 0;
                if (n1 != n2) return n1.CompareTo(n2);
            }
            return 0;
        }

        private void CheckGitHubUpdates(bool force)
        {
            try
            {
                AppendLog("[Auto-Update] Verificando atualizacoes no GitHub (" + GITHUB_REPO + ")...");
                using (WebClient wc = new WebClient())
                {
                    wc.Encoding = Encoding.UTF8;
                    wc.Headers.Add("User-Agent", "YuGiOh-GX-TagForce3-Coop-Updater");
                    wc.Headers.Add("Cache-Control", "no-cache");

                    string url = string.Format("https://raw.githubusercontent.com/{0}/{1}/VERSION.json?t={2}",
                        GITHUB_REPO, GITHUB_BRANCH, DateTime.UtcNow.Ticks);
                    string json = wc.DownloadString(url);

                    Match mVer = Regex.Match(json, "\"version\"\\s*:\\s*\"([^\"]+)\"");
                    if (!mVer.Success) return;
                    string remoteVer = mVer.Groups[1].Value;

                    if (!force && CompareVersions(remoteVer, localVersion) <= 0)
                    {
                        AppendLog("[Auto-Update] Voce ja esta na versao mais recente (v" + localVersion + ").");
                        return;
                    }

                    AppendLog("[Auto-Update] Nova versao encontrada no GitHub: v" + remoteVer + "! Baixando...");
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
                            string fUrl = string.Format("https://raw.githubusercontent.com/{0}/{1}/{2}?t={3}",
                                GITHUB_REPO, GITHUB_BRANCH, f, DateTime.UtcNow.Ticks);
                            string dest = Path.Combine(baseDir, f);
                            wc.DownloadFile(fUrl, dest + ".tmp");
                            if (File.Exists(dest)) File.Delete(dest);
                            File.Move(dest + ".tmp", dest);
                            AppendLog("[Auto-Update] Atualizado: " + f);
                        }
                        catch { }
                    }

                    localVersion = remoteVer;
                    AppendLog("[Auto-Update] Atualizado com sucesso para v" + remoteVer + "!");
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
            try { if (peerStream != null) peerStream.Close(); } catch { }
            try { if (peerClient != null) peerClient.Close(); } catch { }
            try { if (serverListener != null) serverListener.Stop(); } catch { }
            base.OnFormClosing(e);
        }

        [STAThread]
        public static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new LauncherForm());
        }
    }
}
