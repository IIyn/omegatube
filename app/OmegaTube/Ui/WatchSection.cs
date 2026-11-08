using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using OmegaTube.Data;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using static OmegaTube.I18n;

namespace OmegaTube.Ui
{
    // The watch page's lower part, below the player's suggestions row (as on youtube.com):
    //  - the description box: views + date, the first lines of the description, "Show more" (OK) expands it;
    //  - "N comments" with a Sort by button (top comments / newest first);
    //  - the "Add a comment…" field (signed in: OK opens a keyboard, the comment is posted as the account);
    //  - the comments, loaded page by page as the focus goes down; OK expands a long one or opens its replies;
    //  - ◀ ▶ on a comment walk its toolbar: like, dislike, reply, replies (shown inline, indented, below it).
    // Focus moves with Up / Down; Up from the description leaves the section (Key returns false). The section
    // keeps a scroll offset (Scroll) that the player applies to its sliding block so the focused part shows.
    public class WatchSection : View
    {
        public const int SectionWidth = 1780;
        public const int Viewport = 930;       // visible height while the section has the focus
        private const int Pad = 32;
        private const int DescLines = 3, CommentLines = 4;
        private const int MaxComments = 300;   // top-level comments
        private const int MaxEntries = 500;    // comments + replies: views kept alive
        private const int ReplyIndent = 88;
        private const int ToolH = 44;
        private const int MaxCommentLength = 10000;
        private const int TextX = 96;          // comment text, right of the avatar
        private const float LineFactor = 1.35f;

        private enum Part { Description, Sort, Form, Comment }
        private enum Tool { Like, Dislike, Reply, Replies }

        private class ToolButton
        {
            public Tool Kind;
            public View Pill;
            public ImageView Icon;
            public TextLabel Label;
        }

        private class Entry
        {
            public Comment Comment;            // null: a "Show more replies" row
            public Entry Parent;               // a reply's thread
            public readonly List<Entry> Children = new List<Entry>();
            public readonly List<ToolButton> Tools = new List<ToolButton>();
            public string MoreToken = "";
            public int Indent;
            public bool RepliesOpen, RepliesLoading, Voting;
            public View View, Highlight;
            public TextLabel Text, More;
            public View Toolbar;
            public bool Expanded, Truncated;
            public float Top, Height;
        }

        public float Scroll { get; private set; }
        public bool EntryOpen => entry != null;
        // Toasts are the player's.
        public Action<string> Toast;
        // The focused part moved outside a key press (an answer arrived): the player re-applies Scroll.
        public Action ScrollChanged;

        private readonly string videoId;
        private readonly View overlayHost;     // the screen root: the keyboard goes over everything
        private readonly View background;
        private readonly View descBox;
        private readonly TextLabel descMeta, descText, descMore;
        private readonly View header;
        private readonly TextLabel countLabel;
        private readonly View sortButton;
        private readonly ImageView sortIcon;
        private readonly TextLabel sortLabel;
        private readonly View form;
        private readonly ImageView formAvatar;
        private readonly TextLabel formLabel;
        private readonly View formLine;
        private readonly TextLabel status;
        private readonly View list;
        private readonly List<Entry> entries = new List<Entry>();

        private string meta = "", description = "";
        private bool descExpanded, descTruncated;
        private float descHeight, headerTop, formTop, statusTop, listTop, total;
        private int focusIndex;                // 0 description, 1 sort, 2 form, 3.. comments
        private bool focused;
        private bool started, loading, newest, disabled;
        private int generation;
        private string continuation = "", sortTop = "", sortNewest = "", createParams = "";
        private string draft = "";
        private TextEntry entry;
        private Entry replyTarget;             // the entry being answered, null for a new comment
        private int toolIndex = -1;            // the focused comment's toolbar button, -1 = the comment itself
        private Entry painted;                 // the entry whose toolbar shows a selection
        private bool posting;
        private bool closed;

        // The account's avatar / handle for the field and the posted comments (kept for the session).
        private static string myPhoto, myHandle;

        private bool HasComments => started && !disabled;
        private int LastIndex => HasComments ? 2 + entries.Count : 0;

        public WatchSection(string videoId, View overlayHost)
        {
            this.videoId = videoId;
            this.overlayHost = overlayHost;
            Size = new Size(SectionWidth, 100);

            // Opaque enough to read over the video; extends left / right to the screen edges.
            background = new View { BackgroundColor = new Color(0.05f, 0.05f, 0.05f, 0.94f), Position = new Position(-70, -40) };
            Add(background);

            descBox = Widgets.Rounded(new Color(1, 1, 1, 0.1f), SectionWidth, 100, 16);
            Add(descBox);
            descMeta = Widgets.Bold(Widgets.Label("", Theme.BodySize, Theme.Text, SectionWidth - 2 * Pad, 36));
            descMeta.Position = new Position(Pad, 22);
            descBox.Add(descMeta);
            descText = Widgets.Label("", Theme.BodySize, Theme.Text, SectionWidth - 2 * Pad, 10);
            descText.MultiLine = true;
            descText.Ellipsis = false;
            descText.VerticalAlignment = VerticalAlignment.Top;
            descText.Position = new Position(Pad, 64);
            descBox.Add(descText);
            descMore = Widgets.Bold(Widgets.Label("", Theme.BodySize, Theme.Text, 500, 36));
            descBox.Add(descMore);

            header = new View { Size = new Size(SectionWidth, 64) };
            Add(header);
            countLabel = Widgets.Bold(Widgets.Label(T("Comments"), Theme.TitleSize, Theme.Text, 900, 64));
            countLabel.VerticalAlignment = VerticalAlignment.Center;
            header.Add(countLabel);
            sortButton = Widgets.Rounded(Color.Transparent, 460, 60, 30);
            sortButton.PositionY = 2;
            header.Add(sortButton);
            sortIcon = Widgets.Icon("view_list", 36);
            sortIcon.Position = new Position(22, 12);
            sortButton.Add(sortIcon);
            sortLabel = Widgets.Bold(Widgets.Label("", Theme.BodySize, Theme.Text, 380, 60));
            sortLabel.VerticalAlignment = VerticalAlignment.Center;
            sortLabel.Position = new Position(72, 0);
            sortButton.Add(sortLabel);
            header.Hide();

            form = new View { Size = new Size(SectionWidth, 72) };
            Add(form);
            formAvatar = Widgets.Round(new ImageView { Size = new Size(52, 52), Position = new Position(16, 10), BackgroundColor = Theme.SurfaceHigh }, 26);
            form.Add(formAvatar);
            formLabel = Widgets.Label(T("Add a comment…"), Theme.BodySize + 1, Theme.TextDim, SectionWidth - 140, 52);
            formLabel.VerticalAlignment = VerticalAlignment.Center;
            formLabel.Position = new Position(TextX, 6);
            form.Add(formLabel);
            formLine = Widgets.Box(Theme.TextMuted, SectionWidth - TextX - 20, 2);
            formLine.Position = new Position(TextX, 64);
            form.Add(formLine);
            form.Hide();

            status = Widgets.Label("", Theme.BodySize, Theme.TextDim, SectionWidth - 2 * Pad, 50);
            status.PositionX = Pad;
            Add(status);

            list = new View { Size = new Size(SectionWidth, 10) };
            Add(list);

            UpdateSortLabel();
            ArrangeParts();
        }

        // The player is leaving: late answers are dropped.
        public void Close()
        {
            closed = true;
            generation++;
            CloseEntry();
        }

        // ------------------------------------------------------------------ data

        // The description box's texts (the player knows them in steps: card, player answer, details).
        public void SetDescription(string metaLine, string text)
        {
            metaLine = metaLine ?? "";
            text = (text ?? "").Trim();
            if (metaLine == meta && text == description)
            {
                return;
            }
            meta = metaLine;
            description = text;
            FillDescription();
            ArrangeParts();
        }

        // First comments page: called when the focus first comes near (saves a request per video otherwise).
        public void Begin()
        {
            if (started)
            {
                return;
            }
            started = true;
            header.Show();
            form.Show();
            UpdateForm();
            ShowStatus(T("Loading comments…"));
            ArrangeParts();
            UiGuard.Fire("WatchSection.LoadPage", () => LoadPage(""));
            if (YouTube.SignedIn() && myPhoto == null)
            {
                UiGuard.Fire("WatchSection.LoadAccount", LoadAccount);
            }
        }

        private async Task LoadAccount()
        {
            AccountInfo a = await YouTube.Account();
            if (a.Error != null)
            {
                return;
            }
            myPhoto = a.Photo ?? "";
            myHandle = a.Handle ?? a.Name ?? "";
            UpdateForm();
        }

        // token "": first page (top comments); a sort token reloads the list; else the next page.
        private async Task LoadPage(string token)
        {
            loading = true;
            int gen = generation;
            CommentPage page = await YouTube.Comments(videoId, token);
            if (gen != generation || closed)
            {
                return;
            }
            loading = false;
            if (page.Error != null)
            {
                Tizen.Log.Warn("OmegaTube", "comments: " + page.Error);
                if (entries.Count == 0)
                {
                    ShowStatus(T("Comments unavailable"));
                }
                continuation = "";
                ArrangeParts();
                return;
            }
            if (page.Disabled)
            {
                disabled = true;
                header.Hide();
                form.Hide();
                ShowStatus(T("Comments are turned off."));
                focusIndex = 0;
                ArrangeParts();
                Refresh();
                return;
            }
            if (page.Count.Length > 0)
            {
                countLabel.Text = page.Count;
            }
            if (page.SortTop.Length > 0)
            {
                sortTop = page.SortTop;
            }
            if (page.SortNewest.Length > 0)
            {
                sortNewest = page.SortNewest;
            }
            if (page.CreateParams.Length > 0)
            {
                createParams = page.CreateParams;
            }
            sortButton.PositionX = Math.Min(900, EstimateWidth(countLabel.Text, Theme.TitleSize) + 40);
            continuation = page.Continuation;
            int top = 0;
            foreach (Entry e in entries)
            {
                top += e.Parent == null ? 1 : 0;
            }
            foreach (Comment c in page.Items)
            {
                if (top >= MaxComments || entries.Count >= MaxEntries)
                {
                    continuation = "";
                    break;
                }
                AddEntry(c, entries.Count);
                top++;
            }
            if (entries.Count == 0)
            {
                ShowStatus(T("No comments yet."));
            }
            else
            {
                status.Hide();
            }
            ArrangeParts();
            Refresh();
        }

        // Next page when the focus nears the end of the list.
        private void MaybeLoadMore()
        {
            if (!loading && continuation.Length > 0 && focusIndex >= 3 + entries.Count - 5)
            {
                string token = continuation;
                UiGuard.Fire("WatchSection.LoadPage", () => LoadPage(token));
            }
        }

        private void ToggleSort()
        {
            string token = newest ? sortTop : sortNewest;
            if (token.Length == 0)
            {
                return;
            }
            newest = !newest;
            generation++;
            ClearEntries();
            continuation = "";
            UpdateSortLabel();
            ShowStatus(T("Loading comments…"));
            ArrangeParts();
            UiGuard.Fire("WatchSection.LoadPage", () => LoadPage(token));
            Toast?.Invoke(newest ? T("Newest first") : T("Top comments"));
        }

        private void ClearEntries()
        {
            foreach (Entry e in entries)
            {
                list.Remove(e.View);
                e.View.Dispose();
            }
            entries.Clear();
            painted = null;
            toolIndex = -1;
        }

        private void RemoveEntry(Entry e)
        {
            if (painted == e)
            {
                painted = null;
            }
            entries.Remove(e);
            list.Remove(e.View);
            e.View.Dispose();
        }

        private void RemoveChildren(Entry parent)
        {
            foreach (Entry child in parent.Children)
            {
                RemoveEntry(child);
            }
            parent.Children.Clear();
        }

        // ------------------------------------------------------------------ comment views

        private void AddEntry(Comment c, int at, Entry parent = null)
        {
            var e = new Entry { Comment = c, Parent = parent, Indent = parent != null ? ReplyIndent : 0 };
            int left = e.Indent;
            AddFrame(e);

            int avatarSize = parent != null ? 44 : 56;
            var avatar = Widgets.Round(new ImageView { Size = new Size(avatarSize, avatarSize), Position = new Position(16 + left, 18), BackgroundColor = Theme.SurfaceHigh }, avatarSize / 2f);
            if (c.Avatar.Length > 0)
            {
                avatar.ResourceUrl = c.Avatar;
            }
            e.View.Add(avatar);

            int textX = TextX + left;
            int y = 16;
            if (c.Pinned)
            {
                var pin = Widgets.Label(T("Pinned"), Theme.SmallSize, Theme.TextDim, 600, 28);
                pin.Position = new Position(textX, y);
                e.View.Add(pin);
                y += 30;
            }
            string author = c.Author.Length > 0 ? c.Author : T("Unknown");
            float authorW = EstimateWidth(author, Theme.SmallSize + 1);
            var name = Widgets.Bold(Widgets.Label(c.Creator ? " " + author + " " : author, Theme.SmallSize + 1, Theme.Text, (int)Math.Min(900, authorW + 20), 32));
            if (c.Creator)
            {
                name.BackgroundColor = new Color(1, 1, 1, 0.2f);
                Widgets.Round(name, 14);
            }
            name.VerticalAlignment = VerticalAlignment.Center;
            name.Position = new Position(textX, y);
            e.View.Add(name);
            var when = Widgets.Label(c.Published, Theme.SmallSize, Theme.TextDim, 500, 32);
            when.VerticalAlignment = VerticalAlignment.Center;
            when.Position = new Position(textX + Math.Min(900, authorW + 20) + 12, y);
            e.View.Add(when);
            y += 38;

            e.Text = Widgets.Label("", Theme.BodySize, Theme.Text, SectionWidth - textX - 40, 10);
            e.Text.MultiLine = true;
            e.Text.Ellipsis = false;
            e.Text.VerticalAlignment = VerticalAlignment.Top;
            e.Text.Position = new Position(textX, y);
            e.View.Add(e.Text);
            e.More = Widgets.Bold(Widgets.Label("", Theme.SmallSize + 1, Theme.TextDim, 400, 32));
            e.More.PositionX = textX;
            e.View.Add(e.More);

            // The pills' padding hangs left of the text.
            e.Toolbar = new View { Size = new Size(900, ToolH), PositionX = textX - 12 };
            e.View.Add(e.Toolbar);
            BuildToolbar(e);

            e.Top = y;   // reused below as the text's top inside the entry
            FillEntry(e);
            entries.Insert(at, e);
            parent?.Children.Add(e);
            list.Add(e.View);
        }

        // The row and its focus highlight.
        private void AddFrame(Entry e)
        {
            e.View = new View { Size = new Size(SectionWidth, 100) };
            e.Highlight = Widgets.Rounded(new Color(1, 1, 1, 0.09f), SectionWidth - e.Indent, 100, 14);
            e.Highlight.PositionX = e.Indent;
            e.Highlight.Hide();
            e.View.Add(e.Highlight);
        }

        private void AddMoreRow(Entry parent, string token, int at)
        {
            var e = new Entry { Parent = parent, MoreToken = token, Indent = ReplyIndent };
            AddFrame(e);
            e.Text = Widgets.Bold(Widgets.Label(T("Show more replies"), Theme.SmallSize + 1, Theme.Text, 900, 44));
            e.Text.VerticalAlignment = VerticalAlignment.Center;
            e.Text.Position = new Position(TextX + e.Indent, 6);
            e.View.Add(e.Text);
            e.Height = 56;
            e.View.SizeHeight = e.Height;
            e.Highlight.SizeHeight = e.Height;
            entries.Insert(at, e);
            parent.Children.Add(e);
            list.Add(e.View);
        }

        // Like (count), dislike, heart, reply, replies; rebuilt when a count or a label changes.
        private void BuildToolbar(Entry e)
        {
            foreach (View child in new List<View>(e.Toolbar.Children))
            {
                e.Toolbar.Remove(child);
                child.Dispose();
            }
            e.Tools.Clear();
            Comment c = e.Comment;
            bool actions = c.Id.Length > 0;    // a posted comment YouTube didn't return has no id
            int x = 0;
            if (actions)
            {
                x = AddTool(e, Tool.Like, "thumb_up", c.Likes, x);
                x = AddTool(e, Tool.Dislike, "thumb_down", "", x);
            }
            if (c.Hearted)
            {
                var heart = Widgets.Label("♥", Theme.BodySize, Theme.Accent, 40, ToolH);
                heart.VerticalAlignment = VerticalAlignment.Center;
                heart.Position = new Position(x + 12, 0);
                e.Toolbar.Add(heart);
                x += 52;
            }
            if (actions)
            {
                x = AddTool(e, Tool.Reply, null, T("Reply"), x);
            }
            if (c.ReplyToken.Length > 0)
            {
                string replies = e.RepliesOpen ? T("Hide replies") : c.Replies == "1" ? T("1 reply") : T("{0} replies", c.Replies);
                AddTool(e, Tool.Replies, null, replies, x);
            }
            PaintTools(e, e == FocusedEntry && focused ? toolIndex : -1);
        }

        private int AddTool(Entry e, Tool kind, string icon, string text, int x)
        {
            var t = new ToolButton { Kind = kind, Pill = Widgets.Rounded(Color.Transparent, 10, ToolH, ToolH / 2f) };
            t.Pill.PositionX = x;
            int w = 12;
            if (icon != null)
            {
                t.Icon = Widgets.Icon(icon, 30);
                t.Icon.Position = new Position(w, (ToolH - 30) / 2);
                t.Pill.Add(t.Icon);
                w += 30 + (text.Length > 0 ? 8 : 12);
            }
            if (text.Length > 0)
            {
                int textW = (int)(EstimateWidth(text, Theme.SmallSize) * 1.15f) + 10;
                t.Label = Widgets.Bold(Widgets.Label(text, Theme.SmallSize, Theme.Text, textW, ToolH));
                t.Label.VerticalAlignment = VerticalAlignment.Center;
                t.Label.PositionX = w;
                t.Pill.Add(t.Label);
                w += textW + 12;
            }
            t.Pill.SizeWidth = w;
            e.Toolbar.Add(t.Pill);
            e.Tools.Add(t);
            return x + w + 6;
        }

        private static void PaintTools(Entry e, int selected)
        {
            for (int i = 0; i < e.Tools.Count; i++)
            {
                ToolButton t = e.Tools[i];
                bool on = i == selected;
                bool active = t.Kind == Tool.Like ? e.Comment.Liked : t.Kind == Tool.Dislike && e.Comment.Disliked;
                t.Pill.BackgroundColor = on ? Theme.CardInfoFocused : Color.Transparent;
                if (t.Icon != null)
                {
                    t.Icon.Color = on ? new Color(0.16f, 0.16f, 0.16f, 1f) : active ? Theme.Accent : Color.White;
                }
                if (t.Label != null)
                {
                    t.Label.TextColor = on ? Theme.CardTextFocused : Theme.Text;
                }
            }
        }

        // Text (collapsed to a few lines unless expanded), "Read more", toolbar; sets the entry height.
        private void FillEntry(Entry e)
        {
            float textTop = e.Top;
            int perLine = CharsPerLine(SectionWidth - TextX - e.Indent - 40, Theme.BodySize);
            List<string> lines = WrapLines(e.Comment.Text, perLine);
            e.Truncated = lines.Count > CommentLines;
            int shown = e.Expanded || !e.Truncated ? lines.Count : CommentLines;
            e.Text.Text = string.Join("\n", lines.GetRange(0, shown));
            float textH = Math.Max(1, shown) * Theme.BodySize * LineFactor;
            e.Text.SizeHeight = textH + 8;
            float y = textTop + textH + 6;
            if (e.Truncated)
            {
                e.More.Text = e.Expanded ? T("Show less") : T("Read more");
                e.More.PositionY = y;
                e.More.Show();
                y += 36;
            }
            else
            {
                e.More.Hide();
            }
            e.Toolbar.PositionY = y + 4;
            e.Height = y + 4 + ToolH + 16;
            e.View.SizeHeight = e.Height;
            e.Highlight.SizeHeight = e.Height;
        }

        // ------------------------------------------------------------------ description

        private void FillDescription()
        {
            descMeta.Text = meta;
            string text = description.Length > 0 ? description : T("No description.");
            int perLine = CharsPerLine(SectionWidth - 2 * Pad, Theme.BodySize);
            List<string> lines = WrapLines(text, perLine);
            descTruncated = lines.Count > DescLines;
            int shown = descExpanded || !descTruncated ? lines.Count : DescLines;
            string body = string.Join("\n", lines.GetRange(0, shown));
            descText.Text = descTruncated && !descExpanded ? body + "…" : body;
            descText.TextColor = description.Length > 0 ? Theme.Text : Theme.TextDim;
            float textH = Math.Max(1, shown) * Theme.BodySize * LineFactor;
            descText.SizeHeight = textH + 8;
            descText.PositionY = meta.Length > 0 ? 64 : 22;
            float y = descText.PositionY + textH + 8;
            if (descTruncated)
            {
                descMore.Text = descExpanded ? T("Show less") : T("Show more");
                descMore.Position = new Position(Pad, y);
                descMore.Show();
                y += 40;
            }
            else
            {
                descMore.Hide();
            }
            descHeight = y + 20;
            descBox.SizeHeight = descHeight;
        }

        // ------------------------------------------------------------------ layout & focus

        private void ArrangeParts()
        {
            if (descHeight == 0)
            {
                FillDescription();
            }
            float y = descHeight + 50;
            headerTop = y;
            header.PositionY = y;
            if (header.Visibility)
            {
                y += 64 + 30;
            }
            formTop = y;
            form.PositionY = y;
            if (form.Visibility)
            {
                y += 72 + 30;
            }
            statusTop = y;
            status.PositionY = y;
            if (status.Visibility)
            {
                y += 70;
            }
            listTop = y;
            list.PositionY = y;
            float ly = 0;
            foreach (Entry e in entries)
            {
                e.View.PositionY = ly;
                ly += e.Height + 8;
            }
            list.SizeHeight = ly;
            total = y + ly + 60;
            SizeHeight = total;
            background.Size = new Size(1920, total + 40 + 1100);
            if (Scroll > MaxScroll)
            {
                Scroll = MaxScroll;
            }
        }

        private float MaxScroll => Math.Max(0, total - Viewport);

        private void ShowStatus(string text)
        {
            status.Text = text;
            status.Show();
        }

        private void UpdateSortLabel()
        {
            sortLabel.Text = T("Sort by") + "  ·  " + (newest ? T("Newest first") : T("Top comments"));
        }

        private void UpdateForm()
        {
            if (!string.IsNullOrEmpty(myPhoto))
            {
                formAvatar.ResourceUrl = myPhoto;
            }
            else if (formAvatar.ResourceUrl == null || formAvatar.ResourceUrl.Length == 0)
            {
                formAvatar.ResourceUrl = Theme.Icon("person");
            }
            formLabel.Text = draft.Length > 0 ? draft.Replace('\n', ' ') : T("Add a comment…");
            formLabel.TextColor = draft.Length > 0 ? Theme.Text : Theme.TextDim;
        }

        private Entry FocusedEntry => focusIndex >= 3 && focusIndex - 3 < entries.Count ? entries[focusIndex - 3] : null;

        private void FocusOn(Entry e)
        {
            int i = entries.IndexOf(e);
            if (i >= 0)
            {
                focusIndex = i + 3;
            }
        }

        private Part PartOf(int index) => index == 0 ? Part.Description : index == 1 ? Part.Sort : index == 2 ? Part.Form : Part.Comment;

        private (float Top, float Bottom) Bounds(int index)
        {
            switch (PartOf(index))
            {
                case Part.Description:
                    return (0, descHeight);
                case Part.Sort:
                    return (headerTop, headerTop + 64);
                case Part.Form:
                    return (headerTop, formTop + 72);   // keeps the count in view above the field
                default:
                    Entry e = entries[index - 3];
                    return (listTop + e.View.PositionY, listTop + e.View.PositionY + e.Height);
            }
        }

        public void SetFocused(bool value)
        {
            if (value == focused)
            {
                return;
            }
            focused = value;
            if (value)
            {
                focusIndex = Math.Min(focusIndex, LastIndex);
                Reveal(+1);
            }
            Refresh();
        }

        private void Refresh()
        {
            bool desc = focused && focusIndex == 0;
            descBox.BackgroundColor = desc ? new Color(1, 1, 1, 0.2f) : new Color(1, 1, 1, 0.1f);
            bool sort = focused && focusIndex == 1;
            sortButton.BackgroundColor = sort ? Theme.CardInfoFocused : new Color(1, 1, 1, 0.1f);
            sortLabel.TextColor = sort ? Theme.CardTextFocused : Theme.Text;
            sortIcon.Color = sort ? new Color(0.16f, 0.16f, 0.16f, 1f) : Color.White;
            bool field = focused && focusIndex == 2;
            formLine.BackgroundColor = field ? Theme.Text : Theme.TextMuted;
            formLine.SizeHeight = field ? 3 : 2;
            form.BackgroundColor = field ? new Color(1, 1, 1, 0.08f) : Color.Transparent;
            Widgets.Round(form, 12);
            for (int i = 0; i < entries.Count; i++)
            {
                Widgets.SetVisible(entries[i].Highlight, focused && focusIndex == i + 3);
            }
            Entry current = focused ? FocusedEntry : null;
            if (painted != null && painted != current)
            {
                PaintTools(painted, -1);
            }
            if (current != null)
            {
                PaintTools(current, toolIndex);
            }
            painted = current;
        }

        // Scrolls so the focused part shows; a part taller than the viewport shows its top going down, its
        // bottom going up.
        private void Reveal(int direction)
        {
            (float top, float bottom) = Bounds(focusIndex);
            float margin = 30;
            if (bottom - top > Viewport - 2 * margin)
            {
                Scroll = direction > 0 ? top - margin : bottom - Viewport + margin;
            }
            else if (top - margin < Scroll)
            {
                Scroll = top - margin;
            }
            else if (bottom + margin > Scroll + Viewport)
            {
                Scroll = bottom + margin - Viewport;
            }
            Scroll = Math.Max(0, Math.Min(MaxScroll, Scroll));
            if (focusIndex == 0)
            {
                Scroll = Math.Min(Scroll, Math.Max(0, descHeight - Viewport + margin));
            }
        }

        // Returns false for keys that leave the section (Up at the top, Back): the player takes the focus.
        public bool Key(string key)
        {
            if (entry != null)
            {
                entry.Key(key);
                return true;
            }
            (float top, float bottom) = Bounds(focusIndex);
            switch (key)
            {
                case "Down":
                    if (bottom + 30 > Scroll + Viewport && Scroll < MaxScroll)
                    {
                        Scroll = Math.Min(Math.Min(MaxScroll, bottom + 30 - Viewport), Scroll + 400);   // read on in a tall part
                        break;
                    }
                    if (focusIndex < LastIndex)
                    {
                        focusIndex++;
                        toolIndex = -1;
                        Reveal(+1);
                        MaybeLoadMore();
                    }
                    break;
                case "Up":
                    if (top - 30 < Scroll && Scroll > 0)
                    {
                        Scroll = Math.Max(Math.Max(0, top - 30), Scroll - 400);
                        break;
                    }
                    if (focusIndex == 0)
                    {
                        return false;
                    }
                    focusIndex--;
                    toolIndex = -1;
                    Reveal(-1);
                    break;
                case "Left":
                case "Right":
                    if (FocusedEntry != null)
                    {
                        toolIndex = Math.Max(-1, Math.Min(FocusedEntry.Tools.Count - 1, toolIndex + (key == "Right" ? 1 : -1)));
                    }
                    break;
                case "Return":
                    Activate();
                    break;
                case "XF86Back":
                case "Escape":
                    return false;
            }
            Refresh();
            return true;
        }

        private void Activate()
        {
            switch (PartOf(focusIndex))
            {
                case Part.Description:
                    if (descTruncated)
                    {
                        descExpanded = !descExpanded;
                        FillDescription();
                        ArrangeParts();
                        Reveal(descExpanded ? +1 : -1);
                        if (!descExpanded)
                        {
                            Scroll = 0;
                        }
                    }
                    break;
                case Part.Sort:
                    ToggleSort();
                    break;
                case Part.Form:
                    OpenEntry();
                    break;
                case Part.Comment:
                    Entry e = entries[focusIndex - 3];
                    if (e.Comment == null)
                    {
                        LoadMoreReplies(e);
                    }
                    else if (toolIndex >= 0)
                    {
                        UseTool(e, e.Tools[toolIndex].Kind);
                    }
                    else if (e.Truncated)
                    {
                        e.Expanded = !e.Expanded;
                        FillEntry(e);
                        ArrangeParts();
                        Reveal(e.Expanded ? +1 : -1);
                    }
                    else if (e.Comment.ReplyToken.Length > 0)
                    {
                        ToggleReplies(e);
                    }
                    break;
            }
        }

        // ------------------------------------------------------------------ toolbar: votes & replies

        private void UseTool(Entry e, Tool kind)
        {
            switch (kind)
            {
                case Tool.Like:
                    ToggleVote(e, true);
                    break;
                case Tool.Dislike:
                    ToggleVote(e, false);
                    break;
                case Tool.Reply:
                    OpenEntry(e);
                    break;
                case Tool.Replies:
                    ToggleReplies(e);
                    break;
            }
        }

        // Shown at once, undone if YouTube refuses.
        private void ToggleVote(Entry e, bool like)
        {
            if (!YouTube.SignedIn())
            {
                Toast?.Invoke(T("Sign in (Account menu) to rate comments"));
                return;
            }
            if (e.Voting)
            {
                return;
            }
            Comment c = e.Comment;
            bool wasLiked = c.Liked, wasDisliked = c.Disliked;
            string wasLikes = c.Likes;
            string action = like ? (wasLiked ? "unlike" : "like") : (wasDisliked ? "undislike" : "dislike");
            c.Liked = like && !wasLiked;
            c.Disliked = !like && !wasDisliked;
            if (c.Liked != wasLiked)
            {
                c.Likes = Bump(c.Likes, c.Liked ? 1 : -1);
            }
            e.Voting = true;
            BuildToolbar(e);
            UiGuard.Fire("WatchSection.Vote", () => SendVote(e, action, wasLiked, wasDisliked, wasLikes));
        }

        private async Task SendVote(Entry e, string action, bool wasLiked, bool wasDisliked, string wasLikes)
        {
            string error = await YouTube.CommentAction(videoId, e.Comment.Id, action);
            e.Voting = false;
            if (error == null || closed)
            {
                return;
            }
            Tizen.Log.Warn("OmegaTube", "comment vote: " + error);
            Toast?.Invoke(T("Couldn't save your rating: {0}", error));
            if (entries.Contains(e))
            {
                e.Comment.Liked = wasLiked;
                e.Comment.Disliked = wasDisliked;
                e.Comment.Likes = wasLikes;
                BuildToolbar(e);
            }
        }

        // A plain count ("8"); abbreviated ones ("1,2 k") can't be adjusted.
        private static string Bump(string likes, int delta)
        {
            if (likes.Length == 0 && delta > 0)
            {
                return "1";
            }
            if (!int.TryParse(likes, out int n))
            {
                return likes;
            }
            return n + delta > 0 ? (n + delta).ToString() : "";
        }

        private void ToggleReplies(Entry e)
        {
            if (e.RepliesLoading)
            {
                return;
            }
            RemoveChildren(e);          // also a reply just posted: the answer will include it
            e.RepliesOpen = !e.RepliesOpen;
            BuildToolbar(e);
            FocusOn(e);
            ArrangeParts();
            Reveal(-1);
            if (e.RepliesOpen)
            {
                e.RepliesLoading = true;
                string token = e.Comment.ReplyToken;
                UiGuard.Fire("WatchSection.LoadReplies", () => LoadReplies(e, token, null));
            }
        }

        private void LoadMoreReplies(Entry row)
        {
            if (row.Parent.RepliesLoading)
            {
                return;
            }
            row.Parent.RepliesLoading = true;
            row.Text.Text = T("Loading…");
            string token = row.MoreToken;
            UiGuard.Fire("WatchSection.LoadReplies", () => LoadReplies(row.Parent, token, row));
        }

        // The answer goes in place of `row` (the "Show more replies" row), or right below the thread.
        private async Task LoadReplies(Entry parent, string token, Entry row)
        {
            int gen = generation;
            CommentPage page = await YouTube.Comments(videoId, token);
            if (gen != generation || closed || !entries.Contains(parent))
            {
                return;
            }
            parent.RepliesLoading = false;
            if (!parent.RepliesOpen)
            {
                return;
            }
            if (page.Error != null)
            {
                Tizen.Log.Warn("OmegaTube", "replies: " + page.Error);
                Toast?.Invoke(T("Replies unavailable"));
                if (row != null)
                {
                    row.Text.Text = T("Show more replies");
                }
                else
                {
                    parent.RepliesOpen = false;
                    BuildToolbar(parent);
                }
                return;
            }
            float before = Scroll;
            Entry focusedBefore = FocusedEntry;
            int at = row != null ? entries.IndexOf(row) : entries.IndexOf(parent) + 1;
            int first = at;
            if (row != null)
            {
                parent.Children.Remove(row);
                RemoveEntry(row);
            }
            foreach (Comment c in page.Items)
            {
                if (entries.Count >= MaxEntries)
                {
                    page.Continuation = "";
                    break;
                }
                AddEntry(c, at++, parent);
            }
            if (page.Continuation.Length > 0)
            {
                AddMoreRow(parent, page.Continuation, at);
            }
            if (focusedBefore == row && row != null)
            {
                // The focus was on the removed row: on to the first new reply.
                focusIndex = 3 + Math.Min(first, entries.Count - 1);
                toolIndex = -1;
            }
            else if (focusedBefore != null)
            {
                FocusOn(focusedBefore);
            }
            ArrangeParts();
            Reveal(+1);
            Refresh();
            if (Scroll != before)
            {
                ScrollChanged?.Invoke();
            }
        }

        // ------------------------------------------------------------------ posting

        // target: the comment being answered, null for a new comment.
        private void OpenEntry(Entry target = null)
        {
            if (!YouTube.SignedIn())
            {
                Toast?.Invoke(T("Sign in (Account menu) to comment"));
                return;
            }
            if (target == null && createParams.Length == 0)
            {
                Toast?.Invoke(loading ? T("Loading comments…") : T("Comments can't be posted on this video"));
                return;
            }
            replyTarget = target;
            if (target == null)
            {
                entry = new TextEntry(T("Add a comment"), T("Add a comment…"), draft, MaxCommentLength);
            }
            else
            {
                // Answering a reply: it goes in the same thread and names its author, as on the site.
                string author = target.Comment.Author;
                string initial = target.Parent != null && author.Length > 0 ? author + " " : "";
                entry = new TextEntry(T("Reply to {0}", author.Length > 0 ? author : T("Unknown")), T("Add a reply…"), initial, MaxCommentLength);
            }
            entry.Done = text => UiGuard.Run("WatchSection.EntryDone", () => EntryDone(text));
            overlayHost.Add(entry);
        }

        private void EntryDone(string text)
        {
            if (text == null)
            {
                if (replyTarget == null)
                {
                    draft = entry.Text;
                }
                CloseEntry();
                return;
            }
            if (posting)
            {
                return;
            }
            UiGuard.Fire("WatchSection.Post", () => Post(text));
        }

        private void CloseEntry()
        {
            if (entry == null)
            {
                return;
            }
            overlayHost.Remove(entry);
            entry.Dispose();
            entry = null;
            replyTarget = null;
            UpdateForm();
            Refresh();
        }

        private async Task Post(string text)
        {
            Entry target = replyTarget;
            Entry thread = target?.Parent ?? target;
            posting = true;
            entry?.SetBusy(true);
            (Comment created, string error) = target == null
                ? await YouTube.PostComment(createParams, text)
                : await YouTube.PostReply(videoId, thread.Comment.Id, text);
            posting = false;
            if (closed)
            {
                return;
            }
            entry?.SetBusy(false);
            if (error != null)
            {
                Tizen.Log.Warn("OmegaTube", "post comment: " + error);
                Toast?.Invoke(T("Comment not posted: {0}", error));
                return;
            }
            float before = Scroll;
            if (target == null)
            {
                draft = "";
            }
            CloseEntry();
            Comment shown = created ?? new Comment { Author = myHandle ?? "", Avatar = myPhoto ?? "", Text = text };
            if (shown.Published.Length == 0)
            {
                shown.Published = T("just now");
            }
            if (string.IsNullOrEmpty(shown.Avatar) && !string.IsNullOrEmpty(myPhoto))
            {
                shown.Avatar = myPhoto;
            }
            status.Hide();
            int at = target == null ? 0 : entries.IndexOf(target) + 1;
            AddEntry(shown, at, thread);
            ArrangeParts();
            focusIndex = 3 + at;
            toolIndex = -1;
            Reveal(+1);
            Refresh();
            if (Scroll != before)
            {
                ScrollChanged?.Invoke();
            }
            Toast?.Invoke(target == null ? T("Comment posted") : T("Reply posted"));
        }

        // ------------------------------------------------------------------ text helpers

        private static int CharsPerLine(int width, float pixelSize) => Math.Max(10, (int)(width / (pixelSize * 0.55f)));

        private static float EstimateWidth(string text, float pixelSize) => text.Length * pixelSize * 0.55f;

        // Word wrap with over-long words (links) cut, so the line count is the label's.
        private static List<string> WrapLines(string text, int max)
        {
            var lines = new List<string>();
            foreach (string paragraph in text.Replace("\r", "").Split('\n'))
            {
                var line = new System.Text.StringBuilder();
                foreach (string raw in paragraph.Split(' '))
                {
                    string word = raw;
                    while (word.Length > max)
                    {
                        if (line.Length > 0)
                        {
                            lines.Add(line.ToString());
                            line.Clear();
                        }
                        lines.Add(word.Substring(0, max));
                        word = word.Substring(max);
                    }
                    if (word.Length == 0)
                    {
                        continue;
                    }
                    if (line.Length > 0 && line.Length + 1 + word.Length > max)
                    {
                        lines.Add(line.ToString());
                        line.Clear();
                    }
                    if (line.Length > 0)
                    {
                        line.Append(' ');
                    }
                    line.Append(word);
                }
                lines.Add(line.ToString());
            }
            while (lines.Count > 1 && lines[lines.Count - 1].Length == 0)
            {
                lines.RemoveAt(lines.Count - 1);
            }
            return lines;
        }
    }
}
