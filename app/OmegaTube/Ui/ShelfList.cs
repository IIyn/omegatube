using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using OmegaTube.Data;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using static OmegaTube.I18n;

namespace OmegaTube.Ui
{
    // The home as YouTube's TV app shows it: titled rows by topic ("Recommandations", "Jeux vidéo"...), each a
    // horizontally scrolling FeedRow, stacked vertically with the focused row at the top. More rows load as the
    // focus nears the last one; a row asks for more of its own items as its focus nears its end.
    public class ShelfList : View
    {
        private const int RowsAround = 2;   // rows kept shown above / below the focused one
        private const int PrefetchRows = 3;

        public int Count => rows.Count;
        public VideoItem FocusedItem => rows.Count > 0 ? rows[focus].FocusedItem : null;

        private readonly int width;
        private readonly View content;
        private readonly TextLabel status;
        private readonly List<FeedRow> rows = new List<FeedRow>();
        private Func<string, Task<FeedPage>> loader;
        private string cacheKey, continuation = "";
        private bool fresh, loading, focused;
        private int focus;
        private int generation;
        private Animation scroll;

        public ShelfList(int width, int height)
        {
            this.width = width;
            Size = new Size(width, height);
            ClippingMode = ClippingModeType.ClipChildren;
            content = new View { Size = new Size(width, 100000) };
            Add(content);
            status = Widgets.Label("", Theme.BodySize, Theme.TextDim, width - 2 * Theme.FocusMargin, 120);
            status.MultiLine = true;
            status.Position = new Position(Theme.FocusMargin, Theme.FocusMargin + 20);
            Add(status);
        }

        // loader("") gives the first rows, loader(continuation) the next ones. cacheKey: shown meanwhile.
        public void Show(Func<string, Task<FeedPage>> newLoader, string newCacheKey)
        {
            generation++;
            Clear();
            loader = newLoader;
            cacheKey = newCacheKey;
            continuation = "";
            fresh = false;
            loading = false;
            focus = 0;
            content.PositionY = 0;
            status.Text = T("Loading…");
            FeedPage cached = cacheKey != null ? LocalStore.LoadFeed(cacheKey) : null;
            if (cached != null && cached.Shelves.Count > 0)
            {
                AddShelves(cached.Shelves, seedContinuations: false);
                status.Text = "";
                AfterChange();
            }
            _ = Load();
        }

        private void Clear()
        {
            foreach (FeedRow r in rows)
            {
                content.Remove(r);
                r.Dispose();
            }
            rows.Clear();
        }

        private void AddShelves(List<Shelf> shelves, bool seedContinuations)
        {
            foreach (Shelf s in shelves)
            {
                // Items beyond the shelf's first ones: the same browse call with the row's continuation.
                var row = new FeedRow(s.Title.Length > 0 ? s.Title : T("Recommended"), c => YouTube.Browse("", c), width)
                {
                    Position = new Position(0, rows.Count * Theme.RowHeight),
                };
                row.Seed(s.Items, seedContinuations ? s.Continuation : "");
                rows.Add(row);
                content.Add(row);
            }
        }

        private async Task Load()
        {
            if (loading || loader == null)
            {
                return;
            }
            loading = true;
            int gen = generation;
            FeedPage page = await loader(fresh ? continuation : "");
            if (gen != generation)
            {
                return;
            }
            loading = false;
            if (page.Error != null)
            {
                if (rows.Count == 0)
                {
                    status.Text = T("Error: {0}", page.Error);
                }
                return;
            }
            if (!fresh)
            {
                // The first network page replaces the cached rows (keeping the focused row index).
                fresh = true;
                Clear();
                if (cacheKey != null)
                {
                    LocalStore.SaveFeed(cacheKey, page);
                }
            }
            continuation = page.Continuation;
            AddShelves(page.Shelves, seedContinuations: true);
            focus = Math.Min(focus, Math.Max(0, rows.Count - 1));
            status.Text = rows.Count == 0 ? T("No videos") : "";
            AfterChange();
            MaybePrefetch();
        }

        private void MaybePrefetch()
        {
            if (fresh && !loading && !string.IsNullOrEmpty(continuation) && focus >= rows.Count - 1 - PrefetchRows)
            {
                _ = Load();
            }
        }

        private void AfterChange()
        {
            for (int i = 0; i < rows.Count; i++)
            {
                rows[i].SetFocused(focused && i == focus);
            }
            ScrollToFocus();
        }

        public void SetFocused(bool value)
        {
            if (focused == value)
            {
                return;
            }
            focused = value;
            if (rows.Count > 0)
            {
                rows[focus].SetFocused(value);
            }
        }

        public void RefreshProgress()
        {
            foreach (FeedRow r in rows)
            {
                r.RefreshProgress();
            }
        }

        // Arrow keys. Returns false when the move leaves the list on that side (Left at a row's first card).
        public bool Move(string key)
        {
            if (rows.Count == 0)
            {
                return key != "Left";
            }
            switch (key)
            {
                case "Left":
                    return rows[focus].Move(-1);
                case "Right":
                    rows[focus].Move(1);
                    return true;
                case "Up":
                case "Down":
                    int next = Math.Max(0, Math.Min(rows.Count - 1, focus + (key == "Up" ? -1 : 1)));
                    if (next != focus)
                    {
                        rows[focus].SetFocused(false);
                        focus = next;
                        rows[focus].SetFocused(true);
                        ScrollToFocus();
                        MaybePrefetch();
                    }
                    return true;
            }
            return true;
        }

        // The focused row at the top; rows far from it are hidden (nothing to render).
        private void ScrollToFocus()
        {
            scroll = Widgets.Slide(content, 0, -focus * Theme.RowHeight, scroll);
            for (int i = 0; i < rows.Count; i++)
            {
                Widgets.SetVisible(rows[i], i >= focus - RowsAround && i <= focus + RowsAround + 1);
            }
        }
    }
}
