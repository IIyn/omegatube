using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using OmegaTube.Config;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using static OmegaTube.I18n;

namespace OmegaTube.Ui
{
    // On-screen keyboard in the app language's layout (QWERTY, AZERTY, Spanish QWERTY with ñ), with a second
    // page of accents and symbols, + Voice Search (Tizen Native STT + Samsung IME Voice Input) + live suggestions + one row of results.
    public class SearchScreen : Screen
    {
        // Native Tizen Speech-To-Text (libstt.so.0) P/Invoke bindings for Voice Search
        private static class NativeStt
        {
            private const string Lib = "libstt.so.0";

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            public delegate void ResultCb(IntPtr stt, int eventType, IntPtr data, int dataCount, IntPtr msg, IntPtr userData);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            public delegate void StateChangedCb(IntPtr stt, int previous, int current, IntPtr userData);

            [DllImport(Lib, EntryPoint = "stt_create")]
            public static extern int Create(out IntPtr stt);

            [DllImport(Lib, EntryPoint = "stt_destroy")]
            public static extern int Destroy(IntPtr stt);

            [DllImport(Lib, EntryPoint = "stt_prepare")]
            public static extern int Prepare(IntPtr stt);

            [DllImport(Lib, EntryPoint = "stt_unprepare")]
            public static extern int Unprepare(IntPtr stt);

            [DllImport(Lib, EntryPoint = "stt_start")]
            public static extern int Start(IntPtr stt, [MarshalAs(UnmanagedType.LPStr)] string language, [MarshalAs(UnmanagedType.LPStr)] string recognitionType);

            [DllImport(Lib, EntryPoint = "stt_stop")]
            public static extern int Stop(IntPtr stt);

            [DllImport(Lib, EntryPoint = "stt_cancel")]
            public static extern int Cancel(IntPtr stt);

            [DllImport(Lib, EntryPoint = "stt_set_recognition_result_cb")]
            public static extern int SetResultCb(IntPtr stt, ResultCb callback, IntPtr userData);

            [DllImport(Lib, EntryPoint = "stt_set_state_changed_cb")]
            public static extern int SetStateChangedCb(IntPtr stt, StateChangedCb callback, IntPtr userData);
        }

        // Four rows of ten characters per layout; the action row comes below.
        internal static readonly string[][] Qwerty =
        {
            new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" },
            new[] { "q", "w", "e", "r", "t", "y", "u", "i", "o", "p" },
            new[] { "a", "s", "d", "f", "g", "h", "j", "k", "l", "'" },
            new[] { "z", "x", "c", "v", "b", "n", "m", ",", ".", "-" },
        };
        internal static readonly string[][] Azerty =
        {
            new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" },
            new[] { "a", "z", "e", "r", "t", "y", "u", "i", "o", "p" },
            new[] { "q", "s", "d", "f", "g", "h", "j", "k", "l", "m" },
            new[] { "w", "x", "c", "v", "b", "n", "é", "'", ".", "-" },
        };
        internal static readonly string[][] QwertySpanish =
        {
            new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" },
            new[] { "q", "w", "e", "r", "t", "y", "u", "i", "o", "p" },
            new[] { "a", "s", "d", "f", "g", "h", "j", "k", "l", "ñ" },
            new[] { "z", "x", "c", "v", "b", "n", "m", ",", ".", "-" },
        };
        internal static readonly string[][] Symbols =
        {
            new[] { "à", "â", "ä", "á", "é", "è", "ê", "ë", "í", "î" },
            new[] { "ï", "ó", "ô", "ö", "ú", "ù", "û", "ü", "ç", "ñ" },
            new[] { "œ", "æ", "ß", "¿", "¡", "?", "!", "&", "#", "@" },
            new[] { ":", ";", "/", "+", "=", "(", ")", """, "'", "_" },
        };
        private enum KeyAction { Page, Space, Delete, Clear, Voice, Search }
        private const int CharRows = 4, ActionRow = 4, ActionKeys = 6;
        private const int KeyW = 86, KeyH = 72, KeyGap = 8;
        private const int KbX = 80, KbY = 190;

        private enum Zone { Keyboard, Suggestions, AddButton, Results }

        private readonly TextLabel queryLabel;
        private readonly TextLabel addButton;
        private readonly TextField voiceInputField;
        private readonly List<List<TextLabel>> keyLabels = new List<List<TextLabel>>();
        private readonly List<TextLabel> suggestionLabels = new List<TextLabel>();
        private readonly View resultsHost;
        private List<string> suggestions = new List<string>();
        private FeedRow results;
        private string query = "";
        private Zone zone = Zone.Keyboard;
        private int keyRow = 1, keyCol;
        private int suggestionIndex;
        private int suggestGeneration;
        private bool symbolPage;
        private bool isListening;
        private IntPtr sttHandle = IntPtr.Zero;
        private NativeStt.ResultCb sttResultDelegate;
        private NativeStt.StateChangedCb sttStateDelegate;

        private string[][] Layout => symbolPage ? Symbols
                                   : Lang == "fr" ? Azerty : Lang == "es" ? QwertySpanish : Qwerty;

        public SearchScreen(App app) : base(app)
        {
            Root.BackgroundColor = Theme.Background;

            var box = Widgets.Box(Theme.Surface, 900, 80);
            box.Position = new Position(KbX, 70);
            Root.Add(box);
            queryLabel = Widgets.Label("", Theme.TitleSize, Theme.Text, 860, 80);
            queryLabel.Position = new Position(20, 0);
            queryLabel.VerticalAlignment = VerticalAlignment.Center;
            box.Add(queryLabel);

            // Hidden NUI TextField so Samsung Tizen's native IME / Remote Voice Dictation can inject spoken text directly
            voiceInputField = new TextField
            {
                Size = new Size(1, 1),
                Position = new Position(-100, -100),
                Opacity = 0f,
                Focusable = true
            };
            voiceInputField.TextChanged += OnVoiceFieldTextChanged;
            Root.Add(voiceInputField);

            addButton = Widgets.Label("  + " + T("Add to menu") + "  ", Theme.BodySize, Theme.Text, 0, 0);
            addButton.Position = new Position(KbX + 930, 86);
            addButton.BackgroundColor = Theme.Surface;
            addButton.Hide();
            Root.Add(addButton);

            for (int r = 0; r <= ActionRow; r++)
            {
                var line = new List<TextLabel>();
                int x = KbX;
                int count = r == ActionRow ? ActionKeys : 10;
                for (int c = 0; c < count; c++)
                {
                    // The action row has 6 wide keys spanning the 10 columns.
                    int w = r == ActionRow ? (10 * (KeyW + KeyGap) - KeyGap - (ActionKeys - 1) * KeyGap) / ActionKeys : KeyW;
                    var key = Widgets.Label("", r == ActionRow ? Theme.BodySize : Theme.BodySize + 4, Theme.Text, w, KeyH);
                    key.HorizontalAlignment = HorizontalAlignment.Center;
                    key.VerticalAlignment = VerticalAlignment.Center;
                    key.BackgroundColor = Theme.Surface;
                    key.Position = new Position(x, KbY + r * (KeyH + KeyGap));
                    Root.Add(key);
                    line.Add(key);
                    x += w + KeyGap;
                }
                keyLabels.Add(line);
            }
            LabelKeys();

            var sugTitle = Widgets.Label(T("Suggestions"), Theme.BodySize, Theme.TextDim, 700);
            sugTitle.Position = new Position(1060, 190);
            Root.Add(sugTitle);
            for (int i = 0; i < 7; i++)
            {
                var s = Widgets.Label("", Theme.BodySize + 2, Theme.Text, 780, 50);
                s.Position = new Position(1060, 240 + i * 54);
                s.VerticalAlignment = VerticalAlignment.Center;
                Root.Add(s);
                suggestionLabels.Add(s);
            }

            resultsHost = new View { Position = new Position(KbX - Theme.FocusMargin, 610), Size = new Size(1920 - KbX + Theme.FocusMargin, 470),
                                     ClippingMode = ClippingModeType.ClipChildren };
            Root.Add(resultsHost);

            InitNativeStt();
            UpdateFocus();
        }

        private void InitNativeStt()
        {
            try
            {
                if (NativeStt.Create(out sttHandle) == 0 && sttHandle != IntPtr.Zero)
                {
                    sttResultDelegate = OnSttResult;
                    sttStateDelegate = OnSttStateChanged;
                    NativeStt.SetResultCb(sttHandle, sttResultDelegate, IntPtr.Zero);
                    NativeStt.SetStateChangedCb(sttHandle, sttStateDelegate, IntPtr.Zero);
                    NativeStt.Prepare(sttHandle);
                }
            }
            catch (Exception e)
            {
                Tizen.Log.Warn("OmegaTube", "Native STT init skipped: " + e.Message);
                sttHandle = IntPtr.Zero;
            }
        }

        private void OnSttStateChanged(IntPtr stt, int previous, int current, IntPtr userData)
        {
            // Tizen STT states: 0=Created, 1=Ready, 2=Recording, 3=Processing
            if (current == 2)
            {
                isListening = true;
                UiGuard.Run("STT.Recording", () =>
                {
                    LabelKeys();
                    UpdateFocus();
                });
            }
            else if (current == 1)
            {
                isListening = false;
                UiGuard.Run("STT.Ready", () =>
                {
                    LabelKeys();
                    UpdateFocus();
                });
            }
        }

        private void OnSttResult(IntPtr stt, int eventType, IntPtr data, int dataCount, IntPtr msg, IntPtr userData)
        {
            if (data == IntPtr.Zero || dataCount <= 0)
            {
                return;
            }
            try
            {
                IntPtr firstStrPtr = Marshal.ReadIntPtr(data);
                string recognized = NativeCore.Utf8(firstStrPtr);
                if (!string.IsNullOrWhiteSpace(recognized))
                {
                    UiGuard.Run("STT.Result", () =>
                    {
                        isListening = false;
                        query = recognized.Trim();
                        LabelKeys();
                        UpdateFocus();
                        UiGuard.Fire("SearchScreen.RefreshSuggestions", RefreshSuggestions);
                        RunSearch();
                    });
                }
            }
            catch (Exception e)
            {
                Tizen.Log.Warn("OmegaTube", "STT result parse error: " + e.Message);
            }
        }

        private void OnVoiceFieldTextChanged(object sender, TextField.TextChangedEventArgs e)
        {
            string dictated = e.TextField?.Text;
            if (!string.IsNullOrWhiteSpace(dictated))
            {
                query = dictated.Trim();
                e.TextField.Text = "";
                isListening = false;
                LabelKeys();
                UpdateFocus();
                UiGuard.Fire("SearchScreen.RefreshSuggestions", RefreshSuggestions);
                RunSearch();
            }
        }

        public void TriggerVoiceSearch()
        {
            // Focus the hidden NUI TextField so Samsung's remote microphone dictation can also type into it
            try
            {
                FocusManager.Instance.SetCurrentFocusView(voiceInputField);
            }
            catch { }

            if (sttHandle != IntPtr.Zero)
            {
                try
                {
                    if (isListening)
                    {
                        NativeStt.Stop(sttHandle);
                        isListening = false;
                    }
                    else
                    {
                        string sttLang = App.Config.Language == "hi" ? "hi_IN" : "en_US";
                        int rc = NativeStt.Start(sttHandle, sttLang, "stt.recognition.type.FREE");
                        if (rc == 0)
                        {
                            isListening = true;
                        }
                    }
                }
                catch (Exception e)
                {
                    Tizen.Log.Warn("OmegaTube", "STT start error: " + e.Message);
                }
            }
            else
            {
                isListening = !isListening;
            }
            LabelKeys();
            UpdateFocus();
        }

        public override void OnHide()
        {
            if (sttHandle != IntPtr.Zero && isListening)
            {
                try { NativeStt.Cancel(sttHandle); } catch { }
                isListening = false;
            }
            base.OnHide();
        }

        private void LabelKeys()
        {
            string[][] layout = Layout;
            for (int r = 0; r < CharRows; r++)
            {
                for (int c = 0; c < 10; c++)
                {
                    keyLabels[r][c].Text = layout[r][c];
                }
            }
            List<TextLabel> actions = keyLabels[ActionRow];
            actions[(int)KeyAction.Page].Text = symbolPage ? "abc" : "àç?#";
            actions[(int)KeyAction.Space].Text = T("Space");
            actions[(int)KeyAction.Delete].Text = "← " + T("Delete");
            actions[(int)KeyAction.Clear].Text = T("Clear");
            actions[(int)KeyAction.Voice].Text = isListening ? "🔴 Mic…" : "🎤 Voice";
            actions[(int)KeyAction.Search].Text = T("Search");
        }

        private int SuggestionCount => System.Math.Min(suggestions.Count, suggestionLabels.Count);

        private void UpdateFocus()
        {
            queryLabel.Text = isListening ? "🎤 Listening… Speak into Remote"
                            : query.Length > 0 ? query + "_" : T("Search YouTube…");
            queryLabel.TextColor = isListening ? Theme.Accent : query.Length > 0 ? Theme.Text : Theme.TextDim;
            for (int r = 0; r < keyLabels.Count; r++)
            {
                for (int c = 0; c < keyLabels[r].Count; c++)
                {
                    bool f = zone == Zone.Keyboard && r == keyRow && c == keyCol;
                    keyLabels[r][c].BackgroundColor = f ? Theme.Accent : Theme.Surface;
                }
            }
            for (int i = 0; i < suggestionLabels.Count; i++)
            {
                suggestionLabels[i].Text = i < suggestions.Count ? "  " + suggestions[i] : "";
                suggestionLabels[i].BackgroundColor = zone == Zone.Suggestions && i == suggestionIndex
                    ? Theme.SurfaceFocused : Color.Transparent;
            }
            addButton.BackgroundColor = zone == Zone.AddButton ? Theme.Accent : Theme.Surface;
            results?.SetFocused(zone == Zone.Results);
        }

        public override bool OnKey(string key)
        {
            // Handle Samsung Smart Remote Voice / Mic / Search hardware keys from any zone
            if (key == "XF86Voice" || key == "Voice" || key == "XF86AudioMicMute" || key == "XF86Search" || key == "Search")
            {
                TriggerVoiceSearch();
                return true;
            }

            switch (zone)
            {
                case Zone.Keyboard:
                    if (!KeyboardKey(key))
                    {
                        return false;
                    }
                    break;
                case Zone.Suggestions:
                    if (key == "Up")
                    {
                        suggestionIndex--;
                        if (suggestionIndex < 0)
                        {
                            suggestionIndex = 0;
                        }
                    }
                    else if (key == "Down")
                    {
                        suggestionIndex = System.Math.Min(SuggestionCount - 1, suggestionIndex + 1);
                    }
                    else if (key == "Left")
                    {
                        zone = Zone.Keyboard;
                    }
                    else if (key == "Return" && suggestionIndex < suggestions.Count)
                    {
                        query = suggestions[suggestionIndex];
                        RunSearch();
                    }
                    else if (key == "XF86Back" || key == "Escape")
                    {
                        zone = Zone.Keyboard;
                    }
                    break;
                case Zone.AddButton:
                    if (key == "Down" || key == "XF86Back" || key == "Escape")
                    {
                        zone = Zone.Keyboard;
                    }
                    else if (key == "Return")
                    {
                        AddToHome();
                    }
                    break;
                case Zone.Results:
                    if (key == "Up" || key == "XF86Back" || key == "Escape")
                    {
                        zone = Zone.Keyboard;
                        keyRow = ActionRow;
                    }
                    else if (key == "Left")
                    {
                        results.Move(-1);
                    }
                    else if (key == "Right")
                    {
                        results.Move(1);
                    }
                    else if (key == "Return" && results.FocusedItem != null)
                    {
                        App.Play(results.FocusedItem);
                    }
                    break;
            }
            UpdateFocus();
            return true;
        }

        private bool KeyboardKey(string key)
        {
            switch (key)
            {
                case "Up":
                    if (keyRow == 0)
                    {
                        if (results != null)
                        {
                            zone = Zone.AddButton;
                        }
                    }
                    else
                    {
                        // From the action row, back onto the 10 columns under the wide key.
                        keyCol = keyRow == ActionRow ? System.Math.Min(9, keyCol * 10 / ActionKeys) : keyCol;
                        keyRow--;
                    }
                    break;
                case "Down":
                    if (keyRow == ActionRow)
                    {
                        if (results != null && results.Count > 0)
                        {
                            zone = Zone.Results;
                        }
                    }
                    else
                    {
                        keyRow++;
                        // Map the 10 columns onto the wide keys of the action row.
                        keyCol = keyRow == ActionRow ? System.Math.Min(ActionKeys - 1, keyCol * ActionKeys / 10) : keyCol;
                    }
                    break;
                case "Left":
                    if (keyCol == 0)
                    {
                        return false;   // back to the home rail
                    }
                    keyCol--;
                    break;
                case "Right":
                    if (keyCol == keyLabels[keyRow].Count - 1)
                    {
                        if (SuggestionCount > 0)
                        {
                            zone = Zone.Suggestions;
                            suggestionIndex = 0;
                        }
                    }
                    else
                    {
                        keyCol++;
                    }
                    break;
                case "Return":
                    if (keyRow == ActionRow)
                    {
                        PressAction((KeyAction)keyCol);
                    }
                    else
                    {
                        query += Layout[keyRow][keyCol];
                        UiGuard.Fire("SearchScreen.RefreshSuggestions", RefreshSuggestions);
                    }
                    break;
                case "XF86Back":
                case "Escape":
                    if (isListening)
                    {
                        TriggerVoiceSearch();
                        return true;
                    }
                    return false;
            }
            return true;
        }

        private void PressAction(KeyAction action)
        {
            switch (action)
            {
                case KeyAction.Page:
                    symbolPage = !symbolPage;
                    LabelKeys();
                    return;
                case KeyAction.Space:
                    query += " ";
                    break;
                case KeyAction.Delete:
                    if (query.Length > 0)
                    {
                        query = query.Substring(0, query.Length - 1);
                    }
                    break;
                case KeyAction.Clear:
                    query = "";
                    break;
                case KeyAction.Voice:
                    TriggerVoiceSearch();
                    return;
                case KeyAction.Search:
                    RunSearch();
                    return;
            }
            UiGuard.Fire("SearchScreen.RefreshSuggestions", RefreshSuggestions);
        }

        // Debounced: only the latest keystroke's request updates the list.
        private async Task RefreshSuggestions()
        {
            int generation = ++suggestGeneration;
            await Task.Delay(250);
            if (generation != suggestGeneration)
            {
                return;
            }
            List<string> list = query.Trim().Length == 0 ? new List<string>() : await YouTubeSuggest(query);
            if (generation != suggestGeneration)
            {
                return;
            }
            suggestions = list;
            if (zone == Zone.Suggestions && SuggestionCount == 0)
            {
                zone = Zone.Keyboard;
            }
            UpdateFocus();
        }

        private static Task<List<string>> YouTubeSuggest(string q) => Data.YouTube.Suggest(q);

        private void RunSearch()
        {
            string q = query.Trim();
            if (q.Length == 0)
            {
                return;
            }
            if (results != null)
            {
                resultsHost.Remove(results);
                results.Dispose();
            }
            results = new FeedRow(T("Results for “{0}”", q), q, 1920 - KbX);
            resultsHost.Add(results);
            addButton.Show();
            zone = Zone.Keyboard;
            UiGuard.Fire("SearchScreen.LoadResults", LoadResults);
        }

        private async Task LoadResults()
        {
            FeedRow row = results;
            await row.Load();
            if (row == results && row.Count > 0 && zone == Zone.Keyboard)
            {
                zone = Zone.Results;
                UpdateFocus();
            }
        }

        private void AddToHome()
        {
            string q = results?.Query;
            if (string.IsNullOrEmpty(q))
            {
                return;
            }
            App.Config.HomeRows.Add(new HomeRow { Title = q, Query = q, Icon = HomeRow.GuessIcon(q) });
            App.Config.Save();
            App.Home.InvalidateRows();
            addButton.Text = "  ✓ " + T("Added to menu") + "  ";
        }
    }
}
