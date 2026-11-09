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
    // Card filling is ordered here for all rows: the focused row, then the one below, above, and so on.
    public class ShelfList : View, IFeedView
    {
        private const int RowsAround = 2;   // rows kept shown above / below the focused one
        private const int PrefetchRows = 3;
        private static readonly int[] FillOrder = { 0, 1, -1, 2, 3, -2 };   // offsets from the focused row

        public int Count => rows.Count;
        public VideoItem FocusedItem => rows.Count > 0 ? rows[focus].FocusedItem : null;
        public int FocusedRow => focus;

        // Glorious hero mode: the first row sits below the hero text; moving off it scrolls the rows up to fill
        // the screen (and the home screen hides the hero). RowChanged fires when the focused row changes.
        public System.Action RowChanged;
        private const int HeroFirstRowTop = 384;   // where row 0 renders while the hero backdrop is shown

        private readonly int width;
        private readonly int leftInset;   // Glorious: rows start here; earlier cards slide under the nav
        private readonly bool bigFirst;   // Glorious: the first (recommendations) row uses large featured cards
        private readonly bool heroMode;   // Glorious: hero backdrop on row 0 only
        private int lastReportedRow = -1;
        private readonly View content;
        private readonly TextLabel status;
        private readonly List<FeedRow> rows = new List<FeedRow>();
        private Func<string, Task<FeedPage>> loader;
        private string cacheKey, continuation = "";
        private bool fresh, loading, focused;
        private int focus;
        private int generation;
        private readonly Func<bool> pump;
        private Animation scroll;

        public ShelfList(int width, int height, int leftInset = Theme.FocusMargin, bool bigFirst = false, bool heroMode = false)
        {
            this.width = width;
            this.leftInset = leftInset;
            this.bigFirst = bigFirst;
            this.heroMode = heroMode;
            pump = BuildNext;
            Size = new Size(width, height);
            ClippingMode = ClippingModeType.ClipChildren;
            content = new View { Size = new Size(width, 100000) };
            Add(content);
            status = Widgets.Label("", Theme.BodySize, Theme.TextDim, width - leftInset - Theme.FocusMargin, 120);
            status.MultiLine = true;
            status.Position = new Position(leftInset, Theme.FocusMargin + 20);
            Add(status);
        }

        private int RowHeightAt(int i) => bigFirst && i == 0 ? Theme.BigRowHeight : Theme.RowHeight;

        private int RowTop(int i)
        {
            int y = 0;
            for (int k = 0; k < i; k++)
            {
                y += RowHeightAt(k);
            }
            return y;
        }

        private void LayoutRows()
        {
            for (int i = 0; i < rows.Count; i++)
            {
                rows[i].PositionY = RowTop(i);
            }
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
            if (cacheKey != null)
            {
                UiGuard.Fire("ShelfList.ShowCached", () => ShowCached(cacheKey, generation));
            }
            UiGuard.Fire("ShelfList.Load", Load);
        }

        // The cached rows, read and parsed off the UI thread, shown unless the network was faster.
        private async Task ShowCached(string key, int gen)
        {
            FeedPage cached = await Task.Run(() => LocalStore.LoadFeed(key));
            if (cached == null || cached.Shelves.Count == 0 || gen != generation || disposed || fresh)
            {
                return;
            }
            AddShelves(cached.Shelves, seedContinuations: false);
            status.Text = "";
            AfterChange();
        }

        public void Wake()
        {
            UiWork.Request(pump);
        }

        // Asks the first rows again (new recommendations), back at the top right away so they do not land
        // under the focus. Rows and cards still showing the same thing are kept. A next page on its way is
        // dropped (generation).
        public void Refresh()
        {
            if (loader == null)
            {
                return;
            }
            generation++;
            loading = false;
            fresh = false;
            if (rows.Count > 0)
            {
                rows[focus].SetFocused(false);
                focus = 0;
                foreach (FeedRow r in rows)
                {
                    r.ResetFocus();
                }
                AfterChange();
            }
            UiGuard.Fire("ShelfList.Load", Load);
        }

        protected override void Dispose(DisposeTypes type)
        {
            UiWork.Cancel(pump);
            base.Dispose(type);
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

        // `reuse`: rows shown from the cache, recycled when the shelf at the same place has the same title.
        private void AddShelves(List<Shelf> shelves, bool seedContinuations, List<FeedRow> reuse = null)
        {
            foreach (Shelf s in shelves)
            {
                string title = s.Title.Length > 0 ? s.Title : T("Recommended");
                int i = rows.Count;
                FeedRow row = reuse != null && i < reuse.Count && reuse[i]?.Title == title ? reuse[i] : null;
                if (row != null)
                {
                    reuse[i] = null;
                }
                else
                {
                    // Items beyond the shelf's first ones: the same browse call with the row's continuation.
                    bool big = bigFirst && i == 0;
                    row = new FeedRow(title, c => YouTube.Browse("", c), width,
                                      big ? Theme.BigCardWidth : Theme.CardWidth, leftInset, RowHeightAt(i))
                    {
                        Position = new Position(0, RowTop(i)),
                        RequestWork = () => UiWork.Request(pump),
                    };
                    content.Add(row);
                }
                rows.Add(row);
                row.Seed(s.Items, seedContinuations ? s.Continuation : "");
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
            if (gen != generation || disposed)
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
            List<FeedRow> reuse = null;
            if (!fresh)
            {
                // The first network page replaces the cached rows (keeping the focused row index, and the rows
                // and cards that show the same thing).
                fresh = true;
                reuse = new List<FeedRow>(rows);
                rows.Clear();
                if (cacheKey != null)
                {
                    string key = cacheKey;
                    _ = Task.Run(() => LocalStore.SaveFeed(key, page));
                }
            }
            continuation = page.Continuation;
            AddShelves(page.Shelves, seedContinuations: true, reuse);
            foreach (FeedRow r in reuse ?? new List<FeedRow>())
            {
                if (r != null)
                {
                    content.Remove(r);
                    r.Dispose();
                }
            }
            focus = Math.Min(focus, Math.Max(0, rows.Count - 1));
            status.Text = rows.Count == 0 ? T("No videos") : "";
            AfterChange();
            MaybePrefetch();
        }

        private void MaybePrefetch()
        {
            if (fresh && !loading && !string.IsNullOrEmpty(continuation) && focus >= rows.Count - 1 - PrefetchRows)
            {
                UiGuard.Fire("ShelfList.Load", Load);
            }
        }

        private void AfterChange()
        {
            LayoutRows();
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

        // The focused row at the top; rows far from it are hidden (nothing to render). In hero mode the first row
        // is held lower (so the hero title shows above it); moving to any later row scrolls the list up to the top.
        private void ScrollToFocus()
        {
            if (focus != lastReportedRow)
            {
                lastReportedRow = focus;
                RowChanged?.Invoke();
            }
            float target = heroMode && focus == 0 ? HeroFirstRowTop : -RowTop(focus);
            scroll = Widgets.Slide(content, 0, target, scroll);
            for (int i = 0; i < rows.Count; i++)
            {
                bool near = i >= focus - RowsAround && i <= focus + RowsAround + 1;
                if (near != rows[i].Visibility)
                {
                    Widgets.SetVisible(rows[i], near);
                }
            }
            UiWork.Request(pump);
        }

        // One card filled per call, in the rows around the focus (see FillOrder).
        private bool BuildNext()
        {
            if (disposed || !Visibility)
            {
                return false;   // a hidden feed (other menu entry): Wake() when shown again
            }
            foreach (int d in FillOrder)
            {
                int i = focus + d;
                if (i >= 0 && i < rows.Count && rows[i].BuildNext())
                {
                    return true;
                }
            }
            return false;
        }
    }
}
