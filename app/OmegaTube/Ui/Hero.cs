using System.Collections.Generic;
using OmegaTube.Data;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace OmegaTube.Ui
{
    // Full-screen backdrop for the "Glorious" home style: the focused item's thumbnail fills the screen and
    // fades to black on the left (under the nav) and along the bottom (under the text), with the item's title and
    // a metadata line over it. Hero.Display(item) is called by HomeScreen as focus moves.
    //
    // The feed only carries the small mqdefault thumbnail (320×180), which is why a stretched backdrop looked
    // blurry. For the backdrop we fetch the full-resolution frames from the id instead: a reliable hqdefault
    // base layer with the sharp maxresdefault on top of it, so when maxres is missing (not every video has one)
    // the hqdefault still shows rather than a blank or a stale frame.
    public class Hero : View
    {
        private readonly ImageView artBase, artHi;
        private readonly TextLabel title, meta;
        private Animation fade;
        private string currentId = "";

        public Hero()
        {
            Size = new Size(1920, 1080);
            BackgroundColor = Theme.Background;

            // Base: hqdefault (480×360, always present). It is 4:3 with letterbox bars baked in; ScaleToFill
            // crops to the centre 16:9, which is exactly the frame without the bars.
            artBase = new ImageView { Size = new Size(1920, 1080), FittingMode = FittingModeType.ScaleToFill };
            Add(artBase);
            // High: maxresdefault (1280×720, 16:9) when the video has it; drawn over the base.
            artHi = new ImageView { Size = new Size(1920, 1080), FittingMode = FittingModeType.ScaleToFill };
            Add(artHi);

            // Left fade (solid black under the nav column -> transparent) and bottom fade (for the text).
            Add(Gradient(new Vector2(-0.5f, 0f), new Vector2(0.5f, 0f), 1180, 1080, 0, 0));
            Add(Gradient(new Vector2(0f, 0.5f), new Vector2(0f, -0.5f), 1920, 600, 0, 480));

            title = Widgets.Label("", Theme.TitleSize + 30, Theme.Text, 960, 180);
            title.Position = new Position(Theme.GloriousInset, 200);
            title.MultiLine = true;
            Add(title);

            meta = Widgets.Label("", Theme.BodySize + 4, Theme.TextDim, 960, 46);
            meta.Position = new Position(Theme.GloriousInset, 404);
            Add(meta);
        }

        private static string Frame(string id, string variant) => "https://i.ytimg.com/vi/" + id + "/" + variant + ".jpg";

        // Sets the item's artwork + title + metadata. No-op when it is already the shown item.
        public void Display(VideoItem item)
        {
            if (item == null || item.Id == currentId)
            {
                return;
            }
            currentId = item.Id;
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

        private static View Gradient(Vector2 start, Vector2 end, int w, int h, int x, int y)
        {
            var v = new View { Size = new Size(w, h), Position = new Position(x, y) };
            var map = new PropertyMap();
            map.Add((int)Visual.Property.Type, new PropertyValue((int)Visual.Type.Gradient));
            map.Add((int)GradientVisualProperty.StartPosition, new PropertyValue(start));
            map.Add((int)GradientVisualProperty.EndPosition, new PropertyValue(end));
            var offsets = new PropertyArray();
            offsets.Add(new PropertyValue(0f));
            offsets.Add(new PropertyValue(1f));
            map.Add((int)GradientVisualProperty.StopOffset, new PropertyValue(offsets));
            var colors = new PropertyArray();
            colors.Add(new PropertyValue(new Vector4(0f, 0f, 0f, 1f)));
            colors.Add(new PropertyValue(new Vector4(0f, 0f, 0f, 0f)));
            map.Add((int)GradientVisualProperty.StopColor, new PropertyValue(colors));
            v.Background = map;
            return v;
        }
    }
}
