// Test host: runs the real Rootline scanner against the fake Azure/Entra web server (tests/fake_server.py).
using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Rootline.Core;

namespace Rootline.Tests
{
    public sealed class StaticTokens : ITokenSource
    {
        private readonly string _arm, _graph;
        public StaticTokens(string arm, string graph) { _arm = arm; _graph = graph; }
        public Task<string> GetTokenAsync(string resource, CancellationToken ct)
        {
            if (resource == "arm") return Task.FromResult(_arm);
            if (resource == "graph") return Task.FromResult(_graph);
            throw new ArgumentException("Unknown resource " + resource);
        }
    }

    internal sealed class ConsoleProgress : IProgress<string>
    {
        public void Report(string value) { Console.WriteLine("    · " + value); }
    }

    public static class TestHost
    {
        /// <summary>Scans the fake tenant; returns {"data": …, "stats": …} as JSON text.</summary>
        public static string Run(string baseUrl, string tenantId, bool entra, string armToken, string graphToken, bool quiet)
        {
            using (var http = new HttpClient())
            {
                var api = new Api(http, new StaticTokens(armToken, graphToken),
                    new Endpoints { Arm = baseUrl + "/arm", Graph = baseUrl + "/graph/v1.0" }) { RetryDelay = TimeSpan.FromMilliseconds(10) };
                var scanner = new Scanner(api, new ScanOptions { TenantId = tenantId, Entra = entra }, quiet ? null : new ConsoleProgress());
                var sw = Stopwatch.StartNew();
                var data = scanner.ScanAsync().GetAwaiter().GetResult();
                sw.Stop();
                return new JsonObject
                {
                    ["data"] = data,
                    ["stats"] = new JsonObject
                    {
                        ["seconds"] = Math.Round(sw.Elapsed.TotalSeconds, 2), ["resourceGraphQueries"] = api.ResourceGraphQueries,
                        ["armCalls"] = api.ArmCalls, ["graphCalls"] = api.GraphCalls, ["retries"] = api.Retries
                    }
                }.ToJsonString();
            }
        }
    }
}
