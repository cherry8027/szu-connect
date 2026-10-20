using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SzuConnect
{
    internal sealed class Settings
    {
        public string Username { get; set; }
        public string ProtectedPassword { get; set; }
        public int IntervalSeconds { get; set; }
        public static readonly string DataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SzuConnect");
        internal static readonly string FilePath = Path.Combine(DataDirectory, "settings.json");
        internal static readonly string StatusPath = Path.Combine(DataDirectory, "status.json");
        internal static readonly string LogPath = Path.Combine(DataDirectory, "network.log");
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunName = "SzuConnect";
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MoveFileEx(string existingFile, string newFile, int flags);

        public Settings() { Username = ""; ProtectedPassword = ""; IntervalSeconds = 300; }
        internal static Settings Load()
        {
            if (!File.Exists(FilePath)) return new Settings();
            Settings value = new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(FilePath));
            if (value == null || value.IntervalSeconds < 15 || value.IntervalSeconds > 3600)
                throw new InvalidDataException("检测间隔必须为 15 至 3600 秒");
            return value;
        }
        internal string Password()
        {
            if (string.IsNullOrEmpty(ProtectedPassword)) return "";
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(ProtectedPassword), null, DataProtectionScope.CurrentUser));
        }
        internal void SetPassword(string password)
        {
            ProtectedPassword = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(password), null, DataProtectionScope.CurrentUser));
        }
        internal void Save()
        {
            SaveTo(FilePath);
        }
        internal void SaveTo(string path)
        {
            path = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(new JavaScriptSerializer().Serialize(this));
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                // Encrypted/redirected folders may report cross-device moves for sibling paths.
                const int replaceExisting = 0x1, copyAllowed = 0x2, writeThrough = 0x8;
                if (!MoveFileEx(temporary, path, replaceExisting | copyAllowed | writeThrough))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        internal static bool Autostart
        {
            get { using (var key = Registry.CurrentUser.OpenSubKey(RunKey)) return key != null && key.GetValue(RunName) != null; }
        }
        internal static void SetAutostart(bool enabled)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enabled) key.SetValue(RunName, "\"" + System.Windows.Forms.Application.ExecutablePath + "\"");
                else key.DeleteValue(RunName, false);
            }
        }
        internal static void WriteStatus(string message, bool online, DateTime? next)
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(StatusPath, new JavaScriptSerializer().Serialize(new {
                pid = System.Diagnostics.Process.GetCurrentProcess().Id,
                updated = DateTimeOffset.Now.ToString("o"), status = message,
                online = online, nextCheck = next.HasValue ? next.Value.ToString("o") : null
            }), new UTF8Encoding(false));
        }
        internal static void Log(string message)
        {
            try
            {
                Directory.CreateDirectory(DataDirectory);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length >= 1024 * 1024)
                {
                    string backup = LogPath + ".1";
                    if (File.Exists(backup)) File.Delete(backup);
                    File.Move(LogPath, backup);
                }
                File.AppendAllText(LogPath, DateTimeOffset.Now.ToString("o") + " " + message + Environment.NewLine, new UTF8Encoding(false));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
