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
    // Smoothness on the TV: a page's cards are built a row per frame (not all at once), and only the rows
    // around the focus are shown (hidden actors cost the renderer nothing).
    // When the feed has highlights (Subscriptions: YouTube's "most relevant" shelf), they sit above the grid
    // as a horizontally scrolling row; Up from the first grid row enters it, Down leaves it.
    public class FeedGrid : View
    {
        private const int Columns = 4;
        private const int GapX = 34, GapY = 40;
        private const int Pad = Theme.FocusMargin;   // room for the focused card's scale-up
        private const int HeadingH = 56;              // section title above the highlights row and the grid
        private const int RowsBefore = 2, RowsAfter = 4;   // rows kept shown around the focused one
        private const int PrefetchRows = 5;           // next page asked when the focus is this close to the end

        public int Count => cards.Count + highlights.Count;
        public VideoItem FocusedItem => inHighlights ? highlights[hlFocus].Item : cards.Count > 0 ? cards[focus].Item : null;

        private readonly int cardW, rowStep;
        private readonly View content;
        private readonly View hlStrip;
        private readonly TextLabel hlHeading, gridHeading;
        private readonly TextLabel status;
        private readonly List<Card> cards = new List<Card>();
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
        private readonly Queue<VideoItem> pending = new Queue<VideoItem>();   // items waiting for their card
        private Timer builder;

        public FeedGrid(int width, int height)
        {
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
            FeedPage cached = cacheKey != null ? LocalStore.LoadFeed(cacheKey) : null;
            if (cached != null)
            {
                SetHighlights(cached.Highlights);
                AddCards(cached.Items);
                status.Text = "";
            }
            AfterChange();
            _ = Load();
        }

        protected override void Dispose(DisposeTypes type)
        {
            builder?.Stop();
            builder = null;
            pending.Clear();
            base.Dispose(type);
        }

        private void ClearCards()
        {
            pending.Clear();
            foreach (Card c in cards)
            {
                content.Remove(c);
                c.Dispose();
            }
            cards.Clear();
            SetHighlights(new List<VideoItem>());
            inHighlights = false;
        }

        private void SetHighlights(List<VideoItem> items)
        {
            foreach (Card c in highlights)
            {
                hlStrip.Remove(c);
                c.Dispose();
            }
            highlights.Clear();
            foreach (VideoItem item in items)
            {
                var card = new Card(item, cardW) { Position = new Position(Pad + highlights.Count * (cardW + GapX), 0) };
                highlights.Add(card);
                hlStrip.Add(card);
            }
            bool any = highlights.Count > 0;
            foreach (View v in new View[] { hlHeading, hlStrip, gridHeading })
            {
                Widgets.SetVisible(v, any);
            }
            gridTop = any ? 2 * HeadingH + rowStep : 0;
            hlFocus = Math.Min(hlFocus, Math.Max(0, highlights.Count - 1));
            hlStrip.PositionX = 0;
        }

        // The first rows are built right away (they are on screen), the rest a row per timer tick.
        private void AddCards(IEnumerable<VideoItem> items)
        {
            foreach (VideoItem item in items)
            {
                pending.Enqueue(item);
            }
            BuildCards(Math.Max(0, (focus / Columns + RowsAfter + 1) * Columns - cards.Count));
            if (pending.Count > 0 && builder == null)
            {
                builder = new Timer(30);
                builder.Tick += (s, e) =>
                {
                    BuildCards(Columns);
                    if (pending.Count > 0)
                    {
                        return true;
                    }
                    builder = null;
                    return false;
                };
                builder.Start();
            }
        }

        private void BuildCards(int max)
        {
            int built = 0;
            while (pending.Count > 0 && built < max)
            {
                int i = cards.Count;
                var card = new Card(pending.Dequeue(), cardW) { Position = new Position(Pad + (i % Columns) * (cardW + GapX), gridTop + (i / Columns) * rowStep) };
                cards.Add(card);
                content.Add(card);
                built++;
                bool near = NearFocus(i / Columns);
                Widgets.SetVisible(card, near);
                if (near)
                {
                    card.LoadImage();
                }
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
            if (gen != generation)
            {
                return;  // the grid switched to another feed meanwhile
            }
            loading = false;
            if (page.Error != null)
            {
                if (cards.Count == 0)
                {
                    status.Text = T("Error: {0}", page.Error);
                }
                return;
            }
            if (!fresh)
            {
                // First network page replaces the cached one.
                fresh = true;
                bool wasInHighlights = inHighlights;
                ClearCards();
                inHighlights = wasInHighlights;
                if (cacheKey != null)
                {
                    LocalStore.SaveFeed(cacheKey, page);
                }
                SetHighlights(page.Highlights);
                focus = Math.Min(focus, Math.Max(0, page.Items.Count - 1));
            }
            continuation = page.Continuation;
            AddCards(page.Items);
            status.Text = Count == 0 && pending.Count == 0 ? T("No videos") : "";
            AfterChange();
            MaybePrefetch();
        }

        // Asks the next page early, while the user still has rows to look at.
        private void MaybePrefetch()
        {
            int total = cards.Count + pending.Count;
            if (fresh && !loading && !string.IsNullOrEmpty(continuation) && focus / Columns >= (total - 1) / Columns - PrefetchRows)
            {
                _ = Load();
            }
        }

        private void AfterChange()
        {
            inHighlights = highlights.Count > 0 && (inHighlights || cards.Count == 0);
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
            return cards.Count > 0 ? cards[focus] : null;
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
            if (key == "Down" && pending.Count > 0 && focus + Columns >= cards.Count)
            {
                BuildCards(2 * Columns);   // the user outran the builder
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
                    next = focus % Columns < Columns - 1 ? Math.Min(cards.Count - 1, focus + 1) : focus;
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
                    next = Math.Min(cards.Count - 1, focus + Columns);
                    break;
            }
            if (next != focus)
            {
                cards[focus].SetFocused(false);
                focus = next;
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
                    if (cards.Count > 0)
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
                for (int i = Math.Max(0, first - 1); i < Math.Min(highlights.Count, first + Columns + 1); i++)
                {
                    highlights[i].LoadImage();
                }
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

        // Shows the rows around the focus (loading their thumbnails), hides the others.
        private void UpdateVisibleRows()
        {
            for (int i = 0; i < cards.Count; i++)
            {
                bool near = NearFocus(i / Columns);
                if (near != cards[i].Visibility)
                {
                    Widgets.SetVisible(cards[i], near);
                }
                if (near)
                {
                    cards[i].LoadImage();
                }
            }
        }
    }
}
