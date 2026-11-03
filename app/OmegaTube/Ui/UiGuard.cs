using System;
using System.Threading;
using System.Threading.Tasks;

namespace OmegaTube.Ui
{
    // Safety net for every way NUI calls back into managed code: keys, timers, animation ends, async
    // continuations. An exception there would cross the native callback and kill the app, so it is caught and
    // shown as a bug report instead. This matters most for TizenFX versions we never tested: a method or
    // property missing on that TV throws MissingMethodException / MissingFieldException / TypeLoadException
    // when the method using it is JIT-compiled, i.e. right where the guarded delegate is invoked.
    // Rule: always pass a delegate (Fire(() => Load())), never an already started call (Fire(Load())), or the
    // exception is raised before the guard runs.
    public static class UiGuard
    {
        private static App app;
        private static SynchronizationContext ui;
        private static int uiThread;

        public static void Init(App owner)
        {
            app = owner;
            ui = SynchronizationContext.Current;
            uiThread = Thread.CurrentThread.ManagedThreadId;
        }

        public static void Run(string where, Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                Report(where, e);
            }
        }

        // Timer.Tick handlers: a failing tick stops its timer (false) so the error is not shown every frame.
        public static bool Tick(string where, Func<bool> tick)
        {
            try
            {
                return tick();
            }
            catch (Exception e)
            {
                Report(where, e);
                return false;
            }
        }

        // Fire-and-forget async work started from the UI (replaces "_ = Load();").
        public static async void Fire(string where, Func<Task> start)
        {
            try
            {
                await start();
            }
            catch (Exception e)
            {
                Report(where, e);
            }
        }

        public static void Report(string where, Exception e)
        {
            string text = where + ": " + e;
            try
            {
                Tizen.Log.Error("OmegaTube", "UI error in " + text);
                if (BugReport.CoreLoaded)
                {
                    NativeCore.Log("ui error in " + text);
                }
            }
            catch (Exception)
            {
                // logging must never be the second failure
            }
            if (app == null)
            {
                return;
            }
            if (ui != null && Thread.CurrentThread.ManagedThreadId != uiThread)
            {
                ui.Post(_ => app.ShowError(where, e), null);
                return;
            }
            app.ShowError(where, e);
        }
    }
}
