using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Cascade
{
    // One borderless window hosting the shared web UI in a given view (desktop, topbar, dock, launcher, personalize).
    public class WebWindow : Window
    {
        public readonly string View;
        public readonly WebView2 Web;
        public IntPtr Handle;
        public bool BottomMost, IsAppBar;
        public uint Edge;
        readonly Host host;
        readonly List<string> queue = new List<string>();
        bool ready;

        public WebWindow(Host host, string view)
        {
            this.host = host;
            View = view;
            Title = "Cascade " + view;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Background = new SolidColorBrush(Color.FromRgb(8, 10, 9));
            Web = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 8, 10, 9) };
            Content = Web;
            SourceInitialized += (s, e) =>
            {
                Handle = new WindowInteropHelper(this).Handle;
                Native.AddExStyle(Handle, Native.WS_EX_TOOLWINDOW);
                HwndSource.FromHwnd(Handle).AddHook(Hook);
            };
        }

        public async Task InitAsync(CoreWebView2Environment env, double zoom)
        {
            await Web.EnsureCoreWebView2Async(env);
            var c = Web.CoreWebView2;
            c.SetVirtualHostNameToFolderMapping("cascade.local", Paths.Root, CoreWebView2HostResourceAccessKind.Allow);
            c.Settings.AreDefaultContextMenusEnabled = false;
            c.Settings.IsStatusBarEnabled = false;
            c.Settings.IsZoomControlEnabled = false;
            c.Settings.AreBrowserAcceleratorKeysEnabled = false;
            c.Settings.IsSwipeNavigationEnabled = false;
            c.WebMessageReceived += (s, e) =>
            {
                string json;
                try { json = e.WebMessageAsJson; } catch { return; }
                host.OnMessage(this, json);
            };
            c.ProcessFailed += (s, e) => { Log.Write(View + " webview failed: " + e.ProcessFailedKind); try { c.Reload(); } catch { } };
            Web.ZoomFactor = zoom;
            c.Navigate(Paths.Origin + "ui/index.html?view=" + View);
        }

        // Called when the page says it is ready: send the init payload first, then anything queued.
        public void Ready(object init)
        {
            Post(Json.Write(init));
            ready = true;
            foreach (var q in queue) Post(q);
            queue.Clear();
        }

        public void Send(object msg)
        {
            var json = Json.Write(msg);
            if (!ready) { if (queue.Count < 40) queue.Add(json); return; }
            Post(json);
        }

        void Post(string json)
        {
            try { Web.CoreWebView2?.PostWebMessageAsJson(json); }
            catch (Exception e) { Log.Write("post " + View + ": " + e.Message); }
        }

        public void PlacePhysical(RECT r) =>
            Native.SetWindowPos(Handle, IntPtr.Zero, r.Left, r.Top, r.Width, r.Height, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);

        IntPtr Hook(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            if (BottomMost && msg == Native.WM_WINDOWPOSCHANGING)
            {
                var wp = Marshal.PtrToStructure<WINDOWPOS>(l);
                wp.hwndInsertAfter = Native.AboveDesktop(Handle);
                wp.flags &= ~Native.SWP_NOZORDER;
                Marshal.StructureToPtr(wp, l, false);
            }
            if (IsAppBar && msg == (int)AppBar.Message && w.ToInt32() == 1) host.Reflow();
            return IntPtr.Zero;
        }
    }

    // Small label shown above dock icons; separate window so it can sit outside the dock strip.
    public class TipWindow : Window
    {
        readonly TextBlock text;
        readonly Border box;

        public TipWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            Focusable = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            text = new TextBlock { FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(228, 235, 226)) };
            box = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(245, 12, 16, 14)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(9, 4, 9, 4),
                Child = text
            };
            Content = box;
            SourceInitialized += (s, e) => Native.AddExStyle(new WindowInteropHelper(this).Handle, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT);
        }

        public void SetColors(Color fg, Color bg) { text.Foreground = new SolidColorBrush(fg); box.Background = new SolidColorBrush(Color.FromArgb(245, bg.R, bg.G, bg.B)); }

        public void ShowAt(string label, double centerX, double bottom)
        {
            text.Text = label.ToUpperInvariant();
            if (!IsVisible) Show();
            UpdateLayout();
            Left = centerX - ActualWidth / 2;
            Top = bottom - ActualHeight;
        }
    }

    public static class Theme
    {
        public static ResourceDictionary Resources() => (ResourceDictionary)XamlReader.Parse(@"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Style TargetType='ContextMenu'>
    <Setter Property='OverridesDefaultStyle' Value='True'/>
    <Setter Property='HasDropShadow' Value='False'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='ContextMenu'>
        <Border Background='#F50C100E' BorderBrush='#2A3530' BorderThickness='1' CornerRadius='10' Padding='5'>
          <StackPanel IsItemsHost='True'/>
        </Border>
      </ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style TargetType='MenuItem'>
    <Setter Property='OverridesDefaultStyle' Value='True'/>
    <Setter Property='Foreground' Value='#D6DFD6'/>
    <Setter Property='FontFamily' Value='Segoe UI Variable Text, Segoe UI'/>
    <Setter Property='FontSize' Value='13'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='MenuItem'>
        <Border x:Name='bd' Background='Transparent' CornerRadius='6' Padding='12,7,28,7'>
          <ContentPresenter ContentSource='Header' RecognizesAccessKey='False'/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property='IsHighlighted' Value='True'><Setter TargetName='bd' Property='Background' Value='#22FFFFFF'/></Trigger>
          <Trigger Property='IsEnabled' Value='False'><Setter Property='Foreground' Value='#6A776E'/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate></Setter.Value></Setter>
  </Style>
</ResourceDictionary>");
    }
}
