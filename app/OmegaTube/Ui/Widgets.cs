using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace OmegaTube.Ui
{
    public static class Widgets
    {
        public static TextLabel Label(string text, float pixelSize, Color color, int width = 0, int height = 0)
        {
            var label = new TextLabel(text)
            {
                TextColor = color,
                PixelSize = pixelSize,
                Ellipsis = true,
            };
            if (width > 0)
            {
                label.Size = new Size(width, height > 0 ? height : pixelSize * 1.5f);
            }
            else
            {
                // Fits its text (badges, toasts): the default up to API 8, but later TizenFX stretch it to the parent.
                label.WidthResizePolicy = ResizePolicyType.UseNaturalSize;
                label.HeightResizePolicy = ResizePolicyType.UseNaturalSize;
            }
            return label;
        }

        // Bold text. Cosmetic: if this TizenFX lacks FontStyle, the call is JIT-compiled (and fails) inside the
        // try, and the label stays regular.
        public static TextLabel Bold(TextLabel label)
        {
            try
            {
                SetBold(label);
            }
            catch (System.Exception e)
            {
                Tizen.Log.Warn("OmegaTube", "FontStyle: " + e.Message);
            }
            return label;
        }

        private static void SetBold(TextLabel label)
        {
            var style = new PropertyMap();
            style.Add("weight", new PropertyValue("bold"));
            label.FontStyle = style;
        }

        // View.CornerRadius is a float up to API 8 and a Vector4 after (the float setter is gone on
        // Tizen 7+), so it is set through whichever type the running TizenFX has. Older TizenFX (before API 8)
        // have no CornerRadius at all: the view simply stays square.
        private static readonly System.Reflection.PropertyInfo cornerRadius = typeof(View).GetProperty("CornerRadius");

        public static T Round<T>(T view, float radius) where T : View
        {
            if (cornerRadius == null || !cornerRadius.CanWrite)
            {
                return view;
            }
            try
            {
                object value = cornerRadius.PropertyType == typeof(float)
                    ? radius
                    : System.Activator.CreateInstance(cornerRadius.PropertyType, radius, radius, radius, radius);
                cornerRadius.SetValue(view, value);
            }
            catch (System.Exception e)
            {
                Tizen.Log.Warn("OmegaTube", "CornerRadius: " + e.Message);
            }
            return view;
        }

        // DALi corner radius lives at MixColor + 3 in the visual property map (devel API). TizenFX 6.0 (the
        // Tizen 5.5 baseline) exposes no View.CornerRadius, but the firmware's DALi still honours this raw key
        // on a visual, so colour fills and images round even there. We set it on the visual AND call Round()
        // (for the TizenFX versions that do have CornerRadius).
        private static readonly int VisualCornerRadius = (int)Visual.Property.MixColor + 3;

        // A rounded solid-colour fill, applied as a ColorVisual (so it rounds on Tizen 5.5). Call it again
        // to recolour later without losing the radius (a plain BackgroundColor assignment would drop it).
        public static void FillRounded(View v, Color color, float radius)
        {
            var map = new PropertyMap();
            map.Add((int)Visual.Property.Type, new PropertyValue((int)Visual.Type.Color));
            map.Add((int)ColorVisualProperty.MixColor, new PropertyValue(color));
            if (radius > 0f)
            {
                map.Add(VisualCornerRadius, new PropertyValue(radius));
            }
            v.Background = map;
            Round(v, radius);
        }

        // A rounded image: the URL and the rounded-rect alpha mask are set together in one Image visual (setting
        // AlphaMaskURL separately from ResourceUrl does not always combine into the same visual). Alpha masking
        // is an old, widely supported NUI feature, so it rounds on Tizen 5.5 where CornerRadius is absent.
        public static void MaskedImage(ImageView iv, string url, string maskUrl)
        {
            try
            {
                var map = new PropertyMap();
                map.Add((int)Visual.Property.Type, new PropertyValue((int)Visual.Type.Image));
                map.Add((int)ImageVisualProperty.URL, new PropertyValue(url));
                map.Add((int)ImageVisualProperty.AlphaMaskURL, new PropertyValue(maskUrl));
                map.Add((int)ImageVisualProperty.CropToMask, new PropertyValue(true));
                iv.Image = map;
            }
            catch (System.Exception e)
            {
                Tizen.Log.Warn("OmegaTube", "MaskedImage: " + e.Message);
                iv.ResourceUrl = url;
            }
        }

        // One instance per easing: animations are created on key presses, the curves need not be.
        public static readonly AlphaFunction EaseOut = new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOut);
        public static readonly AlphaFunction EaseInOut = new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseInOut);
        public static readonly AlphaFunction Linear = new AlphaFunction(AlphaFunction.BuiltinFunctions.Linear);

        public static View Box(Color color, int width, int height)
        {
            return new View { BackgroundColor = color, Size = new Size(width, height) };
        }

        // "Liquid glass" panel: a translucent, rounded fill. The Tizen 5.5 firmware has no backdrop-blur API, so
        // this is translucent + rounded (you see through it) rather than a true frosted blur. Built from the white
        // rounded nine-patch tinted by `fill` (keeps fixed corners at any size and rounds on 5.5, unlike
        // CornerRadius). `fill` carries the translucency in its alpha.
        public static ImageView Glass(Color fill, int width, int height)
        {
            return new ImageView { ResourceUrl = Theme.Image("round9.9"), Size = new Size(width, height), Color = fill };
        }

        // Baked glass nine-patch (tools/glass-panel/make_glass.py): translucent fill, bright rim and a soft top
        // highlight, rounded at any size. `lit` is the brighter variant for the focused entry. Drawn untinted;
        // tint it through Color (e.g. accent) when needed.
        // `pill` uses the rounder variant (buttons, selected entries) instead of the card one.
        public static ImageView GlassCard(bool lit, int width, int height, bool pill = false)
        {
            string name = (pill ? "glass_pill" : "glass") + (lit ? "_lit" : "") + "9.9";
            return new ImageView { ResourceUrl = Theme.Image(name), Size = new Size(width, height) };
        }

        // A card / panel background: glass in Glorious, the flat rounded fill otherwise.
        public static View Panel(Color flat, int width, int height, float radius)
        {
            return Theme.Glorious ? (View)GlassCard(false, width, height) : Rounded(flat, width, height, radius);
        }

        public static void SetVisible(View view, bool visible)
        {
            if (visible)
            {
                view.Show();
            }
            else
            {
                view.Hide();
            }
        }

        // Loading spinner: a ring of dots with a fading tail, rotating forever. Returns the ring (centre
        // it with Position) and its animation (Stop/Play with the ring's visibility).
        public static View Spinner(int size, Color color, out Animation spin)
        {
            var ring = new View { Size = new Size(size, size), PivotPoint = Tizen.NUI.PivotPoint.Center, PositionUsesPivotPoint = true };
            const int Dots = 10;
            int dot = size / 7;
            float r = size / 2f - dot;
            for (int i = 0; i < Dots; i++)
            {
                double a = 2 * System.Math.PI * i / Dots;
                var d = new View
                {
                    Size = new Size(dot, dot),
                    BackgroundColor = new Color(color.R, color.G, color.B, 0.15f + 0.85f * i / (Dots - 1)),
                    Position = new Position(size / 2f + r * (float)System.Math.Cos(a) - dot / 2f,
                                            size / 2f + r * (float)System.Math.Sin(a) - dot / 2f),
                    PositionUsesPivotPoint = false,
                };
                Round(d, dot / 2f);
                ring.Add(d);
            }
            spin = new Animation(900) { Looping = true };
            spin.AnimateBy(ring, "Orientation", new Rotation(new Radian(new Degree(360f)), PositionAxis.Z));
            spin.Play();
            return ring;
        }

        public static ImageView Icon(string name, int size)
        {
            return new ImageView { ResourceUrl = Theme.Icon(name), Size = new Size(size, size) };
        }

        public static View Rounded(Color color, int width, int height, float radius)
        {
            var v = new View { Size = new Size(width, height) };
            FillRounded(v, color, radius);
            return v;
        }

        // On/off switch (settings rows). Update with SetSwitch.
        public static View Switch(bool on)
        {
            var track = Rounded(Theme.SurfaceHigh, 72, 40, 20);
            var knob = Rounded(Theme.Text, 30, 30, 15);
            knob.Position = new Position(5, 5);
            track.Add(knob);
            SetSwitch(track, on);
            return track;
        }

        public static void SetSwitch(View track, bool on)
        {
            track.BackgroundColor = on ? Theme.Accent : new Color(0.45f, 0.45f, 0.45f, 1f);
            track.GetChildAt(0).PositionX = on ? 37 : 5;
        }

        // Pill button (icon + text) with a focused look, as used by the account screen and player overlays.
        public class PillButton : View
        {
            private readonly TextLabel label;
            private readonly ImageView icon;
            private readonly View glassLit;       // Glorious: brighter glass while focused

            public PillButton(string text, string iconName, int width, int height = 76)
            {
                Size = new Size(width, height);
                Round(this, height / 2f);
                if (Theme.Glorious)
                {
                    Add(GlassCard(false, width, height, true));
                    glassLit = GlassCard(true, width, height, true);
                    Add(glassLit);
                }
                int x = 34;
                if (iconName != null)
                {
                    icon = Icon(iconName, 40);
                    icon.Position = new Position(28, (height - 40) / 2);
                    Add(icon);
                    x = 80;
                }
                label = Label(text, Theme.BodySize + 2, Theme.Text, width - x - 24, height);
                label.VerticalAlignment = VerticalAlignment.Center;
                label.Position = new Position(x, 0);
                if (Theme.Glorious && iconName == null)
                {
                    // A text-only pill (Yes / No ...): the text sits in the middle.
                    label.Size = new Size(width, height);
                    label.Position = new Position(0, 0);
                    label.HorizontalAlignment = HorizontalAlignment.Center;
                }
                Add(label);
                SetFocused(false);
            }

            public string Text
            {
                get => label.Text;
                set => label.Text = value;
            }

            public void SetFocused(bool focused)
            {
                if (glassLit != null)
                {
                    // Glass: the focused pill is a brighter glass and keeps its light text and icon.
                    SetVisible(glassLit, focused);
                    label.TextColor = Theme.Text;
                    Scale = focused ? new Vector3(1.04f, 1.04f, 1f) : Vector3.One;
                    return;
                }
                BackgroundColor = focused ? Theme.CardInfoFocused : Theme.SurfaceHigh;
                label.TextColor = focused ? Theme.CardTextFocused : Theme.Text;
                if (icon != null)
                {
                    // White icons on the light focused pill: darken them through the mix colour.
                    icon.Color = focused ? new Color(0.16f, 0.16f, 0.16f, 1f) : Color.White;
                }
                Scale = focused ? new Vector3(1.04f, 1.04f, 1f) : Vector3.One;
            }
        }

        // Greedy word wrap to lines of at most `max` characters (subtitles: the label sizes to its text).
        public static string Wrap(string text, int max)
        {
            var sb = new System.Text.StringBuilder();
            foreach (string paragraph in text.Split('\n'))
            {
                int line = 0;
                foreach (string word in paragraph.Split(' '))
                {
                    if (word.Length == 0)
                    {
                        continue;
                    }
                    if (line > 0 && line + 1 + word.Length > max)
                    {
                        sb.Append('\n');
                        line = 0;
                    }
                    else if (line > 0)
                    {
                        sb.Append(' ');
                        line++;
                    }
                    sb.Append(word);
                    line += word.Length;
                }
                sb.Append('\n');
            }
            return sb.ToString().TrimEnd('\n');
        }

        private static int qrSerial;

        // QR code on a white `host` of size x size (which gives the quiet zone and the rounded corners);
        // replaces the previous code. Hides the host when there is nothing to draw. One bitmap rather than a
        // view per run of dark modules: a bug report URL makes ~3000 runs, seconds of UI thread on the TV.
        public static void DrawQr(View host, int size, System.Collections.Generic.List<string> rows)
        {
            foreach (View child in new System.Collections.Generic.List<View>(host.Children))
            {
                host.Remove(child);
                child.Dispose();
            }
            if (rows.Count == 0)
            {
                host.Hide();
                return;
            }
            int quiet = 3;
            int module = size / (rows.Count + 2 * quiet);
            int side = module * rows.Count;
            var image = new ImageView { ResourceUrl = WriteQrBitmap(rows, module), Size = new Size(side, side) };
            image.Position = new Position((size - side) / 2, (size - side) / 2);
            host.Add(image);
            host.Show();
        }

        // 24-bit BMP, `module` pixels per module (no scaling, so no blur), in the app's cache folder. A new
        // name each time: NUI caches images by URL.
        private static string WriteQrBitmap(System.Collections.Generic.List<string> rows, int module)
        {
            string dir = Tizen.Applications.Application.Current.DirectoryInfo.Cache;
            foreach (string old in System.IO.Directory.GetFiles(dir, "qr-*.bmp"))
            {
                System.IO.File.Delete(old);
            }
            int side = module * rows.Count;
            int stride = (side * 3 + 3) & ~3;
            var bmp = new byte[54 + stride * side];
            void Put(int at, int value)
            {
                System.BitConverter.GetBytes(value).CopyTo(bmp, at);
            }
            bmp[0] = (byte)'B';
            bmp[1] = (byte)'M';
            Put(2, bmp.Length);
            Put(10, 54);
            Put(14, 40);
            Put(18, side);
            Put(22, side);   // positive: rows bottom-up
            bmp[26] = 1;
            bmp[28] = 24;
            Put(34, stride * side);
            for (int py = 0; py < side; py++)
            {
                string row = rows[(side - 1 - py) / module];
                int line = 54 + py * stride;
                for (int px = 0; px < side; px++)
                {
                    byte v = row[px / module] == '1' ? (byte)0 : (byte)255;
                    bmp[line + px * 3] = v;
                    bmp[line + px * 3 + 1] = v;
                    bmp[line + px * 3 + 2] = v;
                }
            }
            string path = System.IO.Path.Combine(dir, "qr-" + ++qrSerial + ".bmp");
            System.IO.File.WriteAllBytes(path, bmp);
            return path;
        }

        // Smoothly moves a view; returns the animation so callers can stop a running one.
        public static Animation Slide(View view, float x, float y, Animation previous, int ms = 180)
        {
            if (previous != null)
            {
                previous.Stop();
                previous.Dispose();   // one per key press under key repeat: hand the native handle back at once
            }
            var anim = new Animation(ms);
            anim.AnimateTo(view, "PositionX", x, EaseOut);
            anim.AnimateTo(view, "PositionY", y, EaseOut);
            anim.Play();
            return anim;
        }
    }
}
