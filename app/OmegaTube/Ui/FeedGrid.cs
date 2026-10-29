using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using OmegaTube.Data;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using static OmegaTube.I18n;

namespace OmegaTube.Ui
{
    // A vertically scrolling grid of cards backed by a paged feed (Home, Subscriptions, History, sections).
    // The focused row is kept second from the top; more pages load well before the focus reaches the end.
    // Smoothness on the TV: cards exist only for the rows around the focus (and are shown only there: hidden
    // actors cost the renderer nothing); their texts and thumbnails are filled by UiWork, focused row first.
    // When the feed has highlights (Subscriptions: YouTube's "most relevant" shelf), they sit above the grid
    // as a horizontally scrolling row; Up from the first grid row enters it, Down leaves it.
    public class FeedGrid : View, IFeedView
    {
        private const int Columns = 4;
        private const int GapX = 34, GapY = 40;
        private const int Pad = Theme.FocusMargin;   // room for the focused card's scale-up
        private const int HeadingH = 56;              // section title above the highlights row and the grid
        private const int RowsBefore = 2, RowsAfter = 4;   // rows kept shown around the focused one
        private const int PrefetchRows = 5;           // next page asked when the focus is this close to the end

        public int Count => items.Count + highlights.Count;
        public VideoItem FocusedItem => inHighlights ? highlights[hlFocus].Item : items.Count > 0 ? items[focus] : null;

        private readonly int cardW, rowStep;
        private readonly View content;
        private readonly View hlStrip;
        private readonly TextLabel hlHeading, gridHeading;
        private readonly TextLabel status;
        private readonly List<VideoItem> items = new List<VideoItem>();
        private readonly List<Card> cards = new List<Card>();   // cards of items[0 .. cards.Count)
        private readonly List<Card> highlights = new List<Card>();
        private Func<string, Task<FeedPage>> loader;
        private string continuation = "";
        private string cacheKey;
        private bool fresh, loading, focused;
        private int focus;
        private bool inHighlights;
        private int hlFocus;
        private int gridTop;      // y of the first grid row inside content (below the highlights)
        private int generation;   // ignores pages of a previous Show()
        private Animation scroll, hlScroll;
        private readonly Func<bool> pump;

        public FeedGrid(int width, int height)
        {
            pump = BuildNext;
            Size = new Size(width, height);
            ClippingMode = ClippingModeType.ClipChildren;
            cardW = (width - 2 * Pad - (Columns - 1) * GapX) / Columns;
            rowStep = Theme.CardHeight(cardW) + GapY;
            content = new View { Position = new Position(0, Pad), Size = new Size(width, 100000) };
            Add(content);
            hlHeading = Widgets.Label(T("Most relevant"), Theme.TitleSize - 6, Theme.Text, width - 2 * Pad, HeadingH);
            hlHeading.PositionX = Pad;
            hlStrip = new View { Position = new Position(0, HeadingH), Size = new Size(100000, rowStep) };
            gridHeading = Widgets.Label(T("All videos"), Theme.TitleSize - 6, Theme.Text, width - 2 * Pad, HeadingH);
            gridHeading.Position = new Position(Pad, HeadingH + rowStep);
            foreach (View v in new View[] { hlHeading, hlStrip, gridHeading })
            {
                v.Hide();
                content.Add(v);
            }
            status = Widgets.Label("", Theme.BodySize, Theme.TextDim, width - 2 * Pad, 120);
            status.MultiLine = true;
            status.Position = new Position(Pad, Pad + 20);
            Add(status);
        }

        // Replaces the content with another feed. cacheKey: show the cached first page meanwhile.
        public void Show(Func<string, Task<FeedPage>> newLoader, string newCacheKey, string emptyText = null)
        {
            generation++;
            hlHeading.Text = T("Most relevant");   // the app language may have changed since the last feed
            gridHeading.Text = T("All videos");
            ClearCards();
            focus = 0;
            continuation = "";
            fresh = false;
            loading = false;
            loader = newLoader;
            cacheKey = newCacheKey;
            content.PositionY = Pad;
            if (loader == null)
            {
                status.Text = emptyText ?? "";
                return;
            }
            status.Text = T("Loading…");
            AfterChange();
            if (cacheKey != null)
            {
                _ = ShowCached(cacheKey, generation);
            }
            _ = Load();
        }

        // The cached first page, read and parsed off the UI thread, shown unless the network was faster.
        private async Task ShowCached(string key, int gen)
        {
            FeedPage cached = await Task.Run(() => LocalStore.LoadFeed(key));
            if (cached == null || gen != generation || disposed || fresh)
            {
                return;
            }
            SetHighlights(cached.Highlights);
            items.AddRange(cached.Items);
            status.Text = "";
            AfterChange();
        }

        public void Wake()
        {
            UiWork.Request(pump);
        }

        // Asks the first page again (new videos come first), back at the top right away so they do not land
        // under the focus. Cards of videos still there are kept. A next page on its way is dropped (generation).
        public void Refresh()
        {
            if (loader == null)
            {
                return;
            }
            generation++;
            loading = false;
            fresh = false;
            FocusedCard()?.SetFocused(false);
            focus = 0;
            hlFocus = 0;
            inHighlights = false;   // first grid card, as on opening (the highlights stay in view above)
            AfterChange();
            _ = Load();
        }

        protected override void Dispose(DisposeTypes type)
        {
            UiWork.Cancel(pump);
            base.Dispose(type);
        }

        private void ClearCards()
        {
            items.Clear();
            foreach (Card c in cards)
            {
                content.Remove(c);
                c.Dispose();
            }
            cards.Clear();
            SetHighlights(new List<VideoItem>());
            inHighlights = false;
        }

        // Cards of videos already shown are kept (network page after the cached one).
        private void SetHighlights(List<VideoItem> newItems)
        {
            Dictionary<string, Card> old = ById(highlights);
            highlights.Clear();
            foreach (VideoItem item in newItems)
            {
                Card card = Take(old, item) ?? new Card(item, cardW);
                card.Position = new Position(Pad + highlights.Count * (cardW + GapX), 0);
                if (card.GetParent() == null)
                {
                    hlStrip.Add(card);
                }
                highlights.Add(card);
            }
            Drop(old);
            bool any = highlights.Count > 0;
            foreach (View v in new View[] { hlHeading, hlStrip, gridHeading })
            {
                Widgets.SetVisible(v, any);
            }
            gridTop = any ? 2 * HeadingH + rowStep : 0;
            hlFocus = Math.Min(hlFocus, Math.Max(0, highlights.Count - 1));
            hlStrip.PositionX = 0;
        }

        // New items in place of the current ones, keeping the cards of videos still there (and their thumbnail).
        private void ReplaceItems(List<VideoItem> newItems)
        {
            Dictionary<string, Card> old = ById(cards);
            int built = cards.Count;
            cards.Clear();
            items.Clear();
            items.AddRange(newItems);
            for (int i = 0; i < Math.Min(built, items.Count); i++)
            {
                Card card = Take(old, items[i]) ?? new Card(items[i], cardW);
                card.Position = CardPosition(i);
                if (card.GetParent() == null)
                {
                    content.Add(card);
                }
                cards.Add(card);
            }
            Drop(old);
        }

        private static Dictionary<string, Card> ById(List<Card> list)
        {
            var d = new Dictionary<string, Card>();
            foreach (Card c in list)
            {
                if (d.TryGetValue(c.Item.Id ?? "", out Card twin))
                {
                    twin.GetParent()?.Remove(twin);   // the same video twice: one card kept for reuse
                    twin.Dispose();
                }
                d[c.Item.Id ?? ""] = c;
            }
            return d;
        }

        private static Card Take(Dictionary<string, Card> old, VideoItem item)
        {
            if (!old.TryGetValue(item.Id ?? "", out Card card))
            {
                return null;
            }
            old.Remove(item.Id ?? "");
            card.Rebind(item);
            return card;
        }

        private static void Drop(Dictionary<string, Card> old)
        {
            foreach (Card c in old.Values)
            {
                c.GetParent()?.Remove(c);
                c.Dispose();
            }
        }

        private Position CardPosition(int i) => new Position(Pad + (i % Columns) * (cardW + GapX), gridTop + (i / Columns) * rowStep);

        // Builds the (unfilled, cheap) cards up to the last row kept around the focus.
        private void EnsureCards()
        {
            int focusRow = inHighlights ? 0 : focus / Columns;
            int last = Math.Min(items.Count - 1, (focusRow + RowsAfter + 1) * Columns - 1);
            while (cards.Count <= last)
            {
                var card = new Card(items[cards.Count], cardW) { Position = CardPosition(cards.Count) };
                card.Hide();
                cards.Add(card);
                content.Add(card);
            }
        }

        private bool NearFocus(int row)
        {
            int focusRow = inHighlights ? 0 : focus / Columns;
            return row >= focusRow - RowsBefore && row <= focusRow + RowsAfter;
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
                return;  // the grid switched to another feed meanwhile
            }
            loading = false;
            if (page.Error != null)
            {
                if (Count == 0)
                {
                    status.Text = T("Error: {0}", page.Error);
                }
                return;
            }
            if (!fresh)
            {
                // First network page replaces the cached one.
                fresh = true;
                if (cacheKey != null)
                {
                    string key = cacheKey;
                    _ = Task.Run(() => LocalStore.SaveFeed(key, page));
                }
                SetHighlights(page.Highlights);   // first: it moves the grid (gridTop)
                ReplaceItems(page.Items);
                focus = Math.Min(focus, Math.Max(0, items.Count - 1));
            }
            else
            {
                items.AddRange(page.Items);
            }
            continuation = page.Continuation;
            status.Text = Count == 0 ? T("No videos") : "";
            AfterChange();
            MaybePrefetch();
        }

        // Asks the next page early, while the user still has rows to look at.
        private void MaybePrefetch()
        {
            if (fresh && !loading && !string.IsNullOrEmpty(continuation) && focus / Columns >= (items.Count - 1) / Columns - PrefetchRows)
            {
                _ = Load();
            }
        }

        private void AfterChange()
        {
            inHighlights = highlights.Count > 0 && (inHighlights || items.Count == 0);
            EnsureCards();
            for (int i = 0; i < cards.Count; i++)
            {
                cards[i].SetFocused(focused && !inHighlights && i == focus);
            }
            for (int i = 0; i < highlights.Count; i++)
            {
                highlights[i].SetFocused(focused && inHighlights && i == hlFocus);
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
            FocusedCard()?.SetFocused(value);
        }

        private Card FocusedCard()
        {
            if (inHighlights)
            {
                return highlights.Count > 0 ? highlights[hlFocus] : null;
            }
            return focus < cards.Count ? cards[focus] : null;
        }

        public void RefreshProgress()
        {
            foreach (Card c in highlights)
            {
                c.UpdateProgress();
            }
            foreach (Card c in cards)
            {
                c.UpdateProgress();
            }
        }

        // Arrow keys. Returns false when the move leaves the grid on that side (Left at the first column,
        // Up on the first row), so the screen can move the focus elsewhere.
        public bool Move(string key)
        {
            if (Count == 0)
            {
                return key != "Left" && key != "Up";
            }
            if (inHighlights)
            {
                return MoveInHighlights(key);
            }
            int next = focus;
            switch (key)
            {
                case "Left":
                    if (focus % Columns == 0)
                    {
                        return false;
                    }
                    next = focus - 1;
                    break;
                case "Right":
                    next = focus % Columns < Columns - 1 ? Math.Min(items.Count - 1, focus + 1) : focus;
                    break;
                case "Up":
                    if (focus < Columns)
                    {
                        if (highlights.Count == 0)
                        {
                            return false;
                        }
                        SwitchArea(true);
                        return true;
                    }
                    next = focus - Columns;
                    break;
                case "Down":
                    next = Math.Min(items.Count - 1, focus + Columns);
                    break;
            }
            if (next != focus)
            {
                cards[focus].SetFocused(false);
                focus = next;
                EnsureCards();
                cards[focus].SetFocused(true);
                ScrollToFocus();
                MaybePrefetch();
            }
            return true;
        }

        private bool MoveInHighlights(string key)
        {
            int next = hlFocus;
            switch (key)
            {
                case "Left":
                    if (hlFocus == 0)
                    {
                        return false;
                    }
                    next = hlFocus - 1;
                    break;
                case "Right":
                    next = Math.Min(highlights.Count - 1, hlFocus + 1);
                    break;
                case "Up":
                    return false;
                case "Down":
                    if (items.Count > 0)
                    {
                        SwitchArea(false);
                    }
                    return true;
            }
            if (next != hlFocus)
            {
                highlights[hlFocus].SetFocused(false);
                hlFocus = next;
                highlights[hlFocus].SetFocused(true);
                ScrollToFocus();
            }
            return true;
        }

        private void SwitchArea(bool toHighlights)
        {
            FocusedCard()?.SetFocused(false);
            inHighlights = toHighlights;
            FocusedCard()?.SetFocused(focused);
            ScrollToFocus();
        }

        private void ScrollToFocus()
        {
            if (highlights.Count > 0)
            {
                // Keep the focused highlight within the visible columns.
                int first = Math.Max(0, hlFocus - (Columns - 1));
                hlScroll = Widgets.Slide(hlStrip, -first * (cardW + GapX), HeadingH, hlScroll);
            }
            if (inHighlights)
            {
                scroll = Widgets.Slide(content, 0, Pad, scroll);
                UpdateVisibleRows();
                return;
            }
            int row = focus / Columns;
            // The first row keeps the highlights in view; further rows sit second from the top.
            int focusY = gridTop + row * rowStep;
            scroll = Widgets.Slide(content, 0, Pad - (row == 0 ? 0 : Math.Max(0, focusY - rowStep)), scroll);
            UpdateVisibleRows();
        }

        // Shows the rows around the focus, hides the others; filling is left to UiWork.
        private void UpdateVisibleRows()
        {
            EnsureCards();
            for (int i = 0; i < cards.Count; i++)
            {
                bool near = NearFocus(i / Columns);
                if (near != cards[i].Visibility)
                {
                    Widgets.SetVisible(cards[i], near);
                }
            }
            UiWork.Request(pump);
        }

        // Fills one card per call: the focused row, then below (where the user goes), above, further rows,
        // and the highlights on screen. Returns false when everything near the focus is filled.
        private bool BuildNext()
        {
            if (disposed || !Visibility)
            {
                return false;   // a hidden feed (other menu entry): Wake() when shown again
            }
            int first = Math.Max(0, hlFocus - (Columns - 1));
            if (inHighlights && FillHighlights(first))
            {
                return true;
            }
            int focusRow = inHighlights ? 0 : focus / Columns;
            foreach (int d in new[] { 0, 1, -1, 2, 3, 4, -2 })
            {
                int row = focusRow + d;
                for (int i = Math.Max(0, row * Columns); row >= 0 && i < Math.Min(cards.Count, (row + 1) * Columns); i++)
                {
                    if (!cards[i].Filled)
                    {
                        cards[i].Fill();
                        return true;
                    }
                }
            }
            return focusRow <= 1 && FillHighlights(first);
        }

        private bool FillHighlights(int first)
        {
            for (int i = Math.Max(0, first - 1); i < Math.Min(highlights.Count, first + Columns + 1); i++)
            {
                if (!highlights[i].Filled)
                {
                    highlights[i].Fill();
                    return true;
                }
            }
            return false;
        }
    }
}
