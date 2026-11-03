using System;
using System.Linq;
using System.Threading;
using System.Windows;

namespace Cascade
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Native.SetProcessDPIAware();

            if (args.Contains("--quit"))
            {
                var h = Native.FindWindow(null, Host.MsgTitle);
                if (h != IntPtr.Zero) Native.PostMessage(h, Host.WM_QUIT_CASCADE, IntPtr.Zero, IntPtr.Zero);
                return;
            }
            if (args.Contains("--restore"))
            {
                var st = State.Load();
                Taskbar.Restore(st);
                if (args.Contains("--wallpaper")) WallpaperSync.RestoreOriginal(st);
                return;
            }

            var mutex = new Mutex(true, "Cascade.Desktop.Shell", out bool created);
            if (!created)
            {
                if (!args.Contains("--restart")) return;
                try { if (!mutex.WaitOne(10000)) return; } catch (AbandonedMutexException) { }
            }

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(Theme.Resources());
            var host = new Host();
            app.DispatcherUnhandledException += (s, e) => { Log.Write("ui: " + e.Exception); e.Handled = true; };
            AppDomain.CurrentDomain.UnhandledException += (s, e) => { Log.Write("fatal: " + e.ExceptionObject); host.RestoreSystem(); };
            app.SessionEnding += (s, e) => host.SessionEnding();
            app.Exit += (s, e) => host.RestoreSystem();
            app.Startup += (s, e) => host.Start();
            app.Run();
            try { mutex.ReleaseMutex(); } catch { }
        }
    }
}
