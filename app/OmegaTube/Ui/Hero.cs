using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using OmegaTube.Data;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace OmegaTube.Ui
{
    // Full-screen backdrop for the "Glorious" home style: the focused item's thumbnail fills the screen and
    // fades to black on the left (under the nav / behind the text) and along the bottom (under the rows), with
    // the item's title and a metadata line over it. Hero.Display(item) is called by HomeScreen as focus moves.
    //
    // The feed only carries the small mqdefault thumbnail (320×180), which is why a stretched backdrop looked
    // blurry. For the backdrop we fetch the full-resolution frames from the id instead: a reliable hqdefault
    // base layer with the sharp maxresdefault on top of it, so when maxres is missing (not every video has one)
    // the hqdefault still shows rather than a blank or a stale frame.
    //
    // The darkening is two full-screen gradients (not cropped rectangles), so there is no visible band/seam
    // where a gradient rectangle used to end; multiple stops keep the ramp short enough to limit TV banding.
    public class Hero : View
    {
        private const int PreviewDelayMs = 600;   // dwell before the animated preview starts (avoids one per scroll tick)
        private const int KeptSheets = 8;          // decoded preview sheets kept in the cache folder

        // Animated previews: YouTube's moving thumbnail is an animated WebP, which the Tizen 5.5 image loader only
        // shows as a still. The core decodes it into one BMP sprite sheet (all frames in a grid); we load that once
        // and step the visible PixelArea from cell to cell on a timer — one texture, no reloads.
        private static readonly Dictionary<string, PreviewSheet> sheets = new Dictionary<string, PreviewSheet>();
        private static readonly HashSet<string> noPreview = new HashSet<string>();   // looked up: YouTube has none

        private readonly ImageView artBase, artHi, preview;
        private readonly TextLabel title, meta;
        private Animation fade, previewFade;
        private Timer previewTimer, frameTimer;
        private VideoItem currentItem;
        private PreviewSheet playing;
        private string loadingId;   // a preview being looked up / decoded
        private int frame;
        private string currentId = "";

        public Hero()
        {
            Size = new Size(1920, 1080);
            BackgroundColor = Theme.Background;

            artBase = new ImageView { Size = new Size(1920, 1080), FittingMode = FittingModeType.ScaleToFill };
            Add(artBase);
            artHi = new ImageView { Size = new Size(1920, 1080), FittingMode = FittingModeType.ScaleToFill };
            Add(artHi);
            // Animated-preview layer: shows one cell (PixelArea) of the decoded sprite sheet, faded in over the stills.
            // No fitting mode: the whole sheet must load unscaled so the cells line up.
            preview = new ImageView { Size = new Size(1920, 1080), Opacity = 0f };
            Add(preview);

            // Left fade: solid black at the left edge -> clear by the centre (full-screen, so no vertical seam).
            Add(Widgets.Gradient(new Vector2(-0.5f, 0f), new Vector2(0.5f, 0f),
                new[] { 0f, 0.28f, 0.5f },
                new[] { new Vector4(0, 0, 0, 1f), new Vector4(0, 0, 0, 0.55f), new Vector4(0, 0, 0, 0f) },
                1920, 1080, 0, 0));
            // Bottom fade: clear in the upper screen -> dark across the lower band under the rows.
            Add(Widgets.Gradient(new Vector2(0f, 0.5f), new Vector2(0f, -0.5f),
                new[] { 0f, 0.5f, 1f },
                new[] { new Vector4(0, 0, 0, 0.92f), new Vector4(0, 0, 0, 0.15f), new Vector4(0, 0, 0, 0f) },
                1920, 1080, 0, 0));

            title = Widgets.Label("", Theme.TitleSize + 8, Theme.Text, 760, 118);
            title.Position = new Position(Theme.GloriousInset, 196);
            title.MultiLine = true;
            Widgets.Bold(title);
            Add(title);

            meta = Widgets.Label("", Theme.BodySize + 2, Theme.TextDim, 760, 44);
            meta.Position = new Position(Theme.GloriousInset, 322);
            Add(meta);
        }

        private static string Frame(string id, string variant) => "https://i.ytimg.com/vi/" + id + "/" + variant + ".jpg";

        // Sets the item's artwork + title + metadata. Already the shown item: only resumes its preview (the hero
        // was hidden meanwhile).
        public void Display(VideoItem item)
        {
            if (item == null)
            {
                return;
            }
            if (item.Id == currentId)
            {
                ResumePreview();
                return;
            }
            currentId = item.Id;
            currentItem = item;
            if (!string.IsNullOrEmpty(item.Id))
            {
                artBase.ResourceUrl = Frame(item.Id, "hqdefault");
                artHi.ResourceUrl = Frame(item.Id, "maxresdefault");
            }
            else
            {
                artBase.ResourceUrl = item.Thumbnail ?? "";
                artHi.ResourceUrl = "";
            }
            title.Text = item.Title ?? "";
            meta.Text = MetaLine(item);
            fade?.Stop();
            artBase.Opacity = 0.5f;
            fade = new Animation(260);
            fade.AnimateTo(artBase, "Opacity", 1f, new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOut));
            fade.Play();
            SchedulePreview(item);
        }

        // Clears any running preview, then (if this item has one) starts it after a short dwell.
        private void SchedulePreview(VideoItem item)
        {
            StopPreview();
            // No preview URL in the item (the TV home feed's tiles carry none): the core looks it up by id.
            if (string.IsNullOrEmpty(item.Id) || noPreview.Contains(item.Id) || loadingId == item.Id)
            {
                return;
            }
            string url = item.Preview ?? "", id = item.Id;
            previewTimer = new Timer(PreviewDelayMs);
            previewTimer.Tick += (s, e) => UiGuard.Tick("Hero.preview", () =>
            {
                previewTimer = null;
                if (id == currentId)
                {
                    UiGuard.Fire("Hero.preview", () => LoadPreview(id, url));
                }
                return false;   // one-shot
            });
            previewTimer.Start();
        }

        // Decodes the preview (core, off the UI thread) unless this video's sheet is already on disk, then plays it.
        private async Task LoadPreview(string id, string url)
        {
            if (!sheets.TryGetValue(id, out PreviewSheet sheet) || !File.Exists(sheet.Path))
            {
                if (loadingId == id)
                {
                    return;
                }
                string path = System.IO.Path.Combine(CacheDir, "preview-" + id + ".bmp");
                loadingId = id;
                try
                {
                    sheet = await YouTube.Preview(id, url, path);
                }
                finally
                {
                    if (loadingId == id)
                    {
                        loadingId = null;
                    }
                }
                if (sheet == null || sheet.None)
                {
                    noPreview.Add(id);   // none, or failed: not retried this session
                    return;              // the still stays
                }
                sheets[id] = sheet;
                Prune(path);
            }
            if (id == currentId && Visibility)
            {
                Play(sheet);
            }
        }

        private void Play(PreviewSheet sheet)
        {
            playing = sheet;
            frame = 0;
            preview.ResourceUrl = sheet.Path;
            ShowFrame();
            previewFade?.Stop();
            previewFade = new Animation(300);
            previewFade.AnimateTo(preview, "Opacity", 1f, new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOut));
            previewFade.Play();
            StartFrames();
        }

        private void StartFrames()
        {
            frameTimer?.Stop();
            frameTimer = new Timer((uint)Math.Max(40, playing.DelayMs));
            frameTimer.Tick += (s, e) => UiGuard.Tick("Hero.frames", () =>
            {
                if (playing == null || !Visibility)
                {
                    frameTimer = null;   // hidden (later row, hosted screen): resumed by Display / ResumePreview
                    return false;
                }
                frame = (frame + 1) % playing.Frames;
                ShowFrame();
                return true;
            });
            frameTimer.Start();
        }

        private void ShowFrame()
        {
            int col = frame % playing.Cols, row = frame / playing.Cols;
            preview.PixelArea = new RelativeVector4((float)col / playing.Cols, (float)row / playing.Rows,
                                                    1f / playing.Cols, 1f / playing.Rows);
        }

        private void StopPreview()
        {
            previewTimer?.Stop();
            previewTimer = null;
            frameTimer?.Stop();
            frameTimer = null;
            previewFade?.Stop();
            playing = null;
            preview.Opacity = 0f;
            preview.ResourceUrl = "";   // release the sheet's texture
        }

        // The hero stopped being on screen (another screen opened): stop stepping frames, keep the sheet.
        public void PausePreview()
        {
            previewTimer?.Stop();
            previewTimer = null;
            frameTimer?.Stop();
            frameTimer = null;
        }

        private void ResumePreview()
        {
            if (playing != null)
            {
                if (frameTimer == null)
                {
                    StartFrames();
                }
            }
            else if (previewTimer == null && currentItem != null)
            {
                SchedulePreview(currentItem);
            }
        }

        private static string CacheDir => Tizen.Applications.Application.Current.DirectoryInfo.Cache;

        // Keeps the most recent sheets on disk (about 6 MB each), deletes the rest.
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

        // "Channel · 1.2M views · 3 days ago", skipping the parts YouTube did not give.
        private static string MetaLine(VideoItem item)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(item.Channel)) parts.Add(item.Channel);
            if (!string.IsNullOrEmpty(item.Views)) parts.Add(item.Views);
            if (!string.IsNullOrEmpty(item.Published)) parts.Add(item.Published);
            return string.Join("   ·   ", parts);
        }
    }
}
