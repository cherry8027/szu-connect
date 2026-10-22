using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Web.Script.Serialization;

namespace SzuConnect
{
    internal static class Tests
    {
        private static int count;
        private static void Expect(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            count++;
        }
        private sealed class Network : HttpMessageHandler
        {
            internal bool Internet, Online, Reject, BadChallenge, Redirect;
            internal bool PortalUnavailable, LoginDoesNotAuthenticate;
            internal string StatusError;
            internal int Logins, StatusRequests;
            internal Dictionary<string, string> LastLogin;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                string path = request.RequestUri.AbsolutePath;
                string body = "";
                var status = HttpStatusCode.OK;
                if (request.RequestUri.Host != "net.szu.edu.cn")
                {
                    if (Redirect) { status = HttpStatusCode.Redirect; body = "Microsoft Connect Test"; }
                    else if (!Internet) { status = HttpStatusCode.ServiceUnavailable; }
                    else body = path.EndsWith(".txt") ? "Microsoft Connect Test" : "User-agent: *";
                }
                else if (path == "/cgi-bin/rad_user_info")
                {
                    StatusRequests++;
                    if (PortalUnavailable) status = HttpStatusCode.ServiceUnavailable;
                    body = Wrap(new { error = StatusError ?? (Online ? "ok" : "not_online_error") });
                }
                else if (path == "/srun_portal_pc") body = "var CONFIG = {acid: \"12\"};";
                else if (path == "/cgi-bin/get_challenge") body = Wrap(new {
                    error = "ok", challenge = BadChallenge ? "" : "0123456789abcdef0123456789abcdef", client_ip = "10.1.2.3"
                });
                else if (path == "/cgi-bin/srun_portal")
                {
                    Logins++;
                    var query = HttpUtility.ParseQueryString(request.RequestUri.Query);
                    LastLogin = new Dictionary<string, string>();
                    foreach (string key in query.AllKeys) LastLogin[key] = query[key];
                    if (Reject) body = Wrap(new { error = "login_error", error_msg = "password_error" });
                    else { Online = !LoginDoesNotAuthenticate; Internet = true; body = Wrap(new { error = "ok" }); }
                }
                else throw new Exception("Unexpected HTTP request: " + path);
                return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
            }
            private static string Wrap(object value) { return "szuBackground(" + new JavaScriptSerializer().Serialize(value) + ");"; }
        }

        private static async Task Run()
        {
            const string key = "0123456789abcdef0123456789abcdef";
            // Golden vectors executed from the live school's Portal.js, using synthetic credentials.
            Expect(Portal.Encode("hello", key) == "DuhThfWl4vmGXQs5", "ASCII xEncode vector");
            Expect(Portal.Encode("校园网😀", key) == "nSgpO2Q2H4Njk00t", "UTF-16 xEncode vector");
            var parameters = Portal.LoginParameters("123456", "test-password", key, "10.1.2.3", "12");
            Expect(parameters["password"] == "{MD5}7da7d72e92213fc0f6a611e30401dea0", "HMAC-MD5 vector");
            Expect(parameters["info"] == "{SRBX1}pvPx98UmYkpKxUshhtPFYhUnb8itbeyHIYRV6tyBjEQ7su/1jBFS7qkMFDQ/z++Fmfixku7nt3hHYYlHXxHotTlm78NmREDJlp7zK/4o8bmAz/N5JaBS9jj8cF6hrBxm2hU/jf9Qy3v=", "Encoded login info vector");
            Expect(parameters["chksum"] == "4f8b3cf94adada3c0b482d6cb4f1526187f02be3", "SHA1 checksum vector");
            Expect(Portal.Field(Portal.ParseJsonp("szuBackground({\"error\":\"ok\"});"), "error") == "ok", "JSONP wrapper");
            Expect(Portal.Field(Portal.ParseJsonp("{\"error\":\"ok\"}"), "error") == "ok", "Plain JSON");
            bool invalid = false;
            try { Portal.ParseJsonp("szuBackground({\"error\":\"ok\"}"); }
            catch (FormatException) { invalid = true; }
            Expect(invalid, "Truncated JSONP rejected");

            var online = new Network { Internet = true, Online = true };
            using (var portal = new Portal(online))
            {
                var result = await portal.CheckAsync("u", "p", CancellationToken.None);
                Expect(result.Online && online.Logins == 0, "Healthy network never authenticates");
            }
            var masked = new Network { Internet = true };
            using (var portal = new Portal(masked))
            {
                var result = await portal.CheckAsync("u", "p", CancellationToken.None);
                Expect(result.Online && masked.Logins == 1, "Working external/VPN connection must not mask expired campus authentication");
                Expect(masked.StatusRequests == 2, "Campus authentication is checked before and after login");
            }
            var unavailable = new Network { Internet = true, PortalUnavailable = true };
            using (var portal = new Portal(unavailable))
            {
                bool failed = false;
                try { await portal.CheckAsync("u", "p", CancellationToken.None); }
                catch (HttpRequestException error)
                {
                    failed = true;
                    Expect((string)error.Data["Endpoint"] == "/cgi-bin/rad_user_info", "Failure identifies safe endpoint without credentials");
                }
                Expect(failed && unavailable.Logins == 0, "Unreachable portal is not reported healthy or blindly authenticated");
            }
            var busy = new Network { StatusError = "busy_error" };
            using (var portal = new Portal(busy))
            {
                var result = await portal.CheckAsync("u", "p", CancellationToken.None);
                Expect(!result.Online && busy.Logins == 0, "Unknown portal state must not trigger authentication");
            }
            var unconfirmed = new Network { LoginDoesNotAuthenticate = true };
            using (var portal = new Portal(unconfirmed))
            {
                var result = await portal.CheckAsync("u", "p", CancellationToken.None);
                Expect(!result.Online && unconfirmed.Logins == 1, "Login requires confirmed campus session, not only reachable Internet");
            }
            var authenticated = new Network { Online = true };
            using (var portal = new Portal(authenticated))
            {
                var result = await portal.CheckAsync("u", "p", CancellationToken.None);
                Expect(!result.Online && authenticated.Logins == 0, "Online portal with broken uplink never relogs");
            }
            var offline = new Network();
            using (var portal = new Portal(offline))
            {
                var result = await portal.CheckAsync("123456", "test-password", CancellationToken.None);
                Expect(result.Online && offline.Logins == 1, "Offline -> challenge -> authenticate -> verify");
                Expect(offline.LastLogin["chksum"] == parameters["chksum"], "Wire checksum matches oracle");
                Expect(offline.LastLogin["info"] == parameters["info"], "Query round-trip preserves encrypted data");
            }
            var rejected = new Network { Reject = true };
            using (var portal = new Portal(rejected))
            {
                var result = await portal.CheckAsync("u", "secret-test", CancellationToken.None);
                Expect(!result.Online && result.CredentialError, "Credential rejection is not success");
                Expect(!result.Message.Contains("secret-test"), "Failure status never exposes password");
            }
            var malformed = new Network { BadChallenge = true };
            using (var portal = new Portal(malformed))
            {
                var result = await portal.CheckAsync("u", "p", CancellationToken.None);
                Expect(!result.Online && malformed.Logins == 0, "Missing challenge never submits credentials");
            }
            using (var portal = new Portal(new Network { Redirect = true }))
                Expect(!await portal.InternetAsync(CancellationToken.None), "Captive redirect is not connectivity");
            using (var cancellation = new CancellationTokenSource())
            using (var portal = new Portal(new Network()))
            {
                cancellation.Cancel();
                bool cancelled = false;
                try { await portal.CheckAsync("u", "p", cancellation.Token); }
                catch (OperationCanceledException) { cancelled = true; }
                Expect(cancelled, "Shutdown cancels pending check");
            }
            int[] expected = { 300, 15, 30, 60, 120, 240, 300, 300 };
            for (int i = 0; i < expected.Length; i++)
                Expect(Portal.DelaySeconds(i, 300, false) == expected[i], "Retry delay " + i);
            Expect(Portal.DelaySeconds(1, 60, true) == 900, "Bad credentials use long cooldown");
            var settings = new Settings();
            settings.SetPassword("synthetic-password");
            Expect(settings.Password() == "synthetic-password", "DPAPI round trip");
            Expect(!settings.ProtectedPassword.Contains("synthetic-password"), "DPAPI value is not plaintext");
            string savePath = Path.Combine(Settings.DataDirectory, "save-test-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                settings.Username = "synthetic-user";
                settings.SaveTo(savePath);
                settings.IntervalSeconds = 120;
                settings.SaveTo(savePath);
                var reloaded = new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(savePath));
                Expect(reloaded.IntervalSeconds == 120 && reloaded.Username == "synthetic-user", "Existing encrypted-directory config can be updated");
                Expect(reloaded.Password() == "synthetic-password", "Updating config preserves encrypted credentials");
                using (var locked = new FileStream(savePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    bool failed = false;
                    settings.IntervalSeconds = 180;
                    try { settings.SaveTo(savePath); }
                    catch (System.ComponentModel.Win32Exception) { failed = true; }
                    Expect(failed, "Locked config reports a save failure");
                    reloaded = new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(savePath));
                    Expect(reloaded.IntervalSeconds == 120, "Failed save preserves the previous config");
                }
                Expect(Directory.GetFiles(Settings.DataDirectory, Path.GetFileName(savePath) + ".*.tmp").Length == 0, "Failed save cleans temporary files");
            }
            finally { File.Delete(savePath); File.Delete(savePath + ".tmp"); }
            string logPath = Path.Combine(Settings.DataDirectory, "log-test-" + Guid.NewGuid().ToString("N") + ".log");
            try
            {
                using (var writer = new FileStream(logPath, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                {
                    byte[] text = System.Text.Encoding.UTF8.GetBytes("校园网认证状态：在线\r\n网络正常\r\n");
                    writer.Write(text, 0, text.Length);
                    writer.Flush();
                    Expect(LogForm.ReadLog(logPath).Contains("校园网认证状态：在线"), "Log viewer reads UTF-8 while the log is open for writing");
                    writer.Write(text, 0, text.Length);
                    writer.Flush();
                    Expect(LogForm.ReadLog(logPath).Length > System.Text.Encoding.UTF8.GetString(text).Length, "Log refresh reads appended content");
                }
                string rotated = logPath + ".1";
                File.Move(logPath, rotated);
                try
                {
                    File.WriteAllText(logPath, "轮转后的新日志", System.Text.Encoding.UTF8);
                    Expect(LogForm.ReadLog(logPath) == "轮转后的新日志", "Log viewer reads the current file after rotation");
                }
                finally { File.Delete(rotated); }
            }
            finally { File.Delete(logPath); }
        }

        public static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--live-portal")
            {
                using (var portal = new Portal())
                {
                    var status = portal.StatusAsync(CancellationToken.None).GetAwaiter().GetResult();
                    Console.WriteLine("HTTPS portal status: " + Portal.Field(status, "error"));
                    return 0;
                }
            }
            try { Run().GetAwaiter().GetResult(); Console.WriteLine("PASS: " + count + " checks"); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
