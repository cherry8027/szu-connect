using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace SzuConnect
{
    internal sealed class CheckResult
    {
        public bool Online;
        public bool CredentialError;
        public string Message;
        public CheckResult(bool online, string message, bool credentialError = false)
        { Online = online; Message = message; CredentialError = credentialError; }
    }

    internal sealed class Portal : IDisposable
    {
        internal const string Host = "https://net.szu.edu.cn";
        private readonly HttpClient client;
        internal Action<string> Report = delegate { };
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        public Portal() : this(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { }
        internal Portal(HttpMessageHandler handler)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            client = new HttpClient(handler);
            client.Timeout = TimeSpan.FromSeconds(8);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("SzuConnect/1.0");
        }

        internal static Dictionary<string, object> ParseJsonp(string body)
        {
            body = body.Trim();
            const string prefix = "szuBackground(";
            if (body.StartsWith(prefix, StringComparison.Ordinal))
            {
                body = body.Substring(prefix.Length).TrimEnd();
                if (body.EndsWith(";", StringComparison.Ordinal)) body = body.Substring(0, body.Length - 1).TrimEnd();
                if (!body.EndsWith(")", StringComparison.Ordinal)) throw new FormatException("Invalid JSONP");
                body = body.Substring(0, body.Length - 1);
            }
            return Json.Deserialize<Dictionary<string, object>>(body);
        }

        internal static string Field(Dictionary<string, object> data, string name)
        {
            object value;
            return data != null && data.TryGetValue(name, out value) && value != null ? Convert.ToString(value) : "";
        }

        private async Task<string> GetAsync(string url, CancellationToken token)
        {
            int statusCode = 0;
            try
            {
                using (var response = await client.GetAsync(url, token))
                {
                    statusCode = (int)response.StatusCode;
                    response.EnsureSuccessStatusCode();
                    return await response.Content.ReadAsStringAsync();
                }
            }
            catch (Exception error)
            {
                error.Data["Endpoint"] = new Uri(url).AbsolutePath;
                if (statusCode != 0) error.Data["HttpStatus"] = statusCode;
                throw;
            }
        }

        private async Task<Dictionary<string, object>> ApiAsync(string path, Dictionary<string, string> args, CancellationToken token)
        {
            args["callback"] = "szuBackground";
            args["_"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
            string query = string.Join("&", args.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)).ToArray());
            return ParseJsonp(await GetAsync(Host + path + "?" + query, token));
        }

        private async Task<bool> ProbeAsync(string url, string expected, CancellationToken token)
        {
            try
            {
                string body = await GetAsync(url, token);
                return body.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch (OperationCanceledException) { token.ThrowIfCancellationRequested(); return false; }
            catch (HttpRequestException) { return false; }
        }

        internal async Task<bool> InternetAsync(CancellationToken token)
        {
            var results = await Task.WhenAll(
                ProbeAsync("http://www.msftconnecttest.com/connecttest.txt", "Microsoft Connect Test", token),
                ProbeAsync("https://www.baidu.com/robots.txt", "User-agent:", token));
            return results.Any(x => x);
        }

        internal Task<Dictionary<string, object>> StatusAsync(CancellationToken token)
        { return ApiAsync("/cgi-bin/rad_user_info", new Dictionary<string, string>(), token); }

        internal async Task<CheckResult> CheckAsync(string username, string password, CancellationToken token)
        {
            var status = await StatusAsync(token);
            string statusError = Field(status, "error");
            if (statusError == "ok")
            {
                Report("校园网认证状态：在线");
                bool connected = await InternetAsync(token);
                return new CheckResult(connected, connected ? "网络正常" : "认证在线，外网暂不可用");
            }
            if (statusError != "not_online_error" && statusError != "ip_not_online_error" && statusError != "not_online")
                return new CheckResult(false, "无法确认校园网认证状态");
            Report("校园网认证状态：未登录");
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
                return new CheckResult(false, "请设置校园网账号和密码", true);

            string html = await GetAsync(Host + "/srun_portal_pc?ac_id=12&theme=proyx", token);
            var acidMatch = Regex.Match(html, @"\bacid\s*:\s*[""'](\d+)[""']");
            if (!acidMatch.Success) return new CheckResult(false, "认证页面已变化，无法识别接入点");
            string acid = acidMatch.Groups[1].Value;
            var challenge = await ApiAsync("/cgi-bin/get_challenge", new Dictionary<string, string> {
                { "username", username }, { "ip", "" }
            }, token);
            string key = Field(challenge, "challenge");
            string ip = Field(challenge, "client_ip");
            IPAddress parsedIp;
            if (Field(challenge, "error") != "ok" || string.IsNullOrEmpty(key) || !IPAddress.TryParse(ip, out parsedIp))
                return new CheckResult(false, "认证服务器未返回有效挑战值或客户端地址");

            Report("校园网未登录，开始自动认证");
            var result = await ApiAsync("/cgi-bin/srun_portal", LoginParameters(username, password, key, ip, acid), token);
            string error = Field(result, "error");
            bool accepted = error == "ok" || Field(result, "res") == "ok" ||
                Field(result, "suc_msg") == "ip_already_online_error" || error == "ip_already_online_error";
            if (!accepted)
            {
                string code = Field(result, "error_msg");
                if (string.IsNullOrEmpty(code)) code = error;
                bool credentials = Regex.IsMatch(code, "password|username|user_not|E2531|E2553", RegexOptions.IgnoreCase);
                string safeCode = Regex.IsMatch(code, @"\A[A-Za-z0-9_:-]{1,80}\z") ? " (" + code + ")" : "";
                return new CheckResult(false, "认证失败" + safeCode, credentials);
            }
            await Task.Delay(2000, token);
            bool authenticated = Field(await StatusAsync(token), "error") == "ok";
            bool internet = await InternetAsync(token);
            return new CheckResult(authenticated && internet, authenticated && internet ? "已恢复联网" :
                authenticated ? "认证成功，外网暂不可用" : "服务器已接受请求，尚未确认联网");
        }

        internal static Dictionary<string, string> LoginParameters(string username, string password, string key, string ip, string acid)
        {
            string hmd5;
            using (var hmac = new HMACMD5(Encoding.UTF8.GetBytes(key)))
                hmd5 = Hex(hmac.ComputeHash(Encoding.UTF8.GetBytes(password)));
            string payload = Json.Serialize(new Dictionary<string, string> {
                { "username", username }, { "password", password }, { "ip", ip }, { "acid", acid }, { "enc_ver", "srun_bx1" }
            });
            string info = "{SRBX1}" + Encode(payload, key);
            string checksum;
            using (var sha1 = SHA1.Create())
                checksum = Hex(sha1.ComputeHash(Encoding.UTF8.GetBytes(key + username + key + hmd5 + key + acid + key + ip + key + "200" + key + "1" + key + info)));
            return new Dictionary<string, string> {
                { "action", "login" }, { "username", username }, { "password", "{MD5}" + hmd5 },
                { "ac_id", acid }, { "ip", ip }, { "chksum", checksum }, { "info", info },
                { "n", "200" }, { "type", "1" }, { "os", "Windows" }, { "name", "Windows" },
                { "double_stack", "0" }, { "nas_ip", "" }, { "captchaVal", "" }
            };
        }

        private static string Hex(byte[] bytes)
        { return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant(); }

        private static uint[] Words(string text, bool length)
        {
            int count = (text.Length + 3) / 4;
            var words = new uint[Math.Max(4, count + (length ? 1 : 0))];
            for (int i = 0; i < text.Length; i++) words[i / 4] |= (uint)text[i] << ((i % 4) * 8);
            if (length)
            {
                Array.Resize(ref words, count + 1);
                words[count] = (uint)text.Length;
            }
            return words;
        }

        // SRun xEncode uses additive mixing, not the usual XXTEA XOR mixing.
        // Matched against the live portal's _encodeUserInfo JavaScript.
        internal static string Encode(string text, string key)
        {
            if (text.Length == 0) return "";
            uint[] v = Words(text, true), k = Words(key, false);
            int n = v.Length - 1, rounds = 6 + 52 / v.Length;
            uint z = v[n], y, sum = 0;
            unchecked
            {
                while (rounds-- > 0)
                {
                    sum += 0x9E3779B9;
                    uint e = (sum >> 2) & 3;
                    for (int p = 0; p <= n; p++)
                    {
                        y = v[p == n ? 0 : p + 1];
                        uint mix = (z >> 5) ^ (y << 2);
                        mix += (y >> 3) ^ (z << 4) ^ (sum ^ y);
                        mix += k[(p & 3) ^ (int)e] ^ z;
                        z = v[p] += mix;
                    }
                }
            }
            byte[] bytes = new byte[v.Length * 4];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(v[i / 4] >> (8 * (i % 4)));
            const string standard = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
            const string alphabet = "LVoJPiCN2R8G90yg+hmFHuacZ1OWMnrsSTXkYpUq/3dlbfKwv6xztjI7DeBE45QA";
            return new string(Convert.ToBase64String(bytes).Select(c => c == '=' ? '=' : alphabet[standard.IndexOf(c)]).ToArray());
        }

        internal static int DelaySeconds(int failures, int interval, bool credentialError)
        { return credentialError ? 900 : failures == 0 ? interval : (int)Math.Min(300, 15 * Math.Pow(2, Math.Min(failures - 1, 5))); }

        public void Dispose() { client.Dispose(); }
    }
}
