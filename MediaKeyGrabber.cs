using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Tizen.NUI;

namespace OmegaTube
{
    // Grabs media keys directly via ecore_wl2 so Samsung Tizen does not display
    // the system "not available" toast when media keys (Play/Pause, Rewind, etc.) are pressed.
    public static class MediaKeyGrabber
    {
        private static class EcoreWl2
        {
            private const string Lib = "libecore_wl2.so.1";

            [DllImport(Lib, EntryPoint = "ecore_wl2_connected_display_get")]
            public static extern IntPtr ConnectedDisplayGet(IntPtr name);

            [DllImport(Lib, EntryPoint = "ecore_wl2_display_window_find")]
            public static extern IntPtr DisplayWindowFind(IntPtr display, uint id);

            [DllImport(Lib, EntryPoint = "ecore_wl2_window_keygrab_set")]
            public static extern bool WindowKeygrabSet(
                IntPtr window,
                [MarshalAs(UnmanagedType.LPStr)] string key,
                int mod,
                int notMod,
                int priority,
                int mode);

            [DllImport(Lib, EntryPoint = "ecore_wl2_window_keygrab_unset")]
            public static extern bool WindowKeygrabUnset(
                IntPtr window,
                [MarshalAs(UnmanagedType.LPStr)] string key,
                int mod,
                int notMod,
                int priority);
        }

        // Ecore_Wl2_Window_Keygrab_Mode constants: 2 = Shared (allowed with partner/public certs)
        private const int KeygrabModeShared = 2;

        // Media key names to register with the Wayland window manager
        private static readonly string[] MediaKeyNames =
        {
            "XF86PlayBack",       // Play/Pause on Samsung Smart Remote
            "XF86AudioPlay",      // Play on basic remote
            "XF86AudioPause",     // Pause on basic remote
            "XF86AudioStop",      // Stop
            "XF86AudioNext",      // Next track
            "XF86AudioPrev",      // Previous track
            "XF86AudioRewind",    // Rewind
            "XF86AudioForward",   // Fast forward
            "XF86AudioMicMute",
            "XF86Voice",
            "XF86Search",
            "Voice",
        };

        private static readonly List<string> grabbedKeyNames = new List<string>();
        private static IntPtr grabWindow = IntPtr.Zero;

        public static void GrabMediaKeys(Window window)
        {
            if (window == null)
            {
                return;
            }

            try
            {
                grabWindow = App.GetEcoreWindow(window);
                // If not found, attempt resolving via ConnectedDisplayGet + GetNativeId
                if (grabWindow == IntPtr.Zero)
                {
                    IntPtr display = EcoreWl2.ConnectedDisplayGet(IntPtr.Zero);
                    if (display != IntPtr.Zero)
                    {
                        var getIdMethod = typeof(Window).GetMethod("GetNativeId",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (getIdMethod != null)
                        {
                            object val = getIdMethod.Invoke(window, null);
                            uint windowId = Convert.ToUInt32(val);
                            grabWindow = EcoreWl2.DisplayWindowFind(display, windowId);
                        }
                    }
                }

                if (grabWindow == IntPtr.Zero)
                {
                    Tizen.Log.Warn("OmegaTube", "media key grab: native window not found");
                    return;
                }

                foreach (string keyName in MediaKeyNames)
                {
                    bool success = EcoreWl2.WindowKeygrabSet(
                        grabWindow,
                        keyName,
                        0,  // mod
                        0,  // notMod
                        0,  // priority
                        KeygrabModeShared);

                    if (success)
                    {
                        grabbedKeyNames.Add(keyName);
                        Tizen.Log.Info("OmegaTube", $"media key grab: {keyName}");
                    }
                    else
                    {
                        Tizen.Log.Warn("OmegaTube", $"media key grab failed: {keyName}");
                    }
                }
            }
            catch (Exception ex)
            {
                Tizen.Log.Error("OmegaTube", $"media key grab error: {ex.Message}");
                grabWindow = IntPtr.Zero;
            }
        }

        public static void ReleaseMediaKeys()
        {
            if (grabWindow == IntPtr.Zero)
            {
                return;
            }

            foreach (string keyName in grabbedKeyNames)
            {
                try
                {
                    EcoreWl2.WindowKeygrabUnset(grabWindow, keyName, 0, 0, 0);
                }
                catch
                {
                    // Ignore errors during termination cleanup
                }
            }

            grabbedKeyNames.Clear();
            grabWindow = IntPtr.Zero;
        }
    }
}
