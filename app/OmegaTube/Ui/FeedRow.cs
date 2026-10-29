using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using OmegaTube.Data;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using static OmegaTube.I18n;

namespace OmegaTube.Ui
{
    // A titled horizontal strip of cards backed by a paged feed. The focused card stays at the left edge.
    // Lazy for smooth scrolling: cards exist only up to a little past the visible ones, and are filled
    // (texts, thumbnail) by UiWork nearest the focus first.
    public class FeedRow : View
    {
        private const int VisibleAhead = 6;
        private const int StripY = 64;

        public string Title { get; }
        public string Query { get; }   // search rows only (used by "add to home")
        public int Count => items.Count;
        public int FocusIndex { get; private set; }
        public VideoItem FocusedItem => items.Count > 0 ? items[FocusIndex] : null;
        public VideoItem FirstItem => items.Count > 0 ? items[0] : null;

        // Asks UiWork to call BuildNext. A ShelfList replaces it to order the work of all its rows.
        public Action RequestWork;

        private readonly Func<string, Task<FeedPage>> loader;
        private readonly Func<bool> pump;
        private readonly TextLabel status;
        private readonly View strip;
        private readonly List<VideoItem> items = new List<VideoItem>();
        private readonly List<Card> cards = new List<Card>();   // cards of items[0 .. cards.Count)
        private string continuation = "";
        private string cacheKey;   // home rows: first page cached for an instant start-up
        private bool fresh;        // first network page received
        private bool loading;
        private bool focused;
        private Animation slide;

        private static int Step => Theme.CardWidth + Theme.CardGap;

        public FeedRow(string title, string query, int width) : this(title, c => YouTube.Search(query, c), width)
        {
            Query = query;
        }

        public FeedRow(string title, Func<string, Task<FeedPage>> loader, int width)
        {
            Title = title;
            this.loader = loader;
            pump = BuildNext;
            RequestWork = () => UiWork.Request(pump);
            Size = new Size(width, Theme.RowHeight);

            var header = Widgets.Label(title, Theme.TitleSize - 4, Theme.Text, width - 40);
            header.PositionX = Theme.FocusMargin;
            Add(header);

            status = Widgets.Label(T("Loading…"), Theme.BodySize, Theme.TextDim, width - 40);
            status.Position = new Position(Theme.FocusMargin, 140);
            Add(status);

            strip = new View { Position = new Position(Theme.FocusMargin, StripY), Size = new Size(20000, Theme.CardHeight(Theme.CardWidth) + 40) };
            Add(strip);
        }

        protected override void Dispose(DisposeTypes type)
        {
            UiWork.Cancel(pump);
            base.Dispose(type);
        }

        // Shows the cached first page now; the next Load() replaces it with the network one.
        public void UseCache(string key)
        {
            cacheKey = key;
            FeedPage cached = LocalStore.LoadFeed(key);
            if (cached != null)
            {
                items.AddRange(cached.Items);
                status.Text = "";
                UpdateVisible();
            }
        }

        // Items already fetched (a home shelf): no first request; the loader only gives the next pages.
        // Called again with the network version of a cached shelf: cards of the same videos are kept.
        public void Seed(List<VideoItem> newItems, string nextContinuation)
        {
            fresh = true;
            continuation = nextContinuation ?? "";
            ReplaceItems(newItems);
            status.Text = items.Count == 0 ? T("No videos") : "";
            UpdateVisible();
        }

        // New items in place of the current ones. Existing cards of the same videos are reused (their
        // thumbnail is already there), the others are dropped; the focus index stays.
        private void ReplaceItems(List<VideoItem> newItems)
        {
            if (FocusIndex < cards.Count)
            {
                cards[FocusIndex].SetFocused(false);   // its video may land elsewhere; UpdateVisible refocuses
            }
            var old = new Dictionary<string, Card>();
            foreach (Card c in cards)
            {
                if (old.TryGetValue(c.Item.Id ?? "", out Card twin))
                {
                    strip.Remove(twin);   // the same video twice: one card kept for reuse
                    twin.Dispose();
                }
                old[c.Item.Id ?? ""] = c;
            }
            int built = cards.Count;
            cards.Clear();
            items.Clear();
            items.AddRange(newItems);
            for (int i = 0; i < Math.Min(built, items.Count); i++)
            {
                string id = items[i].Id ?? "";
                if (old.TryGetValue(id, out Card card))
                {
                    old.Remove(id);
                    card.Rebind(items[i]);
                    card.Position = new Position(i * Step, 16);
                }
                else
                {
                    card = new Card(items[i]) { Position = new Position(i * Step, 16) };
                    strip.Add(card);
                }
                cards.Add(card);
            }
            foreach (Card c in old.Values)
            {
                strip.Remove(c);
                c.Dispose();
            }
            FocusIndex = Math.Min(FocusIndex, Math.Max(0, items.Count - 1));
            slide = Widgets.Slide(strip, Theme.FocusMargin - FocusIndex * Step, StripY, slide);
        }

        // Builds the (unfilled, cheap) cards up to index `last`.
        private void EnsureCards(int last)
        {
            last = Math.Min(last, items.Count - 1);
            while (cards.Count <= last)
            {
                var card = new Card(items[cards.Count]) { Position = new Position(cards.Count * Step, 16) };
                cards.Add(card);
                strip.Add(card);
            }
        }

        public async Task Load()
        {
            if (loading)
            {
                return;
            }
            loading = true;
            FeedPage page = await loader(fresh ? continuation : "");
            loading = false;
            if (disposed)
            {
                return;
            }
            if (page.Error != null)
            {
                if (items.Count == 0)
                {
                    status.Text = T("Error: {0}", page.Error);
                }
                return;
            }
            if (!fresh)
            {
                // First network page: replaces whatever the cache showed.
                fresh = true;
                if (cacheKey != null)
                {
                    string key = cacheKey;
                    _ = Task.Run(() => LocalStore.SaveFeed(key, page));
                }
                ReplaceItems(page.Items);
            }
            else
            {
                items.AddRange(page.Items);
            }
            continuation = page.Continuation;
            status.Text = items.Count == 0 ? T("No videos") : "";
            UpdateVisible();
        }

        public void SetFocused(bool value)
        {
            if (focused == value)
            {
                return;
            }
            focused = value;
            if (items.Count > 0)
            {
                EnsureCards(FocusIndex);
                cards[FocusIndex].SetFocused(value);
            }
        }

        // Back to the first card (the list refreshes from the top).
        public void ResetFocus()
        {
            if (FocusIndex == 0 || items.Count == 0)
            {
                return;
            }
            if (FocusIndex < cards.Count)
            {
                cards[FocusIndex].SetFocused(false);
            }
            FocusIndex = 0;
            slide = Widgets.Slide(strip, Theme.FocusMargin, StripY, slide);
            UpdateVisible();
        }

        // Returns false when the move would leave the row (so the caller can move focus elsewhere).
        public bool Move(int delta)
        {
            int next = FocusIndex + delta;
            if (items.Count == 0 || next < 0 || next >= items.Count)
            {
                return false;
            }
            EnsureCards(next);
            cards[FocusIndex].SetFocused(false);
            FocusIndex = next;
            cards[FocusIndex].SetFocused(true);
            slide = Widgets.Slide(strip, Theme.FocusMargin - FocusIndex * Step, StripY, slide);
            UpdateVisible();
            if (fresh && FocusIndex >= items.Count - VisibleAhead && !string.IsNullOrEmpty(continuation))
            {
                _ = Load();
            }
            return true;
        }

        // Progress bars may have changed after watching something.
        public void RefreshProgress()
        {
            foreach (Card c in cards)
            {
                c.UpdateProgress();
            }
        }

        // Only cards near the focus are shown: far ones would sit at x coordinates the TV's GPU renders
        // unreliably (and hidden actors cost the renderer nothing). Filling them is left to UiWork.
        private void UpdateVisible()
        {
            EnsureCards(FocusIndex + VisibleAhead + 1);
            if (focused && cards.Count > FocusIndex)
            {
                cards[FocusIndex].SetFocused(true);
            }
            for (int i = 0; i < cards.Count; i++)
            {
                bool near = i >= FocusIndex - 2 && i < FocusIndex + VisibleAhead + 2;
                if (near != cards[i].Visibility)
                {
                    Widgets.SetVisible(cards[i], near);
                }
            }
            RequestWork();
        }

        // Fills one card on screen or next to it, the focused one first, then rightwards (where the user goes),
        // then the one on the left. Returns false when there is none left to fill.
        public bool BuildNext()
        {
            if (disposed)
            {
                return false;
            }
            int last = Math.Min(cards.Count - 1, FocusIndex + VisibleAhead);
            for (int i = FocusIndex; i <= last; i++)
            {
                if (!cards[i].Filled)
                {
                    cards[i].Fill();
                    return true;
                }
            }
            if (FocusIndex > 0 && FocusIndex - 1 < cards.Count && !cards[FocusIndex - 1].Filled)
            {
                cards[FocusIndex - 1].Fill();
                return true;
            }
            return false;
        }
    }
}
