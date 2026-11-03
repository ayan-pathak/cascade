using System;
using System.Collections.Generic;
using System.Linq;

namespace Cascade
{
    // Live DWM thumbnails of other windows, drawn by the compositor on top of one of our windows.
    public class Thumbnails
    {
        readonly IntPtr dest;
        readonly Dictionary<IntPtr, IntPtr> live = new Dictionary<IntPtr, IntPtr>();

        public Thumbnails(IntPtr dest) { this.dest = dest; }

        // Shows each source window fitted (aspect kept, centred) inside its box, in physical pixels
        // relative to the destination window. Sources not listed are removed.
        public void Set(IEnumerable<(IntPtr src, RECT box)> items)
        {
            var keep = new HashSet<IntPtr>();
            foreach (var (src, box) in items)
            {
                if (!Native.IsWindow(src) || box.Width <= 0 || box.Height <= 0) continue;
                if (!live.TryGetValue(src, out var t))
                {
                    if (Native.DwmRegisterThumbnail(dest, src, out t) != 0) continue;
                    live[src] = t;
                }
                keep.Add(src);
                Native.DwmQueryThumbnailSourceSize(t, out var size);
                var r = Fit(box, size.cx, size.cy);
                var p = new DWM_THUMBNAIL_PROPERTIES
                {
                    dwFlags = 0x1 | 0x4 | 0x8 | 0x10,
                    rcDestination = r,
                    opacity = 255,
                    fVisible = true,
                    fSourceClientAreaOnly = false
                };
                Native.DwmUpdateThumbnailProperties(t, ref p);
            }
            foreach (var src in live.Keys.Where(k => !keep.Contains(k)).ToList())
            {
                Native.DwmUnregisterThumbnail(live[src]);
                live.Remove(src);
            }
        }

        public void Clear() => Set(Enumerable.Empty<(IntPtr, RECT)>());

        public bool Has(IntPtr src) => live.ContainsKey(src);

        static RECT Fit(RECT box, int sw, int sh)
        {
            if (sw <= 0 || sh <= 0) return box;
            double k = Math.Min(box.Width / (double)sw, box.Height / (double)sh);
            int w = (int)(sw * k), h = (int)(sh * k);
            int x = box.Left + (box.Width - w) / 2, y = box.Top + (box.Height - h) / 2;
            return new RECT(x, y, x + w, y + h);
        }
    }
}
