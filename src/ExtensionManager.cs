using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace DualPanelBrowser;

public static class ExtensionManager
{
    public static string ManagedDir => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Extensions");

    public static List<string> PendingExtDirs { get; set; } = new();
    private static bool _loaded;
    private static TaskCompletionSource? _loadTcs;
    public static List<CoreWebView2BrowserExtension> LoadedExts { get; } = new();
    public static Dictionary<string, string> ExtDirMapping { get; } = new();
    public static bool IsLoading { get; private set; }
    public static CoreWebView2Environment? SharedEnv { get; set; }

    public static async Task LoadExtensionsAsync(CoreWebView2 core)
    {
        if (_loaded) return;
        _loaded = true;
        _loadTcs = new TaskCompletionSource();
        IsLoading = true;
        var profile = core.Profile;
        if (profile == null) { IsLoading = false; _loadTcs.SetResult(); return; }
        foreach (var dir in PendingExtDirs)
        {
            try
            {
                var ext = await profile.AddBrowserExtensionAsync(dir);
                LoadedExts.Add(ext);
                ExtDirMapping[dir] = ext.Id;
                File.AppendAllText(Path.Combine(ManagedDir, "load.log"),
                    $"OK: {Path.GetFileName(dir)} -> {ext.Id} {ext.Name}\n");
            }
            catch (Exception ex)
            {
                File.AppendAllText(Path.Combine(ManagedDir, "load.log"),
                    $"FAIL: {Path.GetFileName(dir)} -> {ex.Message}\n");
            }
        }
        IsLoading = false;
        _loadTcs.SetResult();
    }

    public static Task WaitForLoadAsync() => _loadTcs?.Task ?? Task.CompletedTask;

    public static List<ExtensionInfo> ListExtensions()
    {
        var list = new List<ExtensionInfo>();
        if (!Directory.Exists(ManagedDir)) return list;
        foreach (var dir in Directory.GetDirectories(ManagedDir))
        {
            var manifestPath = Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifestPath)) continue;
            var disabled = dir.EndsWith(".disabled");
            var cleanDir = disabled ? dir[..^".disabled".Length] : dir;
            var name = Path.GetFileName(cleanDir);
            var optionsPage = "";

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
                var root = doc.RootElement;

                // 读取名称（可能是 __MSG_xxx__）
                if (root.TryGetProperty("name", out var n))
                    name = ResolveI18n(root, n.GetString() ?? "", cleanDir);

                // 读取 options_page
                if (root.TryGetProperty("options_page", out var op))
                    optionsPage = op.GetString() ?? "";
                else if (root.TryGetProperty("options_ui", out var oui) && oui.TryGetProperty("page", out var op2))
                    optionsPage = op2.GetString() ?? "";
            }
            catch { }

            list.Add(new ExtensionInfo
            {
                Id = ComputeExtensionId(cleanDir),
                Name = name,
                IsEnabled = !disabled,
                Directory = cleanDir,
                OptionsPage = optionsPage,
                ToggleText = disabled ? "启用" : "禁用"
            });
        }
        return list;
    }

    private static string ResolveI18n(JsonElement manifest, string rawName, string extDir)
    {
        if (!rawName.StartsWith("__MSG_") || !rawName.EndsWith("__")) return rawName;
        var key = rawName[6..^2];
        var defaultLocale = "en";
        if (manifest.TryGetProperty("default_locale", out var dl))
            defaultLocale = dl.GetString() ?? "en";
        var msgPath = Path.Combine(extDir, "_locales", defaultLocale, "messages.json");
        if (!File.Exists(msgPath)) return rawName;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(msgPath));
            if (doc.RootElement.TryGetProperty(key, out var entry) &&
                entry.TryGetProperty("message", out var msg))
                return msg.GetString() ?? rawName;
        }
        catch { }
        return rawName;
    }

    private static string ComputeExtensionId(string path)
    {
        // Chrome --load-extension ID: SHA256 of path, first 16 bytes, each nibble -> a-p
        var bytes = Encoding.UTF8.GetBytes(path.Replace('\\', '/'));
        var hash = SHA256.HashData(bytes);
        var sb = new StringBuilder();
        for (int i = 0; i < 16; i++)
        {
            sb.Append((char)('a' + (hash[i] >> 4)));
            sb.Append((char)('a' + (hash[i] & 0x0F)));
        }
        return sb.ToString();
    }

    public static async Task<(List<string> installed, List<string> errors)> InstallFromFolderAsync(string folder)
    {
        var installed = new List<string>();
        var errors = new List<string>();
        if (!Directory.Exists(folder)) return (installed, errors);
        Directory.CreateDirectory(ManagedDir);

        foreach (var crx in Directory.GetFiles(folder, "*.crx"))
        {
            try
            {
                var extDir = await ExtractCrxAsync(crx);
                if (extDir == null) { errors.Add(Path.GetFileName(crx) + ": 不是有效CRX"); continue; }
                if (!File.Exists(Path.Combine(extDir, "manifest.json"))) { errors.Add(Path.GetFileName(crx) + ": 缺manifest.json"); continue; }
                installed.Add(Path.GetFileName(crx) + " 已解包，重启后生效");
            }
            catch (Exception ex)
            {
                errors.Add(Path.GetFileName(crx) + ": 解包失败 " + ex.Message);
            }
        }
        return (installed, errors);
    }

    public static void SetEnabled(ExtensionInfo info, bool enabled)
    {
        if (info.IsEnabled == enabled) return;
        var newDir = enabled
            ? info.Directory
            : info.Directory + ".disabled";
        if (Directory.Exists(info.Directory))
        {
            Directory.Move(info.Directory, newDir);
            info.Directory = newDir;
            info.IsEnabled = enabled;
            info.ToggleText = enabled ? "禁用" : "启用";
        }
    }

    public static void Remove(ExtensionInfo info)
    {
        if (Directory.Exists(info.Directory))
            Directory.Delete(info.Directory, true);
    }

    public static async Task<string?> ExtractCrxAsync(string crxPath)
    {
        var all = await File.ReadAllBytesAsync(crxPath);
        if (all.Length < 12) return null;
        var magic = System.Text.Encoding.ASCII.GetString(all, 0, 4);
        if (magic != "Cr24") return null;
        var headerSize = BitConverter.ToInt32(all, 8);
        var zipStart = 12 + headerSize;
        if (zipStart >= all.Length) return null;

        var id = Path.GetFileNameWithoutExtension(crxPath);
        var outDir = Path.Combine(ManagedDir, id);
        if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        Directory.CreateDirectory(outDir);

        var tmpZip = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".zip");
        try
        {
            await File.WriteAllBytesAsync(tmpZip, all[zipStart..]);
            System.IO.Compression.ZipFile.ExtractToDirectory(tmpZip, outDir);
        }
        finally
        {
            if (File.Exists(tmpZip)) File.Delete(tmpZip);
        }
        return outDir;
    }
}

public class ExtensionInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsEnabled { get; set; }
    public string ToggleText { get; set; } = "";
    public string Directory { get; set; } = "";
    public string OptionsPage { get; set; } = "";
}
