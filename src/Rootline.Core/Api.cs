// Rootline.Core — Api.cs
// HTTP access to Azure Resource Graph, Azure Resource Manager and Microsoft Graph (read-only).
// Targets netstandard2.0 so the same code runs in the Windows app (.NET Framework 4.8) and in tests (.NET 8).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Rootline.Core
{
    /// <summary>Hands out access tokens. Resource is "arm" (Azure) or "graph" (Microsoft Graph).</summary>
    public interface ITokenSource
    {
        Task<string> GetTokenAsync(string resource, CancellationToken ct);
    }

    public sealed class Endpoints
    {
        public string Arm { get; set; } = "https://management.azure.com";
        public string Graph { get; set; } = "https://graph.microsoft.com/v1.0";
    }

    /// <summary>What to scan: everything the account can read, some subscriptions, or one management group.</summary>
    public sealed class ScanScope
    {
        public string[] Subscriptions { get; set; }
        public string ManagementGroup { get; set; }

        public string Describe()
        {
            if (!string.IsNullOrEmpty(ManagementGroup)) return "Management group " + ManagementGroup;
            if (Subscriptions != null && Subscriptions.Length > 0) return Subscriptions.Length + " subscription(s)";
            return "All readable subscriptions";
        }
    }

    public sealed class ApiException : Exception
    {
        public int Status { get; }
        public string Code { get; }
        public ApiException(int status, string code, string message) : base(message) { Status = status; Code = code; }
    }

    public sealed class Api
    {
        private readonly HttpClient _http;
        private readonly ITokenSource _tokens;
        private readonly Endpoints _ep;
        private readonly ScanScope _scope;

        public int ResourceGraphQueries { get; private set; }
        public int ArmCalls { get; private set; }
        public int GraphCalls { get; private set; }
        public int Retries { get; private set; }

        /// <summary>For tests: how long to wait before retrying when a service says "slow down" without a Retry-After.</summary>
        public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(2);

        public Api(HttpClient http, ITokenSource tokens, Endpoints endpoints = null, ScanScope scope = null)
        {
            _http = http;
            _tokens = tokens;
            _ep = endpoints ?? new Endpoints();
            _scope = scope ?? new ScanScope();
        }

        private async Task<JsonNode> SendAsync(string resource, HttpMethod method, string url, string body, CancellationToken ct)
        {
            for (var attempt = 0; ; attempt++)
            {
                var token = await _tokens.GetTokenAsync(resource, ct).ConfigureAwait(false);
                using (var req = new HttpRequestMessage(method, url))
                {
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    req.Headers.UserAgent.ParseAdd("Rootline/1.0");
                    if (body != null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    using (var res = await _http.SendAsync(req, ct).ConfigureAwait(false))
                    {
                        var text = res.Content == null ? "" : await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var status = (int)res.StatusCode;
                        if ((status == 429 || status == 503 || status == 504) && attempt < 6)
                        {
                            Retries++;
                            var wait = res.Headers.RetryAfter?.Delta ?? TimeSpan.FromTicks(RetryDelay.Ticks * (attempt + 1));
                            if (wait > TimeSpan.FromSeconds(60)) wait = TimeSpan.FromSeconds(60);
                            await Task.Delay(wait, ct).ConfigureAwait(false);
                            continue;
                        }
                        if (status >= 400)
                        {
                            string code = null, message = null;
                            try
                            {
                                var err = JsonNode.Parse(text)?["error"];
                                code = Str(err?["code"]);
                                message = Str(err?["message"]);
                            }
                            catch (Exception) { /* not JSON */ }
                            throw new ApiException(status, code, $"HTTP {status}{(code != null ? " " + code : "")}: {message ?? res.ReasonPhrase}");
                        }
                        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
                    }
                }
            }
        }

        /// <summary>Runs a Resource Graph query in the scan scope and returns every row (pages of 1,000).</summary>
        public async Task<List<JsonObject>> QueryAsync(string query, CancellationToken ct)
        {
            ResourceGraphQueries++;
            var rows = new List<JsonObject>();
            string skip = null;
            do
            {
                var options = new JsonObject { ["$top"] = 1000, ["resultFormat"] = "objectArray" };
                if (skip != null) options["$skipToken"] = skip;
                var body = new JsonObject { ["query"] = query, ["options"] = options };
                if (!string.IsNullOrEmpty(_scope.ManagementGroup))
                    body["managementGroups"] = new JsonArray(JsonValue.Create(_scope.ManagementGroup));
                else if (_scope.Subscriptions != null && _scope.Subscriptions.Length > 0)
                    body["subscriptions"] = new JsonArray(_scope.Subscriptions.Select(s => (JsonNode)JsonValue.Create(s)).ToArray());
                var r = await SendAsync("arm", HttpMethod.Post, _ep.Arm + "/providers/Microsoft.ResourceGraph/resources?api-version=2022-10-01",
                    body.ToJsonString(), ct).ConfigureAwait(false);
                if (r?["data"] is JsonArray data)
                    foreach (var row in data) if (row is JsonObject o) rows.Add(o);
                skip = Str(r?["$skipToken"]);
            } while (!string.IsNullOrEmpty(skip));
            return rows;
        }

        /// <summary>GET on Azure Resource Manager, following nextLink.</summary>
        public async Task<List<JsonObject>> ArmListAsync(string path, CancellationToken ct)
        {
            var items = new List<JsonObject>();
            var url = _ep.Arm + path;
            while (!string.IsNullOrEmpty(url))
            {
                ArmCalls++;
                var r = await SendAsync("arm", HttpMethod.Get, url, null, ct).ConfigureAwait(false);
                if (r?["value"] is JsonArray v) foreach (var x in v) if (x is JsonObject o) items.Add(o);
                url = Str(r?["nextLink"]);
            }
            return items;
        }

        /// <summary>GET on Microsoft Graph, following @odata.nextLink.</summary>
        public async Task<List<JsonObject>> GraphListAsync(string path, CancellationToken ct)
        {
            var items = new List<JsonObject>();
            var url = _ep.Graph + path;
            while (!string.IsNullOrEmpty(url))
            {
                GraphCalls++;
                var r = await SendAsync("graph", HttpMethod.Get, url, null, ct).ConfigureAwait(false);
                if (r?["value"] is JsonArray v) foreach (var x in v) if (x is JsonObject o) items.Add(o);
                url = Str(r?["@odata.nextLink"]);
            }
            return items;
        }

        /// <summary>GET of a single Microsoft Graph object.</summary>
        public async Task<JsonObject> GraphGetAsync(string path, CancellationToken ct)
        {
            GraphCalls++;
            return await SendAsync("graph", HttpMethod.Get, _ep.Graph + path, null, ct).ConfigureAwait(false) as JsonObject;
        }

        public async Task<JsonNode> GraphPostAsync(string path, JsonNode body, CancellationToken ct)
        {
            GraphCalls++;
            return await SendAsync("graph", HttpMethod.Post, _ep.Graph + path, body.ToJsonString(), ct).ConfigureAwait(false);
        }

        /// <summary>A JSON value as a string ("" for null/missing). Numbers and booleans are converted.</summary>
        public static string Str(JsonNode n)
        {
            if (n == null) return null;
            if (n is JsonValue v)
            {
                if (v.TryGetValue(out string s)) return s;
                return v.ToJsonString().Trim('"');
            }
            return n.ToJsonString();
        }
    }
}
