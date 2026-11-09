using System;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace OmegaTube.Ui
{
    // The full-screen picture behind the Glorious hero. A new picture is loaded in the background and only shown
    // (cross-faded over the old one) once it is ready, so the screen never shows a half-loaded stage: no blank, no
    // blurry placeholder that sharpens a moment later, no stretched frame. The sharp 1280x720 picture is asked for
    // first; a video without one (a 404, no "ready" signal) gets the small 16:9 one after a short wait.
    //
    // Two layers share the screen. The one underneath fades the one on top out; the one on top fades in over the one
    // underneath; each time the layer that is not showing loads the next picture.
    // UI thread only.
    internal class Backdrop : View
    {
        private const int FadeMs = 260;
        private const int FallbackAfterMs = 2000;   // no maxres picture ("ready" never came): use the small one
        private const int GiveUpAfterMs = 6000;

        private readonly ImageView bottom, top;
        private ImageView visible;      // the layer showing the current picture (null: nothing yet)
        private ImageView loading;      // the layer a picture is being loaded into
        private string loadingId;       // video the loading picture belongs to (also the "stale event" guard)
        private string fallbackUrl;
        private Timer waitTimer;
        private Animation fade;
        private Action finishFade;      // what a running fade does when it ends (run early if another picture comes)

        public Backdrop()
        {
            Size = new Size(1920, 1080);
            bottom = NewLayer();
            top = NewLayer();
            Add(bottom);
            Add(top);
        }

        private ImageView NewLayer()
        {
            var v = new ImageView { Size = new Size(1920, 1080), FittingMode = FittingModeType.ScaleToFill, Opacity = 0f };
            v.ResourceReady += (s, e) => UiGuard.Run("Backdrop.ready", () => OnReady(v));
            return v;
        }

        // The picture of this video: its maxres frame, or `primary` when it is not a plain video id.
        public void Show(string videoId, string primary, string fallback)
        {
            FinishFade();
            StopWaiting();
            loading = visible == bottom ? top : bottom;   // the layer that is not on screen
            loadingId = videoId ?? "-";   // (never null while loading: that is the stale-event guard)
            fallbackUrl = fallback;
            // Behind the showing top layer the new picture loads at full opacity (it is revealed by fading the top out);
            // anywhere else it starts clear and fades in.
            loading.Opacity = loading == bottom && visible == top ? 1f : 0f;
            loading.ResourceUrl = primary;
            waitTimer = new Timer(FallbackAfterMs);
            waitTimer.Tick += (s, e) => UiGuard.Tick("Backdrop.wait", OnWait);
            waitTimer.Start();
        }

        // Nothing on screen any more (another category opened): the next picture fades in from nothing.
        public void Clear()
        {
            FinishFade();
            StopWaiting();
            loadingId = null;
            loading = null;
            visible = null;
            foreach (ImageView layer in new[] { bottom, top })
            {
                layer.Opacity = 0f;
                layer.ResourceUrl = "";
            }
        }

        // A video starts to show over the pictures: they fade out with this animation.
        public void AnimateOut(Animation animation, AlphaFunction ease)
        {
            animation.AnimateTo(bottom, "Opacity", 0f, ease);
            animation.AnimateTo(top, "Opacity", 0f, ease);
        }

        // The video is gone: the picture is back.
        public void RestoreStill()
        {
            if (visible != null)
            {
                visible.Opacity = 1f;
            }
        }

        private bool OnWait()
        {
            if (loading == null)
            {
                waitTimer = null;
                return false;
            }
            if (fallbackUrl != null)
            {
                loading.ResourceUrl = fallbackUrl;   // once: the maxres picture never became ready
                fallbackUrl = null;
                waitTimer.Interval = GiveUpAfterMs;
                return true;
            }
            waitTimer = null;   // gave up: the previous picture stays
            return false;
        }

        private void StopWaiting()
        {
            waitTimer?.Stop();
            waitTimer = null;
        }

        private void OnReady(ImageView layer)
        {
            if (layer != loading || loadingId == null || string.IsNullOrEmpty(layer.ResourceUrl))
            {
                return;   // an old load, or a layer that was just cleared
            }
            StopWaiting();
            ImageView incoming = layer, outgoing = visible;
            loading = null;
            loadingId = null;
            fade = new Animation(FadeMs);
            if (outgoing == top && incoming == bottom)
            {
                fade.AnimateTo(top, "Opacity", 0f, Widgets.EaseOut);   // the new picture is under it
            }
            else
            {
                fade.AnimateTo(incoming, "Opacity", 1f, Widgets.EaseOut);   // over the old one (or from nothing)
            }
            finishFade = () =>
            {
                incoming.Opacity = 1f;
                if (outgoing != null && outgoing != incoming)
                {
                    outgoing.Opacity = 0f;
                    outgoing.ResourceUrl = "";   // its texture goes back
                }
                visible = incoming;
            };
            fade.Finished += (s, e) => UiGuard.Run("Backdrop.fade", FinishFade);
            fade.Play();
        }

        private void FinishFade()
        {
            if (finishFade == null)
            {
                return;
            }
            Action done = finishFade;
            finishFade = null;
            fade?.Stop();
            done();
        }
    }
}
