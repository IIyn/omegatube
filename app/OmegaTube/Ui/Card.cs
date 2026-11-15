using System;
using System.Collections.Generic;
using OmegaTube.Data;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using static OmegaTube.I18n;

namespace OmegaTube.Ui
{
    // A video card in one of four looks:
    //  - classic (Omega): rounded 16:9 thumbnail with an info box under it that lights up when focused;
    //  - tube (Tube): the bare rounded thumbnail, the title on two lines and the channel line under it on the page
    //    colour (no box), always visible; the focus adds the white outline and the scale;
    //  - glass (Glorious rows 2+): a bare rounded thumbnail with a soft shadow. Focused, a dark panel with the title
    //    (one line, scrolling when it is longer) appears on the thumbnail, the channel / views / age line under it,
    //    and a hairline outline runs round the card. A short animated preview can play over the thumbnail;
    //  - featured (thumbOnly, Glorious row 1): a bare rounded thumbnail with the same shadow and outline, no text:
    //    the title and details live on the hero backdrop.
    // Duration badge bottom right and a red progress bar for started videos in all of them.
    // Built in two steps for smooth scrolling: the constructor only makes the cheap frame (thumbnail, info box);
    // Fill() adds the texts and starts the thumbnail, when the card is about to be seen (UiWork). Parts a card may
    // never need (outline, animation layers, scrolling title) are created on first use.
    public class Card : View
    {
        public VideoItem Item { get; private set; }
        public bool Filled { get; private set; }
        public bool Shown { get; private set; } = true;   // managed mirror of Visibility (rows show / hide far cards)

        private const int ShadowMargin = 36, ShadowDrop = 10;   // card_shadow9.9: the blur margin is baked into the image
        private const int InfoInset = 8, InfoHeight = 54, MetaRowH = 44, TitlePad = 14, TubePad = 14;
        private const int MarqueePauseMs = 1100, MarqueePxPerSec = 70;

        private readonly int width, thumbH;
        private readonly bool thumbOnly;   // featured: no text at all
        private readonly bool glass;       // glass or featured: shadow and outline
        private readonly bool tube;        // Tube: bare thumbnail, title + channel line under it on the page colour
        private readonly ImageView thumb;
        private readonly ImageView info;   // classic: the box under the thumbnail; glass: the title panel on it
        private readonly View infoTopPatch;
        private readonly View progress;
        private View ring;                 // hairline outline of the focused card
        private ImageView anim, corners;   // animated preview over the thumbnail, and the frame that rounds its corners
        private View titleClip;
        private TextLabel title, meta, marquee;   // marquee: the title at its full width, scrolled while focused
        private TextLabel channel;         // tube only: the channel on its own line, as YouTube has it
        private float marqueeOverflow;
        private bool noMarquee;            // the title fits: nothing to scroll
        private Animation fadeInfo, fadeAnim, marqueeAnim, zoom;
        private bool focused;
        private bool Rich => glass && !thumbOnly;   // glass look with texts

        public Card(VideoItem item, int width = Theme.CardWidth, bool thumbOnly = false, bool glass = false, bool tube = false)
        {
            Item = item;
            this.width = width;
            this.thumbOnly = thumbOnly;
            this.glass = glass;
            this.tube = tube;
            thumbH = Theme.ThumbHeight(width);
            Size = new Size(width, thumbOnly ? thumbH
                                    : glass ? thumbH + MetaRowH
                                    : tube ? Theme.TubeCardHeight(width)
                                    : Theme.CardHeight(width));
            // Scale around the centre so neighbours are overlapped evenly.
            PivotPoint = Tizen.NUI.PivotPoint.Center;
            PositionUsesPivotPoint = false;  // positions stay top-left based

            // Rounding on Tizen 5.5: the firmware's NUI has no CornerRadius, so we round with image techniques that
            // it does support. The thumbnail is clipped by a rounded-rect alpha mask; the classic info box is a white
            // rounded-rect nine-patch tinted to the panel colour (fixed corners at any width). Without a classic info
            // box under it the thumbnail rounds all four corners; with one it extends a radius under the box (the
            // "glued" look) and a square patch hides the box's rounded top corners.
            if (glass)
            {
                // Soft shadow under the thumbnail (no BoxShadow on 5.5: a pre-blurred nine-patch, drawn first = behind).
                Add(new ImageView
                {
                    ResourceUrl = Theme.Image("card_shadow9.9"),
                    Size = new Size(width + 2 * ShadowMargin, thumbH + 2 * ShadowMargin),
                    Position = new Position(-ShadowMargin, -ShadowMargin + ShadowDrop),
                });
            }
            thumb = new ImageView { Size = new Size(width, thumbOnly || glass || tube ? thumbH : thumbH + Theme.CardRadius) };
            Add(thumb);

            if (Rich)
            {
                info = new ImageView
                {
                    Position = new Position(InfoInset, thumbH - InfoHeight - InfoInset),
                    Size = new Size(width - 2 * InfoInset, InfoHeight),
                    ResourceUrl = Theme.Image("card_info9.9"),
                    Opacity = 0f,   // fades in while focused
                };
                info.Hide();
                Add(info);
            }
            else if (!thumbOnly && !tube)
            {
                info = new ImageView
                {
                    Position = new Position(0, thumbH),
                    Size = new Size(width, Theme.CardInfoHeight),
                    ResourceUrl = Theme.Image("round9.9"),
                    Color = Theme.CardInfo,
                };
                Add(info);
                infoTopPatch = Widgets.Box(Theme.CardInfo, width, Theme.CardRadius);
                info.Add(infoTopPatch);
            }

            // On top of the info box's edge: the watched-fraction bar sits on the seam.
            progress = Widgets.Box(Theme.Accent, 0, 5);
            progress.Position = new Position(0, thumbH - 5);
            Add(progress);
            UpdateProgress();
        }

        // Called by Show / Hide of the row: remembers the state without a native read.
        public void SetShown(bool shown)
        {
            if (shown != Shown)
            {
                Shown = shown;
                Widgets.SetVisible(this, shown);
            }
        }

        // Texts, duration badge and thumbnail. Text labels are what costs (shaping on the UI thread).
        public void Fill()
        {
            if (Filled)
            {
                return;
            }
            Filled = true;
            string badge = Item.Live ? T("LIVE") : Item.Duration;
            if (!string.IsNullOrEmpty(badge))
            {
                var duration = Widgets.Label(" " + badge + " ", Theme.SmallSize - 2, Theme.Text);
                duration.BackgroundColor = Item.Live ? Theme.Accent : new Color(0, 0, 0, 0.82f);
                Widgets.Round(duration, 4);
                duration.ParentOrigin = Tizen.NUI.ParentOrigin.TopLeft;
                duration.PivotPoint = Tizen.NUI.PivotPoint.BottomRight;
                duration.PositionUsesPivotPoint = true;
                duration.Position = new Position(width - 8, thumbH - 10);
                thumb.Add(duration);
            }

            if (Rich)
            {
                // The title: one line with an ellipsis (a longer one scrolls while the card is focused).
                int titleW = width - 2 * InfoInset - 2 * TitlePad;
                titleClip = new View { Position = new Position(TitlePad, 9), Size = new Size(titleW, 36),
                                       ClippingMode = ClippingModeType.ClipChildren };
                title = Widgets.Label(Item.Title, Theme.BodySize, Theme.Text, titleW, 36);
                titleClip.Add(title);
                info.Add(titleClip);
                // Channel, views and age on one line under the card.
                meta = Widgets.Label(MetaLine(Item), Theme.SmallSize, Theme.TextDim, width - 12);
                meta.Position = new Position(6, thumbH + 10);
                meta.Opacity = 0f;
                meta.Hide();
                Add(meta);
                if (focused)
                {
                    FadeInfo(true);   // focused before it was filled (the first card of a row): show what it has now
                }
            }
            else if (tube)
            {
                // Tube: YouTube's three lines under the thumbnail — the title on two, then the channel, then the
                // views and age — all on the page colour and always visible; the focus adds the outline and the scale.
                // The type scales with the card: the wall draws 660 wide and takes 36/24px, the player's row 400 and
                // takes 26/19px so three lines still read in the 152px the small card reserves. The title's height
                // must clear two real line boxes or NUI ellipsises on the first line (its line height is ~1.45x).
                bool big = width >= 600;
                float titleSize = big ? Theme.BodySize + 12 : Theme.BodySize + 2;
                float metaSize = big ? Theme.SmallSize + 5 : Theme.SmallSize;
                title = Widgets.Label(Item.Title, titleSize, Theme.Text, width - 2 * TubePad, big ? 112 : 76);
                title.MultiLine = true;
                title.Position = new Position(TubePad, thumbH + 12);
                Add(title);

                channel = Widgets.Label(Item.Channel, metaSize, Theme.TubeSub, width - 2 * TubePad);
                channel.Position = new Position(TubePad, thumbH + (big ? 130 : 92));
                Add(channel);

                meta = Widgets.Label(ViewsLine(Item), metaSize, Theme.TextDim, width - 2 * TubePad);
                meta.Position = new Position(TubePad, thumbH + (big ? 172 : 124));
                Add(meta);
            }
            else if (!thumbOnly)
            {
                title = Widgets.Label(Item.Title, Theme.BodySize, Theme.Text, width - 24, 62);
                title.MultiLine = true;
                title.Position = new Position(12, 8);
                info.Add(title);

                meta = Widgets.Label(MetaLine(Item), Theme.SmallSize, Theme.TextDim, width - 24);
                meta.Position = new Position(12, 70);
                info.Add(meta);
                ApplyFocusColors();
            }

            if (!string.IsNullOrEmpty(Item.Thumbnail))
            {
                // The mask must not be smaller than the picture: Widgets.MaskedImage sets CropToMask, which crops to
                // the mask's pixel rectangle. Sharp draws 1280x720 stills through the 1280x720 mask; Data saver draws
                // the 320x180 file through the original 400x225 one, which cannot crop it (as upstream has always done).
                Widgets.MaskedImage(thumb, Thumbs.Url(Item.Thumbnail),
                                    Theme.Image(Thumbs.Sharp ? "round_mask_hd" : "round_mask"),
                                    smooth: true);
            }
        }

        // The same video in a fresher page (network after cache): keeps the card and its loaded thumbnail.
        public void Rebind(VideoItem item)
        {
            Item = item;
            if (Filled && !thumbOnly)
            {
                title.Text = item.Title;
                if (tube)
                {
                    channel.Text = item.Channel;
                    meta.Text = ViewsLine(item);
                }
                else
                {
                    meta.Text = MetaLine(item);
                }
                DropMarquee();   // measured for the old title
            }
            UpdateProgress();
        }

        private static string MetaLine(VideoItem item)
        {
            string line = item.Channel;            if (!string.IsNullOrEmpty(item.Views))
            {
                line += " • " + item.Views;
            }
            if (!string.IsNullOrEmpty(item.Published))
            {
                line += " • " + item.Published;
            }
            return line;
        }

        public void UpdateProgress()
        {
            // Local position (watched here) or YouTube's (watched on another device), whichever is further.
            double p = Math.Max(LocalStore.Progress(Item.Id), Item.WatchedPercent / 100.0);
            progress.SizeWidth = (float)(width * p);
            Widgets.SetVisible(progress, p > 0.01);
        }

        // Tube: the views and the age alone (the channel has its own line there, as on YouTube's TV layout).
        private static string ViewsLine(VideoItem item)
        {
            string line = item.Views ?? "";
            if (string.IsNullOrEmpty(item.Published))
            {
                return line;
            }
            return line.Length == 0 ? item.Published : line + " • " + item.Published;
        }

        // ------------------------------------------------------------------ animated preview (glass cards)

        public bool CanAnimate => Rich && Filled;

        // Shows an animated preview over the thumbnail: the sheet is loaded unscaled (Desired size = its real size,
        // or the toolkit shrinks it to the view and the cells no longer line up); frames are picked with PixelArea.
        public void BeginAnimation(string sheetPath, int sheetW, int sheetH, RelativeVector4 cell)
        {
            if (!Rich)
            {
                return;
            }
            if (anim == null)
            {
                // Over the thumbnail, under the title panel: a decoded sheet cell, and a page-coloured frame with
                // rounded holes over it that hides the sheet's square corners (a sprite sheet cannot be masked).
                anim = new ImageView { Size = new Size(width, thumbH), Opacity = 0f };
                Add(anim);
                corners = new ImageView { Size = new Size(width, thumbH), Color = Theme.PageTint };
                Add(corners);
                info.RaiseToTop();   // (added last, so lifted back above the new layers)
                progress.RaiseToTop();
                ring?.RaiseToTop();
            }
            var map = new PropertyMap();
            map.Add((int)Visual.Property.Type, new PropertyValue((int)Visual.Type.Image));
            map.Add((int)ImageVisualProperty.URL, new PropertyValue(sheetPath));
            map.Add((int)ImageVisualProperty.DesiredWidth, new PropertyValue(sheetW));
            map.Add((int)ImageVisualProperty.DesiredHeight, new PropertyValue(sheetH));
            anim.Image = map;
            anim.PixelArea = cell;
            corners.ResourceUrl = Theme.Image("round_hole9.9");
            corners.Show();
            anim.Opacity = 0f;
            anim.Show();
            fadeAnim?.Stop();
            fadeAnim = new Animation(250);
            fadeAnim.AnimateTo(anim, "Opacity", 1f, Widgets.EaseOut);
            fadeAnim.Play();
        }

        public void ShowAnimationFrame(RelativeVector4 cell)
        {
            if (anim != null)
            {
                anim.PixelArea = cell;
            }
        }

        public void EndAnimation()
        {
            if (anim == null)
            {
                return;
            }
            fadeAnim?.Stop();
            anim.Hide();
            anim.Opacity = 0f;
            anim.ResourceUrl = "";        // release the sheet's texture
            corners.Hide();
            corners.ResourceUrl = "";
        }

        // ------------------------------------------------------------------ scrolling title (glass cards)

        // A long title scrolls to its end and starts over, after a short pause each way; a title that fits stays put.
        private void StartMarquee()
        {
            if (titleClip == null || noMarquee)
            {
                return;
            }
            if (marquee == null)
            {
                // Measured once, on first focus: the same title at its natural width.
                TextLabel m = Widgets.Label(Item.Title, Theme.BodySize, Theme.Text);
                titleClip.Add(m);
                float overflow = 0f;
                try
                {
                    overflow = m.NaturalSize2D.Width - titleClip.SizeWidth;
                }
                catch (Exception)
                {
                }
                if (overflow <= 2f)
                {
                    titleClip.Remove(m);
                    m.Dispose();
                    noMarquee = true;
                    return;
                }
                m.Hide();
                marquee = m;
                marqueeOverflow = overflow;
            }
            int move = (int)(marqueeOverflow / MarqueePxPerSec * 1000f) + 200;
            title.Hide();
            marquee.PositionX = 0;
            marquee.Show();
            marqueeAnim?.Stop();
            marqueeAnim = new Animation(MarqueePauseMs + move + MarqueePauseMs) { Looping = true };
            marqueeAnim.AnimateTo(marquee, "PositionX", -(marqueeOverflow + 12f), MarqueePauseMs, MarqueePauseMs + move, Widgets.Linear);
            marqueeAnim.Play();
        }

        private void StopMarquee()
        {
            marqueeAnim?.Stop();
            marqueeAnim = null;
            if (marquee != null)
            {
                marquee.Hide();
                marquee.PositionX = 0;
                title.Show();
            }
        }

        private void DropMarquee()
        {
            StopMarquee();
            if (marquee != null)
            {
                titleClip.Remove(marquee);
                marquee.Dispose();
                marquee = null;
            }
            noMarquee = false;
            if (focused)
            {
                StartMarquee();
            }
        }

        // ------------------------------------------------------------------ focus

        public void SetFocused(bool focused)
        {
            if (focused == this.focused)
            {
                return;   // grids refresh every card's state after a page: no animation for unchanged ones
            }
            this.focused = focused;
            if (glass || tube)
            {
                ShowRing(focused);
                if (Rich && Filled)
                {
                    FadeInfo(focused);
                }
            }
            ApplyFocusColors();
            zoom?.Stop();
            zoom = new Animation(140);
            float s = focused ? Theme.FocusScale : 1f;
            zoom.AnimateTo(this, "Scale", new Vector3(s, s, 1f), Widgets.EaseOut);
            zoom.Play();
            if (focused)
            {
                RaiseToTop();
            }
        }

        // The hairline outline, created the first time the card is focused (most cards never are).
        private void ShowRing(bool on)
        {
            if (ring == null)
            {
                if (!on)
                {
                    return;
                }
                ring = new ImageView { ResourceUrl = Theme.Image("card_ring9.9"), Size = new Size(width, thumbH) };
                Add(ring);   // last: over the thumbnail, the title panel and the progress bar
            }
            Widgets.SetVisible(ring, on);
        }

        // Title panel and channel line fade in with the focus and fade out (and leave the render list) without it.
        private void FadeInfo(bool on)
        {
            if (on)
            {
                info.Show();
                meta.Show();
                StartMarquee();
            }
            else
            {
                StopMarquee();
            }
            fadeInfo?.Stop();
            fadeInfo = new Animation(160);
            fadeInfo.AnimateTo(info, "Opacity", on ? 1f : 0f, Widgets.EaseOut);
            fadeInfo.AnimateTo(meta, "Opacity", on ? 1f : 0f, Widgets.EaseOut);
            if (!on)
            {
                fadeInfo.Finished += (s, e) => UiGuard.Run("Card.fade", () =>
                {
                    if (!focused)
                    {
                        info.Hide();
                        meta.Hide();
                    }
                });
            }
            fadeInfo.Play();
        }

        private void ApplyFocusColors()
        {
            if (tube)
            {
                if (Filled)
                {
                    title.TextColor = focused ? Color.White : Theme.Text;
                }
                return;   // no info box to tint: the focus is the outline and the scale
            }
            if (thumbOnly || glass)
            {
                return;   // featured: no info box. Glass: dark panel with fixed colours
            }
            Color infoColor = focused ? Theme.CardInfoFocused : Theme.CardInfo;
            info.Color = infoColor;   // tints the white nine-patch
            infoTopPatch.BackgroundColor = infoColor;
            if (Filled)
            {
                title.TextColor = focused ? Theme.CardTextFocused : Theme.Text;
                meta.TextColor = focused ? Theme.CardTextFocused : Theme.TextDim;
            }
        }

        // The focus outline is a child: a card going away must not take the shared preview ring (Hero's) with it.
        protected override void Dispose(DisposeTypes type)
        {
            if (type == DisposeTypes.Explicit)
            {
                foreach (View child in new List<View>(Children))
                {
                    if (child is PreviewRing)
                    {
                        Remove(child);
                    }
                }
            }
            base.Dispose(type);
        }
    }
}
