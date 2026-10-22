using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace SzuConnect
{
    internal static class Program
    {
        internal static string InstanceName { get { return @"Local\SzuConnect-" + System.Security.Principal.WindowsIdentity.GetCurrent().User.Value; } }
        internal static string StopName { get { return InstanceName + "-Stop"; } }
        internal static string SettingsName { get { return InstanceName + "-Settings"; } }
        internal static string LogName { get { return InstanceName + "-Logs"; } }
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length > 0 && args[0] == "--stop")
                {
                    try { using (var signal = EventWaitHandle.OpenExisting(StopName)) signal.Set(); }
                    catch (WaitHandleCannotBeOpenedException) { }
                    Mutex owner;
                    if (Mutex.TryOpenExisting(InstanceName, out owner))
                    {
                        using (owner)
                        {
                            bool stopped;
                            try { stopped = owner.WaitOne(TimeSpan.FromSeconds(20)); }
                            catch (AbandonedMutexException) { stopped = true; }
                            if (!stopped) return 4;
                            owner.ReleaseMutex();
                        }
                    }
                    return 0;
                }
                if (args.Length > 0 && args[0] == "--import-credentials")
                {
                    using (var importLock = new Mutex(false, InstanceName))
                    {
                        bool imported;
                        try { imported = importLock.WaitOne(0); }
                        catch (AbandonedMutexException) { imported = true; }
                        if (!imported) return 4;
                        try
                        {
                            Console.InputEncoding = new System.Text.UTF8Encoding(false);
                            var input = new JavaScriptSerializer().Deserialize<Dictionary<string, string>>(Console.In.ReadToEnd());
                            var settings = Settings.Load();
                            settings.Username = input["username"].Trim();
                            settings.SetPassword(input["password"]);
                            settings.Save();
                            return 0;
                        }
                        finally { importLock.ReleaseMutex(); }
                    }
                }
                if (args.Length > 0 && args[0] == "--disable-autostart")
                { Settings.SetAutostart(false); return 0; }
                using (var mutex = new Mutex(false, InstanceName))
                {
                    bool owns;
                    try { owns = mutex.WaitOne(0); }
                    catch (AbandonedMutexException) { owns = true; }
                    if (!owns)
                    {
                        if (args.Length > 0 && args[0] == "--check") return 3;
                        string eventName = args.Length > 0 && args[0] == "--logs" ? LogName : SettingsName;
                        try { using (var signal = EventWaitHandle.OpenExisting(eventName)) signal.Set(); }
                        catch (WaitHandleCannotBeOpenedException) { }
                        return 0;
                    }
                    try
                    {
                        if (args.Length > 0 && args[0] == "--check")
                        {
                            var settings = Settings.Load();
                            using (var portal = new Portal())
                            {
                                portal.Report = Settings.Log;
                                var result = portal.CheckAsync(settings.Username, settings.Password(), CancellationToken.None).GetAwaiter().GetResult();
                                Settings.WriteStatus(result.Message, result.Online, null);
                                return result.Online ? 0 : 2;
                            }
                        }
                        Application.EnableVisualStyles();
                        Application.SetCompatibleTextRenderingDefault(false);
                        Application.Run(new TrayApp(args.Length > 0 && args[0] == "--settings", args.Length > 0 && args[0] == "--logs"));
                    }
                    finally { mutex.ReleaseMutex(); }
                }
                return 0;
            }
            catch (Exception error)
            {
                Settings.Log("程序错误: " + error.GetType().Name);
                if (args.Length == 0) MessageBox.Show("程序未能启动，请查看本地日志或重新设置账号。", "校园网助手", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
    }
}
