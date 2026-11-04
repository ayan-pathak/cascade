using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace Cascade
{
    // Opens Chrome's own picture-in-picture for the playing video: a small window that floats over everything
    // and can be dragged and resized. Chrome has no shortcut for it and won't open its media panel through
    // accessibility alone, so: bring the window forward, click "Control your music, videos, and more", press
    // the panel's "Enter picture-in-picture" (through accessibility), close the panel, and put things back.
    public static class Pip
    {
        [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] static extern void mouse_event(uint f, int x, int y, uint d, UIntPtr e);

        const string MediaButton = "Control your music, videos, and more", PipButton = "Enter picture-in-picture";
        static int busy;

        public static void Open(IntPtr h)
        {
            if (h == IntPtr.Zero || !Native.IsWindow(h) || Interlocked.Exchange(ref busy, 1) == 1) return;
            var prev = Native.GetForegroundWindow();
            bool wasMin = Native.IsIconic(h);
            Native.GetCursorPos(out var cursor);
            Native.Activate(h);
            Task.Run(() =>
            {
                try
                {
                    Thread.Sleep(250);
                    // Only click once the browser really is in front, so the click can't land on something else.
                    if (Native.GetForegroundWindow() != h) { Log.Write("pip: couldn't bring the browser forward"); return; }
                    var root = AutomationElement.FromHandle(h);
                    var media = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, MediaButton));
                    if (media == null) { Log.Write("pip: no media button in the toolbar"); return; }

                    AutomationElement pip = null;
                    for (int attempt = 0; attempt < 2 && pip == null; attempt++)
                    {
                        // A window coming back from minimized is still animating; wait until the button stops moving.
                        var r = media.Current.BoundingRectangle;
                        for (int i = 0; i < 10; i++)
                        {
                            Thread.Sleep(80);
                            var now = media.Current.BoundingRectangle;
                            if (now == r && r.X > -1000) break;
                            r = now;
                        }
                        SetCursorPos((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2));
                        Thread.Sleep(60);
                        mouse_event(2, 0, 0, 0, UIntPtr.Zero); mouse_event(4, 0, 0, 0, UIntPtr.Zero);
                        SetCursorPos(cursor.X, cursor.Y);
                        for (int i = 0; i < 15 && pip == null; i++)
                        {
                            Thread.Sleep(100);
                            pip = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, PipButton));
                        }
                        if (pip == null) Native.Keys(0x1B); // close whatever opened before trying again
                    }
                    if (pip == null) { Log.Write("pip: the media panel had no picture-in-picture button (already in picture-in-picture?)"); return; }
                    if (pip.TryGetCurrentPattern(InvokePattern.Pattern, out var p)) { ((InvokePattern)p).Invoke(); Log.Write("pip: opened"); }
                    Thread.Sleep(400);
                    if (root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, PipButton)) != null) Native.Keys(0x1B); // panel still open
                }
                catch (Exception e) { Log.Write("pip: " + e.Message); }
                finally
                {
                    // The floating video stays up on its own; put the browser back the way it was.
                    if (wasMin) Native.ShowWindow(h, Native.SW_MINIMIZE);
                    else if (prev != IntPtr.Zero && prev != h && !WindowTracker.IsOwn(prev)) Native.Activate(prev);
                    Interlocked.Exchange(ref busy, 0);
                }
            });
        }
    }
}
