using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace OmegaTube.Ui
{
    // The buffering mark of the player: a ring with a bright arc that turns in place around the head of the progress
    // bar while the picture is frozen waiting for data. Place it with Position (its centre sits on that point).
    internal class StallRing : ImageView
    {
        private readonly Animation turn;
        private bool spinning;

        public StallRing(int diameter)
        {
            ResourceUrl = Theme.Image("stall_ring");
            Size = new Size(diameter, diameter);
            ParentOrigin = Tizen.NUI.ParentOrigin.TopLeft;
            PivotPoint = Tizen.NUI.PivotPoint.Center;       // turns about its centre
            PositionUsesPivotPoint = true;                   // and Position is that centre
            turn = new Animation(900) { Looping = true };
            turn.AnimateBy(this, "Orientation", new Rotation(new Radian(new Degree(360f)), PositionAxis.Z));
            Hide();
        }

        public void Spin(bool on)
        {
            if (on == spinning)
            {
                return;
            }
            spinning = on;
            if (on)
            {
                Show();
                turn.Play();
            }
            else
            {
                turn.Stop();
                Hide();
            }
        }
    }
}
