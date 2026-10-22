using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// ════════════════════════════════════════════════════════════════════════════
//  ScreenShareSuite.cs — 통합 단일 파일 (DLL 없음)
//   런처 한 화면에서 버튼으로 아래 창을 띄운다:
//     1) 내 화면 미러 (로컬 미리보기)
//     2) 서버  — 내 화면 송출 / 원격 피제어 (TLS·비밀번호·멀티모니터·UPnP)
//     3) 클라이언트 — 상대 화면 보기 / 원격 제어
//   빌드(.NET Framework 4.8 내장 csc, SDK 불필요):
//     csc /target:winexe /out:ScreenShareSuite.exe ^
//       /reference:System.dll /reference:System.Core.dll ^
//       /reference:System.Drawing.dll /reference:System.Windows.Forms.dll ^
//       /reference:System.Xml.dll  ScreenShareSuite.cs
// ════════════════════════════════════════════════════════════════════════════

namespace ScreenShareSuite
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new LauncherForm());
        }
    }

    // ════════ 통합 런처 화면 ════════════════════════════════════════════
    class LauncherForm : Form
    {
        private ServerForm _server;

        public LauncherForm()
        {
            Text = "화면 공유 도구 모음";
            ClientSize = new Size(460, 312);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            BackColor = Color.White;
            Build();
        }

        private void Build()
        {
            FlowLayoutPanel root = new FlowLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.FlowDirection = FlowDirection.TopDown;
            root.WrapContents = false;
            root.Padding = new Padding(20, 16, 20, 12);

            Label title = new Label();
            title.Text = "화면 공유 도구 모음";
            title.Font = new Font("Segoe UI", 15F, FontStyle.Bold);
            title.ForeColor = Color.FromArgb(40, 40, 40);
            title.AutoSize = true;
            title.Margin = new Padding(2, 0, 0, 2);
            root.Controls.Add(title);

            Label sub = new Label();
            sub.Text = "필요한 기능의 버튼을 눌러 창을 여세요.";
            sub.Font = new Font("Segoe UI", 9F);
            sub.ForeColor = Color.Gray;
            sub.AutoSize = true;
            sub.Margin = new Padding(2, 0, 0, 12);
            root.Controls.Add(sub);

            Button bServer = MakeButton(
                "서버 — 내 화면 송출 / 원격 피제어\n다른 PC가 내 화면을 보고 조작 (TLS·비밀번호)", Theme.Primary);
            bServer.Click += (s, e) => OpenServer();
            root.Controls.Add(bServer);

            Button bClient = MakeButton(
                "클라이언트 — 상대 화면 보기 / 원격 제어\n다른 PC에 접속해서 보고 조작", Color.FromArgb(33, 99, 170));
            bClient.Click += (s, e) => OpenClient();
            root.Controls.Add(bClient);

            Label foot = new Label();
            foot.Text = "각 창은 독립적으로 동작합니다. 이 창을 닫으면 모두 종료됩니다.\n클라이언트는 여러 개 띄울 수 있습니다.";
            foot.Font = new Font("Segoe UI", 8.5F);
            foot.ForeColor = Color.Gray;
            foot.AutoSize = true;
            foot.Margin = new Padding(2, 12, 0, 0);
            root.Controls.Add(foot);

            Controls.Add(root);
        }

        private Button MakeButton(string text, Color back)
        {
            Button b = new Button();
            b.Text = text;
            b.Font = new Font("Segoe UI", 10.5F, FontStyle.Bold);
            b.ForeColor = Color.White;
            b.BackColor = back;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Theme.Lighten(back, 0.12);
            b.FlatAppearance.MouseDownBackColor = Theme.Darken(back, 0.15);
            b.TextAlign = ContentAlignment.MiddleCenter;
            b.Size = new Size(404, 66);
            b.Margin = new Padding(2, 6, 2, 6);
            b.Cursor = Cursors.Hand;
            b.UseVisualStyleBackColor = false;
            return b;
        }

        private void OpenServer() { _server = ShowSingle(_server, delegate { return new ServerForm(); }); }
        private void OpenClient() { ClientForm c = new ClientForm(); c.Show(); } // 여러 개 허용

        // 단일 인스턴스: 이미 열려 있으면 앞으로 가져오고, 아니면 새로 띄움
        private T ShowSingle<T>(T existing, Func<T> create) where T : Form
        {
            if (existing != null && !existing.IsDisposed)
            {
                if (existing.WindowState == FormWindowState.Minimized)
                    existing.WindowState = FormWindowState.Normal;
                existing.Activate();
                existing.BringToFront();
                return existing;
            }
            T f = create();
            f.Show();
            return f;
        }
    }

    // ════════ 공통: 메시지 프로토콜 ════════════════════════════════════
    static class Wire
    {
        public const int DefaultPort = 5900;

        public const byte MSG_CHALLENGE = 1;
        public const byte MSG_AUTH      = 2;
        public const byte MSG_AUTHOK    = 3;
        public const byte MSG_AUTHFAIL  = 4;
        public const byte MSG_SELECT    = 5;
        public const byte MSG_FRAME     = 6;
        public const byte MSG_INPUT     = 7;
        public const byte MSG_FILE_OFFER = 8;  // [int32 id][int64 size][int32 nameLen][utf8 name]
        public const byte MSG_FILE_CHUNK = 9;  // [int32 id][bytes...]
        public const byte MSG_FILE_END   = 10; // [int32 id]

        public static bool ReadFull(Stream s, byte[] buf, int count)
        {
            int off = 0;
            while (off < count)
            {
                int r = s.Read(buf, off, count - off);
                if (r <= 0) return false;
                off += r;
            }
            return true;
        }

        public static void WriteMsg(Stream s, byte type, byte[] payload)
        {
            int len = payload == null ? 0 : payload.Length;
            byte[] hdr = new byte[5];
            hdr[0] = type;
            Buffer.BlockCopy(BitConverter.GetBytes(len), 0, hdr, 1, 4);
            s.Write(hdr, 0, 5);
            if (len > 0) s.Write(payload, 0, len);
            s.Flush();
        }

        public static byte[] BuildMsg(byte type, byte[] payload)
        {
            int len = payload == null ? 0 : payload.Length;
            byte[] m = new byte[5 + len];
            m[0] = type;
            Buffer.BlockCopy(BitConverter.GetBytes(len), 0, m, 1, 4);
            if (len > 0) Buffer.BlockCopy(payload, 0, m, 5, len);
            return m;
        }

        public static Msg ReadMsg(Stream s)
        {
            byte[] hdr = new byte[5];
            if (!ReadFull(s, hdr, 5)) return null;
            int len = BitConverter.ToInt32(hdr, 1);
            if (len < 0 || len > 64 * 1024 * 1024) return null;
            byte[] payload = len > 0 ? new byte[len] : new byte[0];
            if (len > 0 && !ReadFull(s, payload, len)) return null;
            Msg m = new Msg();
            m.Type = hdr[0];
            m.Payload = payload;
            return m;
        }
    }

    class Msg { public byte Type; public byte[] Payload; }

    // ════════ 공통: 파일 전송 (송신) ══════════════════════════════════
    static class FileXfer
    {
        public const int ChunkSize = 64 * 1024;

        // 파일을 청크로 나눠 send(framed bytes) 로 흘려보낸다. 백그라운드 스레드에서 호출 권장.
        public static void Send(string path, Action<byte[]> send, Action<string> log)
        {
            FileInfo fi = new FileInfo(path);
            int id = Math.Abs(Guid.NewGuid().GetHashCode());
            string name = fi.Name;
            long size = fi.Length;

            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter bw = new BinaryWriter(ms))
            {
                bw.Write(id); bw.Write(size);
                byte[] nm = Encoding.UTF8.GetBytes(name);
                bw.Write(nm.Length); bw.Write(nm);
                send(Wire.BuildMsg(Wire.MSG_FILE_OFFER, ms.ToArray()));
            }
            if (log != null) log("파일 전송 시작: " + name + " (" + Human(size) + ")");

            long sent = 0; int lastPct = -1;
            using (FileStream fs = File.OpenRead(path))
            {
                byte[] buf = new byte[ChunkSize];
                int n;
                while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                {
                    byte[] chunk = new byte[4 + n];
                    Buffer.BlockCopy(BitConverter.GetBytes(id), 0, chunk, 0, 4);
                    Buffer.BlockCopy(buf, 0, chunk, 4, n);
                    send(Wire.BuildMsg(Wire.MSG_FILE_CHUNK, chunk));
                    sent += n;
                    int pct = size > 0 ? (int)(sent * 100 / size) : 100;
                    if (pct != lastPct && pct % 10 == 0) { lastPct = pct; if (log != null) log("전송 중: " + name + "  " + pct + "%"); }
                    Thread.Sleep(2); // 화면 영상에 대역폭 양보
                }
            }
            send(Wire.BuildMsg(Wire.MSG_FILE_END, BitConverter.GetBytes(id)));
            if (log != null) log("파일 전송 완료: " + name);
        }

        public static string Human(long bytes)
        {
            string[] u = { "B", "KB", "MB", "GB" };
            double v = bytes; int i = 0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return v.ToString("0.#") + u[i];
        }
    }

    // ════════ 공통: 파일 전송 (수신) ══════════════════════════════════
    // 받은 파일은 [내 문서]\ScreenShareRecv 에 저장. 한 연결당 하나씩 사용(단일 스레드).
    class FileReceiver
    {
        class St { public FileStream Fs; public string Name; public string Path; public long Total; public long Got; public int LastPct = -1; }
        private readonly Dictionary<int, St> _m = new Dictionary<int, St>();
        private readonly string _folder;
        public Action<string> OnLog;
        public Action<string> OnSaved;   // 완료된 파일의 전체 경로

        public FileReceiver()
        {
            // 받은 파일 기본 저장 위치 = 바탕화면
            _folder = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrEmpty(_folder) || !Directory.Exists(_folder))
            {
                _folder = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                try { Directory.CreateDirectory(_folder); } catch { }
            }
        }
        public string Folder { get { return _folder; } }

        public void Handle(byte type, byte[] payload)
        {
            try
            {
                if (type == Wire.MSG_FILE_OFFER)
                {
                    using (MemoryStream ms = new MemoryStream(payload))
                    using (BinaryReader br = new BinaryReader(ms))
                    {
                        int id = br.ReadInt32();
                        long size = br.ReadInt64();
                        int nl = br.ReadInt32();
                        string name = Encoding.UTF8.GetString(br.ReadBytes(nl));
                        string safe = System.IO.Path.GetFileName(name); // 경로 탈출(../) 방지
                        if (string.IsNullOrEmpty(safe)) safe = "received.bin";
                        string path = UniquePath(System.IO.Path.Combine(_folder, safe));
                        St st = new St();
                        st.Name = safe; st.Path = path; st.Total = size;
                        st.Fs = File.Create(path);
                        _m[id] = st;
                        Log("파일 수신 시작: " + safe + " (" + FileXfer.Human(size) + ")");
                    }
                }
                else if (type == Wire.MSG_FILE_CHUNK)
                {
                    int id = BitConverter.ToInt32(payload, 0);
                    St st; if (!_m.TryGetValue(id, out st)) return;
                    st.Fs.Write(payload, 4, payload.Length - 4);
                    st.Got += payload.Length - 4;
                    int pct = st.Total > 0 ? (int)(st.Got * 100 / st.Total) : 100;
                    if (pct != st.LastPct && pct % 10 == 0) { st.LastPct = pct; Log("수신 중: " + st.Name + "  " + pct + "%"); }
                }
                else if (type == Wire.MSG_FILE_END)
                {
                    int id = BitConverter.ToInt32(payload, 0);
                    St st; if (!_m.TryGetValue(id, out st)) return;
                    _m.Remove(id);
                    try { st.Fs.Close(); } catch { }
                    Log("파일 수신 완료: " + st.Path);
                    if (OnSaved != null) OnSaved(st.Path);
                }
            }
            catch (Exception ex) { Log("파일 수신 오류: " + ex.Message); }
        }

        public void CloseAll()
        {
            foreach (St st in _m.Values) { try { st.Fs.Close(); } catch { } }
            _m.Clear();
        }

        private static string UniquePath(string path)
        {
            if (!File.Exists(path)) return path;
            string dir = System.IO.Path.GetDirectoryName(path);
            string b = System.IO.Path.GetFileNameWithoutExtension(path);
            string ext = System.IO.Path.GetExtension(path);
            for (int i = 1; i < 10000; i++)
            {
                string p = System.IO.Path.Combine(dir, b + " (" + i + ")" + ext);
                if (!File.Exists(p)) return p;
            }
            return path;
        }

        private void Log(string m) { Action<string> h = OnLog; if (h != null) h(m); }
    }

    // ════════ 공통: TLS 자체서명 인증서 ════════════════════════════════
    static class CertGen
    {
        public static X509Certificate2 Create()
        {
            RSA rsa = RSA.Create(2048);
            CertificateRequest req = new CertificateRequest("CN=ScreenShareLAN", rsa,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            req.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
            X509Certificate2 cert = req.CreateSelfSigned(
                DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(5));
            byte[] pfx = cert.Export(X509ContentType.Pfx, "");
            return new X509Certificate2(pfx, "",
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
        }
    }

    // ════════ 공통: 비밀번호 인증 ══════════════════════════════════════
    static class Auth
    {
        public static byte[] Hmac(string password, byte[] nonce)
        {
            byte[] key = Encoding.UTF8.GetBytes(password == null ? "" : password);
            using (HMACSHA256 h = new HMACSHA256(key)) return h.ComputeHash(nonce);
        }
        public static bool ConstEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }

    // ════════ 공통: JPEG 인코딩 ════════════════════════════════════════
    static class Jpeg
    {
        private static readonly ImageCodecInfo Codec = Find();
        private static ImageCodecInfo Find()
        {
            foreach (ImageCodecInfo c in ImageCodecInfo.GetImageEncoders())
                if (c.FormatID == ImageFormat.Jpeg.Guid) return c;
            return null;
        }
        public static byte[] Encode(Bitmap bmp, int quality)
        {
            using (EncoderParameters ep = new EncoderParameters(1))
            {
                ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
                using (MemoryStream ms = new MemoryStream())
                {
                    bmp.Save(ms, Codec, ep);
                    return ms.ToArray();
                }
            }
        }
    }

    // ════════ 공통: 멀티 모니터 ════════════════════════════════════════
    static class Monitors
    {
        public static Rectangle Bounds(int index)
        {
            if (index < 0) return SystemInformation.VirtualScreen;
            if (index < Screen.AllScreens.Length) return Screen.AllScreens[index].Bounds;
            return Screen.PrimaryScreen.Bounds;
        }
        public static int PrimaryIndex()
        {
            for (int i = 0; i < Screen.AllScreens.Length; i++)
                if (Screen.AllScreens[i].Primary) return i;
            return 0;
        }
        public static byte[] BuildList()
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter bw = new BinaryWriter(ms))
            {
                Screen[] sc = Screen.AllScreens;
                bw.Write(sc.Length);
                for (int i = 0; i < sc.Length; i++)
                {
                    Rectangle b = sc[i].Bounds;
                    bw.Write(i); bw.Write(b.X); bw.Write(b.Y); bw.Write(b.Width); bw.Write(b.Height);
                    bw.Write(sc[i].Primary);
                }
                return ms.ToArray();
            }
        }
        public static List<MonitorInfo> ParseList(byte[] payload)
        {
            List<MonitorInfo> list = new List<MonitorInfo>();
            using (MemoryStream ms = new MemoryStream(payload))
            using (BinaryReader br = new BinaryReader(ms))
            {
                int count = br.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    MonitorInfo mi = new MonitorInfo();
                    mi.Index = br.ReadInt32();
                    mi.X = br.ReadInt32(); mi.Y = br.ReadInt32();
                    mi.W = br.ReadInt32(); mi.H = br.ReadInt32();
                    mi.Primary = br.ReadBoolean();
                    list.Add(mi);
                }
            }
            return list;
        }
    }

    class MonitorInfo { public int Index; public int X, Y, W, H; public bool Primary; }

    // ════════ 공통: 입력 메시지 빌더 (클라이언트) ══════════════════════
    static class InputMsg
    {
        public static byte[] Move(float nx, float ny) { return Wire.BuildMsg(Wire.MSG_INPUT, P(0, w => { w.Write(nx); w.Write(ny); })); }
        public static byte[] Button(bool down, int button, float nx, float ny) { return Wire.BuildMsg(Wire.MSG_INPUT, P((byte)(down ? 1 : 2), w => { w.Write((byte)button); w.Write(nx); w.Write(ny); })); }
        public static byte[] Wheel(int delta, float nx, float ny) { return Wire.BuildMsg(Wire.MSG_INPUT, P(3, w => { w.Write(delta); w.Write(nx); w.Write(ny); })); }
        public static byte[] Key(bool down, int vk, bool ext) { return Wire.BuildMsg(Wire.MSG_INPUT, P((byte)(down ? 4 : 5), w => { w.Write(vk); w.Write(ext); })); }

        private static byte[] P(byte kind, Action<BinaryWriter> body)
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter bw = new BinaryWriter(ms))
            {
                bw.Write(kind); body(bw); return ms.ToArray();
            }
        }
    }

    // ════════ 공통: 입력 주입 (서버, SendInput) ════════════════════════
    static class InputInjector
    {
        public static void Apply(byte[] payload, Rectangle bounds)
        {
            using (MemoryStream ms = new MemoryStream(payload))
            using (BinaryReader br = new BinaryReader(ms))
            {
                byte kind = br.ReadByte();
                switch (kind)
                {
                    case 0: { float nx = br.ReadSingle(), ny = br.ReadSingle(); MoveTo(bounds, nx, ny); break; }
                    case 1: { byte b = br.ReadByte(); float nx = br.ReadSingle(), ny = br.ReadSingle(); Click(bounds, nx, ny, b, true); break; }
                    case 2: { byte b = br.ReadByte(); float nx = br.ReadSingle(), ny = br.ReadSingle(); Click(bounds, nx, ny, b, false); break; }
                    case 3: { int d = br.ReadInt32(); float nx = br.ReadSingle(), ny = br.ReadSingle(); WheelAt(bounds, nx, ny, d); break; }
                    case 4: { int vk = br.ReadInt32(); bool ext = br.ReadBoolean(); KeyEv(vk, ext, false); break; }
                    case 5: { int vk = br.ReadInt32(); bool ext = br.ReadBoolean(); KeyEv(vk, ext, true); break; }
                }
            }
        }

        private static void ToAbsolute(Rectangle bounds, float nx, float ny, out int ax, out int ay)
        {
            int sx = bounds.X + (int)Math.Round(nx * (bounds.Width - 1));
            int sy = bounds.Y + (int)Math.Round(ny * (bounds.Height - 1));
            int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
            int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
            int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            int vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            if (vw < 2) vw = 2; if (vh < 2) vh = 2;
            ax = (int)Math.Round((sx - vx) * 65535.0 / (vw - 1));
            ay = (int)Math.Round((sy - vy) * 65535.0 / (vh - 1));
        }

        private static void MoveTo(Rectangle bounds, float nx, float ny)
        {
            int ax, ay; ToAbsolute(bounds, nx, ny, out ax, out ay);
            Send(MakeMouse(ax, ay, 0, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK));
        }
        private static void Click(Rectangle bounds, float nx, float ny, int button, bool down)
        {
            int ax, ay; ToAbsolute(bounds, nx, ny, out ax, out ay);
            uint f = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK;
            if (button == 1) f |= down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP;
            else if (button == 2) f |= down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP;
            else f |= down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP;
            Send(MakeMouse(ax, ay, 0, f));
        }
        private static void WheelAt(Rectangle bounds, float nx, float ny, int delta)
        {
            int ax, ay; ToAbsolute(bounds, nx, ny, out ax, out ay);
            Send(MakeMouse(ax, ay, (uint)delta, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK | MOUSEEVENTF_WHEEL));
        }
        private static void KeyEv(int vk, bool ext, bool up)
        {
            INPUT inp = new INPUT();
            inp.type = INPUT_KEYBOARD;
            inp.U.ki.wVk = (ushort)vk;
            inp.U.ki.wScan = 0;
            inp.U.ki.dwFlags = (ext ? KEYEVENTF_EXTENDEDKEY : 0u) | (up ? KEYEVENTF_KEYUP : 0u);
            inp.U.ki.time = 0;
            inp.U.ki.dwExtraInfo = IntPtr.Zero;
            Send(inp);
        }
        private static INPUT MakeMouse(int ax, int ay, uint data, uint flags)
        {
            INPUT inp = new INPUT();
            inp.type = INPUT_MOUSE;
            inp.U.mi.dx = ax; inp.U.mi.dy = ay; inp.U.mi.mouseData = data;
            inp.U.mi.dwFlags = flags; inp.U.mi.time = 0; inp.U.mi.dwExtraInfo = IntPtr.Zero;
            return inp;
        }
        private static void Send(INPUT inp)
        {
            INPUT[] a = new INPUT[1]; a[0] = inp;
            SendInput(1, a, Marshal.SizeOf(typeof(INPUT)));
        }

        const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
        const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
        const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004,
                   MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010, MOUSEEVENTF_MIDDLEDOWN = 0x0020,
                   MOUSEEVENTF_MIDDLEUP = 0x0040, MOUSEEVENTF_WHEEL = 0x0800, MOUSEEVENTF_ABSOLUTE = 0x8000,
                   MOUSEEVENTF_VIRTUALDESK = 0x4000;
        const uint KEYEVENTF_EXTENDEDKEY = 0x0001, KEYEVENTF_KEYUP = 0x0002;

        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)]
        struct HARDWAREINPUT { public uint uMsg; public ushort wParamL; public ushort wParamH; }
        [StructLayout(LayoutKind.Explicit)]
        struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct INPUT { public uint type; public InputUnion U; }

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
        [DllImport("user32.dll")]
        static extern int GetSystemMetrics(int nIndex);
    }

    // ════════ 공통: 캡처에 마우스 커서 합성 ════════════════════════════
    static class CursorOverlay
    {
        const int CURSOR_SHOWING = 0x00000001, DI_NORMAL = 0x0003;
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int x; public int y; }
        [StructLayout(LayoutKind.Sequential)] struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINT ptScreenPos; }
        [StructLayout(LayoutKind.Sequential)] struct ICONINFO { public bool fIcon; public int xHotspot; public int yHotspot; public IntPtr hbmMask; public IntPtr hbmColor; }
        [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CURSORINFO pci);
        [DllImport("user32.dll")] static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO p);
        [DllImport("user32.dll")] static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr hIcon, int w, int h, int step, IntPtr brush, int flags);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);

        public static void Draw(Graphics g, int originX, int originY)
        {
            CURSORINFO ci = new CURSORINFO(); ci.cbSize = Marshal.SizeOf(typeof(CURSORINFO));
            if (!GetCursorInfo(ref ci) || ci.flags != CURSOR_SHOWING) return;
            int hotX = 0, hotY = 0; ICONINFO ii;
            if (GetIconInfo(ci.hCursor, out ii))
            {
                hotX = ii.xHotspot; hotY = ii.yHotspot;
                if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
                if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
            }
            int x = ci.ptScreenPos.x - originX - hotX, y = ci.ptScreenPos.y - originY - hotY;
            IntPtr hdc = g.GetHdc();
            try { DrawIconEx(hdc, x, y, ci.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL); }
            finally { g.ReleaseHdc(hdc); }
        }
    }

    // ════════ 공통: 화면 표시 + 원격 입력 캡처 패널 (클라이언트) ════════
    class DisplayPanel : Panel
    {
        private Bitmap _frame;
        private Rectangle _imgRect;
        public bool ControlEnabled;
        public Action<byte[]> SendInput;

        private string _notice;
        private Color _noticeColor = Color.Gray;

        public DisplayPanel()
        {
            DoubleBuffered = true;
            BackColor = Color.Black;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.Selectable, true);
            TabStop = true;
        }

        public void SetFrame(Bitmap frame)
        {
            Bitmap old = _frame;
            _frame = frame;
            _notice = null;
            if (old != null && old != frame) old.Dispose();
            Invalidate();
        }
        public void Clear()
        {
            Bitmap old = _frame; _frame = null;
            if (old != null) old.Dispose();
            Invalidate();
        }
        public void ShowNotice(string text, Color color)
        {
            Bitmap old = _frame; _frame = null;
            if (old != null) old.Dispose();
            _notice = text; _noticeColor = color;
            Invalidate();
        }

        private bool Norm(int mx, int my, out float nx, out float ny)
        {
            nx = ny = 0;
            if (_imgRect.Width <= 0 || _imgRect.Height <= 0 || !_imgRect.Contains(mx, my)) return false;
            nx = (mx - _imgRect.X) / (float)_imgRect.Width;
            ny = (my - _imgRect.Y) / (float)_imgRect.Height;
            return true;
        }
        private bool Active { get { return ControlEnabled && SendInput != null && _frame != null; } }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            float nx, ny;
            if (Active && Norm(e.X, e.Y, out nx, out ny)) SendInput(InputMsg.Button(true, BtnId(e.Button), nx, ny));
            base.OnMouseDown(e);
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            float nx, ny;
            if (Active && Norm(e.X, e.Y, out nx, out ny)) SendInput(InputMsg.Button(false, BtnId(e.Button), nx, ny));
            base.OnMouseUp(e);
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            float nx, ny;
            if (Active && Norm(e.X, e.Y, out nx, out ny)) SendInput(InputMsg.Move(nx, ny));
            base.OnMouseMove(e);
        }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            float nx, ny;
            if (Active && Norm(e.X, e.Y, out nx, out ny)) SendInput(InputMsg.Wheel(e.Delta, nx, ny));
            base.OnMouseWheel(e);
        }
        private static int BtnId(MouseButtons b)
        {
            if (b == MouseButtons.Right) return 1;
            if (b == MouseButtons.Middle) return 2;
            return 0;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Color.Black);
            if (_frame == null)
            {
                _imgRect = Rectangle.Empty;
                string text = _notice != null ? _notice
                    : "상대 IP·비밀번호 입력 후 [접속] 하면 여기에 상대 화면이 표시됩니다.";
                Color col = _notice != null ? _noticeColor : Color.Gray;
                using (Font f = new Font(Font.FontFamily, _notice != null ? 16F : Font.Size,
                                         _notice != null ? FontStyle.Bold : FontStyle.Regular))
                    TextRenderer.DrawText(g, text, f, ClientRectangle, col,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.WordBreak);
                return;
            }
            float scale = Math.Min((float)Width / _frame.Width, (float)Height / _frame.Height);
            int w = (int)(_frame.Width * scale), h = (int)(_frame.Height * scale);
            int x = (Width - w) / 2, y = (Height - h) / 2;
            _imgRect = new Rectangle(x, y, w, h);
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(_frame, x, y, w, h);
        }
    }

    // ════════ 공통: 네트워크 유틸 ══════════════════════════════════════
    static class NetUtil
    {
        public static List<string> GetLocalIPs()
        {
            List<string> list = new List<string>();
            try
            {
                foreach (IPAddress ip in Dns.GetHostEntry(Dns.GetHostName()).AddressList)
                    if (ip.AddressFamily == AddressFamily.InterNetwork) list.Add(ip.ToString());
            }
            catch { }
            if (list.Count == 0) list.Add("127.0.0.1");
            return list;
        }
    }

    // ════════ 공통: UPnP 자동 포트포워딩 ═══════════════════════════════
    class UpnpResult
    {
        public bool Mapped;
        public string ExternalIP;
        public string Message = "";
        public string ServiceType;
        public string ControlUrl;
        public int Port;
        public bool ExternalIsPrivate;
    }

    static class Upnp
    {
        public static UpnpResult MapPort(int port, string description)
        {
            UpnpResult r = new UpnpResult();
            r.Port = port;
            try
            {
                string location = Discover();
                if (location == null) { r.Message = "공유기에서 UPnP 장치를 찾지 못함 (UPnP 꺼져있거나 미지원)"; return r; }

                string controlUrl, serviceType;
                if (!FindService(location, out controlUrl, out serviceType))
                { r.Message = "공유기에 WAN 연결 서비스가 없음"; return r; }
                r.ControlUrl = controlUrl; r.ServiceType = serviceType;

                string gatewayHost = new Uri(location).Host;
                string localIp = LocalIpToward(gatewayHost);

                string inner =
                    "<NewRemoteHost></NewRemoteHost>" +
                    "<NewExternalPort>" + port + "</NewExternalPort>" +
                    "<NewProtocol>TCP</NewProtocol>" +
                    "<NewInternalPort>" + port + "</NewInternalPort>" +
                    "<NewInternalClient>" + localIp + "</NewInternalClient>" +
                    "<NewEnabled>1</NewEnabled>" +
                    "<NewPortMappingDescription>" + Escape(description) + "</NewPortMappingDescription>" +
                    "<NewLeaseDuration>0</NewLeaseDuration>";
                Soap(controlUrl, serviceType, "AddPortMapping", inner);

                r.Mapped = true;
                r.Message = "포트 포워딩 성공";

                try
                {
                    string resp = Soap(controlUrl, serviceType, "GetExternalIPAddress", "");
                    string ext = ExtractTag(resp, "NewExternalIPAddress");
                    r.ExternalIP = ext;
                    r.ExternalIsPrivate = IsPrivate(ext);
                    if (r.ExternalIsPrivate)
                        r.Message += " (단, 공인 IP가 사설망 " + ext + " → ISP 이중 NAT 의심, 외부 접속 불가할 수 있음)";
                }
                catch { r.ExternalIP = null; }

                return r;
            }
            catch (Exception ex) { r.Mapped = false; r.Message = "UPnP 요청 실패: " + Short(ex.Message); return r; }
        }

        public static void DeleteMapping(UpnpResult r)
        {
            if (r == null || !r.Mapped || r.ControlUrl == null) return;
            try
            {
                string inner =
                    "<NewRemoteHost></NewRemoteHost>" +
                    "<NewExternalPort>" + r.Port + "</NewExternalPort>" +
                    "<NewProtocol>TCP</NewProtocol>";
                Soap(r.ControlUrl, r.ServiceType, "DeletePortMapping", inner);
            }
            catch { }
        }

        private static string Discover()
        {
            foreach (string localIp in NetUtil.GetLocalIPs())
            {
                string loc = DiscoverOn(localIp);
                if (loc != null) return loc;
            }
            return null;
        }

        private static string DiscoverOn(string localIp)
        {
            string req =
                "M-SEARCH * HTTP/1.1\r\n" +
                "HOST: 239.255.255.250:1900\r\n" +
                "MAN: \"ssdp:discover\"\r\n" +
                "MX: 1\r\n" +
                "ST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n\r\n";
            byte[] data = Encoding.ASCII.GetBytes(req);
            try
            {
                using (UdpClient udp = new UdpClient(new IPEndPoint(IPAddress.Parse(localIp), 0)))
                {
                    udp.Client.ReceiveTimeout = 1300;
                    IPEndPoint mcast = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
                    udp.Send(data, data.Length, mcast);
                    udp.Send(data, data.Length, mcast);

                    DateTime end = DateTime.Now.AddMilliseconds(1500);
                    while (DateTime.Now < end)
                    {
                        try
                        {
                            IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
                            byte[] resp = udp.Receive(ref from);
                            string s = Encoding.ASCII.GetString(resp);
                            if (s.IndexOf("InternetGatewayDevice", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                s.IndexOf("WANConnectionDevice", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                Header(s, "ST") != null)
                            {
                                string loc = Header(s, "LOCATION");
                                if (loc != null) return loc.Trim();
                            }
                        }
                        catch { break; }
                    }
                }
            }
            catch { }
            return null;
        }

        private static bool FindService(string location, out string controlUrl, out string serviceType)
        {
            controlUrl = null; serviceType = null;
            string xml = HttpGet(location);
            if (xml == null) return false;
            System.Xml.XmlDocument doc = new System.Xml.XmlDocument();
            doc.LoadXml(xml);
            foreach (System.Xml.XmlNode sv in doc.GetElementsByTagName("service"))
            {
                string st = null, ctrl = null;
                foreach (System.Xml.XmlNode ch in sv.ChildNodes)
                {
                    if (ch.LocalName == "serviceType") st = ch.InnerText;
                    else if (ch.LocalName == "controlURL") ctrl = ch.InnerText;
                }
                if (st != null && ctrl != null &&
                    (st.IndexOf("WANIPConnection", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     st.IndexOf("WANPPPConnection", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    controlUrl = new Uri(new Uri(location), ctrl).ToString();
                    serviceType = st;
                    return true;
                }
            }
            return false;
        }

        private static string Soap(string controlUrl, string serviceType, string action, string innerXml)
        {
            string body =
                "<?xml version=\"1.0\"?>" +
                "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
                "<s:Body><u:" + action + " xmlns:u=\"" + serviceType + "\">" + innerXml +
                "</u:" + action + "></s:Body></s:Envelope>";
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(controlUrl);
            req.Method = "POST";
            req.ContentType = "text/xml; charset=\"utf-8\"";
            req.Headers.Add("SOAPAction", "\"" + serviceType + "#" + action + "\"");
            req.Proxy = null;
            req.Timeout = 5000;
            byte[] b = Encoding.UTF8.GetBytes(body);
            req.ContentLength = b.Length;
            using (Stream rs = req.GetRequestStream()) rs.Write(b, 0, b.Length);
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (StreamReader sr = new StreamReader(resp.GetResponseStream()))
                return sr.ReadToEnd();
        }

        private static string HttpGet(string url)
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Proxy = null; req.Timeout = 5000;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream()))
                    return sr.ReadToEnd();
            }
            catch { return null; }
        }

        private static string LocalIpToward(string host)
        {
            try
            {
                IPAddress[] addrs = Dns.GetHostAddresses(host);
                using (Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    s.Connect(addrs[0], 1900);
                    return ((IPEndPoint)s.LocalEndPoint).Address.ToString();
                }
            }
            catch
            {
                List<string> ips = NetUtil.GetLocalIPs();
                return ips.Count > 0 ? ips[0] : "127.0.0.1";
            }
        }

        private static string Header(string http, string name)
        {
            foreach (string line in http.Split('\n'))
            {
                int i = line.IndexOf(':');
                if (i > 0 && line.Substring(0, i).Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                    return line.Substring(i + 1).Trim();
            }
            return null;
        }

        private static string ExtractTag(string xml, string tag)
        {
            int a = xml.IndexOf("<" + tag + ">", StringComparison.OrdinalIgnoreCase);
            if (a < 0) return null;
            a += tag.Length + 2;
            int b = xml.IndexOf("</" + tag + ">", a, StringComparison.OrdinalIgnoreCase);
            if (b < 0) return null;
            return xml.Substring(a, b - a).Trim();
        }

        public static bool IsPrivate(string ip)
        {
            if (string.IsNullOrEmpty(ip)) return false;
            IPAddress a;
            if (!IPAddress.TryParse(ip, out a)) return false;
            byte[] o = a.GetAddressBytes();
            if (o.Length != 4) return false;
            if (o[0] == 10) return true;
            if (o[0] == 192 && o[1] == 168) return true;
            if (o[0] == 172 && o[1] >= 16 && o[1] <= 31) return true;
            if (o[0] == 100 && o[1] >= 64 && o[1] <= 127) return true;
            if (o[0] == 169 && o[1] == 254) return true;
            return false;
        }

        private static string Escape(string s) { return s == null ? "" : s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;"); }
        private static string Short(string s) { if (s == null) return ""; return s.Length > 120 ? s.Substring(0, 120) : s; }
    }

    // ════════ 공통: UI 테마 ════════════════════════════════════════════
    static class Theme
    {
        public static readonly Color Primary  = Color.FromArgb(33, 150, 83);
        public static readonly Color Danger   = Color.FromArgb(198, 40, 40);
        public static readonly Color Neutral  = Color.FromArgb(69, 90, 120);
        public static readonly Color ToggleOn = Color.FromArgb(216, 67, 21);
        public static readonly Color BarBack  = Color.FromArgb(238, 240, 244);

        private static readonly Font ButtonFont = new Font("Segoe UI", 9F, FontStyle.Bold);
        private static readonly Font LabelFont  = new Font("Segoe UI", 9F, FontStyle.Regular);

        public static void Accent(ToolStripButton b, Color back)
        {
            b.BackColor = back;
            b.ForeColor = Color.White;
        }

        public static void StyleBar(ToolStrip ts)
        {
            ts.Renderer = new ToolbarRenderer();
            ts.BackColor = BarBack;
            ts.Padding = new Padding(4, 3, 4, 3);
            ts.ImageScalingSize = new Size(16, 16);
            ts.CanOverflow = false;

            foreach (ToolStripItem it in ts.Items)
            {
                ToolStripButton b = it as ToolStripButton;
                if (b != null)
                {
                    if (b.BackColor == Color.Empty || b.BackColor == SystemColors.Control)
                        Accent(b, Neutral);
                    b.Font = ButtonFont;
                    b.Padding = new Padding(10, 4, 10, 4);
                    b.Margin = new Padding(3, 1, 3, 1);
                    b.AutoToolTip = false;
                    b.DisplayStyle = ToolStripItemDisplayStyle.Text;
                    continue;
                }
                ToolStripLabel l = it as ToolStripLabel;
                if (l != null)
                {
                    l.ForeColor = Color.FromArgb(70, 70, 70);
                    l.Font = LabelFont;
                    l.Margin = new Padding(4, 1, 0, 1);
                }
            }
        }

        public static Color Mix(Color c, Color t, double f)
        {
            return Color.FromArgb(c.A,
                (int)(c.R + (t.R - c.R) * f),
                (int)(c.G + (t.G - c.G) * f),
                (int)(c.B + (t.B - c.B) * f));
        }
        public static Color Lighten(Color c, double f) { return Mix(c, Color.White, f); }
        public static Color Darken(Color c, double f) { return Mix(c, Color.Black, f); }
    }

    class ToolbarRenderer : ToolStripProfessionalRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (SolidBrush sb = new SolidBrush(Theme.BarBack))
                e.Graphics.FillRectangle(sb, e.AffectedBounds);
        }

        protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
        {
            Graphics g = e.Graphics;
            ToolStripItem item = e.Item;
            ToolStripButton tb = item as ToolStripButton;
            bool on = tb != null && tb.Checked;

            Color face = item.BackColor;
            if (face == Color.Empty || face == SystemColors.Control) face = Theme.Neutral;

            Color fill = face;
            if (item.Pressed) fill = Theme.Darken(face, 0.25);
            else if (on) fill = Theme.Darken(face, 0.15);
            else if (item.Selected) fill = Theme.Lighten(face, 0.14);

            Rectangle r = new Rectangle(0, 0, item.Width - 1, item.Height - 1);
            g.SmoothingMode = SmoothingMode.None;
            using (SolidBrush sb = new SolidBrush(fill)) g.FillRectangle(sb, r);
            using (Pen hp = new Pen(Theme.Lighten(fill, 0.35)))
                g.DrawLine(hp, r.Left + 1, r.Top + 1, r.Right - 1, r.Top + 1);
            using (Pen bp = new Pen(Theme.Darken(face, 0.40)))
                g.DrawRectangle(bp, r);
            if (on)
                using (Pen ap = new Pen(Color.White, 1))
                    g.DrawRectangle(ap, r.Left + 2, r.Top + 2, r.Width - 4, r.Height - 4);
        }
    }

    // ════════ 서버 코어 ════════════════════════════════════════════════
    class ScreenServer
    {
        private TcpListener _listener;
        private X509Certificate2 _cert;
        private volatile bool _running;

        public volatile int Fps = 10;
        public volatile int Quality = 40;
        public volatile bool AllowControl = true;
        public volatile string Password = "";
        public int ClientCount;

        private long _lastInputTicks;
        public string LastController = "";

        private readonly List<Session> _sessions = new List<Session>();

        public event Action<string> Log;
        public bool Running { get { return _running; } }

        public bool RemoteActive
        {
            get
            {
                long t = Interlocked.Read(ref _lastInputTicks);
                if (t == 0) return false;
                return (DateTime.UtcNow - new DateTime(t, DateTimeKind.Utc)).TotalMilliseconds < 1500;
            }
        }

        public void Start(int port)
        {
            if (_running) return;
            _cert = CertGen.Create();
            _running = true;
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            Thread t = new Thread(AcceptLoop); t.IsBackground = true; t.Start();
            Raise("송출 시작 (포트 " + port + ", TLS)");
        }

        public void Stop()
        {
            _running = false;
            try { if (_listener != null) _listener.Stop(); } catch { }
            Raise("송출 중지");
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    TcpClient c = _listener.AcceptTcpClient();
                    Thread t = new Thread(() => Serve(c)); t.IsBackground = true; t.Start();
                }
                catch { if (_running) Thread.Sleep(200); }
            }
        }

        private void Serve(TcpClient tcp)
        {
            string who = "?";
            try { who = tcp.Client.RemoteEndPoint.ToString(); } catch { }
            SslStream ssl = null;
            Session s = null;
            bool counted = false;
            try
            {
                tcp.NoDelay = true;
                ssl = new SslStream(tcp.GetStream(), false);
                ssl.AuthenticateAsServer(_cert, false, SslProtocols.Tls12, false);

                byte[] nonce = new byte[16];
                using (RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider()) rng.GetBytes(nonce);
                Wire.WriteMsg(ssl, Wire.MSG_CHALLENGE, nonce);

                Msg auth = Wire.ReadMsg(ssl);
                if (auth == null || auth.Type != Wire.MSG_AUTH) { Raise("인증 실패(형식) ← " + who); return; }
                if (!Auth.ConstEquals(Auth.Hmac(Password, nonce), auth.Payload))
                {
                    Wire.WriteMsg(ssl, Wire.MSG_AUTHFAIL, Encoding.UTF8.GetBytes("비밀번호가 일치하지 않습니다"));
                    Raise("✗ 비밀번호 불일치 ← " + who);
                    return;
                }
                Wire.WriteMsg(ssl, Wire.MSG_AUTHOK, Monitors.BuildList());

                s = new Session();
                s.Ssl = ssl;
                s.Who = who;
                s.MonitorIndex = Monitors.PrimaryIndex();
                s.Receiver = new FileReceiver();
                s.Receiver.OnLog = m => Raise(m);
                s.Receiver.OnSaved = p => Raise("📁 받은 파일 ← " + who + " : " + p);
                Interlocked.Increment(ref ClientCount); counted = true;
                lock (_sessions) _sessions.Add(s);
                Raise("✓ 접속 허용 ← " + who);

                Thread reader = new Thread(() => ReaderLoop(s)); reader.IsBackground = true; reader.Start();
                SenderLoop(s);
            }
            catch { }
            finally
            {
                if (s != null) { lock (_sessions) _sessions.Remove(s); try { s.Receiver.CloseAll(); } catch { } }
                try { if (ssl != null) ssl.Close(); } catch { }
                try { tcp.Close(); } catch { }
                if (counted) Interlocked.Decrement(ref ClientCount);
                Raise("연결 종료 ← " + who);
            }
        }

        private void ReaderLoop(Session s)
        {
            try
            {
                while (_running && s.Running)
                {
                    Msg m = Wire.ReadMsg(s.Ssl);
                    if (m == null) break;
                    if (m.Type == Wire.MSG_SELECT && m.Payload.Length >= 4)
                        s.MonitorIndex = BitConverter.ToInt32(m.Payload, 0);
                    else if (m.Type == Wire.MSG_INPUT && AllowControl)
                    {
                        InputInjector.Apply(m.Payload, Monitors.Bounds(s.MonitorIndex));
                        Interlocked.Exchange(ref _lastInputTicks, DateTime.UtcNow.Ticks);
                        LastController = s.Who;
                    }
                    else if (m.Type == Wire.MSG_FILE_OFFER || m.Type == Wire.MSG_FILE_CHUNK || m.Type == Wire.MSG_FILE_END)
                        s.Receiver.Handle(m.Type, m.Payload);
                }
            }
            catch { }
            finally { s.Running = false; }
        }

        private void SenderLoop(Session s)
        {
            Bitmap bmp = null; Size last = Size.Empty;
            try
            {
                while (_running && s.Running)
                {
                    Rectangle b = Monitors.Bounds(s.MonitorIndex);
                    if (bmp == null || b.Size != last)
                    {
                        if (bmp != null) bmp.Dispose();
                        bmp = new Bitmap(b.Width, b.Height, PixelFormat.Format24bppRgb);
                        last = b.Size;
                    }
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.CopyFromScreen(b.X, b.Y, 0, 0, b.Size, CopyPixelOperation.SourceCopy);
                        CursorOverlay.Draw(g, b.X, b.Y);
                    }
                    byte[] frameMsg = Wire.BuildMsg(Wire.MSG_FRAME, Jpeg.Encode(bmp, Quality));
                    lock (s.WriteLock) { s.Ssl.Write(frameMsg, 0, frameMsg.Length); s.Ssl.Flush(); }

                    int fps = Fps; if (fps < 1) fps = 1;
                    Thread.Sleep(Math.Max(1, 1000 / fps));
                }
            }
            catch { }
            finally { s.Running = false; if (bmp != null) bmp.Dispose(); }
        }

        private void Raise(string m) { Action<string> h = Log; if (h != null) h(m); }

        // 접속 중인 모든 클라이언트에게 파일 전송 (프레임과 같은 통로를 WriteLock 으로 직렬화)
        public void SendFileToAll(string path)
        {
            Session[] arr; lock (_sessions) arr = _sessions.ToArray();
            if (arr.Length == 0) { Raise("파일 보낼 대상(접속자)이 없습니다."); return; }
            foreach (Session sx in arr)
            {
                Session target = sx;
                Thread t = new Thread(delegate ()
                {
                    try
                    {
                        FileXfer.Send(path,
                            b => { lock (target.WriteLock) { target.Ssl.Write(b, 0, b.Length); target.Ssl.Flush(); } },
                            m => Raise(m + "  → " + target.Who));
                    }
                    catch (Exception ex) { Raise("파일 전송 실패(" + target.Who + "): " + ex.Message); }
                });
                t.IsBackground = true; t.Start();
            }
        }

        class Session
        {
            public SslStream Ssl;
            public volatile int MonitorIndex;
            public volatile bool Running = true;
            public string Who = "";
            public readonly object WriteLock = new object();
            public FileReceiver Receiver;
        }
    }

    // ════════ 서버: 오버레이(클릭 통과/항상 위) ════════════════════════
    class ClickThroughOverlay : Form
    {
        const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
        public ClickThroughOverlay()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Enabled = false;
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return cp;
            }
        }
    }

    class BannerOverlay : ClickThroughOverlay
    {
        private readonly Label _lbl = new Label();
        private Rectangle _screen = Rectangle.Empty;
        public BannerOverlay()
        {
            BackColor = Color.FromArgb(200, 30, 30);
            Opacity = 0.88;
            _lbl.Dock = DockStyle.Fill;
            _lbl.ForeColor = Color.White;
            _lbl.TextAlign = ContentAlignment.MiddleCenter;
            _lbl.Font = new Font("Segoe UI", 12F, FontStyle.Bold);
            Controls.Add(_lbl);
        }
        public void Update(string text, Rectangle screenBounds)
        {
            _lbl.Text = text;
            if (screenBounds != _screen)
            {
                _screen = screenBounds;
                Bounds = new Rectangle(screenBounds.Left, screenBounds.Top, screenBounds.Width, 40);
            }
        }
    }

    class CursorRingOverlay : ClickThroughOverlay
    {
        public CursorRingOverlay()
        {
            Size = new Size(64, 64);
            BackColor = Color.Magenta;
            TransparencyKey = Color.Magenta;
        }
        public void MoveToCursor(Point c)
        {
            Location = new Point(c.X - Width / 2, c.Y - Height / 2);
            Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.None;
            using (Pen p1 = new Pen(Color.FromArgb(255, 255, 215, 0), 4f))
                g.DrawEllipse(p1, 6, 6, Width - 13, Height - 13);
            using (Pen p2 = new Pen(Color.FromArgb(255, 220, 20, 20), 3f))
                g.DrawEllipse(p2, 13, 13, Width - 27, Height - 27);
        }
    }

    // ════════ 서버 UI ══════════════════════════════════════════════════
    class ServerForm : Form
    {
        private readonly ScreenServer _server = new ScreenServer();
        private UpnpResult _upnp;

        private ToolStripTextBox _portBox = new ToolStripTextBox();
        private ToolStripTextBox _pwBox = new ToolStripTextBox();
        private ToolStripComboBox _fpsBox = new ToolStripComboBox();
        private ToolStripComboBox _qualityBox = new ToolStripComboBox();
        private ToolStripButton _allowCtrlBtn = new ToolStripButton();
        private ToolStripButton _sendFileBtn = new ToolStripButton();
        private ToolStripButton _copyBtn = new ToolStripButton();
        private ToolStripButton _startStopBtn = new ToolStripButton();
        private ToolStripLabel _state = new ToolStripLabel();
        private TextBox _log = new TextBox();
        private Label _ipLabel = new Label();

        private System.Windows.Forms.Timer _uiTimer = new System.Windows.Forms.Timer();
        private System.Windows.Forms.Timer _overlayTimer = new System.Windows.Forms.Timer();
        private System.Windows.Forms.Timer _copyResetTimer = new System.Windows.Forms.Timer();
        private BannerOverlay _banner = new BannerOverlay();
        private CursorRingOverlay _cursorRing = new CursorRingOverlay();

        public ServerForm()
        {
            Text = "ScreenServer — 내 화면 송출 / 원격 피제어 (TLS)";
            Width = 1060; Height = 500;
            MinimumSize = new Size(1000, 360);
            StartPosition = FormStartPosition.CenterScreen;

            BuildUI();

            _server.Log += msg => SafeInvoke(() => AppendLog(msg));

            _uiTimer.Interval = 1000;
            _uiTimer.Tick += (s, e) => UpdateState();
            _uiTimer.Start();

            _overlayTimer.Interval = 40;
            _overlayTimer.Tick += (s, e) => UpdateOverlays();
            _overlayTimer.Start();

            _copyResetTimer.Interval = 1300;
            _copyResetTimer.Tick += (s, e) => { _copyResetTimer.Stop(); _copyBtn.Text = "주소 복사"; Theme.Accent(_copyBtn, Color.FromArgb(0, 120, 130)); };

            Load += (s, e) => StartServer();
            FormClosed += (s, e) =>
            {
                _server.Stop(); CleanupUpnp();
                try { _banner.Dispose(); } catch { }
                try { _cursorRing.Dispose(); } catch { }
            };
        }

        private void UpdateOverlays()
        {
            bool active = _server.Running && _server.RemoteActive;
            if (active)
            {
                Point c = Cursor.Position;
                Rectangle scr = Screen.FromPoint(c).Bounds;
                string who = _server.LastController;
                _banner.Update("⚠  원격 조작 중" + (string.IsNullOrEmpty(who) ? "" : "   ·   " + who), scr);
                if (!_banner.Visible) _banner.Show();
                _cursorRing.MoveToCursor(c);
                if (!_cursorRing.Visible) _cursorRing.Show();
            }
            else
            {
                if (_banner.Visible) _banner.Hide();
                if (_cursorRing.Visible) _cursorRing.Hide();
            }
        }

        private void BuildUI()
        {
            ToolStrip bar = new ToolStrip(); bar.GripStyle = ToolStripGripStyle.Hidden;
            bar.Items.Add(new ToolStripLabel("포트:"));
            _portBox.Text = Wire.DefaultPort.ToString(); _portBox.Width = 50; bar.Items.Add(_portBox);
            bar.Items.Add(new ToolStripLabel("내 비밀번호:"));
            _pwBox.Width = 100; _pwBox.TextBox.UseSystemPasswordChar = true;
            _pwBox.TextChanged += (s, e) => _server.Password = _pwBox.Text;
            bar.Items.Add(_pwBox);
            bar.Items.Add(new ToolStripLabel("FPS:"));
            _fpsBox.DropDownStyle = ComboBoxStyle.DropDownList; _fpsBox.Width = 50;
            foreach (string f in new[] { "5", "10", "15", "20", "30" }) _fpsBox.Items.Add(f);
            _fpsBox.SelectedItem = "10";
            _fpsBox.SelectedIndexChanged += (s, e) => _server.Fps = int.Parse(_fpsBox.SelectedItem.ToString());
            bar.Items.Add(_fpsBox);
            bar.Items.Add(new ToolStripLabel("화질:"));
            _qualityBox.DropDownStyle = ComboBoxStyle.DropDownList; _qualityBox.Width = 85;
            _qualityBox.Items.Add("낮음(20)"); _qualityBox.Items.Add("보통(40)"); _qualityBox.Items.Add("높음(70)");
            _qualityBox.SelectedIndex = 1;
            _qualityBox.SelectedIndexChanged += (s, e) => { int[] q = { 20, 40, 70 }; _server.Quality = q[_qualityBox.SelectedIndex]; };
            bar.Items.Add(_qualityBox);
            _allowCtrlBtn.Text = "원격조작 허용"; _allowCtrlBtn.CheckOnClick = true; _allowCtrlBtn.Checked = true;
            _allowCtrlBtn.CheckedChanged += (s, e) => { _server.AllowControl = _allowCtrlBtn.Checked; StyleAllowBtn(); };
            bar.Items.Add(_allowCtrlBtn);
            _sendFileBtn.Text = "파일 보내기"; _sendFileBtn.Click += (s, e) => SendFile();
            bar.Items.Add(_sendFileBtn);
            _copyBtn.Text = "주소 복사"; _copyBtn.Click += (s, e) => CopyAddress();
            bar.Items.Add(_copyBtn);
            _startStopBtn.Text = "중지"; _startStopBtn.Click += (s, e) => ToggleServer();
            bar.Items.Add(_startStopBtn);

            _ipLabel.Dock = DockStyle.Top; _ipLabel.Height = 46; _ipLabel.Padding = new Padding(8, 6, 8, 6);
            _ipLabel.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            _ipLabel.ForeColor = Color.DarkBlue;
            _ipLabel.Text = "내부망 접속 주소(같은 공유기): " + string.Join("  ", NetUtil.GetLocalIPs().ToArray()) + "   |   외부망: UPnP 확인 중...";

            ToolStrip statusBar = new ToolStrip(); statusBar.GripStyle = ToolStripGripStyle.Hidden;
            _state.ForeColor = Color.Green; _state.Text = "● 준비중";
            statusBar.Items.Add(_state);

            _log.Multiline = true; _log.ReadOnly = true; _log.ScrollBars = ScrollBars.Vertical;
            _log.Dock = DockStyle.Fill; _log.BackColor = Color.White; _log.Font = new Font("Consolas", 9F);

            Controls.Add(_log);
            Controls.Add(statusBar);
            Controls.Add(_ipLabel);
            Controls.Add(bar);

            Theme.StyleBar(bar);
            Theme.StyleBar(statusBar);
            Theme.Accent(_sendFileBtn, Color.FromArgb(33, 99, 170));
            Theme.Accent(_copyBtn, Color.FromArgb(0, 120, 130));
            StyleAllowBtn();
            RefreshStartStop();
        }

        // 내 PC 접속 주소(내부망 + 외부망)를 클립보드로 복사 → 클라이언트에게 전달용
        private void CopyAddress()
        {
            int port = ParsePort();
            StringBuilder sb = new StringBuilder();
            foreach (string ip in NetUtil.GetLocalIPs()) sb.AppendLine("내부망: " + ip + ":" + port);
            if (_upnp != null && _upnp.Mapped && !_upnp.ExternalIsPrivate && !string.IsNullOrEmpty(_upnp.ExternalIP))
                sb.AppendLine("외부망: " + _upnp.ExternalIP + ":" + port);
            string text = sb.ToString().TrimEnd();

            if (TrySetClipboard(text))
            {
                AppendLog("✓ 접속 주소를 클립보드에 복사했습니다. 클라이언트에게 붙여넣어 전달하세요.");
                _copyBtn.Text = "복사됨 ✓"; Theme.Accent(_copyBtn, Theme.Primary);
                _copyResetTimer.Stop(); _copyResetTimer.Start();
            }
            else AppendLog("✗ 클립보드 복사 실패 — 잠시 후 다시 시도하세요.");
        }

        private static bool TrySetClipboard(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            for (int i = 0; i < 3; i++)
            {
                try { Clipboard.SetText(text); return true; }
                catch { Thread.Sleep(60); }
            }
            return false;
        }

        private void SendFile()
        {
            if (!_server.Running) { MessageBox.Show("먼저 송출을 시작하세요."); return; }
            if (_server.ClientCount == 0) { MessageBox.Show("접속한 클라이언트가 없습니다. (보는 사람에게만 파일을 보낼 수 있습니다)"); return; }
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = "보낼 파일 선택";
                if (d.ShowDialog(this) == DialogResult.OK) _server.SendFileToAll(d.FileName);
            }
        }

        private void StyleAllowBtn()
        {
            if (_allowCtrlBtn.Checked) { Theme.Accent(_allowCtrlBtn, Theme.Primary); _allowCtrlBtn.Text = "원격조작 허용 ✓"; }
            else { Theme.Accent(_allowCtrlBtn, Theme.Neutral); _allowCtrlBtn.Text = "원격조작 차단"; }
        }

        private void RefreshStartStop()
        {
            if (_server.Running) { Theme.Accent(_startStopBtn, Theme.Danger); _startStopBtn.Text = "중지"; }
            else { Theme.Accent(_startStopBtn, Theme.Primary); _startStopBtn.Text = "시작"; }
        }

        private void StartServer()
        {
            _server.Password = _pwBox.Text;
            _server.Fps = int.Parse(_fpsBox.SelectedItem.ToString());
            int[] q = { 20, 40, 70 }; _server.Quality = q[_qualityBox.SelectedIndex];
            _server.AllowControl = _allowCtrlBtn.Checked;
            try { _server.Start(ParsePort()); }
            catch (Exception ex) { MessageBox.Show("송출 시작 실패: " + ex.Message, "오류", MessageBoxButtons.OK, MessageBoxIcon.Warning); RefreshStartStop(); UpdateState(); return; }
            RefreshStartStop();
            UpdateState();
            TryUpnpAsync(ParsePort());
        }

        private void ToggleServer()
        {
            if (_server.Running) { _server.Stop(); CleanupUpnp(); }
            else StartServer();
            RefreshStartStop();
            UpdateState();
        }

        private void TryUpnpAsync(int port)
        {
            AppendLog("UPnP 포트 포워딩 시도 중... (수 초 소요)");
            ThreadPool.QueueUserWorkItem(delegate
            {
                if (_upnp != null) Upnp.DeleteMapping(_upnp);
                UpnpResult r = Upnp.MapPort(port, "ScreenShareLAN");
                _upnp = r;
                SafeInvoke(() => ApplyUpnp(r, port));
            });
        }

        private void ApplyUpnp(UpnpResult r, int port)
        {
            string intern = "내부망(같은 공유기): " + string.Join("  ", NetUtil.GetLocalIPs().ToArray());
            string firstIp = NetUtil.GetLocalIPs().Count > 0 ? NetUtil.GetLocalIPs()[0] : "내PC";
            if (r.Mapped && !r.ExternalIsPrivate && !string.IsNullOrEmpty(r.ExternalIP))
            {
                AppendLog("✓ UPnP 성공 — 외부(인터넷)에서  " + r.ExternalIP + " : " + port + "  로 접속 가능");
                _ipLabel.ForeColor = Color.DarkGreen;
                _ipLabel.Text = intern + "   |   외부망(인터넷): " + r.ExternalIP + ":" + port + "  ← 다른 공유기 PC는 이 주소로 접속";
            }
            else if (r.Mapped && r.ExternalIsPrivate)
            {
                AppendLog("△ UPnP 매핑은 됐지만 " + r.Message);
                _ipLabel.ForeColor = Color.DarkOrange;
                _ipLabel.Text = intern + "   |   외부 IP " + r.ExternalIP + " 가 사설망(ISP 이중 NAT) → 인터넷 접속은 어려움. 내부망 주소를 쓰세요.";
            }
            else
            {
                AppendLog("✗ UPnP 실패: " + r.Message);
                AppendLog("   → 인터넷 너머로 쓰려면 공유기 관리페이지 [포트포워딩]에서:  외부포트 " + port + " → " + firstIp + " : " + port + " (TCP) 수동 설정");
                _ipLabel.ForeColor = Color.DarkBlue;
                _ipLabel.Text = intern + "   |   외부망: UPnP 불가 → 수동 포트포워딩 필요 (로그 참고)";
            }
        }

        private void CleanupUpnp()
        {
            UpnpResult u = _upnp; _upnp = null;
            if (u != null && u.Mapped)
                try { Upnp.DeleteMapping(u); } catch { }
        }

        private void UpdateState()
        {
            if (_server.Running)
            {
                _state.ForeColor = Color.Green;
                _state.Text = "● 송출중 (보는 사람 " + _server.ClientCount + "명)";
            }
            else { _state.ForeColor = Color.Gray; _state.Text = "● 중지됨"; }
        }

        private void AppendLog(string msg)
        {
            _log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + msg + "\r\n");
        }

        private int ParsePort()
        {
            int p;
            if (int.TryParse(_portBox.Text.Trim(), out p) && p > 0 && p < 65536) return p;
            return Wire.DefaultPort;
        }

        private void SafeInvoke(Action a)
        {
            if (IsDisposed) return;
            try { BeginInvoke(a); } catch { }
        }
    }

    // ════════ 클라이언트 코어 ══════════════════════════════════════════
    class ScreenClient
    {
        private TcpClient _tcp;
        private SslStream _ssl;
        private Thread _recvThread;
        private Thread _sendThread;
        private volatile bool _running;
        private BlockingCollection<byte[]> _outbox;
        private FileReceiver _receiver;

        public event Action<Bitmap> FrameReceived;
        public event Action<string> StatusChanged;
        public event Action<List<MonitorInfo>> MonitorListReceived;
        public event Action<bool> ConnectionClosed;
        public event Action<string> FileLog;     // 파일 전송/수신 상태 텍스트
        public event Action<string> FileSaved;   // 받은 파일의 전체 경로

        public bool Connected { get { return _running; } }

        public void Connect(string ip, int port, string password)
        {
            Disconnect();
            _running = true;
            _outbox = new BlockingCollection<byte[]>();
            _recvThread = new Thread(() => Run(ip, port, password));
            _recvThread.IsBackground = true;
            _recvThread.Start();
        }

        public void Disconnect()
        {
            _running = false;
            try { if (_outbox != null) _outbox.CompleteAdding(); } catch { }
            try { if (_ssl != null) _ssl.Close(); } catch { }
            try { if (_tcp != null) _tcp.Close(); } catch { }
            _ssl = null; _tcp = null;
        }

        public void Send(byte[] framedMsg)
        {
            try { if (_running && _outbox != null) _outbox.Add(framedMsg); } catch { }
        }

        public void SelectMonitor(int index)
        {
            Send(Wire.BuildMsg(Wire.MSG_SELECT, BitConverter.GetBytes(index)));
        }

        // 서버로 파일 업로드 (입력 큐와 같은 단일 writer 로 안전하게 전송)
        public void SendFile(string path)
        {
            if (!_running) { Action<string> h = FileLog; if (h != null) h("접속된 상태가 아닙니다."); return; }
            Thread t = new Thread(delegate ()
            {
                try { FileXfer.Send(path, b => Send(b), m => { Action<string> h = FileLog; if (h != null) h(m); }); }
                catch (Exception ex) { Action<string> h = FileLog; if (h != null) h("파일 전송 실패: " + ex.Message); }
            });
            t.IsBackground = true; t.Start();
        }

        private void Run(string ip, int port, string password)
        {
            bool established = false;
            try
            {
                Status("접속 중... " + ip + ":" + port);
                _tcp = new TcpClient();
                _tcp.Connect(ip, port);
                _tcp.NoDelay = true;
                _ssl = new SslStream(_tcp.GetStream(), false, (a, b, c, d) => true);
                _ssl.AuthenticateAsClient("ScreenShareLAN", null, SslProtocols.Tls12, false);

                Msg ch = Wire.ReadMsg(_ssl);
                if (ch == null || ch.Type != Wire.MSG_CHALLENGE) { Status("핸드셰이크 실패"); return; }
                Wire.WriteMsg(_ssl, Wire.MSG_AUTH, Auth.Hmac(password, ch.Payload));

                Msg ok = Wire.ReadMsg(_ssl);
                if (ok == null) { Status("연결 끊김"); return; }
                if (ok.Type == Wire.MSG_AUTHFAIL) { Status("인증 실패: " + Encoding.UTF8.GetString(ok.Payload)); return; }
                if (ok.Type != Wire.MSG_AUTHOK) { Status("프로토콜 오류"); return; }

                List<MonitorInfo> mons = Monitors.ParseList(ok.Payload);
                Action<List<MonitorInfo>> mh = MonitorListReceived;
                if (mh != null) mh(mons);
                Status("접속됨 (TLS, AES256 암호화) — 화면 수신 중");
                established = true;

                _receiver = new FileReceiver();
                _receiver.OnLog = m => { Action<string> h = FileLog; if (h != null) h(m); };
                _receiver.OnSaved = p => { Action<string> h = FileSaved; if (h != null) h(p); };

                _sendThread = new Thread(SendLoop); _sendThread.IsBackground = true; _sendThread.Start();

                while (_running)
                {
                    Msg m = Wire.ReadMsg(_ssl);
                    if (m == null) break;
                    if (m.Type == Wire.MSG_FRAME)
                    {
                        Bitmap frame;
                        using (MemoryStream ms = new MemoryStream(m.Payload))
                        using (Bitmap tmp = new Bitmap(ms))
                            frame = new Bitmap(tmp);
                        Action<Bitmap> fh = FrameReceived;
                        if (fh != null) fh(frame); else frame.Dispose();
                    }
                    else if (m.Type == Wire.MSG_FILE_OFFER || m.Type == Wire.MSG_FILE_CHUNK || m.Type == Wire.MSG_FILE_END)
                        _receiver.Handle(m.Type, m.Payload);
                }
            }
            catch (Exception ex) { if (_running) Status("연결 오류: " + ex.Message); }
            finally
            {
                _running = false;
                try { if (_outbox != null) _outbox.CompleteAdding(); } catch { }
                try { if (_ssl != null) _ssl.Close(); } catch { }
                Status("연결 끊김");
                Action<bool> ch2 = ConnectionClosed;
                if (ch2 != null) ch2(established);
            }
        }

        private void SendLoop()
        {
            try
            {
                foreach (byte[] m in _outbox.GetConsumingEnumerable())
                {
                    if (!_running) break;
                    _ssl.Write(m, 0, m.Length);
                    _ssl.Flush();
                }
            }
            catch { }
        }

        private void Status(string s) { Action<string> h = StatusChanged; if (h != null) h(s); }
    }

    // ════════ 클라이언트 UI ════════════════════════════════════════════
    class ClientForm : Form
    {
        private readonly ScreenClient _client = new ScreenClient();
        private readonly DisplayPanel _display = new DisplayPanel();

        private ToolStripTextBox _ipBox = new ToolStripTextBox();
        private ToolStripTextBox _portBox = new ToolStripTextBox();
        private ToolStripTextBox _pwBox = new ToolStripTextBox();
        private ToolStripComboBox _monitorBox = new ToolStripComboBox();
        private ToolStripButton _connectBtn = new ToolStripButton();
        private ToolStripButton _disconnectBtn = new ToolStripButton();
        private ToolStripButton _controlBtn = new ToolStripButton();
        private ToolStripButton _sendFileBtn = new ToolStripButton();
        private ToolStripLabel _state = new ToolStripLabel();
        private ToolStripLabel _fileState = new ToolStripLabel();
        private ToolStrip _topBar;

        private List<MonitorInfo> _remoteMonitors;
        private volatile bool _userDisconnect;

        public ClientForm()
        {
            Text = "ScreenClient — 상대 화면 보기 / 원격 제어 (TLS)";
            Width = 1040; Height = 680;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.Black;
            KeyPreview = true;

            BuildUI();

            _display.Dock = DockStyle.Fill;
            _display.SendInput = bytes => _client.Send(bytes);
            Controls.Add(_display);
            Controls.Add(_topBar);

            _client.FrameReceived += frame => SafeInvoke(() => _display.SetFrame(frame));
            _client.StatusChanged += s => SafeInvoke(() => _state.Text = "상태: " + s);
            _client.MonitorListReceived += list => SafeInvoke(() => PopulateMonitors(list));
            _client.ConnectionClosed += established => SafeInvoke(() => OnConnectionClosed(established));
            _client.FileLog += msg => SafeInvoke(() => { _fileState.ForeColor = Color.FromArgb(33, 99, 170); _fileState.Text = "📁 " + msg; });
            _client.FileSaved += path => SafeInvoke(() =>
            {
                _fileState.Text = "📁 받음: " + System.IO.Path.GetFileName(path);
                MessageBox.Show("파일을 받았습니다:\n" + path, "파일 수신", MessageBoxButtons.OK, MessageBoxIcon.Information);
            });

            KeyDown += (s, e) => ForwardKey(e, true);
            KeyUp += (s, e) => ForwardKey(e, false);
            FormClosed += (s, e) => _client.Disconnect();
        }

        private void BuildUI()
        {
            _topBar = new ToolStrip(); _topBar.GripStyle = ToolStripGripStyle.Hidden;
            _topBar.Items.Add(new ToolStripLabel("상대 IP:"));
            _ipBox.Width = 130; _topBar.Items.Add(_ipBox);
            _topBar.Items.Add(new ToolStripLabel("포트:"));
            _portBox.Text = Wire.DefaultPort.ToString(); _portBox.Width = 45; _topBar.Items.Add(_portBox);
            _topBar.Items.Add(new ToolStripLabel("접속 비번:"));
            _pwBox.Width = 100; _pwBox.TextBox.UseSystemPasswordChar = true; _topBar.Items.Add(_pwBox);
            _connectBtn.Text = "접속"; _connectBtn.Click += (s, e) => ConnectRemote(); _topBar.Items.Add(_connectBtn);
            _disconnectBtn.Text = "끊기"; _disconnectBtn.Click += (s, e) => { _userDisconnect = true; _client.Disconnect(); }; _topBar.Items.Add(_disconnectBtn);
            _topBar.Items.Add(new ToolStripLabel("모니터:"));
            _monitorBox.DropDownStyle = ComboBoxStyle.DropDownList; _monitorBox.Width = 130;
            _monitorBox.SelectedIndexChanged += (s, e) => OnMonitorPick();
            _topBar.Items.Add(_monitorBox);
            _controlBtn.Text = "원격 제어 OFF"; _controlBtn.CheckOnClick = true;
            _controlBtn.CheckedChanged += (s, e) => SetControl(_controlBtn.Checked);
            _topBar.Items.Add(_controlBtn);
            _sendFileBtn.Text = "파일 보내기"; _sendFileBtn.Click += (s, e) => SendFile();
            _topBar.Items.Add(_sendFileBtn);
            _state.Text = "상태: 대기"; _topBar.Items.Add(_state);
            _topBar.Items.Add(new ToolStripSeparator());
            _fileState.Text = ""; _topBar.Items.Add(_fileState);

            Theme.Accent(_connectBtn, Theme.Primary);
            Theme.Accent(_disconnectBtn, Theme.Danger);
            Theme.Accent(_controlBtn, Theme.Neutral);
            Theme.Accent(_sendFileBtn, Color.FromArgb(33, 99, 170));
            Theme.StyleBar(_topBar);
        }

        private void SendFile()
        {
            if (!_client.Connected) { MessageBox.Show("먼저 서버에 접속하세요."); return; }
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = "보낼 파일 선택";
                if (d.ShowDialog(this) == DialogResult.OK) _client.SendFile(d.FileName);
            }
        }

        private void ConnectRemote()
        {
            string ip = _ipBox.Text.Trim();
            if (ip.Length == 0) { MessageBox.Show("상대 IP 를 입력하세요."); return; }
            _userDisconnect = false;
            _monitorBox.Items.Clear();
            _state.ForeColor = Color.FromArgb(70, 70, 70);
            _display.Clear();
            _client.Connect(ip, ParsePort(), _pwBox.Text);
        }

        private void OnConnectionClosed(bool wasEstablished)
        {
            SetControl(false);
            _monitorBox.Items.Clear();
            _remoteMonitors = null;

            if (_userDisconnect)
            {
                _userDisconnect = false;
                _state.ForeColor = Color.Gray;
                _state.Text = "상태: 연결 종료(내가 끊음)";
                _display.ShowNotice("연결을 끊었습니다.", Color.Gray);
                return;
            }

            _state.ForeColor = Color.Red;
            if (wasEstablished)
            {
                _state.Text = "상태: ⚠ 서버 연결 종료됨";
                _display.ShowNotice("⚠ 서버 연결이 종료되었습니다.\n\n상대가 송출을 중지했거나 네트워크가 끊겼습니다.\n[접속] 으로 다시 연결하세요.",
                    Color.OrangeRed);
            }
            else
            {
                _state.Text = "상태: ⚠ 접속 실패";
                _display.ShowNotice("⚠ 접속하지 못했습니다.\n\nIP·포트·비밀번호와 상대 서버 실행 여부를 확인하세요.",
                    Color.OrangeRed);
            }
            FlashTaskbar();
        }

        [DllImport("user32.dll")]
        private static extern bool FlashWindow(IntPtr hwnd, bool invert);

        private void FlashTaskbar()
        {
            try { if (!Focused) FlashWindow(Handle, true); } catch { }
        }

        private void PopulateMonitors(List<MonitorInfo> list)
        {
            _remoteMonitors = list;
            _monitorBox.Items.Clear();
            int sel = 0;
            for (int i = 0; i < list.Count; i++)
            {
                MonitorInfo m = list[i];
                _monitorBox.Items.Add("#" + (m.Index + 1) + " " + m.W + "x" + m.H + (m.Primary ? " (주)" : ""));
                if (m.Primary) sel = i;
            }
            if (list.Count > 1) _monitorBox.Items.Add("전체 화면");
            if (_monitorBox.Items.Count > 0) _monitorBox.SelectedIndex = sel;
        }

        private void OnMonitorPick()
        {
            if (_remoteMonitors == null || _monitorBox.SelectedIndex < 0) return;
            int idx = _monitorBox.SelectedIndex;
            int monIndex = (idx >= _remoteMonitors.Count) ? -1 : _remoteMonitors[idx].Index;
            _client.SelectMonitor(monIndex);
        }

        private void SetControl(bool on)
        {
            _display.ControlEnabled = on;
            if (_controlBtn.Checked != on) _controlBtn.Checked = on;
            _controlBtn.Text = on ? "원격 제어 ON" : "원격 제어 OFF";
            Theme.Accent(_controlBtn, on ? Theme.ToggleOn : Theme.Neutral);
            if (on) _display.Focus();
        }

        private void ForwardKey(KeyEventArgs e, bool down)
        {
            if (!_display.ControlEnabled || !_client.Connected) return;
            _client.Send(InputMsg.Key(down, e.KeyValue, IsExtended(e.KeyCode)));
            e.SuppressKeyPress = true;
            e.Handled = true;
        }

        private static bool IsExtended(Keys k)
        {
            switch (k)
            {
                case Keys.Left: case Keys.Right: case Keys.Up: case Keys.Down:
                case Keys.Home: case Keys.End: case Keys.PageUp: case Keys.PageDown:
                case Keys.Insert: case Keys.Delete: case Keys.NumLock:
                case Keys.RControlKey: case Keys.RMenu: case Keys.Divide:
                    return true;
                default: return false;
            }
        }

        private int ParsePort()
        {
            int p;
            if (int.TryParse(_portBox.Text.Trim(), out p) && p > 0 && p < 65536) return p;
            return Wire.DefaultPort;
        }

        private void SafeInvoke(Action a)
        {
            if (IsDisposed) return;
            try { BeginInvoke(a); } catch { }
        }
    }

}
