using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using OmegaTube.Data;
using OmegaTube.Util;

namespace OmegaTube.Plugins
{
    // An entry of the official index (plugins validated by the OmegaTube maintainer, docs/plugins.md).
    public sealed class OfficialPlugin
    {
        public string Id, Name, Author, Description, Homepage, Version, Sdk, Url, Sha256;
        public bool Compatible;
    }

    // A failure to tell the user as is (translated).
    public sealed class PluginException : Exception
    {
        public PluginException(string message) : base(message)
        {
        }
    }

    // A downloaded and unpacked package waiting for the user's go-ahead (Commit) or a Discard.
    public sealed class StagedPlugin
    {
        public PluginInfo Info;
        public bool Official;     // its id + sha256 are in the official index
        public string Staging;    // the unpacked folder
        public List<string> Warnings = new List<string>();
    }

    // Installed plugins live in <data>/plugins/<id>/, each with a .install.json record (hash, source). The official
    // index is cached in <data>/plugins/index.json. Install: download -> unpack into a staging folder with strict
    // rules (sizes, names, file types) -> read plugin.json -> the caller asks the user if needed -> Commit.
    public static class PluginStore
    {
        public const string IndexUrl = "https://iiyn.github.io/omegatube/plugins/index.json";
        private const int MaxZipBytes = 20 << 20;
        private const long MaxUnpackedBytes = 20 << 20;
        private const int MaxEntries = 1000;
        private const int MaxIndexBytes = 512 << 10;
        private static readonly string[] Extensions = { ".json", ".png", ".jpg", ".jpeg", ".webp", ".js", ".txt", ".md" };
        private static readonly Regex Segment = new Regex(@"^[A-Za-z0-9_][A-Za-z0-9._ -]{0,63}$");

        private static string root;
        private static List<OfficialPlugin> index = new List<OfficialPlugin>();

        public static string Root => root;
        public static IReadOnlyList<OfficialPlugin> Index => index;

        public static void Init(string dataDir)
        {
            root = Path.Combine(dataDir, "plugins");
            try
            {
                Directory.CreateDirectory(root);
                foreach (string d in Directory.GetDirectories(root, ".staging-*"))
                {
                    Directory.Delete(d, true);   // an install cut short by the app closing
                }
                string cached = Path.Combine(root, "index.json");
                if (File.Exists(cached))
                {
                    index = ParseIndex(Json.Parse(File.ReadAllText(cached)));
                }
            }
            catch (Exception e)
            {
                Tizen.Log.Error("OmegaTube", "plugins: " + e.Message);
            }
        }

        // ---- start-up guard ----
        // A plugin that kills the app while the home is being built would do it at every launch: a marker written
        // before applying it and removed once the home shows makes the next launch fall back to Omega.

        private static string Marker => Path.Combine(root, ".starting");

        public static bool LastStartFailed(out string id)
        {
            id = null;
            try
            {
                if (File.Exists(Marker))
                {
                    id = File.ReadAllText(Marker).Trim();
                    File.Delete(Marker);
                    return true;
                }
            }
            catch (Exception)
            {
            }
            return false;
        }

        public static void Starting(string id)
        {
            try
            {
                File.WriteAllText(Marker, id);
            }
            catch (Exception)
            {
            }
        }

        public static void Started()
        {
            try
            {
                File.Delete(Marker);
            }
            catch (Exception)
            {
            }
        }

        // ---- installed ----

        public static List<PluginInfo> Installed()
        {
            var list = new List<PluginInfo>();
            if (root == null)
            {
                return list;
            }
            foreach (string d in Directory.GetDirectories(root))
            {
                string name = Path.GetFileName(d);
                if (name.StartsWith("."))
                {
                    continue;
                }
                PluginInfo info = Read(d);
                if (info != null && info.Id == name)
                {
                    list.Add(info);
                }
            }
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        public static PluginInfo Find(string id)
        {
            if (root == null || id == null || PluginPackage.Reserved(id) || id.IndexOf('/') >= 0 || id.StartsWith("."))
            {
                return null;
            }
            string d = Path.Combine(root, id);
            PluginInfo info = Directory.Exists(d) ? Read(d) : null;
            return info != null && info.Id == id ? info : null;
        }

        private static PluginInfo Read(string dir)
        {
            PluginInfo info = PluginPackage.ReadInfo(dir, out string error);
            if (info == null)
            {
                Tizen.Log.Error("OmegaTube", "plugin " + Path.GetFileName(dir) + ": " + error);
                return null;
            }
            try
            {
                string record = Path.Combine(dir, ".install.json");
                object r = File.Exists(record) ? Json.Parse(File.ReadAllText(record)) : null;
                info.Sha256 = Json.Str(r, "sha256", null);
                info.Source = Json.Str(r, "source", null);
            }
            catch (Exception)
            {
            }
            return info;
        }

        // Validated by the maintainer: this very file (id + hash) is in the official index.
        public static bool IsOfficial(PluginInfo p) => OfficialEntry(p.Id, p.Sha256) != null;

        public static bool Listed(string id) => index.Exists(o => o.Id == id);

        public static OfficialPlugin OfficialEntry(string id, string sha256) =>
            sha256 == null ? null : index.Find(o => o.Id == id && o.Sha256 == sha256);

        // The index's other validated file for an installed plugin, or null
        // (for a non-official copy of an official id: the validated version.)
        public static OfficialPlugin OfficialUpdate(PluginInfo p)
        {
            OfficialPlugin o = index.Find(e => e.Id == p.Id);
            return o != null && o.Compatible && o.Sha256 != p.Sha256 ? o : null;
        }

        // The installed plugin's look, or null (missing, made for a newer app, unreadable).
        public static Ui.ThemeSpec Load(string id)
        {
            PluginInfo p = Find(id);
            if (p == null || p.Incompatible != null)
            {
                Tizen.Log.Error("OmegaTube", "plugin " + id + ": " + (p == null ? "not installed" : p.Incompatible));
                return null;
            }
            try
            {
                var warnings = new List<string>();
                Ui.ThemeSpec spec = PluginPackage.ReadTheme(p.Dir, warnings);
                if (warnings.Count > 0)
                {
                    Tizen.Log.Warn("OmegaTube", "plugin " + id + ": " + string.Join("; ", warnings));
                }
                return spec;
            }
            catch (Exception e)
            {
                Tizen.Log.Error("OmegaTube", "plugin " + id + ": " + e);
                return null;
            }
        }

        public static void Remove(string id)
        {
            PluginInfo p = Find(id);
            if (p != null)
            {
                Directory.Delete(p.Dir, true);
            }
        }

        // ---- official index ----

        // Downloads the index (kept for the next launches). False when it could not be had: the cached copy stays.
        public static async Task<bool> RefreshIndex()
        {
            string path = Path.Combine(root, "index.json");
            object res = await Task.Run(() => YouTube.Call(NativeCore.Download(IndexUrl, path, MaxIndexBytes)));
            string error = Json.Str(res, "error", null);
            if (error != null)
            {
                NativeCore.Log("plugins: index: " + error);
                return false;
            }
            try
            {
                index = ParseIndex(Json.Parse(File.ReadAllText(path)));
                return true;
            }
            catch (Exception e)
            {
                NativeCore.Log("plugins: index: " + e.Message);
                return false;
            }
        }

        private static List<OfficialPlugin> ParseIndex(object root)
        {
            var list = new List<OfficialPlugin>();
            if (Json.Num(root, "format") != 1)
            {
                return list;
            }
            foreach (object e in Json.Arr(root, "plugins") ?? new List<object>())
            {
                var o = new OfficialPlugin
                {
                    Id = Json.Str(e, "id"),
                    Name = Json.Str(e, "name"),
                    Author = Json.Str(e, "author"),
                    Description = Json.Str(Json.Obj(e, "description"), I18n.Lang, null)
                                  ?? Json.Str(Json.Obj(e, "description"), "en", null) ?? Json.Str(e, "description"),
                    Homepage = Json.Str(e, "homepage"),
                    Version = Json.Str(e, "version"),
                    Sdk = Json.Str(e, "sdk"),
                    Url = Json.Str(e, "url"),
                    Sha256 = Json.Str(e, "sha256").ToLowerInvariant(),
                };
                Match m = Regex.Match(o.Sdk, @"^(\d+)\.(\d+)$");
                o.Compatible = m.Success && int.Parse(m.Groups[1].Value) == PluginPackage.SdkMajor
                               && int.Parse(m.Groups[2].Value) <= PluginPackage.SdkMinor;
                if (o.Id.Length > 0 && o.Name.Length > 0 && o.Url.StartsWith("https://") && o.Sha256.Length == 64)
                {
                    list.Add(o);
                }
            }
            return list;
        }

        // ---- install ----

        // Downloads and unpacks a plugin (https, or http on the local network: see omega_download). expectedSha256: an
        // official install (the file must be that one). Throws with a message for the user when it is not a usable plugin.
        public static async Task<StagedPlugin> Download(string url, string expectedSha256 = null)
        {
            if (!url.StartsWith("https://") && !url.StartsWith("http://"))
            {
                throw new PluginException(I18n.T("The address must start with https://"));
            }
            string zip = Path.Combine(root, ".download.zip");
            object res = await Task.Run(() => YouTube.Call(NativeCore.Download(url, zip, MaxZipBytes)));
            string error = Json.Str(res, "error", null);
            if (error != null)
            {
                throw new PluginException(I18n.T("Download failed: {0}", error));
            }
            string sha = Json.Str(res, "sha256").ToLowerInvariant();
            try
            {
                if (expectedSha256 != null && sha != expectedSha256)
                {
                    throw new PluginException(I18n.T("The file is not the validated one (its fingerprint differs)"));
                }
                StagedPlugin staged;
                try
                {
                    staged = await Task.Run(() => Unpack(zip));
                }
                catch (PluginException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    // Not a zip, a broken one, a full disk...
                    throw new PluginException(I18n.T("Not a plugin: {0}", e.Message));
                }
                staged.Info.Sha256 = sha;
                staged.Info.Source = url;
                staged.Official = OfficialEntry(staged.Info.Id, sha) != null;
                return staged;
            }
            finally
            {
                TryDelete(zip);
            }
        }

        private static StagedPlugin Unpack(string zipPath)
        {
            string staging = Path.Combine(root, ".staging-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(staging);
            try
            {
                var staged = new StagedPlugin { Staging = staging };
                using (FileStream file = File.OpenRead(zipPath))
                using (var zip = new ZipArchive(file, ZipArchiveMode.Read))
                {
                    if (zip.Entries.Count > MaxEntries)
                    {
                        throw new PluginException(I18n.T("Not a plugin: {0}", "more than " + MaxEntries + " files"));
                    }
                    string prefix = Prefix(zip);
                    long total = 0;
                    foreach (ZipArchiveEntry entry in zip.Entries)
                    {
                        string name = entry.FullName;
                        if (!name.StartsWith(prefix) || name.EndsWith("/"))
                        {
                            continue;
                        }
                        name = name.Substring(prefix.Length);
                        if (!SafePath(name))
                        {
                            staged.Warnings.Add("skipped " + name);
                            continue;
                        }
                        string target = Path.Combine(staging, name);
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        using (Stream input = entry.Open())
                        using (FileStream output = File.Create(target))
                        {
                            // Counted while copying: the sizes the zip announces are not trusted.
                            var buffer = new byte[81920];
                            int n;
                            while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                total += n;
                                if (total > MaxUnpackedBytes)
                                {
                                    throw new PluginException(I18n.T("Not a plugin: {0}", "larger than 20 MB unpacked"));
                                }
                                output.Write(buffer, 0, n);
                            }
                        }
                    }
                }
                staged.Info = PluginPackage.ReadInfo(staging, out string error);
                if (staged.Info == null)
                {
                    throw new PluginException(I18n.T("Not a plugin: {0}", error));
                }
                if (staged.Info.Incompatible != null)
                {
                    throw new PluginException(I18n.T("This plugin needs a newer version of OmegaTube ({0})", staged.Info.Incompatible));
                }
                PluginPackage.ReadTheme(staging, staged.Warnings);   // logged now rather than at each launch
                foreach (string w in staged.Warnings)
                {
                    NativeCore.Log("plugin " + staged.Info.Id + ": " + w);
                }
                return staged;
            }
            catch (Exception)
            {
                TryDeleteDir(staging);
                throw;
            }
        }

        // plugin.json at the root of the zip, or in its single top folder (GitHub's source archives).
        private static string Prefix(ZipArchive zip)
        {
            string prefix = null;
            foreach (ZipArchiveEntry e in zip.Entries)
            {
                if (e.FullName == "plugin.json")
                {
                    return "";
                }
                int slash = e.FullName.IndexOf('/');
                if (slash > 0 && e.FullName.Substring(slash + 1) == "plugin.json")
                {
                    prefix = e.FullName.Substring(0, slash + 1);
                }
            }
            if (prefix == null)
            {
                throw new PluginException(I18n.T("Not a plugin: {0}", "no plugin.json"));
            }
            return prefix;
        }

        // Relative, at most 6 folders deep, plain names (no "..", no hidden files), known file types only.
        private static bool SafePath(string name)
        {
            if (name.Length > 200 || name.IndexOf('\\') >= 0 || name.IndexOf(':') >= 0 || name.StartsWith("/"))
            {
                return false;
            }
            string[] parts = name.Split('/');
            if (parts.Length > 7)
            {
                return false;
            }
            foreach (string p in parts)
            {
                if (!Segment.IsMatch(p) || p.Contains(".."))
                {
                    return false;
                }
            }
            return Array.IndexOf(Extensions, Path.GetExtension(name).ToLowerInvariant()) >= 0;
        }

        // Moves the staged package into place (replacing an installed copy) and records where it came from.
        public static PluginInfo Commit(StagedPlugin staged)
        {
            string target = Path.Combine(root, staged.Info.Id);
            string old = Path.Combine(root, ".old-" + staged.Info.Id);
            TryDeleteDir(old);
            if (Directory.Exists(target))
            {
                Directory.Move(target, old);
            }
            Directory.Move(staged.Staging, target);
            TryDeleteDir(old);
            File.WriteAllText(Path.Combine(target, ".install.json"), Json.Write(new Dictionary<string, object>
            {
                ["sha256"] = staged.Info.Sha256,
                ["source"] = staged.Info.Source,
                ["installed"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            }));
            return Find(staged.Info.Id);
        }

        public static void Discard(StagedPlugin staged)
        {
            if (staged != null)
            {
                TryDeleteDir(staged.Staging);
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception)
            {
            }
        }

        private static void TryDeleteDir(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
