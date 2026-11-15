using System;

namespace OmegaTube.Data
{
    // Card artwork. The core asks YouTube for mqdefault (320x180), which is what this app has always drawn and what
    // the small Omega/Glorious cards still use. The Tube look draws stills at 660x371, where that file is a 2x
    // upscale, so "Sharp" asks for YouTube's 1280x720 copy of the same picture instead. "Data saver" keeps the
    // published size, i.e. exactly what upstream does — the setting exists so nobody inherits our change by accident.
    //
    // Sharp mirrors AppConfig.SharpThumbnails, the way Theme.Glorious and Theme.Tube mirror the interface style: a
    // card is a View and has no handle on the app, and the cards need this on the UI thread only.
    public static class Thumbs
    {
        private const string Small = "/mqdefault.jpg";
        private const string Hd = "/hq720.jpg";

        public static bool Sharp;

        public static string Url(string url)
        {
            if (string.IsNullOrEmpty(url) || !Sharp)
            {
                return url;
            }
            int at = url.IndexOf(Small, StringComparison.Ordinal);
            return at < 0 ? url : url.Substring(0, at) + Hd;
        }
    }
}
