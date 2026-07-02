// ScoutZh.cs —— Scout Loader（.NET Framework 版，语言无关的通用加载器，无需 Node）
//
// 这是 inject.mjs 的 C# 移植：用 --remote-debugging-port 拉起已安装的 Microsoft Scout，
// 通过 Chrome DevTools Protocol (CDP) 把 overlay 引擎 + 语言字典注入渲染进程，界面即变目标语言。
// 原程序 app.asar / exe 一个字节都不改 —— 数字签名与完整性校验保持完好。
//
// 设计：exe 只内嵌“引擎”（overlay-engine.js，语言无关的机器逻辑），**不内嵌任何字库**。
// 字库是独立的语言包文件，放在 exe 外面，命名 dictionary.<语言标签>.json（如 dictionary.zh-CN.json）。
// 这样 exe 是纯粹的通用加载器：想做别的语言，只需另放一个语言包并用 --lang 指定，exe 无需重编译。
//
// 为什么用 .NET Framework：
//   Windows 10/11 自带 .NET Framework 4.x（本机 4.8），csc.exe 也随框架附带在
//   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\ 下。所以编译与运行都不需要装
//   任何 SDK / 运行时 / Node —— 客户机只要装了 Scout（Win10+）就能直接跑本 exe。
//
// 用法：
//   "Scout Loader.exe"                双击即用：无窗口后台常驻（回收漏翻 + 页面重载后自动重注入）；默认语言 zh-CN
//                                     再次双击 = 关闭汉化。从命令行运行则复用终端显示日志，可 Ctrl+C 退出。
//   "Scout Loader.exe" --lang ja-JP   指定语言包（加载 dictionary.ja-JP.json）
//   "Scout Loader.exe" --dict path     直接指定字库文件路径（优先级最高）
//   "Scout Loader.exe" --once          只注入一次就退出（不常驻、不回收漏翻）
//   "Scout Loader.exe" --capture       只抓取首页可见文字（用于建/扩字典），不注入
//   "Scout Loader.exe" --port 9333     自定义调试端口（默认 9222）
//   环境变量 SCOUT_EXE 可覆盖 Scout 可执行文件路径
//
// 编译为 winexe（Windows 子系统）：双击不弹控制台黑框。无窗口时日志写入 exe 同目录的
// scout-loader.log；从终端启动时用 AttachConsole 复用父控制台，日志照常打印。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Scout Loader")]
[assembly: AssemblyProduct("Scout Loader")]
[assembly: AssemblyDescription("Microsoft Scout 运行时本地化加载器——DOM 覆盖式翻译，不改动原程序（保持数字签名完整）")]
[assembly: AssemblyCompany("Scout Loader")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

internal static class ScoutZh
{
    private const string Tag = "[Scout Loader] ";
    private const string DefaultLang = "zh-CN";
    // 命名事件：常驻实例创建并等待它；再次启动 exe 时另一进程 Set 它 = 让已在跑的实例把窗口显示出来。
    private const string ShowEventName = "ScoutLoaderShowWindowEvent";
    private static int _port = 9222;
    private static string _lang = DefaultLang;
    private static string _dictPath; // 由 --dict 指定时非空
    private static TrayContext _tray;  // 常驻模式下的系统托盘上下文（非空即有 UI）

    // winexe 子系统不会自动接管父终端的控制台。若从命令行启动，我们 AttachConsole 复用它，
    // 日志照常显示；若是双击（无父控制台），日志改写到 exe 同目录的 scout-loader.log，
    // 并同时投递到托盘的日志窗口。
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);
    private const int AttachParentProcess = -1;
    private static bool _consoleAttached;
    private static readonly object _logLock = new object();

    private static string LogFilePath()
    {
        return Path.Combine(BaseDir(), "scout-loader.log");
    }

    internal static void Log(string msg)
    {
        var line = Tag + msg;
        if (_consoleAttached)
        {
            try { Console.WriteLine(line); } catch { }
        }
        try
        {
            lock (_logLock)
                File.AppendAllText(LogFilePath(),
                    DateTime.Now.ToString("HH:mm:ss ") + line + Environment.NewLine,
                    new UTF8Encoding(false));
        }
        catch { /* 日志文件写不了就算了，不影响主流程 */ }

        // 投递到托盘日志窗口（若有）。
        try { if (_tray != null) _tray.AppendLog(DateTime.Now.ToString("HH:mm:ss ") + line); } catch { }
    }

    private static string BaseDir()
    {
        return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
    }

    // 读取内嵌的引擎（语言无关机器逻辑）。优先编译时嵌入的资源，
    // 回退到 exe 同目录的磁盘文件（开发时改引擎无需重编译）。
    private static string LoadEngine(string name)
    {
        var asm = Assembly.GetExecutingAssembly();
        using (var s = asm.GetManifestResourceStream(name))
        {
            if (s != null)
                using (var r = new StreamReader(s, Encoding.UTF8))
                    return r.ReadToEnd();
        }
        var disk = Path.Combine(BaseDir(), name);
        if (File.Exists(disk)) return File.ReadAllText(disk, Encoding.UTF8);
        throw new FileNotFoundException("找不到内嵌引擎（既非嵌入亦无同目录文件）：" + name);
    }

    // 解析外部语言包文件路径（字库不内嵌，独立于 exe）。解析顺序：
    //   1) --dict <path> 显式指定（最高优先）
    //   2) --lang <tag> → 依次找 <exe目录>\dictionary.<tag>.json、<exe目录>\lang\dictionary.<tag>.json
    // 找不到则抛出带“查找位置 + 可用语言包清单”的友好错误。
    private static string ResolveDictPath()
    {
        if (!string.IsNullOrEmpty(_dictPath))
        {
            if (File.Exists(_dictPath)) return _dictPath;
            throw new FileNotFoundException("--dict 指定的字库文件不存在：" + _dictPath);
        }

        var dir = BaseDir();
        var fileName = "dictionary." + _lang + ".json";
        var candidates = new[]
        {
            Path.Combine(dir, fileName),
            Path.Combine(dir, "lang", fileName),
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return c;

        var available = ListAvailableLangs(dir);
        var hint = available.Count > 0
            ? "可用语言包：" + string.Join(", ", available)
            : "未在 exe 目录（或 lang 子目录）找到任何 dictionary.<语言>.json 语言包。";
        throw new FileNotFoundException(
            "找不到语言包 " + fileName + "。已查找：\n  " + string.Join("\n  ", candidates) + "\n" + hint +
            "\n提示：把语言包（如 dictionary.zh-CN.json）与本 exe 放同一目录，或用 --dict 指定路径。");
    }

    // 列出 exe 目录及 lang 子目录里所有 dictionary.<tag>.json 的语言标签，供错误提示。
    private static List<string> ListAvailableLangs(string dir)
    {
        var langs = new List<string>();
        foreach (var d in new[] { dir, Path.Combine(dir, "lang") })
        {
            if (!Directory.Exists(d)) continue;
            foreach (var f in Directory.GetFiles(d, "dictionary.*.json"))
            {
                var n = Path.GetFileName(f); // dictionary.<tag>.json
                var tag = n.Substring("dictionary.".Length, n.Length - "dictionary.".Length - ".json".Length);
                if (tag.Length > 0 && !tag.Contains(".") && !langs.Contains(tag)) langs.Add(tag);
            }
        }
        return langs;
    }

    // 定位 Microsoft Scout 主程序。支持两种安装方式：
    //   - 个人安装（仅当前用户）：登记在 HKCU 的卸载项，exe 在 %LOCALAPPDATA%\Programs\...
    //   - 所有人安装（per-machine）：登记在 HKLM（64/32 视图），exe 在 %ProgramFiles%\...
    // electron-builder 的 NSIS 安装器会在卸载项写 DisplayIcon（指向主 exe），这是最可靠的锚点；
    // InstallLocation 有时为空，所以优先用 DisplayIcon。查不到再兜底默认路径。
    private static string ScoutExePath()
    {
        var env = Environment.GetEnvironmentVariable("SCOUT_EXE");
        if (!string.IsNullOrEmpty(env)) return env;

        var fromReg = FindScoutInRegistry();
        if (fromReg != null) return fromReg;

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft Scout", "Microsoft Scout.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft Scout", "Microsoft Scout.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Scout", "Microsoft Scout.exe"),
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return c;
        return candidates[0]; // 都没有：返回个人版默认路径，让上层抛出带路径的"未找到"
    }

    // 遍历注册表卸载项（HKCU + HKLM 的 64/32 视图）找 Microsoft Scout，返回主 exe 路径。
    private static string FindScoutInRegistry()
    {
        var views = new[]
        {
            new KeyValuePair<RegistryHive, RegistryView>(RegistryHive.CurrentUser, RegistryView.Registry64),
            new KeyValuePair<RegistryHive, RegistryView>(RegistryHive.LocalMachine, RegistryView.Registry64),
            new KeyValuePair<RegistryHive, RegistryView>(RegistryHive.LocalMachine, RegistryView.Registry32),
        };
        const string uninstall = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
        foreach (var v in views)
        {
            try
            {
                using (var baseKey = RegistryKey.OpenBaseKey(v.Key, v.Value))
                using (var unin = baseKey.OpenSubKey(uninstall))
                {
                    if (unin == null) continue;
                    foreach (var name in unin.GetSubKeyNames())
                    {
                        using (var k = unin.OpenSubKey(name))
                        {
                            if (k == null) continue;
                            var dn = k.GetValue("DisplayName") as string;
                            if (dn == null || dn.IndexOf("Microsoft Scout", StringComparison.OrdinalIgnoreCase) < 0) continue;

                            // DisplayIcon 形如 "C:\...\Microsoft Scout.exe,0" —— 去掉图标索引与引号。
                            var icon = k.GetValue("DisplayIcon") as string;
                            if (!string.IsNullOrEmpty(icon))
                            {
                                var p = icon.Split(',')[0].Trim().Trim('"');
                                if (p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(p)) return p;
                            }
                            var loc = k.GetValue("InstallLocation") as string;
                            if (!string.IsNullOrEmpty(loc))
                            {
                                var p = Path.Combine(loc.Trim().Trim('"'), "Microsoft Scout.exe");
                                if (File.Exists(p)) return p;
                            }
                        }
                    }
                }
            }
            catch
            {
                /* 某个视图读不到就跳过 */
            }
        }
        return null;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        // 从命令行启动时复用父终端控制台（双击则无父控制台，保持无窗口，日志走文件 + 托盘窗口）。
        _consoleAttached = AttachConsole(AttachParentProcess);
        if (_consoleAttached)
        {
            try { Console.OutputEncoding = Encoding.UTF8; }
            catch { /* 某些宿主不支持切编码，忽略 */ }
        }

        bool capture = false, once = false;
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "--capture") capture = true;
            else if (a == "--once") once = true;
            else if (a == "--port" && i + 1 < args.Length) int.TryParse(args[++i], out _port);
            else if (a == "--lang" && i + 1 < args.Length) _lang = args[++i];
            else if (a == "--dict" && i + 1 < args.Length) _dictPath = args[++i];
        }

        bool resident = !capture && !once;

        // ---- 一次性任务（--once / --capture）：无 UI，直接跑完退出 ----
        if (!resident)
        {
            try
            {
                RunAsync(capture, once).GetAwaiter().GetResult();
                return 0;
            }
            catch (Exception e)
            {
                Log("失败：" + e.Message);
                if (_consoleAttached && !Console.IsInputRedirected)
                {
                    try
                    {
                        Console.Error.WriteLine("\n按任意键退出…");
                        Console.ReadKey(true);
                    }
                    catch { }
                }
                return 1;
            }
        }

        // ---- 常驻任务：跑到系统托盘（右下角），带日志窗口 ----
        // 若已有一个加载器在跑，本次启动只让它把窗口显示出来，然后自己退出（避免多开）。
        EventWaitHandle existing;
        if (EventWaitHandle.TryOpenExisting(ShowEventName, out existing))
        {
            existing.Set();
            existing.Dispose();
            return 0;
        }
        // 常驻模式每次启动重置日志文件，避免无限增长。
        try { File.WriteAllText(LogFilePath(), "", new UTF8Encoding(false)); } catch { }

        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            _tray = new TrayContext();

            // 常驻注入跑在专用后台线程上。真正的坑：这个线程用 GetResult() 阻塞等待，
            // 而 WinForms 会在本线程（经由 Control.InvokeRequired 等调用）自动装上
            // WindowsFormsSynchronizationContext；若 CDP async 链里的 await 捕获了它，
            // 续体就被投递回这个没有消息泵的阻塞线程 → ConnectAsync 永久死锁。
            // 根治办法：CDP/常驻链路里每个 await 都 .ConfigureAwait(false)，续体永远回线程池。
            var ct = _tray.Cancellation.Token;
            var worker = new Thread(() =>
            {
                try { RunResidentAsync(ct).GetAwaiter().GetResult(); }
                catch (Exception e)
                {
                    Log("失败：" + e.Message);
                    try { File.AppendAllText(LogFilePath(), e.ToString() + Environment.NewLine, new UTF8Encoding(false)); } catch { }
                    if (_tray != null) _tray.ShowError(e.Message);
                }
            });
            worker.IsBackground = true;
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();

            Application.Run(_tray);      // 阻塞直到托盘“退出”
            _tray.Cancellation.Cancel(); // 通知后台收尾
            try { worker.Join(4000); } catch { }
            return 0;
        }
        catch (Exception e)
        {
            Log("失败：" + e.Message);
            return 1;
        }
    }

    // ---- CDP 的 HTTP 端点：用 localhost（IP 会被 DNS-rebinding 保护拒绝）----
    private static string HttpGet(string path)
    {
        var req = (HttpWebRequest)WebRequest.Create("http://localhost:" + _port + path);
        req.Timeout = 4000;
        req.KeepAlive = false;
        using (var resp = (HttpWebResponse)req.GetResponse())
        using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
        {
            return sr.ReadToEnd();
        }
    }

    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

    private static async Task<Dictionary<string, object>> WaitForPageTarget(int timeoutMs = 40000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var raw = HttpGet("/json");
                var arr = Json.DeserializeObject(raw) as object[];
                if (arr != null)
                {
                    foreach (var item in arr)
                    {
                        var t = item as Dictionary<string, object>;
                        if (t == null) continue;
                        var type = t.ContainsKey("type") ? t["type"] as string : null;
                        var ws = t.ContainsKey("webSocketDebuggerUrl") ? t["webSocketDebuggerUrl"] as string : null;
                        var url = t.ContainsKey("url") ? t["url"] as string : "";
                        if (type == "page" && !string.IsNullOrEmpty(ws) && (url == null || !url.StartsWith("devtools://")))
                            return t;
                    }
                }
            }
            catch
            {
                /* 端口还没起来，继续等 */
            }
            await Task.Delay(800).ConfigureAwait(false);
        }
        throw new Exception("在超时时间内未找到可注入的页面目标");
    }

    // 把 overlay-engine.js 的占位符替换成真实字典；用 JSON.parse 包一层，运行时零解析风险。
    private static string BuildInjection()
    {
        var engine = LoadEngine("overlay-engine.js");        // 内嵌的语言无关引擎
        var dictJson = File.ReadAllText(ResolveDictPath(), Encoding.UTF8); // 外部语言包
        // Json.Serialize(string) 等价于 JS 的 JSON.stringify(字符串)，生成合法 JS 字符串字面量。
        var literal = Json.Serialize(dictJson);
        return engine.Replace("__ZH_DICT__", "JSON.parse(" + literal + ")");
    }

    private const string CaptureExpr = @"(function(){
  try {
    var out = [];
    var w = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT, null);
    var skip = {SCRIPT:1,STYLE:1,CODE:1,PRE:1,TEXTAREA:1,SVG:1};
    var seen = {};
    var n = w.nextNode();
    while (n) {
      var p = n.parentElement, bad = false, e = p;
      while (e) { if (skip[e.tagName] || e.isContentEditable) { bad = true; break; } e = e.parentElement; }
      var t = (n.nodeValue || '').trim();
      if (!bad && t.length >= 2 && t.length <= 80 && /[A-Za-z]/.test(t) && !seen[t]) { seen[t] = 1; out.push(t); }
      n = w.nextNode();
    }
    return JSON.stringify(out, null, 2);
  } catch (e) { return 'CAPTURE_ERR: ' + e.message; }
})()";

    // 探测 CDP 调试端口是否已开启（已开 = Scout 正以调试模式运行，可直接连）。
    private static bool IsDebugPortOpen()
    {
        try
        {
            HttpGet("/json/version");
            return true;
        }
        catch
        {
            return false;
        }
    }

    // 找出正在运行的 Microsoft Scout 进程（按主 exe 文件名匹配）。
    private static Process[] FindRunningScout(string exe)
    {
        try
        {
            var name = Path.GetFileNameWithoutExtension(exe); // "Microsoft Scout"
            return Process.GetProcessesByName(name);
        }
        catch
        {
            return new Process[0];
        }
    }

    // 确保 Scout 以调试模式运行并连上 CDP，返回已连接的 Cdp。
    private static async Task<Cdp> StartAndConnectAsync()
    {
        var exe = ScoutExePath();

        // 情况一：调试端口已经开着（例如本程序已经跑过一次、Scout 正以调试模式运行）。直接连。
        if (IsDebugPortOpen())
        {
            Log("检测到调试端口 " + _port + " 已开启，直接连接现有 Scout。");
        }
        else
        {
            // 情况二：Scout 已在运行，但没开调试端口。Electron 单实例锁会把我们新起的
            // “--remote-debugging-port” 实例转发给旧实例（旧实例没有调试端口），导致注入
            // 永远连不上。必须先关掉正在运行的 Scout，再由我们以调试模式重新拉起。
            var running = FindRunningScout(exe);
            if (running.Length > 0)
            {
                Log("检测到 Microsoft Scout 已在运行但未开调试端口，正在关闭以便以调试模式重启…");
                foreach (var p in running)
                {
                    try { p.CloseMainWindow(); } catch { }
                }
                await Task.Delay(1500).ConfigureAwait(false);
                foreach (var p in running)
                {
                    try { if (!p.HasExited) p.Kill(); } catch { }
                }
                await Task.Delay(1200).ConfigureAwait(false);
            }

            Log("启动 Microsoft Scout（调试端口 " + _port + "）…");
            Log("exe: " + exe);
            if (!File.Exists(exe))
                throw new Exception("未找到 Microsoft Scout：" + exe + "（可用环境变量 SCOUT_EXE 指定路径）");

            var psi = new ProcessStartInfo(exe, "--remote-debugging-port=" + _port)
            {
                UseShellExecute = true,
            };
            Process.Start(psi);
        }

        var page = await WaitForPageTarget().ConfigureAwait(false);
        Log("已连接页面：" + (page.ContainsKey("url") ? page["url"] : ""));

        var cdp = new Cdp((string)page["webSocketDebuggerUrl"], _port);
        await cdp.Connect().ConfigureAwait(false);
        await cdp.Send("Runtime.enable", null).ConfigureAwait(false);
        await cdp.Send("Page.enable", null).ConfigureAwait(false);
        return cdp;
    }

    // 把 overlay 引擎 + 字典注入页面（注入前清掉旧实例，确保是最新引擎）。
    private static async Task<string> InjectAsync(Cdp cdp, string injection)
    {
        await cdp.Evaluate(
            "try{window.__scoutZhActive&&window.__scoutZhActive.stop()}catch(e){}" +
            "try{delete window.__scoutZhActive;delete window.__scoutZhMisses}catch(e){}").ConfigureAwait(false);
        return await cdp.Evaluate(injection).ConfigureAwait(false);
    }

    // 一次性任务：--capture（抓首页文字）或 --once（注入一次即退出）。
    private static async Task RunAsync(bool capture, bool once)
    {
        var cdp = await StartAndConnectAsync().ConfigureAwait(false);

        if (capture)
        {
            await Task.Delay(3500).ConfigureAwait(false);
            var dump = await cdp.Evaluate(CaptureExpr).ConfigureAwait(false);
            Console.WriteLine("\n===== 首页可见文字 =====\n");
            Console.WriteLine(dump);
            return;
        }

        Log("语言：" + _lang + "，语言包：" + ResolveDictPath());
        var injection = BuildInjection();
        var result = await InjectAsync(cdp, injection).ConfigureAwait(false);
        Log("注入结果：" + result);
        Log("已注入（--once 模式，退出；Scout 仍在运行）。");
    }

    // 常驻任务：注入 + 页面重载后自动重注入 + 每 5 秒回收漏翻。由 CancellationToken 结束
    //（托盘菜单“退出”触发）。跑在后台线程，UI 消息循环由 Application.Run 维持。
    private static async Task RunResidentAsync(CancellationToken ct)
    {
        Cdp cdp = null;
        try
        {
            cdp = await StartAndConnectAsync().ConfigureAwait(false);

            Log("语言：" + _lang + "，语言包：" + ResolveDictPath());
            var injection = BuildInjection();
            var result = await InjectAsync(cdp, injection).ConfigureAwait(false);
            Log("注入结果：" + result);

            // 页面整体重载后（更新 / 路由硬刷新）重新注入。
            cdp.OnLoad = async () =>
            {
                try
                {
                    var r = await cdp.Evaluate(injection).ConfigureAwait(false);
                    Log("重载后重新注入：" + r);
                }
                catch (Exception e)
                {
                    Log("重新注入失败：" + e.Message);
                }
            };

            Log("翻译已生效。漏翻字符串每 5 秒回收一次并写入 missing." + _lang + ".json / missing." + _lang + ".skeleton.json。");
            Log("已最小化到系统托盘（右下角）。双击托盘图标可展开日志窗口；右键“退出”结束汉化（不影响 Scout）。");
            if (_tray != null) _tray.SetReady();

            var cdpRef = cdp;
            using (var timer = new System.Threading.Timer(async state => await Harvest(cdpRef, true).ConfigureAwait(false), null, 5000, 5000))
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    /* 用户退出 */
                }
            }

            await Harvest(cdp, false).ConfigureAwait(false);
            Log("已退出（Scout 仍在运行）。");
        }
        catch (Exception e)
        {
            Log("失败：" + e.Message);
            try { File.AppendAllText(LogFilePath(), e.ToString() + Environment.NewLine, new UTF8Encoding(false)); } catch { }
            if (_tray != null) _tray.ShowError(e.Message);
        }
    }

    // 把回收到的漏翻写入 missing.<lang>.json（{原文:次数}）与 missing.<lang>.skeleton.json
    //（排除当前语言包已有键，可直接补译进语言包）。日志按语言分文件，多语言并行不冲突。
    private static async Task Harvest(Cdp cdp, bool quiet)
    {
        try
        {
            var json = await cdp.Evaluate("JSON.stringify(window.__scoutZhMisses || {})").ConfigureAwait(false);
            var misses = Json.DeserializeObject(json ?? "{}") as Dictionary<string, object>;
            if (misses == null) return;

            var entries = new List<KeyValuePair<string, int>>();
            foreach (var kv in misses)
            {
                int c;
                int.TryParse(Convert.ToString(kv.Value), out c);
                entries.Add(new KeyValuePair<string, int>(kv.Key, c));
            }
            entries.Sort((a, b) => b.Value.CompareTo(a.Value));

            var missingFile = "missing." + _lang + ".json";
            var skeletonFile = "missing." + _lang + ".skeleton.json";

            var counts = new StringBuilder("{\n");
            for (int i = 0; i < entries.Count; i++)
                counts.Append("  ").Append(Json.Serialize(entries[i].Key)).Append(": ")
                      .Append(entries[i].Value).Append(i < entries.Count - 1 ? ",\n" : "\n");
            counts.Append("}\n");
            File.WriteAllText(Path.Combine(BaseDir(), missingFile), counts.ToString(), new UTF8Encoding(false));

            var dictKeys = new HashSet<string>();
            try
            {
                var dictRaw = File.ReadAllText(ResolveDictPath(), Encoding.UTF8);
                var dict = Json.DeserializeObject(dictRaw) as Dictionary<string, object>;
                if (dict != null) foreach (var k in dict.Keys) dictKeys.Add(k);
            }
            catch
            {
                /* 字典读不到就当空 */
            }

            var stub = new StringBuilder("{\n");
            var notInDict = entries.FindAll(e => !dictKeys.Contains(e.Key));
            for (int i = 0; i < notInDict.Count; i++)
                stub.Append("  ").Append(Json.Serialize(notInDict[i].Key)).Append(": \"\"")
                    .Append(i < notInDict.Count - 1 ? ",\n" : "\n");
            stub.Append("}\n");
            File.WriteAllText(Path.Combine(BaseDir(), skeletonFile), stub.ToString(), new UTF8Encoding(false));

            if (!quiet)
                Log("已写入漏翻日志：" + entries.Count + " 条（其中 " + notInDict.Count +
                    " 条尚未翻译）→ " + missingFile + " / " + skeletonFile);
        }
        catch (Exception e)
        {
            if (!quiet) Log("回收漏翻失败：" + e.Message);
        }
    }
}

// 系统托盘上下文：程序跑到右下角托盘（带图标 + 提示气泡说明是翻译工具），
// 双击托盘图标展开/收起日志窗口，右键菜单可显示窗口或退出。
internal sealed class TrayContext : ApplicationContext
{
    private const string ProjectUrl = "https://github.com/kukisama/ScoutLauncher";
    private readonly NotifyIcon _icon;
    private readonly LogForm _form;
    private readonly EventWaitHandle _showEvent;
    private readonly RegisteredWaitHandle _showReg;
    public readonly CancellationTokenSource Cancellation = new CancellationTokenSource();

    public TrayContext()
    {
        _form = new LogForm();
        _form.HideRequested += () => HideWindow();

        var menu = new ContextMenuStrip();
        menu.Items.Add("显示日志窗口(&S)", null, (s, e) => ShowWindow());
        menu.Items.Add("项目主页 · ScoutLauncher(&H)", null, (s, e) => OpenProjectPage());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出并关闭汉化(&X)", null, (s, e) => ExitApp());

        _icon = new NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "Scout 语言翻译工具（运行中）", // 鼠标悬停提示
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (s, e) => ToggleWindow();

        // 启动气泡：明确告知用户这是什么，避免被误判为可疑后台程序。
        _icon.BalloonTipTitle = "Scout 语言翻译工具";
        _icon.BalloonTipText = "已在后台运行，正在把 Microsoft Scout 界面翻译成中文。\n双击右下角托盘图标可查看运行状态。";
        _icon.BalloonTipIcon = ToolTipIcon.Info;
        try { _icon.ShowBalloonTip(6000); } catch { }

        // 命名事件：再次启动 exe 时，另一进程 Set 它 → 本实例把窗口显示出来（而不是多开）。
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "ScoutLoaderShowWindowEvent");
        _showReg = ThreadPool.RegisterWaitForSingleObject(
            _showEvent, (state, timedOut) => ShowWindow(), null, Timeout.Infinite, false);
    }

    // 用 exe 自身的图标做托盘图标（编译时已 /win32icon 嵌入）。
    private static Icon LoadTrayIcon()
    {
        try
        {
            var ico = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location);
            if (ico != null) return ico;
        }
        catch { }
        return SystemIcons.Application;
    }

    public void AppendLog(string line)
    {
        try { _form.AppendLine(line); } catch { }
    }

    // 注入完成后刷新托盘提示为“已生效”。
    public void SetReady()
    {
        try { if (_icon != null) _icon.Text = "Scout 语言翻译工具（中文已生效）"; } catch { }
    }

    public void ShowError(string msg)
    {
        try
        {
            _icon.BalloonTipTitle = "Scout 语言翻译工具 - 出错";
            _icon.BalloonTipText = msg;
            _icon.BalloonTipIcon = ToolTipIcon.Error;
            _icon.ShowBalloonTip(8000);
        }
        catch { }
        ShowWindow(); // 出错时自动弹出日志窗口，方便排查
    }

    private void ShowWindow()
    {
        if (_form.InvokeRequired) { _form.BeginInvoke((Action)ShowWindow); return; }
        _form.Show();
        _form.WindowState = FormWindowState.Normal;
        _form.Activate();
    }

    private void HideWindow()
    {
        if (_form.InvokeRequired) { _form.BeginInvoke((Action)HideWindow); return; }
        _form.Hide();
    }

    private void ToggleWindow()
    {
        if (_form.Visible && _form.WindowState != FormWindowState.Minimized) HideWindow();
        else ShowWindow();
    }

    private void OpenProjectPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(ProjectUrl) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            ScoutZh.Log("打开项目主页失败：" + e.Message);
        }
    }

    private void ExitApp()
    {
        try { _icon.Visible = false; } catch { }
        Cancellation.Cancel();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _showReg.Unregister(null); } catch { }
            try { _showEvent.Dispose(); } catch { }
            try { _icon.Dispose(); } catch { }
            try { _form.Dispose(); } catch { }
            try { Cancellation.Dispose(); } catch { }
        }
        base.Dispose(disposing);
    }
}

// 日志窗口（“黑框”）：深色背景 + 等宽字体，滚动显示运行日志。
// 关闭/最小化不退出程序，而是收回托盘。
internal sealed class LogForm : Form
{
    private readonly TextBox _box;
    public event Action HideRequested;

    public LogForm()
    {
        Text = "Scout 语言翻译工具 - 运行日志";
        Width = 760;
        Height = 420;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        try { Icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location); } catch { }

        _box = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            Dock = DockStyle.Fill,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            BackColor = Color.FromArgb(12, 12, 12),
            ForeColor = Color.FromArgb(210, 210, 210),
            Font = new Font("Consolas", 9f),
            BorderStyle = BorderStyle.None,
        };
        Controls.Add(_box);

        var hint = new Label
        {
            Text = "  这是 Microsoft Scout 的中文翻译工具。程序在系统托盘（右下角）后台运行；关闭本窗口会收回托盘，不会停止翻译。要彻底退出请右键托盘图标选“退出”。",
            Dock = DockStyle.Bottom,
            Height = 44,
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Color.FromArgb(30, 30, 30),
            ForeColor = Color.FromArgb(180, 180, 180),
        };
        Controls.Add(hint);
    }

    public void AppendLine(string line)
    {
        if (_box.InvokeRequired) { _box.BeginInvoke((Action<string>)AppendLine, line); return; }
        if (_box.TextLength > 200000) _box.Clear(); // 防止无限增长
        _box.AppendText(line + Environment.NewLine);
    }

    // 点关闭按钮 → 收回托盘而非退出。
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            var h = HideRequested;
            if (h != null) h();
        }
        base.OnFormClosing(e);
    }

    // 最小化 → 收回托盘。
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized)
        {
            var h = HideRequested;
            if (h != null) h();
        }
    }
}

// 极简 CDP over WebSocket 客户端（对应 inject.mjs 的 Cdp 类）。
internal sealed class Cdp
{
    private readonly ClientWebSocket _ws = new ClientWebSocket();
    private readonly string _url;
    private int _id;
    private readonly Dictionary<int, TaskCompletionSource<Dictionary<string, object>>> _pending =
        new Dictionary<int, TaskCompletionSource<Dictionary<string, object>>>();
    private readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    public Func<Task> OnLoad;

    public Cdp(string wsUrl, int port)
    {
        // 127.0.0.1 会被 rebind 保护拒绝 → 换 localhost；某些情况下 Chromium 回传的 URL 缺端口，兜底补上。
        var u = wsUrl.Replace("127.0.0.1", "localhost");
        u = u.Replace("ws://localhost/", "ws://localhost:" + port + "/");
        _url = u;
    }

    public string Url { get { return _url; } }

    public async Task Connect()
    {
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            await _ws.ConnectAsync(new Uri(_url), cts.Token).ConfigureAwait(false);
        }
        Task.Run((Func<Task>)ReceiveLoop);
    }

    private async Task ReceiveLoop()
    {
        var buf = new byte[64 * 1024];
        var acc = new MemoryStream();
        try
        {
            while (_ws.State == WebSocketState.Open)
            {
                var seg = new ArraySegment<byte>(buf);
                var res = await _ws.ReceiveAsync(seg, CancellationToken.None).ConfigureAwait(false);
                if (res.MessageType == WebSocketMessageType.Close) break;
                acc.Write(buf, 0, res.Count);
                if (!res.EndOfMessage) continue;

                var text = Encoding.UTF8.GetString(acc.ToArray());
                acc.SetLength(0);
                Dictionary<string, object> msg;
                try { msg = _json.DeserializeObject(text) as Dictionary<string, object>; }
                catch { continue; }
                if (msg == null) continue;

                if (msg.ContainsKey("id"))
                {
                    int id = Convert.ToInt32(msg["id"]);
                    TaskCompletionSource<Dictionary<string, object>> tcs;
                    lock (_pending)
                    {
                        if (!_pending.TryGetValue(id, out tcs)) continue;
                        _pending.Remove(id);
                    }
                    if (msg.ContainsKey("error"))
                    {
                        var err = msg["error"] as Dictionary<string, object>;
                        var em = err != null && err.ContainsKey("message") ? Convert.ToString(err["message"]) : "CDP error";
                        tcs.TrySetException(new Exception(em));
                    }
                    else
                    {
                        tcs.TrySetResult(msg.ContainsKey("result") ? msg["result"] as Dictionary<string, object> : new Dictionary<string, object>());
                    }
                }
                else if (msg.ContainsKey("method") && Convert.ToString(msg["method"]) == "Page.loadEventFired" && OnLoad != null)
                {
                    OnLoad();
                }
            }
        }
        catch
        {
            /* 连接断开：不影响 Scout；进程退出即可 */
        }
    }

    public Task<Dictionary<string, object>> Send(string method, object prms)
    {
        int id = Interlocked.Increment(ref _id);
        var tcs = new TaskCompletionSource<Dictionary<string, object>>();
        lock (_pending) _pending[id] = tcs;

        var payload = new Dictionary<string, object>
        {
            { "id", id },
            { "method", method },
            { "params", prms ?? new Dictionary<string, object>() },
        };
        var bytes = Encoding.UTF8.GetBytes(_json.Serialize(payload));
        _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);

        Task.Delay(10000).ContinueWith(t =>
        {
            lock (_pending)
            {
                if (_pending.ContainsKey(id))
                {
                    _pending.Remove(id);
                    tcs.TrySetException(new Exception(method + " 超时"));
                }
            }
        });
        return tcs.Task;
    }

    public async Task<string> Evaluate(string expression)
    {
        var prms = new Dictionary<string, object>
        {
            { "expression", expression },
            { "returnByValue", true },
            { "awaitPromise", true },
        };
        var r = await Send("Runtime.evaluate", prms).ConfigureAwait(false);
        if (r != null && r.ContainsKey("exceptionDetails"))
        {
            var ex = r["exceptionDetails"] as Dictionary<string, object>;
            var txt = ex != null && ex.ContainsKey("text") ? Convert.ToString(ex["text"]) : "evaluate exception";
            throw new Exception(txt);
        }
        var result = r != null && r.ContainsKey("result") ? r["result"] as Dictionary<string, object> : null;
        if (result != null && result.ContainsKey("value")) return Convert.ToString(result["value"]);
        return null;
    }
}
