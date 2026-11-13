using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace OmegaTube.Ui
{
    // Resolves the streams of the focused first-row video and the next ones in the background (core:
    // omega_preview_prefetch — InnerTube only, no decoder, no watch history), so their previews skip that round
    // trip and start on time. One request at a time, the focused video first; only the latest wanted list is
    // worked through, so scrolling on drops what is no longer around the focus. UI thread.
    internal static class PreviewPrefetcher
    {
        private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(80);    // the core keeps them 90 min
        private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(2);
        private static readonly Dictionary<string, DateTime> resolvedUntil = new Dictionary<string, DateTime>();
        private static List<string> wanted = new List<string>();
        private static bool running;

        public static void Want(IEnumerable<string> ids)
        {
            var list = new List<string>();
            foreach (string id in ids)
            {
                if (!string.IsNullOrEmpty(id) && !list.Contains(id))
                {
                    list.Add(id);
                }
            }
            wanted = list;
            if (!running)
            {
                UiGuard.Fire("PreviewPrefetcher.Run", Run);
            }
        }

        // Nothing around the focus any more (hero hidden): stop after the request in flight.
        public static void Clear()
        {
            wanted = new List<string>();
        }

        private static async Task Run()
        {
            running = true;
            try
            {
                for (;;)
                {
                    string next = Next();
                    if (next == null)
                    {
                        return;
                    }
                    bool ok = await Task.Run(() => NativeCore.PreviewPrefetch(next));
                    resolvedUntil[next] = DateTime.Now + (ok ? Fresh : RetryAfter);   // a failure is not hammered
                }
            }
            finally
            {
                running = false;
            }
        }

        private static string Next()
        {
            DateTime now = DateTime.Now;
            foreach (string key in new List<string>(resolvedUntil.Keys))
            {
                if (resolvedUntil[key] <= now)
                {
                    resolvedUntil.Remove(key);
                }
            }
            foreach (string id in wanted)
            {
                if (!resolvedUntil.ContainsKey(id))
                {
                    return id;
                }
            }
            return null;
        }
    }
}
