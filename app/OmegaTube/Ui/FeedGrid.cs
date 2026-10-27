using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using OmegaTube.Data;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace OmegaTube.Ui
{
    // A vertically scrolling grid of cards backed by a paged feed (Home, Subscriptions, History, sections).
    // The focused row is kept second from the top; more pages load as the focus nears the end.
    public class FeedGrid : View
    {
        private const int Columns = 4;
        private const int GapX = 34, GapY = 40;
        private const int Pad = Theme.FocusMargin;   // room for the focused card's scale-up

        public int Count => cards.Count;
        public VideoItem FocusedItem => cards.Count > 0 ? cards[focus].Item : null;

        private readonly int cardW, rowStep;
        private readonly View content;
        private readonly TextLabel status;
        private readonly List<Card> cards = new List<Card>();
        private Func<string, Task<FeedPage>> loader;
        private string continuation = "";
        private string cacheKey;
        private bool fresh, loading, focused;
        private int focus;
        private int generation;   // ignores pages of a previous Show()
        private Animation scroll;

        public FeedGrid(int width, int height)
        {
            Size = new Size(width, height);
            ClippingMode = ClippingModeType.ClipChildren;
            cardW = (width - 2 * Pad - (Columns - 1) * GapX) / Columns;
            rowStep = Theme.CardHeight(cardW) + GapY;
            content = new View { Position = new Position(0, Pad), Size = new Size(width, 100000) };
            Add(content);
            status = Widgets.Label("", Theme.BodySize, Theme.TextDim, width - 2 * Pad, 120);
            status.MultiLine = true;
            status.Position = new Position(Pad, Pad + 20);
            Add(status);
        }

        // Replaces the content with another feed. cacheKey: show the cached first page meanwhile.
        public void Show(Func<string, Task<FeedPage>> newLoader, string newCacheKey, string emptyText = null)
        {
            generation++;
            foreach (Card c in cards)
            {
                content.Remove(c);
                c.Dispose();
            }
            cards.Clear();
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
            status.Text = "Chargement…";
            FeedPage cached = cacheKey != null ? LocalStore.LoadFeed(cacheKey) : null;
            if (cached != null)
            {
                AddCards(cached.Items);
                status.Text = "";
            }
            AfterChange();
            _ = Load();
        }

        private void AddCards(IEnumerable<VideoItem> items)
        {
            foreach (VideoItem item in items)
            {
                int i = cards.Count;
                var card = new Card(item, cardW) { Position = new Position(Pad + (i % Columns) * (cardW + GapX), (i / Columns) * rowStep) };
                cards.Add(card);
                content.Add(card);
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
                return;  // the grid switched to another feed meanwhile
            }
            loading = false;
            if (page.Error != null)
            {
                if (cards.Count == 0)
                {
                    status.Text = "Erreur : " + page.Error;
                }
                return;
            }
            if (!fresh)
            {
                // First network page replaces the cached one.
                fresh = true;
                foreach (Card c in cards)
                {
                    content.Remove(c);
                    c.Dispose();
                }
                cards.Clear();
                if (cacheKey != null)
                {
                    LocalStore.SaveFeed(cacheKey, page);
                }
                focus = Math.Min(focus, Math.Max(0, page.Items.Count - 1));
            }
            continuation = page.Continuation;
            AddCards(page.Items);
            status.Text = cards.Count == 0 ? "Aucune vidéo" : "";
            AfterChange();
        }

        private void AfterChange()
        {
            for (int i = 0; i < cards.Count; i++)
            {
                cards[i].SetFocused(focused && i == focus);
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
            if (cards.Count > 0)
            {
                cards[focus].SetFocused(value);
            }
        }

        public void RefreshProgress()
        {
            foreach (Card c in cards)
            {
                c.UpdateProgress();
            }
        }

        // Arrow keys. Returns false when the move leaves the grid on that side (Left at the first column,
        // Up on the first row), so the screen can move the focus elsewhere.
        public bool Move(string key)
        {
            if (cards.Count == 0)
            {
                return key != "Left" && key != "Up";
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
                        return false;
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
                if (fresh && focus / Columns >= (cards.Count - 1) / Columns - 2 && !string.IsNullOrEmpty(continuation))
                {
                    _ = Load();
                }
            }
            return true;
        }

        private void ScrollToFocus()
        {
            int row = focus / Columns;
            scroll = Widgets.Slide(content, 0, Pad - Math.Max(0, row - 1) * rowStep, scroll);
            for (int i = Math.Max(0, (row - 1) * Columns); i < Math.Min(cards.Count, (row + 3) * Columns); i++)
            {
                cards[i].LoadImage();
            }
        }
    }
}
