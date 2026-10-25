// WhaleQQ Watchdog - WinForms desktop app (C#, .NET Framework 4.8).
// Built with the in-box csc.exe; no external dependencies.
//
// Every 30 seconds it:
//   1. Detects the DeepSeek Harness desktop port (randomly assigned at every
//      launch) and rewrites config.json's dsh.baseUrl to match, then restarts
//      qq-bridge so it reconnects.
//   2. Relaunches SnowLuma if its WebUI/OneBot ports are all dead.
//   3. Relaunches qq-bridge if its console on :3100 is dead.
//   4. Relaunches the DSH app if it disappeared for more than 2 minutes.
//
// The port is re-checked on EVERY cycle (not only when it changes), so a
// hand-edited config.json is silently corrected.
//
// Note: this file is plain ASCII on purpose. The .NET Framework csc.exe that
// ships with Windows reads source files as the system ANSI codepage, which
// would mangle non-ASCII literals (and can even break string quoting).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Web.Script.Serialization;

namespace WhaleQQWatchdog
{
    internal static class Paths
    {
        // C:\WhaleQQ is an ASCII junction to the real (Chinese-named) project
        // folder. Scripts and this app must not embed the Chinese path.
        public const string Root = @"C:\WhaleQQ";
        public static readonly string BridgeDir = Path.Combine(Root, "qq-bridge");
        public static readonly string Cfg = Path.Combine(BridgeDir, "config.json");
        public static readonly string ToolsDir = Path.Combine(Root, "tools");
        public static readonly string LogFile = Path.Combine(ToolsDir, "watchdog.log");
        public static readonly string PauseFile = Path.Combine(ToolsDir, "watchdog.pause");
        public static readonly string PortHelper = Path.Combine(ToolsDir, "set-dsh-port.mjs");
        public static readonly string DshProbeScript = Path.Combine(ToolsDir, "probe-dsh-port.mjs");
        public static readonly string NodeBin = @"C:\Users\xt621\.dsh\dsh-runtimes\dsh-primary-runtime\dependencies\node\bin\node.exe";
        public const string SnowDir = @"C:\SnowLuma\app";
        public const string DshExe = @"C:\Users\xt621\Desktop\deepseek\DeepSeek Harness.exe";
        public const string DshDir = @"C:\Users\xt621\Desktop\deepseek";
        public const string TaskName = "WhaleQQ Watchdog";
        /// <summary>Single-instance lock: only one watchdog may run at a time.</summary>
        public static readonly string LockFile = Path.Combine(ToolsDir, "watchdog.lock");
    }

    internal sealed class LogEvent
    {
        public string Time;
        public string Text;
        public bool IsError;
        public bool IsAction;
    }

    internal sealed class WatchdogEngine
    {
        private readonly Action<string, bool, bool> _sink;   // (text, isError, isAction)
        private readonly object _logLock = new object();
        private readonly List<LogEvent> _entries = new List<LogEvent>();
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();

        // These are written by the worker thread and read by the UI thread, so
        // they must be volatile - otherwise the UI can show a stale value (or
        // never see the update at all). DateTime cannot be volatile (it is a
        // struct) and the old csc.exe rejects volatile long, so the timestamp
        // is kept as a volatile int holding seconds since midnight.
        public volatile int DshPort;
        public volatile bool BridgeUp;
        public volatile bool SnowlumaUp;
        public volatile bool DshAppUp;
        public volatile bool Paused;
        public volatile int ConfigPort;
        private volatile int _lastRunSeconds = -1;

        public DateTime LastRun
        {
            get
            {
                int s = _lastRunSeconds;
                if (s < 0) return DateTime.MinValue;
                return DateTime.Today.AddSeconds(s);
            }
            set
            {
                _lastRunSeconds = value == DateTime.MinValue
                    ? -1
                    : (int)(value.TimeOfDay.TotalSeconds);
            }
        }

        private int _lastDshPort;
        private DateTime _dshMissingSince = DateTime.MinValue;
        private int _tickCount;
        private int _bridgeUnhealthyRounds;

        public WatchdogEngine(Action<string, bool, bool> sink)
        {
            _sink = sink;
        }

        // ---------- logging ----------
        public void Log(string msg, bool isError, bool isAction)
        {
            lock (_logLock)
            {
                _entries.Add(new LogEvent
                {
                    Time = DateTime.Now.ToString("HH:mm:ss"),
                    Text = msg,
                    IsError = isError,
                    IsAction = isAction
                });
                if (_entries.Count > 500) _entries.RemoveRange(0, _entries.Count - 500);
            }
            try
            {
                if (File.Exists(Paths.LogFile) && new FileInfo(Paths.LogFile).Length > 5 * 1024 * 1024)
                {
                    File.Delete(Paths.LogFile + ".old");
                    File.Move(Paths.LogFile, Paths.LogFile + ".old");
                }
                File.AppendAllText(Paths.LogFile,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + msg + Environment.NewLine,
                    Encoding.UTF8);
            }
            catch { }
            _sink(msg, isError, isAction);
        }

        public List<LogEvent> Snapshot()
        {
            lock (_logLock) return new List<LogEvent>(_entries);
        }

        // ---------- helpers ----------
        public static bool PortOpen(int port, int timeoutMs)
        {
            timeoutMs = timeoutMs <= 0 ? 1200 : timeoutMs;
            try
            {
                using (TcpClient c = new TcpClient())
                {
                    IAsyncResult ar = c.BeginConnect(IPAddress.Loopback, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) return false;
                    c.EndConnect(ar);
                    return c.Connected;
                }
            }
            catch { return false; }
        }

        private static void RunPowerShell(string script)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("powershell.exe",
                    "-NoProfile -NonInteractive -Command \"" + script.Replace("\"", "\\\"") + "\"");
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                using (Process p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    p.WaitForExit(15000);
                }
            }
            catch { }
        }

        /// <summary>
        /// Find the DSH desktop API port.
        ///
        /// The DSH process can listen on SEVERAL ports at once (observed:
        /// 8326, 55525, 61800) - only one of them serves the JSON API
        /// (the others answer 401 with a different auth scope, or 404).
        ///
        /// The earlier version picked the HIGHEST listening port, which
        /// picked a dead 404 port and left the bridge pointed at garbage.
        /// Now every candidate is probed against a real RPC endpoint and the
        /// one that answers properly wins.
        /// </summary>
        public int FindDshPort()
        {
            List<int> candidates = GetDshCandidatePorts();
            if (candidates.Count == 0) return 0;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Paths.NodeBin, "\"" + Paths.DshProbeScript + "\"");
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.RedirectStandardInput = true;
                psi.CreateNoWindow = true;
                using (Process p = Process.Start(psi))
                {
                    for (int i = 0; i < candidates.Count; i++)
                        p.StandardInput.WriteLine(candidates[i].ToString());
                    p.StandardInput.Close();
                    string outp = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(25000);
                    int n;
                    if (int.TryParse(outp.Trim(), out n) && n > 0) return n;
                }
            }
            catch { }
            return 0;
        }

        /// <summary>All ports the DSH processes are listening on, highest first.</summary>
        public List<int> GetDshCandidatePorts()
        {
            List<int> result = new List<int>();
            try
            {
                string script =
                    "$ids = @(Get-Process -Name 'DeepSeek Harness' -ErrorAction SilentlyContinue | ForEach-Object { $_.Id }); " +
                    "if ($ids.Count -eq 0) { exit 0 }; " +
                    "Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | " +
                    "Where-Object { $ids -contains $_.OwningProcess } | " +
                    "Select-Object -ExpandProperty LocalPort -Unique";
                ProcessStartInfo psi = new ProcessStartInfo("powershell.exe",
                    "-NoProfile -NonInteractive -Command \"" + script.Replace("\"", "\\\"") + "\"");
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.CreateNoWindow = true;
                using (Process p = Process.Start(psi))
                {
                    string outp = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(10000);
                    string[] lines = outp.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        int n;
                        if (int.TryParse(lines[i].Trim(), out n) && n > 0) result.Add(n);
                    }
                }
            }
            catch { }
            result.Sort();
            result.Reverse();
            return result;
        }

        /// <summary>
        /// True when the port really serves the DSH JSON API.
        /// (Kept for single-port checks; the multi-port path uses the Node
        /// probe script so only one process is spawned per round.)
        /// </summary>
        public bool ProbeDshApi(int port)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Paths.NodeBin, "\"" + Paths.DshProbeScript + "\"");
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.RedirectStandardInput = true;
                psi.CreateNoWindow = true;
                using (Process p = Process.Start(psi))
                {
                    p.StandardInput.WriteLine(port.ToString());
                    p.StandardInput.Close();
                    string outp = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(25000);
                    int n;
                    return int.TryParse(outp.Trim(), out n) && n == port;
                }
            }
            catch { return false; }
        }

        public bool DshAppRunning()
        {
            try { return Process.GetProcessesByName("DeepSeek Harness").Length > 0; }
            catch { return false; }
        }

        /// <summary>
        /// Read-only probe of the ports that matter. The UI must never call
        /// this directly: it spawns PowerShell, and doing that on the UI thread
        /// froze the window (AppHangB1 -> Windows killed the process).
        /// </summary>
        public void RefreshStatus()
        {
            BridgeUp = PortOpen(3100, 800);
            SnowlumaUp = PortOpen(5099, 800) || PortOpen(3000, 800) || PortOpen(3001, 800);
            DshAppUp = DshAppRunning();
            ConfigPort = ReadConfiguredPort();
        }

        public int ReadConfiguredPort()
        {
            try
            {
                var root = _json.DeserializeObject(File.ReadAllText(Paths.Cfg)) as Dictionary<string, object>;
                if (root == null) return 0;
                object dshObj;
                if (!root.TryGetValue("dsh", out dshObj)) return 0;
                var dsh = dshObj as Dictionary<string, object>;
                if (dsh == null) return 0;
                object urlObj;
                if (!dsh.TryGetValue("baseUrl", out urlObj)) return 0;
                string url = Convert.ToString(urlObj);
                int idx = url.LastIndexOf(':');
                if (idx < 0) return 0;
                int n;
                return int.TryParse(url.Substring(idx + 1).TrimEnd('/'), out n) ? n : 0;
            }
            catch { return 0; }
        }

        public bool UpdateConfigPort(int port)
        {
            try
            {
                if (!File.Exists(Paths.PortHelper))
                {
                    var root = _json.DeserializeObject(File.ReadAllText(Paths.Cfg)) as Dictionary<string, object>;
                    if (root == null) return false;
                    object dshObj;
                    if (!root.TryGetValue("dsh", out dshObj)) dshObj = null;
                    var dsh = dshObj as Dictionary<string, object>;
                    if (dsh == null) { dsh = new Dictionary<string, object>(); root["dsh"] = dsh; }
                    dsh["baseUrl"] = "http://127.0.0.1:" + port;
                    File.WriteAllText(Paths.Cfg, _json.Serialize(root), new UTF8Encoding(false));
                    return true;
                }
                ProcessStartInfo psi = new ProcessStartInfo(Paths.NodeBin,
                    "\"" + Paths.PortHelper + "\" \"" + Paths.Cfg + "\" " + port);
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                using (Process p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    p.WaitForExit(15000);
                    return p.ExitCode == 0;
                }
            }
            catch (Exception e)
            {
                Log("rewrite config.json failed: " + e.Message, true, false);
                return false;
            }
        }

        public void KillBridge()
        {
            try
            {
                string script =
                    "$ids = @(Get-CimInstance Win32_Process -Filter \"Name='node.exe'\" | " +
                    "Where-Object { $_.CommandLine -like '*bridge.js*' -and $_.CommandLine -notlike '*mcp-*' -and $_.CommandLine -notlike '*node_modules*' } | " +
                    "ForEach-Object { $_.ProcessId }); " +
                    "$ids | ForEach-Object { Stop-Process -Id $_ -Force -ErrorAction SilentlyContinue }; " +
                    "Get-CimInstance Win32_Process -Filter \"Name='cmd.exe'\" | " +
                    "Where-Object { $_.CommandLine -like '*start.bat*' } | " +
                    "ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }";
                RunPowerShell(script);
                Thread.Sleep(1500);
                try { File.Delete(Path.Combine(Paths.BridgeDir, "state", "bridge.lock")); } catch { }
            }
            catch (Exception e) { Log("stop bridge failed: " + e.Message, true, false); }
        }

        public void StartBridge()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c start.bat");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WorkingDirectory = Paths.BridgeDir;
                Process.Start(psi);
            }
            catch (Exception e) { Log("start bridge failed: " + e.Message, true, false); }
        }

        public void StartSnowluma()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(
                    Path.Combine(Paths.SnowDir, "node.exe"),
                    "\"" + Path.Combine(Paths.SnowDir, "index.mjs") + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WorkingDirectory = Paths.SnowDir;
                Process.Start(psi);
            }
            catch (Exception e) { Log("start SnowLuma failed: " + e.Message, true, false); }
        }

        public void StartDshApp()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Paths.DshExe, "");
                psi.UseShellExecute = true;
                psi.WorkingDirectory = Paths.DshDir;
                Process.Start(psi);
            }
            catch (Exception e) { Log("start DSH failed: " + e.Message, true, false); }
        }

        // ---------- the main cycle ----------
        public void Tick()
        {
            Paused = File.Exists(Paths.PauseFile);
            if (Paused)
            {
                // Paused = no intervention, but keep OBSERVING so the UI shows
                // the real state (and the user can see the port drift). Only
                // the write actions (rewrite config, restart processes) are
                // skipped.
                int observed = FindDshPort();
                DshPort = observed;
                ConfigPort = ReadConfiguredPort();
                RefreshStatus();
                if (observed > 0 && ConfigPort != observed)
                {
                    Log("paused: DSH api port is " + observed + " but config says " +
                        ConfigPort + " - not fixing while paused", true, false);
                }
                LastRun = DateTime.Now;
                return;
            }

            // 1) DSH port tracking - compare every cycle, not just on change.
            int port = FindDshPort();
            DshAppUp = DshAppRunning();
            if (port > 0)
            {
                DshPort = port;
                int cfgPort = ReadConfiguredPort();
                ConfigPort = cfgPort;
                if (cfgPort != port)
                {
                    if (UpdateConfigPort(port))
                    {
                        Log("DSH port " + port + " != config (" + (cfgPort == 0 ? "none" : cfgPort.ToString()) +
                            ") - config.json updated, restarting bridge", false, true);
                        KillBridge();
                        StartBridge();
                    }
                    else
                    {
                        Log("DSH port " + port + ": could not update config.json", true, false);
                    }
                }
                else if (port != _lastDshPort)
                {
                    Log("DSH port " + port + " confirmed (config matches)", false, false);
                }
                _lastDshPort = port;
                _dshMissingSince = DateTime.MinValue;
            }
            else
            {
                DshPort = 0;
                if (_lastDshPort != 0)
                {
                    Log("DSH unreachable (closed or restarting) - waiting", true, false);
                    _lastDshPort = 0;
                }
                // 4) whole DSH app gone for more than 2 min -> relaunch it.
                if (!DshAppUp)
                {
                    if (_dshMissingSince == DateTime.MinValue) _dshMissingSince = DateTime.UtcNow;
                    double gone = (DateTime.UtcNow - _dshMissingSince).TotalSeconds;
                    if (gone >= 120)
                    {
                        Log("DSH app absent for " + ((int)gone) + "s - launching it", false, true);
                        _dshMissingSince = DateTime.MinValue;
                        StartDshApp();
                        Thread.Sleep(15000);
                    }
                }
                else
                {
                    _dshMissingSince = DateTime.MinValue;
                }
            }

            // 2) SnowLuma
            bool snow = PortOpen(5099, 1200) || PortOpen(3000, 1200) || PortOpen(3001, 1200);
            SnowlumaUp = snow;
            if (!snow)
            {
                Log("SnowLuma is down - starting it", false, true);
                StartSnowluma();
                Thread.Sleep(8000);
            }

            // 3) bridge
            bool bridge = PortOpen(3100, 1200);
            BridgeUp = bridge;
            if (!bridge)
            {
                Log("bridge console (:3100) is down - restarting bridge", false, true);
                _bridgeUnhealthyRounds = 0;
                KillBridge();
                StartBridge();
                Thread.Sleep(8000);
            }
            else
            {
                // 3b) The bridge console is listening, but it may still be
                // pointed at a dead DSH port - that is the "connected but
                // nothing works" case the user reported. Ask the bridge
                // itself whether DSH is ready.
                if (port > 0)
                {
                    bool ready = BridgeDshReady();
                    if (!ready)
                    {
                        _bridgeUnhealthyRounds++;
                        if (_bridgeUnhealthyRounds >= 2)
                        {
                            Log("bridge is up but DSH is not ready (round " + _bridgeUnhealthyRounds +
                                ") - restarting bridge", false, true);
                            _bridgeUnhealthyRounds = 0;
                            KillBridge();
                            StartBridge();
                            Thread.Sleep(8000);
                        }
                    }
                    else if (_bridgeUnhealthyRounds > 0)
                    {
                        Log("bridge reports DSH ready again", false, true);
                        _bridgeUnhealthyRounds = 0;
                    }
                }
            }

            _tickCount++;
            // Heartbeat every 6th round (3 min) - enough to prove the loop is
            // alive without flooding the log file.
            if (_tickCount % 6 == 0)
            {
                Log("heartbeat #" + _tickCount + " - DSH api port " + port +
                    ", config " + ConfigPort + ", bridge " + (BridgeUp ? "up" : "down") +
                    ", snowluma " + (SnowlumaUp ? "up" : "down"), false, false);
            }

            LastRun = DateTime.Now;
        }

        /// <summary>
        /// Ask the bridge console whether its DSH link is healthy.
        ///
        /// The response is pretty-printed JSON ("dshReady": true - with a
        /// space after the colon), so a naive substring match on
        /// '"dshReady":true' NEVER matches and made the watchdog restart a
        /// perfectly healthy bridge every single round. Parse the JSON
        /// properly instead.
        /// </summary>
        public bool BridgeDshReady()
        {
            try
            {
                string token = "";
                string tf = Path.Combine(Paths.BridgeDir, "state", "console-token");
                if (File.Exists(tf)) token = File.ReadAllText(tf).Trim();
                string url = "http://127.0.0.1:3100/api/status";
                if (token.Length > 0) url += "?token=" + token;
                // HttpWebRequest (not WebClient) because it has real timeouts -
                // without them a hung bridge would block the worker thread
                // forever and the watchdog would silently stop working.
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Timeout = 8000;
                req.ReadWriteTimeout = 8000;
                if (token.Length > 0) req.Headers.Add("x-console-token", token);
                string json;
                using (StreamReader sr = new StreamReader(req.GetResponse().GetResponseStream()))
                {
                    json = sr.ReadToEnd();
                }
                object parsed = _json.DeserializeObject(json);
                var map = parsed as Dictionary<string, object>;
                if (map == null) return true;      // cannot tell -> do not restart
                object v;
                if (!map.TryGetValue("dshReady", out v)) return true;
                return Convert.ToBoolean(v);
            }
            catch { return true; }   // cannot tell -> do not restart
        }
    }

    internal sealed class MainForm : Form
    {
        private readonly WatchdogEngine _engine;
        private readonly ListBox _list = new ListBox();
        private Label _lblDsh;
        private Label _lblCfg;
        private Label _lblBridge;
        private Label _lblSnow;
        private Label _lblState;
        private Label _lblPaused;
        private readonly Button _btnPause = new Button();
        private readonly Button _btnCheck = new Button();
        private readonly Button _btnOpenConsole = new Button();
        private readonly Button _btnRestartBridge = new Button();
        private readonly NotifyIcon _tray = new NotifyIcon();
        private readonly System.Windows.Forms.Timer _uiTimer = new System.Windows.Forms.Timer();
        private bool _pausedByUser;
        private Thread _worker;
        private volatile bool _workerRunning = true;
        private volatile bool _tickRequested = false;

        public MainForm()
        {
            _engine = new WatchdogEngine(OnLog);
            _lblDsh = new Label();
            _lblCfg = new Label();
            _lblBridge = new Label();
            _lblSnow = new Label();
            _lblState = new Label();
            BuildUi();
            // The watchdog loop runs on its OWN thread. It spawns PowerShell and
            // starts processes; doing that on the UI thread froze the window
            // (Windows killed it with AppHangB1). The UI thread now only
            // renders whatever the worker has collected.
            _worker = new Thread(WorkerLoop);
            _worker.IsBackground = true;
            _worker.Start();
            // UI refresh: read-only render of the last snapshot.
            _uiTimer.Interval = 2000;
            _uiTimer.Tick += OnUiTimerTick;
            _uiTimer.Start();
            Shown += OnShown;
        }

        /// <summary>Background loop: does all the real work, never touches the UI.</summary>
        private void WorkerLoop()
        {
            try { _engine.Tick(); } catch (Exception ex) { _engine.Log("watchdog error: " + ex.Message, true, false); }
            while (_workerRunning)
            {
                // Wait for either the 30s period or an explicit "check now".
                for (int i = 0; i < 30 && _workerRunning; i++)
                {
                    Thread.Sleep(1000);
                    if (_tickRequested) { _tickRequested = false; break; }
                }
                if (!_workerRunning) break;
                try { _engine.Tick(); }
                catch (Exception ex) { _engine.Log("watchdog error: " + ex.Message, true, false); }
            }
        }

        private void OnUiTimerTick(object s, EventArgs e)
        {
            UpdateStats();
        }

        /// <summary>Ask the worker to run a check right now (from the UI thread).</summary>
        private void RequestTick()
        {
            _tickRequested = true;
        }

        private void OnShown(object s, EventArgs e)
        {
            UpdateStats();
        }

        private void BuildUi()
        {
            Text = "WhaleQQ Watchdog";
            ClientSize = new Size(840, 470);
            MinimumSize = new Size(640, 400);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9F);
            BackColor = Color.FromArgb(248, 250, 252);

            Panel header = new Panel();
            header.Dock = DockStyle.Top;
            header.Height = 140;
            header.BackColor = Color.White;

            Label title = new Label();
            title.Text = "WhaleQQ Watchdog";
            title.Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold);
            title.ForeColor = Color.FromArgb(15, 23, 42);
            title.AutoSize = true;
            title.Location = new Point(20, 12);
            header.Controls.Add(title);

            Label sub = new Label();
            sub.Text = "Auto DSH port tracking  |  keeps SnowLuma + qq-bridge alive";
            sub.ForeColor = Color.FromArgb(100, 116, 139);
            sub.AutoSize = true;
            sub.Location = new Point(22, 44);
            header.Controls.Add(sub);

            // A loud banner when the watchdog is paused. The user paused it by
            // accident once and then the DSH port changed and nothing was
            // fixed - the paused state has to be obvious, not buried in a log.
            _lblPaused = new Label();
            _lblPaused.Text = "PAUSED - not watching. Click Resume to re-enable.";
            _lblPaused.AutoSize = true;
            _lblPaused.Location = new Point(22, 66);
            _lblPaused.ForeColor = Color.White;
            _lblPaused.BackColor = Color.FromArgb(220, 38, 38);
            _lblPaused.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            _lblPaused.Padding = new Padding(6, 3, 6, 3);
            _lblPaused.Visible = false;
            header.Controls.Add(_lblPaused);

            _lblState.Text = "last check: -";
            _lblState.AutoSize = true;
            _lblState.Location = new Point(22, 92);
            _lblState.ForeColor = Color.FromArgb(71, 85, 105);
            header.Controls.Add(_lblState);

            // Four status tiles: caption on one line, value on the next.
            _lblDsh = MakeStat("DSH port", header, 20, 116);
            _lblCfg = MakeStat("config port", header, 185, 116);
            _lblBridge = MakeStat("bridge", header, 350, 116);
            _lblSnow = MakeStat("SnowLuma", header, 500, 116);

            Panel body = new Panel();
            body.Dock = DockStyle.Fill;
            body.Padding = new Padding(12);
            body.BackColor = Color.FromArgb(248, 250, 252);
            _list.Dock = DockStyle.Fill;
            _list.Font = new Font("Consolas", 9.5F);
            _list.BorderStyle = BorderStyle.FixedSingle;
            _list.BackColor = Color.White;
            _list.HorizontalScrollbar = true;
            body.Controls.Add(_list);

            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 52;
            bottom.BackColor = Color.White;

            _btnPause.Text = "Pause";
            _btnPause.Width = 100;
            _btnPause.Height = 30;
            _btnPause.Location = new Point(12, 11);
            _btnPause.Click += OnPauseClick;

            _btnCheck.Text = "Check now";
            _btnCheck.Width = 100;
            _btnCheck.Height = 30;
            _btnCheck.Location = new Point(120, 11);
            _btnCheck.Click += OnCheckClick;

            _btnOpenConsole.Text = "Open console";
            _btnOpenConsole.Width = 110;
            _btnOpenConsole.Height = 30;
            _btnOpenConsole.Location = new Point(228, 11);
            _btnOpenConsole.Click += OnOpenConsoleClick;

            _btnRestartBridge.Text = "Restart bridge";
            _btnRestartBridge.Width = 110;
            _btnRestartBridge.Height = 30;
            _btnRestartBridge.Location = new Point(346, 11);
            _btnRestartBridge.Click += OnRestartBridgeClick;

            bottom.Controls.Add(_btnPause);
            bottom.Controls.Add(_btnCheck);
            bottom.Controls.Add(_btnOpenConsole);
            bottom.Controls.Add(_btnRestartBridge);

            Controls.Add(body);
            Controls.Add(bottom);
            Controls.Add(header);

            _tray.Icon = SystemIcons.Application;
            _tray.Text = "WhaleQQ Watchdog";
            _tray.Visible = true;
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("Show window", null, OnTrayShow);
            menu.Items.Add("Open bridge console", null, OnOpenConsoleClick);
            menu.Items.Add("Check now", null, OnCheckClick);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit (stop watching)", null, OnTrayExit);
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += OnTrayShow;
        }

        private Label MakeStat(string caption, Control parent, int x, int y)
        {
            Label cap = new Label();
            cap.Text = caption;
            cap.AutoSize = true;
            cap.Location = new Point(x, y);
            cap.ForeColor = Color.FromArgb(148, 163, 184);
            cap.Font = new Font("Microsoft YaHei UI", 8F);
            parent.Controls.Add(cap);

            Label val = new Label();
            val.Text = "-";
            val.AutoSize = true;
            val.Location = new Point(x, y + 18);
            val.ForeColor = Color.FromArgb(15, 23, 42);
            val.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
            parent.Controls.Add(val);
            return val;
        }

        private void OnPauseClick(object s, EventArgs e)
        {
            try
            {
                if (_pausedByUser)
                {
                    if (File.Exists(Paths.PauseFile)) File.Delete(Paths.PauseFile);
                    _pausedByUser = false;
                    _btnPause.Text = "Pause";
                    _engine.Log("watching resumed", false, true);
                }
                else
                {
                    File.WriteAllText(Paths.PauseFile, DateTime.Now.ToString());
                    _pausedByUser = true;
                    _btnPause.Text = "Resume";
                    _engine.Log("watching paused (no automatic intervention)", false, true);
                }
            }
            catch (Exception ex) { _engine.Log("toggle pause failed: " + ex.Message, true, false); }
        }

        private void OnCheckClick(object s, EventArgs e)
        {
            _btnCheck.Enabled = false;
            _engine.Log("manual check requested", false, true);
            RequestTick();
            // Re-enable shortly after; the worker does the actual work.
            ThreadPool.QueueUserWorkItem(delegate
            {
                Thread.Sleep(1500);
                try
                {
                    if (IsHandleCreated)
                    {
                        BeginInvoke(new Action(delegate { _btnCheck.Enabled = true; UpdateStats(); }));
                    }
                }
                catch { }
            });
        }

        private void OnOpenConsoleClick(object s, EventArgs e)
        {
            try
            {
                string token = "";
                string tf = Path.Combine(Paths.BridgeDir, "state", "console-token");
                if (File.Exists(tf)) token = File.ReadAllText(tf).Trim();
                string url = "http://127.0.0.1:3100/";
                if (token.Length > 0) url += "?token=" + token;
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex) { _engine.Log("open console failed: " + ex.Message, true, false); }
        }

        private void OnRestartBridgeClick(object s, EventArgs e)
        {
            _engine.Log("manual bridge restart", false, true);
            // Kill/start spawns processes; keep it off the UI thread.
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { _engine.KillBridge(); _engine.StartBridge(); }
                catch (Exception ex) { _engine.Log("manual restart failed: " + ex.Message, true, false); }
            });
        }

        private void OnTrayShow(object s, EventArgs e)
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        private void OnTrayExit(object s, EventArgs e)
        {
            _workerRunning = false;
            _uiTimer.Stop();
            _tray.Visible = false;
            Application.Exit();
        }

        private void OnLog(string text, bool isError, bool isAction)
        {
            try
            {
                // Called from the worker thread; marshal to the UI thread. If the
                // window is closing the handle may already be gone - then the
                // log line still went to the file, so just drop it.
                if (IsHandleCreated && InvokeRequired)
                {
                    BeginInvoke(new Action<string, bool, bool>(OnLog), text, isError, isAction);
                    return;
                }
                string prefix = isAction ? ">> " : (isError ? "!! " : "   ");
                _list.Items.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + prefix + text);
                _list.SelectedIndex = _list.Items.Count - 1;
                _list.TopIndex = Math.Max(0, _list.Items.Count - 1);
            }
            catch { }
        }

        /// <summary>Refresh the status tiles from the engine's last known state.</summary>
        private void UpdateStats()
        {
            try
            {
                if (InvokeRequired) { BeginInvoke(new Action(UpdateStats)); return; }
                // NOTE: no probing here. The UI thread only renders the values
                // the worker thread has already collected - probing spawns
                // PowerShell and would freeze the window.
                _lblDsh.Text = _engine.DshPort > 0 ? _engine.DshPort.ToString() : "not running";
                _lblDsh.ForeColor = _engine.DshPort > 0 ? Color.FromArgb(22, 163, 74) : Color.FromArgb(220, 38, 38);

                _lblCfg.Text = _engine.ConfigPort > 0 ? _engine.ConfigPort.ToString() : "-";
                bool same = _engine.DshPort > 0 && _engine.DshPort == _engine.ConfigPort;
                _lblCfg.ForeColor = same ? Color.FromArgb(22, 163, 74) : Color.FromArgb(220, 38, 38);

                _lblBridge.Text = _engine.BridgeUp ? "running" : "stopped";
                _lblBridge.ForeColor = _engine.BridgeUp ? Color.FromArgb(22, 163, 74) : Color.FromArgb(220, 38, 38);

                _lblSnow.Text = _engine.SnowlumaUp ? "running" : "stopped";
                _lblSnow.ForeColor = _engine.SnowlumaUp ? Color.FromArgb(22, 163, 74) : Color.FromArgb(220, 38, 38);

                _lblState.Text = "last check: " +
                    (_engine.LastRun == DateTime.MinValue ? "-" : _engine.LastRun.ToString("HH:mm:ss")) +
                    "    next: " + DateTime.Now.AddSeconds(30).ToString("HH:mm:ss");

                // Paused state must be impossible to miss.
                bool paused = _engine.Paused;
                _lblPaused.Visible = paused;
                _btnPause.Text = paused ? "Resume" : "Pause";
                if (paused)
                {
                    _lblState.ForeColor = Color.FromArgb(220, 38, 38);
                    _lblState.Text = "PAUSED - DSH port changes will NOT be tracked. " + _lblState.Text;
                }
                else
                {
                    _lblState.ForeColor = Color.FromArgb(71, 85, 105);
                }
            }
            catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                _tray.ShowBalloonTip(3000, "WhaleQQ Watchdog",
                    "Minimized to tray - watching continues.", ToolTipIcon.Info);
                return;
            }
            base.OnFormClosing(e);
        }
    }

    internal static class Program
    {
        // The display is scaled (1707x1067 logical on a 1920x1080 panel).
        // Without explicit Per-Monitor DPI awareness WinForms gets scaled by
        // the system and the layout overflows the window.
        [DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        private static void EnableDpiAwareness()
        {
            try
            {
                // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4
                if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return;
            }
            catch { }
            try { SetProcessDPIAware(); }
            catch { }
        }

        [STAThread]
        private static void Main(string[] args)
        {
            EnableDpiAwareness();

            // Single-instance guard. Without it, starting the app from the
            // desktop shortcut while the scheduled task instance is already
            // running gives you two watchdogs fighting over the same
            // config.json and bridge process.
            int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
            try
            {
                if (File.Exists(Paths.LockFile))
                {
                    string raw = File.ReadAllText(Paths.LockFile).Trim();
                    int other;
                    if (int.TryParse(raw, out other) && other != pid)
                    {
                        Process running = null;
                        try { running = Process.GetProcessById(other); } catch { }
                        if (running != null)
                        {
                            MessageBox.Show(
                                "WhaleQQ Watchdog is already running (PID " + other + ").\n\n" +
                                "Use the tray icon to control it, or close the other instance first.",
                                "WhaleQQ Watchdog", MessageBoxButtons.OK, MessageBoxIcon.Information);
                            return;
                        }
                    }
                }
                File.WriteAllText(Paths.LockFile, pid.ToString());
            }
            catch { }

            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            try { File.Delete(Paths.LockFile); } catch { }
        }
    }
}
