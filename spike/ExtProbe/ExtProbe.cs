// ExtProbe - empirical test: does the installed WebView2 Runtime accept MV2 and/or MV3
// unpacked Chromium extensions via CoreWebView2Profile.AddBrowserExtensionAsync?
//
// Deliberately short-lived: hard Environment.Exit after --timeout seconds no matter what.
// Uses its own user-data folder, an off-screen non-topmost 1-window WinForms host, and
// never goes fullscreen or topmost, so it cannot disturb a running shell.
//
// C# 5 / .NET Framework 4.8 / x64, built with inbox csc.exe.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

static class ExtProbe
{
    static string s_udf = @"C:\MarwanOS\exttest\udf";
    static int s_timeoutSec = 25;
    static List<string> s_exts = new List<string>();
    static bool s_doFunctional = false;
    static bool s_doPopup = false;

    static Form s_form;
    static WebView2 s_view;
    static CoreWebView2Environment s_env;
    static CoreWebView2Profile s_profile;
    static System.Threading.Timer s_watchdog;

    static void Log(string s)
    {
        Console.WriteLine(s);
        Console.Out.Flush();
    }

    static string Describe(Exception ex)
    {
        StringBuilder sb = new StringBuilder();
        Exception cur = ex;
        int depth = 0;
        while (cur != null && depth < 6)
        {
            sb.Append(depth == 0 ? "    EXCEPTION: " : "    INNER[" + depth + "]: ");
            sb.Append(cur.GetType().FullName);
            sb.Append("\n      Message : ");
            sb.Append(cur.Message);
            sb.Append("\n      HResult : 0x");
            sb.Append(cur.HResult.ToString("X8", CultureInfo.InvariantCulture));
            COMException ce = cur as COMException;
            if (ce != null)
            {
                sb.Append("\n      COM.ErrorCode : 0x");
                sb.Append(ce.ErrorCode.ToString("X8", CultureInfo.InvariantCulture));
            }
            sb.Append("\n");
            cur = cur.InnerException;
            depth++;
        }
        return sb.ToString().TrimEnd();
    }

    [STAThread]
    static int Main(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--udf" && i + 1 < args.Length) s_udf = args[++i];
            else if (a == "--timeout" && i + 1 < args.Length) s_timeoutSec = int.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (a == "--ext" && i + 1 < args.Length) s_exts.Add(args[++i]);
            else if (a == "--functional") s_doFunctional = true;
            else if (a == "--popup") s_doPopup = true;
            else s_exts.Add(a);
        }

        Log("=== ExtProbe start " + DateTime.Now.ToString("s", CultureInfo.InvariantCulture));
        Log("udf=" + s_udf);
        Log("timeoutSec=" + s_timeoutSec);
        for (int i = 0; i < s_exts.Count; i++) Log("extArg[" + i + "]=" + s_exts[i]);

        // Hard watchdog. Nothing below can outlive this.
        s_watchdog = new System.Threading.Timer(delegate(object o)
        {
            Console.Error.WriteLine("=== WATCHDOG FIRED - forcing exit");
            Console.Error.Flush();
            Console.Out.Flush();
            Environment.Exit(3);
        }, null, s_timeoutSec * 1000, System.Threading.Timeout.Infinite);

        try
        {
            Log("GetAvailableBrowserVersionString=" + CoreWebView2Environment.GetAvailableBrowserVersionString());
        }
        catch (Exception ex)
        {
            Log("GetAvailableBrowserVersionString FAILED");
            Log(Describe(ex));
        }

        Application.EnableVisualStyles();

        s_form = new Form();
        s_form.FormBorderStyle = FormBorderStyle.None;
        s_form.StartPosition = FormStartPosition.Manual;
        s_form.Location = new Point(-4000, -4000);   // off-screen, cannot be seen by the human
        s_form.Size = new Size(1200, 800);           // real size so pages actually lay out
        s_form.ShowInTaskbar = false;
        s_form.TopMost = false;
        s_form.Shown += delegate(object o, EventArgs e) { StartWork(); };

        Application.Run(s_form);
        Log("=== ExtProbe end (message loop exited)");
        Console.Out.Flush();
        Environment.Exit(0);
        return 0;
    }

    static async void StartWork()
    {
        try
        {
            await RunAsync();
            Log("=== DONE");
        }
        catch (Exception ex)
        {
            Log("=== FATAL");
            Log(Describe(ex));
        }
        Console.Out.Flush();
        Environment.Exit(0);
    }

    static async Task RunAsync()
    {
        CoreWebView2EnvironmentOptions opts = new CoreWebView2EnvironmentOptions();
        opts.AreBrowserExtensionsEnabled = true;
        // Keep an off-screen window from being throttled so network work still happens.
        opts.AdditionalBrowserArguments =
            "--disable-background-timer-throttling --disable-backgrounding-occluded-windows --disable-renderer-backgrounding";
        Log("AreBrowserExtensionsEnabled=" + opts.AreBrowserExtensionsEnabled);

        s_env = await CoreWebView2Environment.CreateAsync(null, s_udf, opts);
        Log("env.BrowserVersionString=" + s_env.BrowserVersionString);

        s_view = new WebView2();
        s_view.Dock = DockStyle.Fill;
        s_form.Controls.Add(s_view);
        await s_view.EnsureCoreWebView2Async(s_env);
        Log("CoreWebView2 ready");

        s_profile = s_view.CoreWebView2.Profile;
        Log("profile.ProfileName=" + s_profile.ProfileName);
        Log("profile.ProfilePath=" + s_profile.ProfilePath);

        List<CoreWebView2BrowserExtension> loaded = new List<CoreWebView2BrowserExtension>();

        for (int i = 0; i < s_exts.Count; i++)
        {
            string folder = s_exts[i];
            Log("---- AddBrowserExtensionAsync: " + folder);
            Log("     folderExists=" + System.IO.Directory.Exists(folder) +
                " manifestExists=" + System.IO.File.Exists(System.IO.Path.Combine(folder, "manifest.json")));
            try
            {
                CoreWebView2BrowserExtension ext = await s_profile.AddBrowserExtensionAsync(folder);
                Log("LOADED " + ext.Id + " " + ext.Name + " enabled=" + ext.IsEnabled);
                loaded.Add(ext);
            }
            catch (Exception ex)
            {
                Log("REJECTED " + folder);
                Log(Describe(ex));
            }
        }

        Log("---- GetBrowserExtensionsAsync");
        try
        {
            IReadOnlyList<CoreWebView2BrowserExtension> list = await s_profile.GetBrowserExtensionsAsync();
            Log("count=" + list.Count);
            for (int i = 0; i < list.Count; i++)
            {
                Log("  [" + i + "] Id=" + list[i].Id + " Name=" + list[i].Name + " IsEnabled=" + list[i].IsEnabled);
            }
        }
        catch (Exception ex)
        {
            Log("GetBrowserExtensionsAsync FAILED");
            Log(Describe(ex));
        }

        if (s_doPopup)
        {
            IReadOnlyList<CoreWebView2BrowserExtension> list = await s_profile.GetBrowserExtensionsAsync();
            for (int i = 0; i < list.Count; i++)
            {
                string url = "chrome-extension://" + list[i].Id + "/popup.html";
                Log("---- popup probe " + url);
                bool ok = await Navigate(url, 10000);
                Log("     navOk=" + ok);
                string t = await Eval("JSON.stringify([document.title, document.body?document.body.innerText.length:-1, document.querySelectorAll('*').length, location.href])");
                Log("     [title, innerTextLen, nodeCount, href]=" + t);

                // Definitive DNR evidence: ask the extension's own page what rulesets are live.
                await Eval(
                    "window.__dnr=null;(function(){try{" +
                    "if(typeof chrome==='undefined'||!chrome.declarativeNetRequest){window.__dnr=JSON.stringify({hasApi:false});return;}" +
                    "var res={hasApi:true};" +
                    "chrome.declarativeNetRequest.getEnabledRulesets().then(function(rs){" +
                    "res.enabledRulesets=rs;res.enabledRulesetCount=rs.length;" +
                    "return chrome.declarativeNetRequest.getAvailableStaticRuleCount();}).then(function(n){" +
                    "res.availableStaticRuleCount=n;" +
                    "return chrome.declarativeNetRequest.getDynamicRules();}).then(function(d){" +
                    "res.dynamicRuleCount=d.length;window.__dnr=JSON.stringify(res);}).catch(function(e){" +
                    "res.error=String(e&&e.message);window.__dnr=JSON.stringify(res);});" +
                    "}catch(e){window.__dnr=JSON.stringify({thrown:String(e&&e.message)});}})();");
                for (int k = 0; k < 20; k++)
                {
                    await Task.Delay(400);
                    string d = await Eval("window.__dnr");
                    if (d != null && d != "null" && d.Length > 4) { Log("     DNR=" + d); break; }
                    if (k == 19) Log("     DNR=TIMED-OUT");
                }
            }
        }

        if (s_doFunctional)
        {
            await Functional(loaded);
        }
    }

    // ---- functional (declarativeNetRequest) test -------------------------------------

    static async Task Functional(List<CoreWebView2BrowserExtension> loaded)
    {
        Log("---- FUNCTIONAL: waiting 8s for service worker / DNR rulesets to come up");
        await Task.Delay(8000);

        string a = await FetchProbe("EXT-ENABLED");
        Log("PHASE-A(enabled) " + a);

        for (int i = 0; i < loaded.Count; i++)
        {
            try
            {
                await loaded[i].EnableAsync(false);
                Log("disabled " + loaded[i].Id + " IsEnabled=" + loaded[i].IsEnabled);
            }
            catch (Exception ex)
            {
                Log("EnableAsync(false) FAILED for " + loaded[i].Id);
                Log(Describe(ex));
            }
        }
        await Task.Delay(3000);

        string b = await FetchProbe("EXT-DISABLED");
        Log("PHASE-B(disabled) " + b);
    }

    static async Task<string> FetchProbe(string tag)
    {
        bool ok = await Navigate("https://example.com/", 12000);
        if (!ok) return tag + " NAV-FAILED";

        string js =
            "window.__pr=null;(function(){var urls=[" +
            "'https://pagead2.googlesyndication.com/pagead/js/adsbygoogle.js'," +
            "'https://securepubads.g.doubleclick.net/tag/js/gpt.js'," +
            "'https://www.googletagmanager.com/gtag/js?id=G-TEST'," +
            "'https://static.doubleclick.net/instream/ad_status.js'," +
            "'https://cdn.taboola.com/libtrc/unip/1/tfa.js'," +
            "'https://ads.pubmatic.com/AdServer/js/pwt/1/2.js'," +
            "'https://www.googleadservices.com/pagead/conversion.js'," +
            "'https://ad.doubleclick.net/ddm/trackimp/x'," +
            "'https://example.com/robots.txt'," +
            "'https://www.iana.org/robots.txt'" +
            "];var out=[];var n=0;" +
            "function done(){if(n===urls.length){window.__pr=JSON.stringify(out);}}" +
            "urls.forEach(function(u,i){" +
            "var cb=(u.indexOf('?')>=0?'&':'?')+'cb='+Math.random();" +
            "var ac=new AbortController();var to=setTimeout(function(){ac.abort();},4000);" +
            "fetch(u+cb,{mode:'no-cors',cache:'no-store',signal:ac.signal})" +
            ".then(function(r){clearTimeout(to);out[i]=[u,'REACHED type='+r.type+' status='+r.status+' redirected='+r.redirected+' finalUrl='+r.url];n++;done();})" +
            ".catch(function(e){clearTimeout(to);out[i]=[u,'FAILED '+(e&&e.name)+': '+(e&&e.message)];n++;done();});" +
            "});})();";

        await Eval(js);

        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(500);
            string r = await Eval("window.__pr");
            if (r != null && r != "null" && r.Length > 4) return tag + " " + r;
        }
        return tag + " TIMED-OUT-COLLECTING";
    }

    static async Task<string> Eval(string js)
    {
        try { return await s_view.CoreWebView2.ExecuteScriptAsync(js); }
        catch (Exception ex) { return "EVAL-FAILED " + ex.Message; }
    }

    static async Task<bool> Navigate(string url, int ms)
    {
        TaskCompletionSource<bool> tcs = new TaskCompletionSource<bool>();
        EventHandler<CoreWebView2NavigationCompletedEventArgs> h = null;
        h = delegate(object o, CoreWebView2NavigationCompletedEventArgs e)
        {
            s_view.CoreWebView2.NavigationCompleted -= h;
            Log("     nav done success=" + e.IsSuccess + " webErrorStatus=" + e.WebErrorStatus);
            tcs.TrySetResult(e.IsSuccess);
        };
        s_view.CoreWebView2.NavigationCompleted += h;
        s_view.CoreWebView2.Navigate(url);
        Task t = await Task.WhenAny(tcs.Task, Task.Delay(ms));
        if (t != tcs.Task) { try { s_view.CoreWebView2.NavigationCompleted -= h; } catch { } return false; }
        return tcs.Task.Result;
    }
}
