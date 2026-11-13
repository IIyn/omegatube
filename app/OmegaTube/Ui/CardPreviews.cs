using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using OmegaTube.Data;
using Tizen.NUI;

namespace OmegaTube.Ui
{
    // Glorious: the focused card of the second row and below plays YouTube's animated preview (a short moving
    // thumbnail) over its thumbnail after a short rest. The Tizen 5.5 image loader shows an animated WebP as a
    // still, so the core decodes it into one BMP sprite sheet (all frames in a grid); the card loads that once and
    // this steps its visible PixelArea from cell to cell on a timer: one texture, no reloads, and no video decoder
    // (so it never competes with the first row's video preview). Sheets are cached per video, a few on disk.
    // One download + decode runs at a time; a card that waited for it is served when it finishes. UI thread only.
    internal static class CardPreviews
    {
        private const int DwellMs = 600;          // rest on a card this long before its preview starts (not per scroll tick)
        private const int KeptSheets = 8;         // decoded sheets kept in the cache folder (about 6 MB each)
        private const int RetryAfterFailureS = 60;   // a download that failed (network) is tried again after this

        private static readonly Dictionary<string, PreviewSheet> sheets = new Dictionary<string, PreviewSheet>();
        private static readonly HashSet<string> noPreview = new HashSet<string>();   // looked up: YouTube has none
        private static readonly Dictionary<string, DateTime> retryAt = new Dictionary<string, DateTime>();
        private static readonly RelativeVector4 cell = new RelativeVector4(0f, 0f, 1f, 1f);   // reused: one per frame otherwise
        private static Timer dwell, frameTimer;
        private static Card card;                 // the card showing, or about to show, a preview
        private static string wantedId, wantedUrl, loadingId;
        private static PreviewSheet playing;
        private static int frame;

        private static string CacheDir => Tizen.Applications.Application.Current.DirectoryInfo.Cache;

        // The focus is on this card (null / not animatable: nothing plays). The same card again changes nothing.
        public static void Show(Card focused)
        {
            if (focused == card && (playing != null || dwell != null || loadingId != null))
            {
                return;
            }
            Stop();
            if (focused == null || !focused.CanAnimate || focused.Item == null || string.IsNullOrEmpty(focused.Item.Id)
                || noPreview.Contains(focused.Item.Id)
                || (retryAt.TryGetValue(focused.Item.Id, out DateTime later) && later > DateTime.Now))
            {
                return;
            }
            card = focused;
            string id = focused.Item.Id;
            wantedId = id;
            wantedUrl = focused.Item.Preview ?? "";
            dwell = new Timer(DwellMs);
            dwell.Tick += (s, e) => UiGuard.Tick("CardPreviews.dwell", () =>
            {
                dwell = null;
                if (wantedId == id && card != null)
                {
                    UiGuard.Fire("CardPreviews.load", () => Load(id, wantedUrl));
                }
                return false;   // one-shot
            });
            dwell.Start();
        }

        // Focus left the card (or the home screen / the setting went away): stop and give the texture back.
        public static void Stop()
        {
            dwell?.Stop();
            dwell = null;
            frameTimer?.Stop();
            frameTimer = null;
            wantedId = null;
            playing = null;
            if (card != null)
            {
                try
                {
                    card.EndAnimation();
                }
                catch (Exception e)
                {
                    Tizen.Log.Warn("OmegaTube", "card preview stop: " + e.Message);   // card already gone
                }
                card = null;
            }
        }

        // Makes the video's sheet (core, off the UI thread, unless it is already on disk) and plays it if its card
        // is still the focused one. Only one decode at a time: another card that rests meanwhile waits its turn.
        private static async Task Load(string id, string url)
        {
            if (sheets.TryGetValue(id, out PreviewSheet sheet) && File.Exists(sheet.Path))
            {
                TryTouch(sheet.Path);   // recently used: pruned last
            }
            else
            {
                if (loadingId != null)
                {
                    return;   // taken up when the one in flight finishes (see below)
                }
                string path = System.IO.Path.Combine(CacheDir, "preview-" + id + ".bmp");
                loadingId = id;
                try
                {
                    sheet = await YouTube.Preview(id, url, path);
                }
                finally
                {
                    loadingId = null;
                }
                if (sheet == null)
                {
                    retryAt[id] = DateTime.Now.AddSeconds(RetryAfterFailureS);   // probably the network: not for good
                }
                else if (sheet.None)
                {
                    noPreview.Add(id);   // YouTube has none (or it is not an animation)
                    sheet = null;
                }
                else
                {
                    sheets[id] = sheet;
                    Prune(path);
                }
                if (wantedId != null && wantedId != id && card != null)
                {
                    string next = wantedId, nextUrl = wantedUrl;   // a card rested while this one was decoding
                    UiGuard.Fire("CardPreviews.load", () => Load(next, nextUrl));
                }
                if (sheet == null)
                {
                    return;
                }
            }
            if (wantedId == id && card != null && BmpSize(sheet.Path, out int w, out int h))
            {
                Play(sheet, w, h);
            }
        }

        private static void Play(PreviewSheet sheet, int w, int h)
        {
            playing = sheet;
            frame = 0;
            card.BeginAnimation(sheet.Path, w, h, Cell());
            frameTimer = new Timer((uint)Math.Max(40, sheet.DelayMs));
            frameTimer.Tick += (s, e) => UiGuard.Tick("CardPreviews.frames", () =>
            {
                if (playing == null || card == null)
                {
                    frameTimer = null;
                    return false;
                }
                frame = (frame + 1) % playing.Frames;
                card.ShowAnimationFrame(Cell());
                return true;
            });
            frameTimer.Start();
        }

        // The visible cell of the sheet for the current frame (one shared object, updated in place).
        private static RelativeVector4 Cell()
        {
            cell.X = (float)(frame % playing.Cols) / playing.Cols;
            cell.Y = (float)(frame / playing.Cols) / playing.Rows;
            cell.Z = 1f / playing.Cols;
            cell.W = 1f / playing.Rows;
            return cell;
        }

        // Width and height of the BMP sprite sheet from its header (the sheet must be loaded at its real size).
        private static bool BmpSize(string path, out int w, out int h)
        {
            w = h = 0;
            try
            {
                using (FileStream f = File.OpenRead(path))
                {
                    var b = new byte[26];
                    if (f.Read(b, 0, 26) < 26 || b[0] != (byte)'B' || b[1] != (byte)'M')
                    {
                        return false;
                    }
                    w = BitConverter.ToInt32(b, 18);
                    h = Math.Abs(BitConverter.ToInt32(b, 22));
                    return w > 0 && h > 0;
                }
            }
            catch (Exception e)
            {
                Tizen.Log.Warn("OmegaTube", "preview sheet: " + e.Message);
                return false;
            }
        }

        private static void TryTouch(string path)
        {
            try
            {
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            }
            catch (Exception)
            {
                // read-only cache or a vanished file: the sheet just keeps its age
            }
        }

        // Keeps the most recent sheets on disk, deletes the rest (and any half-written leftovers).
        private static void Prune(string keep)
        {
            try
            {
                FileInfo[] files = new DirectoryInfo(CacheDir).GetFiles("preview-*.bmp");
                Array.Sort(files, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                for (int i = KeptSheets; i < files.Length; i++)
                {
                    if (files[i].FullName != keep)
                    {
                        files[i].Delete();
                    }
                }
                foreach (FileInfo stale in new DirectoryInfo(CacheDir).GetFiles("preview-*.bmp.tmp"))
                {
                    stale.Delete();   // a write that was interrupted
                }
                foreach (string key in new List<string>(sheets.Keys))
                {
                    if (!File.Exists(sheets[key].Path))
                    {
                        sheets.Remove(key);
                    }
                }
            }
            catch (Exception e)
            {
                Tizen.Log.Warn("OmegaTube", "preview prune: " + e.Message);
            }
        }
    }
}
