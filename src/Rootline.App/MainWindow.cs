// Rootline.App — MainWindow.cs
// The app window: hosts the Rootline page in WebView2 and answers its requests (sign in, scan, open a scan,
// save a report, load an older scan to compare, save the audit report or CSV, sign out). Scans are stored on this PC only, under %LOCALAPPDATA%\Rootline.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Rootline.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Rootline.App
{
    public sealed class MainWindow : Window
    {
        private const string Host = "rootline.app";
        private const string CdnTag = "<script src=\"https://cdnjs.cloudflare.com/ajax/libs/cytoscape/3.30.2/cytoscape.min.js\"></script>";
        private const int KeepScans = 30;

        private static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rootline");
        private static readonly string ViewDir = Path.Combine(DataDir, "view");
        private static readonly string SettingsFile = Path.Combine(DataDir, "settings.json");
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        private readonly WebView2 _web = new WebView2();
        private Auth _auth;
        private string _tenantName;
        private string _current;
        private CancellationTokenSource _scanCts;

        public MainWindow()
        {
            Title = "Rootline";
            Width = 1440; Height = 900; MinWidth = 820; MinHeight = 560;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Content = _web;
            using (var icon = Resource("rootline.ico")) Icon = BitmapFrame.Create(icon, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            Loaded += async (s, e) => await InitAsync();
            Closing += (s, e) => _scanCts?.Cancel();
        }

        // ------------------------------------------------------------------ start-up
        private async Task InitAsync()
        {
            try
            {
                Directory.CreateDirectory(ViewDir);
                var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(DataDir, "WebView2"));
                await _web.EnsureCoreWebView2Async(env);
            }
            catch (WebView2RuntimeNotFoundException)
            {
                MessageBox.Show(this, "Rootline needs the Microsoft Edge WebView2 Runtime, which is part of Windows 11 and most Windows 10 PCs.\n\n" +
                    "Install it from https://go.microsoft.com/fwlink/p/?LinkId=2124703 and open Rootline again.", "Rootline", MessageBoxButton.OK, MessageBoxImage.Warning);
                Close();
                return;
            }
            var core = _web.CoreWebView2;
#if !DEBUG
            core.Settings.AreDevToolsEnabled = false;
#endif
            core.Settings.IsStatusBarEnabled = false;
            core.SetVirtualHostNameToFolderMapping(Host, ViewDir, CoreWebView2HostResourceAccessKind.Deny);
            core.WebMessageReceived += OnMessage;
            core.NewWindowRequested += (s, e) => { e.Handled = true; OpenExternal(e.Uri); };
            core.NavigationStarting += (s, e) =>
            {
                if (!e.Uri.StartsWith("https://" + Host + "/", StringComparison.OrdinalIgnoreCase)) { e.Cancel = true; OpenExternal(e.Uri); }
            };
            using (var lib = Resource("cytoscape.min.js"))
            using (var f = File.Create(Path.Combine(ViewDir, "cytoscape.min.js"))) lib.CopyTo(f);

            var clientId = ClientIdFromSettings();
            if (clientId != null) _auth = new Auth(clientId, () => new WindowInteropHelper(this).Handle);
            ShowView();
        }

        // ------------------------------------------------------------------ the page
        private void ShowView()
        {
            string data = null;
            if (_auth != null && _auth.SignedIn && _current != null) data = ReadSnapshot(_current);
            if (data == null)
            {
                _current = null;
                data = new JsonObject
                {
                    ["meta"] = new JsonObject { ["title"] = "Rootline", ["empty"] = true, ["tenantId"] = _auth?.TenantId, ["entra"] = _auth?.GraphOk ?? false,
                        ["generated"] = DateTime.UtcNow.ToString("o"), ["scope"] = "", ["warnings"] = new JsonArray() },
                    ["nodes"] = new JsonArray(), ["edges"] = new JsonArray(), ["access"] = null
                }.ToJsonString();
            }
            var app = new JsonObject
            {
                ["native"] = true,
                ["version"] = Assembly.GetExecutingAssembly().GetName().Version.ToString(3),
                ["signedIn"] = _auth != null && _auth.SignedIn,
                ["needsClientId"] = _auth == null,
                ["account"] = _auth?.Account,
                ["tenantId"] = _auth?.TenantId,
                ["tenantName"] = _tenantName,
                ["entra"] = _auth?.GraphOk ?? false,
                ["entraProblem"] = _auth?.GraphProblem,
                ["consentUrl"] = _auth?.SignedIn == true ? _auth.AdminConsentUrl : null,
                ["snapshots"] = new JsonArray(Snapshots().Select(s => (JsonNode)new JsonObject { ["name"] = s, ["label"] = Label(s) }).ToArray()),
                ["current"] = _current
            };
            File.WriteAllText(Path.Combine(ViewDir, "index.html"), Page(data, app.ToJsonString(), inlineLibrary: false), new UTF8Encoding(false));
            _web.CoreWebView2.Navigate($"https://{Host}/index.html?v={DateTime.UtcNow.Ticks}");
        }

        private static string Page(string dataJson, string appJson, bool inlineLibrary)
        {
            string template;
            using (var r = new StreamReader(Resource("rootline.template.html"), Encoding.UTF8)) template = r.ReadToEnd();
            string lib;
            if (inlineLibrary)
                using (var r = new StreamReader(Resource("cytoscape.min.js"), Encoding.UTF8)) lib = "<script>" + r.ReadToEnd().Replace("</script", "<\\/script") + "</script>";
            else lib = "<script src=\"cytoscape.min.js\"></script>";
            return template.Replace(CdnTag, lib)
                .Replace("/*__DATA__*/null", dataJson.Replace("</", "<\\/"))
                .Replace("/*__APP__*/null", appJson.Replace("</", "<\\/"));
        }

        // ------------------------------------------------------------------ requests from the page
        private async void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (!e.Source.StartsWith("https://" + Host + "/", StringComparison.OrdinalIgnoreCase)) return;
            JsonObject msg;
            try { msg = JsonNode.Parse(e.WebMessageAsJson) as JsonObject; } catch (Exception) { return; }
            if (msg == null) return;
            var id = Api.Str(msg["id"]);
            var cmd = Api.Str(msg["cmd"]);
            var args = msg["args"] as JsonObject ?? new JsonObject();
            try
            {
                JsonNode result = null;
                var reload = false;
                switch (cmd)
                {
                    case "signIn": await SignInAsync(Api.Str(args["email"]), Api.Str(args["tenant"]), Api.Str(args["clientId"])); reload = true; break;
                    case "scan": result = await ScanAsync(); reload = true; break;
                    case "open": _current = Api.Str(args["name"]); reload = true; break;
                    case "download": result = SaveReport(Api.Str(args["name"])); break;
                    case "signOut": if (_auth != null) await _auth.SignOutAsync(); _current = null; _tenantName = null; reload = true; break;
                    case "openExternal": OpenExternal(Api.Str(args["url"])); break;
                    case "load":                                        // an older scan, for "Changes"
                        result = JsonNode.Parse(ReadSnapshot(Api.Str(args["name"])) ?? throw new InvalidOperationException("That scan no longer exists."));
                        break;
                    case "saveText": result = SaveText(Api.Str(args["name"]), Api.Str(args["text"])); break;
                    default: throw new InvalidOperationException("Unknown request " + cmd);
                }
                Reply(id, true, result, null);
                if (reload) ShowView();
            }
            catch (Exception ex)
            {
                Reply(id, false, null, ex is InvalidOperationException ? ex.Message : Auth.Explain(ex));
            }
        }

        private void Reply(string id, bool ok, JsonNode result, string error)
        {
            _web.CoreWebView2.PostWebMessageAsJson(new JsonObject { ["type"] = "reply", ["id"] = id, ["ok"] = ok, ["result"] = result, ["error"] = error }.ToJsonString());
        }

        private void ReportProgress(string text)
        {
            _web.CoreWebView2?.PostWebMessageAsJson(new JsonObject { ["type"] = "progress", ["text"] = text }.ToJsonString());
        }

        private async Task SignInAsync(string email, string tenant, string clientId)
        {
            if (!string.IsNullOrWhiteSpace(clientId))
            {
                clientId = clientId.Trim();
                if (!Guid.TryParse(clientId, out _)) throw new InvalidOperationException("The app (client) ID should look like 00000000-0000-0000-0000-000000000000.");
                SaveClientId(clientId);
                _auth = new Auth(clientId, () => new WindowInteropHelper(this).Handle);
            }
            if (_auth == null) throw new InvalidOperationException("Enter the app (client) ID of your Rootline app registration.");
            await _auth.SignInAsync(email, tenant, CancellationToken.None);
            _tenantName = null;
            _current = Snapshots().FirstOrDefault();
            if (_current != null)
                try { _tenantName = Api.Str(JsonNode.Parse(ReadSnapshot(_current))?["meta"]?["tenantName"]); } catch (Exception) { }
        }

        private async Task<JsonNode> ScanAsync()
        {
            if (_auth == null || !_auth.SignedIn) throw new InvalidOperationException("Sign in first.");
            _scanCts = new CancellationTokenSource();
            var api = new Api(Http, _auth);
            var scanner = new Scanner(api, new ScanOptions { TenantId = _auth.TenantId, Entra = _auth.GraphOk }, new Progress<string>(ReportProgress));
            var data = await Task.Run(() => scanner.ScanAsync(_scanCts.Token));
            if (!_auth.GraphOk && _auth.GraphProblem != null)
                (data["meta"]["warnings"] as JsonArray)?.Add("Entra not read: " + _auth.GraphProblem);
            var name = DateTime.Now.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture);
            var dir = SnapshotDir();
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, name + ".json"), data.ToJsonString(), new UTF8Encoding(false));
            foreach (var old in Directory.GetFiles(dir, "*.json").OrderByDescending(f => f, StringComparer.Ordinal).Skip(KeepScans)) File.Delete(old);
            _current = name;
            _tenantName = Api.Str(data["meta"]?["tenantName"]) ?? _tenantName;
            return new JsonObject { ["name"] = name };
        }

        private JsonNode SaveReport(string name)
        {
            var data = ReadSnapshot(name) ?? throw new InvalidOperationException("That scan no longer exists.");
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save an offline copy of this scan",
                FileName = $"rootline_{name}.html",
                Filter = "Web page (*.html)|*.html",
                DefaultExt = ".html"
            };
            if (dlg.ShowDialog(this) != true) return new JsonObject { ["cancelled"] = true };
            File.WriteAllText(dlg.FileName, Page(data, "null", inlineLibrary: true), new UTF8Encoding(false));
            return new JsonObject { ["path"] = dlg.FileName };
        }

        /// <summary>Saves a file the page made (audit report or CSV) where the user chooses.</summary>
        private JsonNode SaveText(string name, string text)
        {
            name = Path.GetFileName(name ?? "");
            var ext = Path.GetExtension(name).ToLowerInvariant();
            if (text == null || (ext != ".html" && ext != ".csv")) throw new InvalidOperationException("Can only save .html or .csv files.");
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = ext == ".csv" ? "Save the access list" : "Save the audit report",
                FileName = name,
                Filter = ext == ".csv" ? "CSV for Excel (*.csv)|*.csv" : "Web page (*.html)|*.html",
                DefaultExt = ext
            };
            if (dlg.ShowDialog(this) != true) return new JsonObject { ["cancelled"] = true };
            File.WriteAllText(dlg.FileName, text, new UTF8Encoding(ext == ".csv"));   // BOM so Excel reads UTF-8
            return new JsonObject { ["path"] = dlg.FileName };
        }

        // ------------------------------------------------------------------ storage
        private string SnapshotDir() => Path.Combine(DataDir, "snapshots", (_auth?.TenantId ?? "unknown").ToLowerInvariant());

        private List<string> Snapshots()
        {
            if (_auth == null || !_auth.SignedIn || !Directory.Exists(SnapshotDir())) return new List<string>();
            return Directory.GetFiles(SnapshotDir(), "*.json").Select(Path.GetFileNameWithoutExtension)
                .Where(IsSnapshotName).OrderByDescending(n => n, StringComparer.Ordinal).ToList();
        }

        private static bool IsSnapshotName(string n) =>
            n != null && DateTime.TryParseExact(n, "yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

        private string ReadSnapshot(string name)
        {
            if (!IsSnapshotName(name)) return null;
            var f = Path.Combine(SnapshotDir(), name + ".json");
            return File.Exists(f) ? File.ReadAllText(f, Encoding.UTF8) : null;
        }

        private static string Label(string name) =>
            "Scan " + DateTime.ParseExact(name, "yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture).ToString("ddd d MMM yyyy, HH:mm", CultureInfo.CurrentCulture);

        private static string ClientIdFromSettings()
        {
            try
            {
                if (File.Exists(SettingsFile))
                {
                    var id = Api.Str(JsonNode.Parse(File.ReadAllText(SettingsFile))?["clientId"]);
                    if (Guid.TryParse(id, out _)) return id;
                }
            }
            catch (Exception) { }
            var builtIn = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "DefaultClientId")?.Value;
            return Guid.TryParse(builtIn, out _) ? builtIn : null;
        }

        private static void SaveClientId(string id)
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(SettingsFile, new JsonObject { ["clientId"] = id }.ToJsonString(), new UTF8Encoding(false));
        }

        private static Stream Resource(string name) =>
            Assembly.GetExecutingAssembly().GetManifestResourceStream(name) ?? throw new InvalidOperationException("Missing resource " + name);

        private static void OpenExternal(string url)
        {
            if (url == null || !(url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))) return;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception) { }
        }
    }
}
