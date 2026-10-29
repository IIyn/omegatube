using System;
using System.Collections.Generic;
using System.Diagnostics;
using Tizen.NUI;

namespace OmegaTube.Ui
{
    // Deferred UI work (card texts, thumbnails) spread over frames, so filling content never holds up a key
    // press. Owners queue a pump: each call does one unit of their most urgent work (nearest the focus) and
    // returns false when nothing is left. While the user is moving (a key in the last QuietMs) a single unit
    // runs per frame; at rest units run until the frame budget is spent.
    public static class UiWork
    {
        private const int TickMs = 16;
        private const int QuietMs = 200;
        private const long BudgetMs = 8;

        private static readonly List<Func<bool>> pumps = new List<Func<bool>>();
        private static readonly Stopwatch clock = Stopwatch.StartNew();
        private static long lastKey = -QuietMs;
        private static Timer timer;
        private static int next;

        public static bool Moving => clock.ElapsedMilliseconds - lastKey < QuietMs;

        // No key for that long: time for bigger jobs (building a hidden screen ahead of use).
        public static bool IdleFor(int ms) => clock.ElapsedMilliseconds - lastKey >= ms;

        public static void KeyPressed()
        {
            lastKey = clock.ElapsedMilliseconds;
        }

        public static void Request(Func<bool> pump)
        {
            if (!pumps.Contains(pump))
            {
                pumps.Add(pump);
            }
            if (timer == null)
            {
                timer = new Timer(TickMs);
                timer.Tick += Tick;
                timer.Start();
            }
        }

        public static void Cancel(Func<bool> pump)
        {
            pumps.Remove(pump);
        }

        private static bool Tick(object sender, Timer.TickEventArgs e)
        {
            long start = clock.ElapsedMilliseconds;
            bool moving = Moving;
            do
            {
                if (pumps.Count == 0)
                {
                    timer = null;
                    return false;
                }
                next %= pumps.Count;
                Func<bool> pump = pumps[next];
                bool more;
                try
                {
                    more = pump();
                }
                catch (Exception ex)
                {
                    Tizen.Log.Error("OmegaTube", "UiWork: " + ex);
                    more = false;
                }
                if (more)
                {
                    next++;
                }
                else
                {
                    pumps.Remove(pump);
                }
            }
            while (!moving && clock.ElapsedMilliseconds - start < BudgetMs);
            return true;
        }
    }
}
