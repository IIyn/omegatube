using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace OmegaTube
{
    // Bug reports from users' TVs. The report screen shows a QR code that opens a GitHub issue pre-filled
    // (issue form .github/ISSUE_TEMPLATE/bug.yml) with the TV's details and the end of the log; the whole log
    // can be attached through a paste service (NativeCore.ReportUpload).
    // Everything here must work without libomegacore.so: failing to load it is the first thing to report.
    public static class BugReport
    {
        // Not read from remote-config.json (updates.repo): that needs the core.
        private const string IssueBase = "https://github.com/IIyn/omegatube/issues/new?template=bug.yml";
        // QR code version ~24 at ECC low: still easy to scan from the couch on a 1080p screen.
        private const int UrlBudget = 1100;

        private static string dataDir = "";

        // Set once NativeCore.Init succeeded: the native log and the upload are available.
        public static bool CoreLoaded { get; set; }

        private static string CrashPath => Path.Combine(dataDir, "crash.txt");

        // Unhandled exceptions end the app: keep them for the report screen at the next launch.
        public static void Init(string dir)
        {
            dataDir = dir;
            AppDomain.CurrentDomain.UnhandledException += (s, e) => SaveCrash(e.ExceptionObject as Exception);
        }

        public static void SaveCrash(Exception e)
        {
            try
            {
                string text = Describe(e);
                if (CoreLoaded)
                {
                    NativeCore.Log("crash: " + e);
                }
                string log = CoreLoaded ? RedactLog(NativeCore.RecentLog(40)) : "";
                File.WriteAllText(CrashPath, log.Length > 0 ? text + "\n\n" + log : text);
            }
            catch (Exception)
            {
                // nothing more we can do while the app goes down
            }
        }

        // The crash saved by the previous session (deleted once read), or null.
        public static string TakeCrash()
        {
            try
            {
                if (!File.Exists(CrashPath))
                {
                    return null;
                }
                string text = File.ReadAllText(CrashPath);
                File.Delete(CrashPath);
                return text;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static string Describe(Exception e)
        {
            return $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {(e != null ? e.ToString() : "unknown error")}";
        }

        public static string AppVersion
        {
            get
            {
                try
                {
                    // res/../tizen-manifest.xml: the installed package's own manifest.
                    string manifest = Path.Combine(Tizen.Applications.Application.Current.DirectoryInfo.Resource, "..", "tizen-manifest.xml");
                    Match m = Regex.Match(File.ReadAllText(manifest), "<manifest[^>]*\\sversion=\"([^\"]+)\"");
                    if (m.Success)
                    {
                        return m.Groups[1].Value;
                    }
                }
                catch (Exception)
                {
                }
                return CoreLoaded ? NativeCore.Version : "?";
            }
        }

        public static string TizenVersion => Info("http://tizen.org/feature/platform.version");

        public static string Model => Info("http://tizen.org/system/model_name");

        // One "key: value" line per item, for the issue's "device" field and the full log's header.
        public static string DeviceInfo()
        {
            return string.Join("\n", new[]
            {
                "OmegaTube: " + AppVersion,
                "Model: " + Model,
                "Tizen: " + TizenVersion,
                "Firmware: " + Info("http://tizen.org/system/build.string"),
                "CPU: " + Info("http://tizen.org/feature/platform.core.cpu.arch")
                    + ", process " + RuntimeInformation.ProcessArchitecture + ", OS " + RuntimeInformation.OSArchitecture,
                ".NET: " + Environment.Version,
                "Core loaded: " + (CoreLoaded ? "yes" : "no"),
            });
        }

        private static string Info(string key)
        {
            try
            {
                return Tizen.System.Information.TryGetValue(key, out string value) && !string.IsNullOrEmpty(value) ? value : "?";
            }
            catch (Exception)
            {
                return "?";
            }
        }

        // This session's and the previous session's core log (omega.log, omega.log.prev).
        private static string LogFile(string name)
        {
            try
            {
                string path = Path.Combine(dataDir, name);
                return File.Exists(path) ? File.ReadAllText(path) : "";
            }
            catch (Exception e)
            {
                return "(" + e.Message + ")";
            }
        }

        // Everything, for the paste service.
        public static string FullText(string crash)
        {
            var sb = new StringBuilder();
            sb.Append(DeviceInfo()).Append("\n\n");
            if (!string.IsNullOrEmpty(crash))
            {
                sb.Append("=== crash ===\n").Append(Redact(crash)).Append("\n\n");
            }
            sb.Append("=== this session ===\n").Append(RedactLog(LogFile("omega.log"))).Append('\n');
            sb.Append("=== previous session ===\n").Append(RedactLog(LogFile("omega.log.prev")));
            return sb.ToString();
        }

        // New issue URL with the form fields filled in. The "log" field gets the crash and/or the end of the
        // log, trimmed to fit the QR code; fullLogUrl (paste) is added when the user attached the whole log.
        public static string IssueUrl(string crash, string fullLogUrl)
        {
            string title = crash != null ? $"Crash on {Model} (Tizen {TizenVersion})" : $"Problem on {Model} (Tizen {TizenVersion})";
            string url = IssueBase + "&title=" + Enc(title) + "&version=" + Enc(AppVersion) + "&device=" + Enc(DeviceInfo());
            if (!string.IsNullOrEmpty(fullLogUrl))
            {
                url += "&fulllog=" + Enc(fullLogUrl);
            }

            var lines = new List<string>();
            if (!string.IsNullOrEmpty(crash))
            {
                lines.AddRange(Redact(crash).Split('\n'));
            }
            else
            {
                string log = RedactLog(CoreLoaded ? NativeCore.RecentLog(30) : "");
                lines.AddRange(log.TrimEnd('\n').Split('\n'));
            }
            // A crash keeps its first lines (exception, top frames); a plain log its last ones.
            string field = "";
            int budget = UrlBudget - url.Length - "&log=".Length;
            for (int i = 0; i < lines.Count; i++)
            {
                string line = crash != null ? lines[i] : lines[lines.Count - 1 - i];
                string next = field.Length == 0 ? line : crash != null ? field + "\n" + line : line + "\n" + field;
                if (Enc(next).Length > budget)
                {
                    break;
                }
                field = next;
            }
            return field.Length > 0 ? url + "&log=" + Enc(field) : url;
        }

        // '+' for spaces: shorter than %20 and decoded the same way by GitHub.
        private static string Enc(string s) => Uri.EscapeDataString(s).Replace("%20", "+");

        // Core log lines also quote what the user watched and searched ('title' by channel, search 'query'):
        // masked, the video ids stay. Not applied to exception texts, whose quotes name what failed.
        public static string RedactLog(string text)
        {
            return Regex.Replace(Redact(text), @"'(?:[^'\n]|'(?=\w))*'( by [^,\n]+)?", "'…'");
        }

        // Keeps account data, tokens, stream URLs and addresses out of public reports.
        public static string Redact(string text)
        {
            text = Regex.Replace(text, @"(https?://[^\s/?#""']+)[^\s""']*", "$1/…");
            text = Regex.Replace(text, @"(?i)\b(bearer)\s+\S+", "$1 …");
            text = Regex.Replace(text, @"(?i)(token|secret|cookie|authorization|visitor_?data|device_?code|user_?code)([""']?\s*[:=]\s*[""']?)[^\s""',&]+", "$1$2…");
            text = Regex.Replace(text, @"[\w.+-]+@[\w-]+\.[\w.-]+", "…@…");
            return Regex.Replace(text, @"\b\d{1,3}(\.\d{1,3}){3}(:\d+)?\b", "x.x.x.x");
        }
    }
}
