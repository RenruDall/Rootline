// Rootline.Core — Scanner.cs
// Reads the estate (read-only) and builds the Rootline data model: resources and how they connect,
// management groups, Azure RBAC, Azure Policy, Entra directory roles, PIM eligibility, groups and Conditional Access.
// The output format is the one rootline.template.html reads.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Rootline.Core
{
    public sealed class ScanOptions
    {
        public string TenantId { get; set; }
        public bool Entra { get; set; } = true;
        public ScanScope Scope { get; set; } = new ScanScope();
        public string[] ExcludeTypes { get; set; } =
        {
            "microsoft.compute/virtualmachines/extensions",
            "microsoft.network/privatednszones/virtualnetworklinks",
            "microsoft.alertsmanagement/smartdetectoralertrules"
        };
        public int MaxGroupsExpanded { get; set; } = 300;
    }

    public sealed class Scanner
    {
        private static readonly string[] PrivilegedAzureRoles =
            { "Owner", "Contributor", "User Access Administrator", "Role Based Access Control Administrator", "Reservations Administrator" };
        private static readonly string[] PrivilegedEntraRoles =
        {
            "Global Administrator", "Privileged Role Administrator", "Privileged Authentication Administrator", "Security Administrator",
            "Application Administrator", "Cloud Application Administrator", "Conditional Access Administrator", "Authentication Administrator",
            "Authentication Policy Administrator", "User Administrator", "Exchange Administrator", "SharePoint Administrator", "Intune Administrator",
            "Hybrid Identity Administrator", "Helpdesk Administrator", "Partner Tier2 Support", "Domain Name Administrator",
            "External Identity Provider Administrator", "Groups Administrator", "Password Administrator", "Security Operator",
            "Directory Synchronization Accounts", "Azure DevOps Administrator"
        };
        private static readonly string[] NodeFields = { "id", "rid", "name", "type", "cat", "parent", "sub", "rg", "loc", "sku", "kind", "prefix", "tags", "external" };

        private readonly Api _api;
        private readonly ScanOptions _o;
        private readonly IProgress<string> _progress;
        private readonly string _tenant;

        private readonly Dictionary<string, JsonObject> _nodes = new Dictionary<string, JsonObject>();
        private readonly List<string> _order = new List<string>();
        private readonly JsonArray _edges = new JsonArray();
        private readonly HashSet<string> _edgeKeys = new HashSet<string>();
        private readonly List<string> _warnings = new List<string>();

        public Scanner(Api api, ScanOptions options, IProgress<string> progress = null)
        {
            _api = api;
            _o = options;
            _progress = progress;
            _tenant = (options.TenantId ?? "").ToLowerInvariant();
        }

        public IReadOnlyList<string> Warnings => _warnings;

        // ---------------------------------------------------------------- helpers
        private void Step(string text) { _progress?.Report(text); }

        private static string S(JsonObject row, string prop) => row == null ? "" : (Api.Str(row[prop]) ?? "");
        private static string Low(string s) => (s ?? "").ToLowerInvariant();

        private void AddNode(IDictionary<string, object> n)
        {
            var id = (string)n["id"];
            if (string.IsNullOrEmpty(id) || _nodes.ContainsKey(id)) return;
            var o = new JsonObject();
            foreach (var k in NodeFields)
            {
                if (!n.TryGetValue(k, out var v) || v == null) continue;
                if (v is string s) { if (s.Length > 0) o[k] = s; }
                else if (v is bool b) { if (b) o[k] = true; }
                else if (v is JsonNode j) o[k] = j;
            }
            _nodes[id] = o;
            _order.Add(id);
        }

        private void AddEdge(string s, string t, string rel)
        {
            if (string.IsNullOrEmpty(s) || string.IsNullOrEmpty(t)) return;
            s = s.ToLowerInvariant(); t = t.ToLowerInvariant();
            if (s == t) return;
            var key = rel == "peered" ? string.Join("|", new[] { s, t }.OrderBy(x => x, StringComparer.Ordinal)) : s + "|" + t;
            if (_edgeKeys.Add(key)) _edges.Add(new JsonObject { ["s"] = s, ["t"] = t, ["rel"] = rel });
        }

        private async Task TryStep(string what, Func<Task> action)
        {
            try { await action().ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _warnings.Add(what + " skipped: " + ex.Message); Step("Note: " + what + " skipped (" + ex.Message + ")"); }
        }

        public static string Category(string type)
        {
            if (Regex.IsMatch(type ?? "", @"^microsoft\.(compute|containerservice|containerinstance|web|app|batch)/")) return "compute";
            if (Regex.IsMatch(type ?? "", @"^microsoft\.(network|cdn)/")) return "network";
            if (Regex.IsMatch(type ?? "", @"^microsoft\.(storage|sql|documentdb|dbfor\w+|cache|keyvault|synapse|datafactory|databricks|eventhub|servicebus|kusto|recoveryservices|dataprotection)/")) return "data";
            return "other";
        }

        private static string Root(string id)          // /subscriptions/s/resourcegroups/r/providers/ns/type/name
        {
            var p = id.Split('/');
            return p.Length >= 9 ? string.Join("/", p.Take(9)) : id;
        }

        private static string TypeFromId(string id)
        {
            if (Regex.IsMatch(id, "/subnets/[^/]+$")) return "subnet";
            var p = id.Split('/');
            return p.Length >= 9 ? p[6] + "/" + p[7] : "unknown";
        }

        private string MgId(string name) => Low(name) == _tenant ? "tenant" : "mg:" + Low(name);

        private string ScopeNode(string scope)
        {
            var s = Low(scope).TrimEnd('/');
            if (s.Length == 0) return "tenant";
            var m = Regex.Match(s, "^/providers/microsoft\\.management/managementgroups/([^/]+)$");
            if (m.Success) return MgId(m.Groups[1].Value);
            return s;
        }

        private static JsonNode Tags(JsonNode t)
        {
            if (!(t is JsonObject o) || o.Count == 0) return null;
            var r = new JsonObject();
            foreach (var kv in o) r[kv.Key] = Api.Str(kv.Value) ?? "";
            return r;
        }

        private Task<List<JsonObject>> Q(string query, CancellationToken ct) => _api.QueryAsync(query, ct);

        // ---------------------------------------------------------------- scan
        public async Task<JsonObject> ScanAsync(CancellationToken ct = default(CancellationToken))
        {
            var rootLabel = "Tenant Root Group";
            var mgNames = new List<string>();
            void Mg(string name) { var n = Low(name); if (n.Length > 0 && !mgNames.Contains(n)) mgNames.Add(n); }
            var exclude = string.Join(", ", _o.ExcludeTypes.Select(x => "'" + x.ToLowerInvariant().Replace("'", "''") + "'"));

            // ---------- management groups + subscriptions
            Step("Reading management groups and subscriptions…");
            await TryStep("Management group list", async () =>
            {
                foreach (var m in await Q(@"resourcecontainers
| where type =~ 'microsoft.management/managementgroups'
| project name, displayName = tostring(properties.displayName), parent = tostring(properties.details.parent.name)", ct).ConfigureAwait(false))
                {
                    var id = MgId(S(m, "name"));
                    Mg(S(m, "name"));
                    if (id == "tenant") { rootLabel = S(m, "displayName"); continue; }
                    var parent = S(m, "parent");
                    AddNode(new Dictionary<string, object> { ["id"] = id, ["name"] = S(m, "displayName"), ["type"] = "managementgroup", ["cat"] = "mg",
                        ["parent"] = parent.Length > 0 ? MgId(parent) : "tenant" });
                }
            }).ConfigureAwait(false);

            foreach (var s in await Q(@"resourcecontainers
| where type =~ 'microsoft.resources/subscriptions'
| project subscriptionId, name, chain = properties.managementGroupAncestorsChain", ct).ConfigureAwait(false))
            {
                var chain = (s["chain"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
                var ids = chain.Select(c => MgId(S(c, "name"))).ToList();
                for (var i = 0; i < chain.Count; i++)
                {
                    Mg(S(chain[i], "name"));
                    if (ids[i] == "tenant") { rootLabel = S(chain[i], "displayName"); continue; }
                    AddNode(new Dictionary<string, object> { ["id"] = ids[i], ["name"] = S(chain[i], "displayName"), ["type"] = "managementgroup", ["cat"] = "mg",
                        ["parent"] = i + 1 < chain.Count ? ids[i + 1] : "tenant" });
                }
                var sid = Low(S(s, "subscriptionId"));
                AddNode(new Dictionary<string, object> { ["id"] = "/subscriptions/" + sid, ["rid"] = "/subscriptions/" + S(s, "subscriptionId"), ["name"] = S(s, "name"),
                    ["type"] = "microsoft.resources/subscriptions", ["cat"] = "sub", ["parent"] = ids.Count > 0 ? ids[0] : "tenant", ["sub"] = sid });
            }
            // the tenant root goes first so it is the mind map's centre
            _nodes["tenant"] = new JsonObject { ["id"] = "tenant", ["name"] = rootLabel, ["type"] = "tenant", ["cat"] = "tenant" };
            _order.Insert(0, "tenant");

            Step("Reading resource groups…");
            foreach (var g in await Q(@"resourcecontainers
| where type =~ 'microsoft.resources/subscriptions/resourcegroups'
| project id = tolower(id), rid = id, name, subscriptionId = tolower(subscriptionId), location, tags", ct).ConfigureAwait(false))
            {
                AddNode(new Dictionary<string, object> { ["id"] = S(g, "id"), ["rid"] = S(g, "rid"), ["name"] = S(g, "name"), ["type"] = "microsoft.resources/resourcegroups",
                    ["cat"] = "rg", ["parent"] = "/subscriptions/" + S(g, "subscriptionId"), ["sub"] = S(g, "subscriptionId"), ["rg"] = S(g, "name"),
                    ["loc"] = S(g, "location"), ["tags"] = Tags(g["tags"]) });
            }

            // ---------- resources
            Step("Reading resources…");
            foreach (var r in await Q($@"resources
| where tolower(type) !in ({exclude})
| project id = tolower(id), rid = id, name, type = tolower(type), subscriptionId = tolower(subscriptionId),
          resourceGroup, location, sku = tostring(sku.name), kind, tags", ct).ConfigureAwait(false))
            {
                var type = S(r, "type");
                AddNode(new Dictionary<string, object> { ["id"] = S(r, "id"), ["rid"] = S(r, "rid"), ["name"] = S(r, "name"), ["type"] = type, ["cat"] = Category(type),
                    ["parent"] = "/subscriptions/" + S(r, "subscriptionId") + "/resourcegroups/" + Low(S(r, "resourceGroup")),
                    ["sub"] = S(r, "subscriptionId"), ["rg"] = S(r, "resourceGroup"), ["loc"] = S(r, "location"), ["sku"] = S(r, "sku"),
                    ["kind"] = S(r, "kind"), ["tags"] = Tags(r["tags"]) });
            }
            foreach (var id in _order)                  // child resources (e.g. SQL databases) hang under their parent resource
            {
                var n = _nodes[id];
                var cat = Api.Str(n["cat"]);
                if (cat == "tenant" || cat == "mg" || cat == "sub" || cat == "rg") continue;
                var p = id.Split('/');
                if (p.Length > 9)
                {
                    var parentId = string.Join("/", p.Take(p.Length - 2));
                    if (_nodes.ContainsKey(parentId)) n["parent"] = parentId;
                }
            }

            // ---------- subnets
            Step("Reading networks and connections…");
            foreach (var s in await Q(@"resources
| where type =~ 'microsoft.network/virtualnetworks'
| mv-expand sn = properties.subnets
| project vnet = tolower(id), id = tolower(tostring(sn.id)), rid = tostring(sn.id), name = tostring(sn.name),
          prefix = iff(isnotempty(tostring(sn.properties.addressPrefix)), tostring(sn.properties.addressPrefix), tostring(sn.properties.addressPrefixes)),
          nsg = tolower(tostring(sn.properties.networkSecurityGroup.id)), rt = tolower(tostring(sn.properties.routeTable.id)),
          nat = tolower(tostring(sn.properties.natGateway.id)), subscriptionId = tolower(subscriptionId), resourceGroup, location", ct).ConfigureAwait(false))
            {
                if (S(s, "id").Length == 0) continue;
                AddNode(new Dictionary<string, object> { ["id"] = S(s, "id"), ["rid"] = S(s, "rid"), ["name"] = S(s, "name"), ["type"] = "subnet", ["cat"] = "subnet",
                    ["parent"] = S(s, "vnet"), ["sub"] = S(s, "subscriptionId"), ["rg"] = S(s, "resourceGroup"), ["loc"] = S(s, "location"), ["prefix"] = S(s, "prefix") });
                AddEdge(S(s, "nsg"), S(s, "id"), "protects");
                AddEdge(S(s, "rt"), S(s, "id"), "routes");
                AddEdge(S(s, "nat"), S(s, "id"), "NAT for");
            }

            // ---------- connections
            var hops = new Dictionary<string, string>();   // private IP -> firewall / appliance VM ("" = ambiguous, overlapping ranges)
            void Hop(string ip, string owner)
            {
                if (string.IsNullOrEmpty(ip) || string.IsNullOrEmpty(owner)) return;
                hops[ip] = hops.TryGetValue(ip, out var existing) && existing != owner ? "" : owner;
            }

            foreach (var r in await Q(@"resources
| where type =~ 'microsoft.network/networkinterfaces'
| extend owner = tolower(coalesce(tostring(properties.virtualMachine.id), tostring(properties.privateEndpoint.id)))
| extend nsg = tolower(tostring(properties.networkSecurityGroup.id))
| mv-expand c = properties.ipConfigurations
| project s = tolower(id), owner, nsg, subnet = tolower(tostring(c.properties.subnet.id)),
          pip = tolower(tostring(c.properties.publicIPAddress.id)),
          lb = tolower(tostring(c.properties.loadBalancerBackendAddressPools[0].id)),
          agw = tolower(tostring(c.properties.applicationGatewayBackendAddressPools[0].id)),
          ip = tostring(c.properties.privateIPAddress)", ct).ConfigureAwait(false))
            {
                var nic = S(r, "s"); var owner = S(r, "owner");
                Hop(S(r, "ip"), owner.Length > 0 && !owner.Contains("/privateendpoints/") ? owner : nic);
                AddEdge(nic, owner, "NIC of");
                AddEdge(S(r, "nsg"), nic, "protects");
                AddEdge(nic, S(r, "subnet"), "in subnet");
                AddEdge(S(r, "pip"), nic, "public IP of");
                if (S(r, "lb").Length > 0) AddEdge(Root(S(r, "lb")), nic, "balances");
                if (S(r, "agw").Length > 0) AddEdge(Root(S(r, "agw")), nic, "routes to");
            }
            foreach (var r in await Q(@"resources
| where type =~ 'microsoft.network/virtualnetworks'
| mv-expand p = properties.virtualNetworkPeerings
| project s = tolower(id), t = tolower(tostring(p.properties.remoteVirtualNetwork.id))", ct).ConfigureAwait(false))
                AddEdge(S(r, "s"), S(r, "t"), "peered");
            foreach (var r in await Q(@"resources
| where type =~ 'microsoft.network/publicipaddresses'
| project s = tolower(id), cfg = tolower(tostring(properties.ipConfiguration.id)), nat = tolower(tostring(properties.natGateway.id))", ct).ConfigureAwait(false))
            {
                if (S(r, "cfg").Length > 0) AddEdge(S(r, "s"), Root(S(r, "cfg")), "public IP of");
                AddEdge(S(r, "s"), S(r, "nat"), "public IP of");
            }
            foreach (var r in await Q(@"resources
| where type =~ 'microsoft.network/privateendpoints'
| extend c = iff(array_length(properties.privateLinkServiceConnections) > 0, properties.privateLinkServiceConnections[0], properties.manualPrivateLinkServiceConnections[0])
| project s = tolower(id), subnet = tolower(tostring(properties.subnet.id)), target = tolower(tostring(c.properties.privateLinkServiceId))", ct).ConfigureAwait(false))
            {
                AddEdge(S(r, "s"), S(r, "subnet"), "in subnet");
                AddEdge(S(r, "s"), S(r, "target"), "private link to");
            }
            foreach (var r in await Q(@"resources
| where type =~ 'microsoft.compute/disks' and isnotempty(managedBy)
| project s = tolower(id), t = tolower(managedBy)", ct).ConfigureAwait(false))
                AddEdge(S(r, "s"), S(r, "t"), "disk of");
            foreach (var r in await Q(@"resources
| where type =~ 'microsoft.web/sites'
| project s = tolower(id), plan = tolower(tostring(properties.serverFarmId)), subnet = tolower(tostring(properties.virtualNetworkSubnetId))", ct).ConfigureAwait(false))
            {
                AddEdge(S(r, "s"), S(r, "plan"), "hosted on");
                AddEdge(S(r, "s"), S(r, "subnet"), "integrated with");
            }
            foreach (var r in await Q(@"resources
| where type =~ 'microsoft.containerservice/managedclusters'
| mv-expand p = properties.agentPoolProfiles
| project s = tolower(id), t = tolower(tostring(p.vnetSubnetID))", ct).ConfigureAwait(false))
                AddEdge(S(r, "s"), S(r, "t"), "nodes in");
            foreach (var r in await Q(@"resources
| where type =~ 'microsoft.compute/virtualmachinescalesets'
| mv-expand n = properties.virtualMachineProfile.networkProfile.networkInterfaceConfigurations
| mv-expand c = n.properties.ipConfigurations
| project s = tolower(id), t = tolower(tostring(c.properties.subnet.id))", ct).ConfigureAwait(false))
                AddEdge(S(r, "s"), S(r, "t"), "in subnet");
            foreach (var r in await Q(@"resources
| where type =~ 'microsoft.network/privatednszones/virtualnetworklinks'
| project s = tostring(split(tolower(id), '/virtualnetworklinks/')[0]), t = tolower(tostring(properties.virtualNetwork.id))", ct).ConfigureAwait(false))
                AddEdge(S(r, "s"), S(r, "t"), "DNS linked");
            foreach (var r in await Q(@"resources
| where type =~ 'microsoft.network/connections'
| project s = tolower(id), gw = tolower(tostring(properties.virtualNetworkGateway1.id)),
          peer = tolower(coalesce(tostring(properties.virtualNetworkGateway2.id), tostring(properties.localNetworkGateway2.id), tostring(properties.peer.id)))", ct).ConfigureAwait(false))
            {
                AddEdge(S(r, "s"), S(r, "gw"), "connection of");
                AddEdge(S(r, "s"), S(r, "peer"), "connects to");
            }
            foreach (var prop in new[] { "ipConfigurations", "gatewayIPConfigurations", "frontendIPConfigurations" })
            {
                foreach (var r in await Q($@"resources
| where type !in~ ('microsoft.network/networkinterfaces', 'microsoft.network/publicipaddresses')
| where isnotnull(properties.{prop})
| mv-expand c = properties.{prop}
| project s = tolower(id), subnet = tolower(tostring(c.properties.subnet.id)), pip = tolower(tostring(c.properties.publicIPAddress.id)),
          ip = tostring(c.properties.privateIPAddress)", ct).ConfigureAwait(false))
                {
                    AddEdge(S(r, "s"), S(r, "subnet"), "in subnet");
                    AddEdge(S(r, "pip"), S(r, "s"), "public IP of");
                    Hop(S(r, "ip"), S(r, "s"));
                }
            }
            foreach (var r in await Q(@"resources
| where type =~ 'microsoft.network/routetables'
| mv-expand r = properties.routes
| where tostring(r.properties.nextHopType) =~ 'VirtualAppliance'
| project s = tolower(id), hop = tostring(r.properties.nextHopIpAddress)", ct).ConfigureAwait(false))
            {
                if (hops.TryGetValue(S(r, "hop"), out var target) && target.Length > 0) AddEdge(S(r, "s"), target, "routes to");
            }

            // targets outside the scanned scope become dashed "external" nodes
            foreach (var e in _edges.OfType<JsonObject>().ToList())
            {
                foreach (var id in new[] { Api.Str(e["s"]), Api.Str(e["t"]) })
                {
                    if (_nodes.ContainsKey(id)) continue;
                    var type = TypeFromId(id);
                    string parent = null;
                    if (type == "subnet")
                    {
                        parent = Regex.Replace(id, "/subnets/[^/]+$", "");
                        if (!_nodes.ContainsKey(parent))
                            AddNode(new Dictionary<string, object> { ["id"] = parent, ["rid"] = parent, ["name"] = parent.Split('/').Last(),
                                ["type"] = "microsoft.network/virtualnetworks", ["cat"] = "network", ["external"] = true });
                    }
                    AddNode(new Dictionary<string, object> { ["id"] = id, ["rid"] = id, ["name"] = id.Split('/').Last(), ["type"] = type,
                        ["cat"] = type == "subnet" ? "subnet" : Category(type), ["parent"] = parent, ["external"] = true });
                }
            }

            // ---------- Azure RBAC
            Step("Reading Azure role assignments…");
            var roleDefs = new Dictionary<string, (string name, bool custom)>();
            await TryStep("Built-in role names", async () =>
            {
                foreach (var d in await _api.ArmListAsync("/providers/Microsoft.Authorization/roleDefinitions?api-version=2022-04-01", ct).ConfigureAwait(false))
                    roleDefs[Low(S(d, "name"))] = (Api.Str(d["properties"]?["roleName"]), Api.Str(d["properties"]?["type"]) == "CustomRole");
            }).ConfigureAwait(false);
            await TryStep("Custom role names", async () =>
            {
                foreach (var d in await Q(@"authorizationresources
| where type =~ 'microsoft.authorization/roledefinitions'
| project name = tolower(name), roleName = tostring(properties.roleName), roleType = tostring(properties.type)", ct).ConfigureAwait(false))
                    roleDefs[S(d, "name")] = (S(d, "roleName"), S(d, "roleType") == "CustomRole");
            }).ConfigureAwait(false);

            var assignments = new Dictionary<string, (string p, string ptype, string roleDef, string scope, string cond)>();
            await TryStep("Role assignments (subscriptions and below)", async () =>
            {
                foreach (var a in await Q(@"authorizationresources
| where type =~ 'microsoft.authorization/roleassignments'
| project id = tolower(id), principalId = tostring(properties.principalId), principalType = tostring(properties.principalType),
          roleDefinitionId = tolower(tostring(properties.roleDefinitionId)), scope = tolower(tostring(properties.scope)),
          condition = tostring(properties.condition)", ct).ConfigureAwait(false))
                    assignments[S(a, "id")] = (S(a, "principalId"), S(a, "principalType"), S(a, "roleDefinitionId"), S(a, "scope"), S(a, "condition"));
            }).ConfigureAwait(false);
            foreach (var mg in mgNames)                  // management-group and root assignments come from ARM
            {
                await TryStep("Role assignments at management group " + mg, async () =>
                {
                    foreach (var a in await _api.ArmListAsync($"/providers/Microsoft.Management/managementGroups/{Uri.EscapeDataString(mg)}/providers/Microsoft.Authorization/roleAssignments?api-version=2022-04-01&$filter=atScope()", ct).ConfigureAwait(false))
                    {
                        var p = a["properties"] as JsonObject;
                        assignments[Low(S(a, "id"))] = (S(p, "principalId"), S(p, "principalType"), Low(S(p, "roleDefinitionId")), Low(S(p, "scope")), S(p, "condition"));
                    }
                }).ConfigureAwait(false);
            }
            // PIM-eligible Azure roles: per management group (at and above) and per subscription (at, above and below)
            Step("Reading PIM-eligible Azure roles…");
            var eligible = new Dictionary<string, (string p, string ptype, string roleDef, string scope, string until)>();
            void AddEligible(JsonObject e)
            {
                var p = e["properties"] as JsonObject;
                if (p == null) return;
                // the same eligibility shows up from several scopes; key it by who / what / where
                var key = S(p, "principalId") + "|" + Low(S(p, "roleDefinitionId")).Split('/').Last() + "|" + Low(S(p, "scope"));
                eligible[key] = (S(p, "principalId"), S(p, "principalType"), Low(S(p, "roleDefinitionId")), Low(S(p, "scope")), S(p, "endDateTime"));
            }
            foreach (var mg in mgNames)
                await TryStep("Eligible Azure roles at management group " + mg, async () =>
                {
                    foreach (var e in await _api.ArmListAsync($"/providers/Microsoft.Management/managementGroups/{Uri.EscapeDataString(mg)}/providers/Microsoft.Authorization/roleEligibilityScheduleInstances?api-version=2020-10-01&$filter=atScope()", ct).ConfigureAwait(false))
                        AddEligible(e);
                }).ConfigureAwait(false);
            foreach (var sid in _order.Where(i => i.StartsWith("/subscriptions/") && i.Count(c => c == '/') == 2).ToList())
                await TryStep("Eligible Azure roles in " + Api.Str(_nodes[sid]["name"]), async () =>
                {
                    foreach (var e in await _api.ArmListAsync(sid + "/providers/Microsoft.Authorization/roleEligibilityScheduleInstances?api-version=2020-10-01", ct).ConfigureAwait(false))
                        AddEligible(e);
                }).ConfigureAwait(false);

            var azure = new JsonArray();
            var principalKinds = new Dictionary<string, string>();
            void AddAzure(string p, string ptype, string roleDef, string scope, string cond, string status, string until)
            {
                var guid = roleDef.Split('/').Last();
                var has = roleDefs.TryGetValue(guid, out var def);
                var role = has ? def.name : "Role " + guid;
                var o = new JsonObject { ["p"] = p, ["role"] = role, ["custom"] = has && def.custom, ["priv"] = PrivilegedAzureRoles.Contains(role),
                    ["scope"] = ScopeNode(scope), ["cond"] = cond.Length > 0, ["status"] = status };
                if (!string.IsNullOrEmpty(until)) o["until"] = until;
                azure.Add(o);
                if (ptype.Length > 0) principalKinds[p] = ptype;
            }
            foreach (var a in assignments.Values) AddAzure(a.p, a.ptype, a.roleDef, a.scope, a.cond, "active", null);
            foreach (var e in eligible.Values) AddAzure(e.p, e.ptype, e.roleDef, e.scope, "", "eligible", e.until);

            // ---------- Azure Policy
            Step("Reading policy assignments and compliance…");
            var policiesRaw = new Dictionary<string, (string name, string scope, string def, string enforce)>();
            await TryStep("Policy assignments (subscriptions and below)", async () =>
            {
                foreach (var p in await Q(@"policyresources
| where type =~ 'microsoft.authorization/policyassignments'
| project id = tolower(id), name = tostring(properties.displayName), scope = tolower(tostring(properties.scope)),
          definitionId = tolower(tostring(properties.policyDefinitionId)), enforcement = tostring(properties.enforcementMode)", ct).ConfigureAwait(false))
                    policiesRaw[S(p, "id")] = (S(p, "name"), S(p, "scope"), S(p, "definitionId"), S(p, "enforcement"));
            }).ConfigureAwait(false);
            foreach (var mg in mgNames)
            {
                await TryStep("Policy assignments at management group " + mg, async () =>
                {
                    foreach (var p in await _api.ArmListAsync($"/providers/Microsoft.Management/managementGroups/{Uri.EscapeDataString(mg)}/providers/Microsoft.Authorization/policyAssignments?api-version=2023-04-01&$filter=atScope()", ct).ConfigureAwait(false))
                    {
                        var pr = p["properties"] as JsonObject;
                        policiesRaw[Low(S(p, "id"))] = (S(pr, "displayName"), Low(S(pr, "scope")), Low(S(pr, "policyDefinitionId")), S(pr, "enforcementMode"));
                    }
                }).ConfigureAwait(false);
            }
            var nonCompliant = new Dictionary<string, JsonObject>();
            await TryStep("Policy compliance", async () =>
            {
                foreach (var c in await Q(@"policyresources
| where type =~ 'microsoft.policyinsights/policystates'
| where tostring(properties.complianceState) =~ 'NonCompliant'
| summarize n = dcount(tostring(properties.resourceId)) by a = tolower(tostring(properties.policyAssignmentId)), subscriptionId = tolower(subscriptionId)", ct).ConfigureAwait(false))
                {
                    if (!nonCompliant.TryGetValue(S(c, "a"), out var byS)) nonCompliant[S(c, "a")] = byS = new JsonObject();
                    byS[S(c, "subscriptionId")] = int.TryParse(S(c, "n"), out var n) ? n : 0;
                }
            }).ConfigureAwait(false);
            var policies = new JsonArray();
            foreach (var kv in policiesRaw)
            {
                var p = kv.Value;
                policies.Add(new JsonObject { ["id"] = kv.Key, ["name"] = p.name.Length > 0 ? p.name : kv.Key.Split('/').Last(), ["scope"] = ScopeNode(p.scope),
                    ["initiative"] = p.def.Contains("/policysetdefinitions/"), ["enforce"] = p.enforce.Length > 0 ? p.enforce : "Default",
                    ["nonCompliant"] = nonCompliant.TryGetValue(kv.Key, out var nc) ? nc : null });
            }

            // ---------- Entra
            var principals = new Dictionary<string, JsonObject>();
            var entra = new JsonArray();
            var members = new JsonObject();
            var ca = new JsonArray();
            var packages = new JsonArray();
            var packageAssignments = new JsonArray();
            var packageRequests = new JsonArray();
            var groupEligible = new JsonArray();
            string tenantName = null;
            void AddPrincipal(JsonObject o)
            {
                var t = S(o, "@odata.type").ToLowerInvariant();
                var kind = t.EndsWith("user") ? "user" : t.EndsWith("group") ? "group" : t.EndsWith("serviceprincipal") ? "sp" : null;
                var id = S(o, "id");
                if (kind == null || id.Length == 0 || principals.ContainsKey(id)) return;
                var upn = S(o, "userPrincipalName");
                var po = new JsonObject { ["id"] = id, ["name"] = S(o, "displayName").Length > 0 ? S(o, "displayName") : id,
                    ["upn"] = upn.Length > 0 ? upn : null, ["kind"] = kind };
                if (kind == "user" && (upn.IndexOf("#EXT#", StringComparison.OrdinalIgnoreCase) >= 0 || S(o, "userType") == "Guest")) po["guest"] = true;
                principals[id] = po;
            }
            if (_o.Entra)
            {
                Step("Reading Entra directory roles…");
                await TryStep("Tenant name", async () =>
                {
                    tenantName = S((await _api.GraphListAsync("/organization?$select=id,displayName", ct).ConfigureAwait(false)).FirstOrDefault(), "displayName");
                }).ConfigureAwait(false);
                var dirRoles = new Dictionary<string, (string name, bool priv)>();
                await TryStep("Directory role definitions", async () =>
                {
                    List<JsonObject> defs;
                    try { defs = await _api.GraphListAsync("/roleManagement/directory/roleDefinitions?$select=id,displayName,isPrivileged", ct).ConfigureAwait(false); }
                    catch (ApiException) { defs = await _api.GraphListAsync("/roleManagement/directory/roleDefinitions?$select=id,displayName", ct).ConfigureAwait(false); }
                    foreach (var d in defs)
                    {
                        var name = S(d, "displayName");
                        var ip = d["isPrivileged"];
                        dirRoles[S(d, "id")] = (name, ip != null ? S(d, "isPrivileged") == "true" : PrivilegedEntraRoles.Contains(name));
                    }
                }).ConfigureAwait(false);
                async Task ReadRoles(string what, string path, string status)
                {
                    await TryStep(what, async () =>
                    {
                        foreach (var a in await _api.GraphListAsync(path, ct).ConfigureAwait(false))
                        {
                            var has = dirRoles.TryGetValue(S(a, "roleDefinitionId"), out var d);
                            var o = new JsonObject { ["p"] = S(a, "principalId"), ["role"] = has ? d.name : S(a, "roleDefinitionId"), ["priv"] = has && d.priv,
                                ["status"] = status, ["scope"] = S(a, "directoryScopeId") };
                            var until = Api.Str(a["scheduleInfo"]?["expiration"]?["endDateTime"]);
                            if (!string.IsNullOrEmpty(until)) o["until"] = until;
                            entra.Add(o);
                        }
                    }).ConfigureAwait(false);
                }
                await ReadRoles("Directory role assignments", "/roleManagement/directory/roleAssignments?$select=id,principalId,roleDefinitionId,directoryScopeId", "active").ConfigureAwait(false);
                await ReadRoles("PIM eligible roles (needs Entra ID P2)", "/roleManagement/directory/roleEligibilitySchedules?$select=id,principalId,roleDefinitionId,directoryScopeId,scheduleInfo", "eligible").ConfigureAwait(false);

                Step("Resolving identities and group members…");
                var ids = azure.Select(a => Api.Str(a["p"])).Concat(entra.Select(a => Api.Str(a["p"])))
                    .Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList();
                await TryStep("Identity names", async () =>
                {
                    for (var i = 0; i < ids.Count; i += 1000)
                    {
                        var body = new JsonObject
                        {
                            ["ids"] = new JsonArray(ids.Skip(i).Take(1000).Select(x => (JsonNode)JsonValue.Create(x)).ToArray()),
                            ["types"] = new JsonArray(JsonValue.Create("user"), JsonValue.Create("group"), JsonValue.Create("servicePrincipal"))
                        };
                        var r = await _api.GraphPostAsync("/directoryObjects/getByIds", body, ct).ConfigureAwait(false);
                        if (r?["value"] is JsonArray v) foreach (var o in v.OfType<JsonObject>()) AddPrincipal(o);
                    }
                }).ConfigureAwait(false);
                var groups = principals.Values.Where(p => Api.Str(p["kind"]) == "group").ToList();   // also used for PIM for Groups below
                if (groups.Count > _o.MaxGroupsExpanded)
                    _warnings.Add($"Only the first {_o.MaxGroupsExpanded} of {groups.Count} groups with roles were expanded to members.");
                foreach (var g in groups.Take(_o.MaxGroupsExpanded))
                {
                    var gid = Api.Str(g["id"]);
                    await TryStep("Members of " + Api.Str(g["name"]), async () =>
                    {
                        var list = new JsonArray();
                        foreach (var o in await _api.GraphListAsync($"/groups/{gid}/transitiveMembers?$select=id,displayName,userPrincipalName,userType&$top=999", ct).ConfigureAwait(false))
                        {
                            AddPrincipal(o);
                            var id = S(o, "id");
                            if (principals.TryGetValue(id, out var p) && Api.Str(p["kind"]) != "group") list.Add(id);
                        }
                        members[gid] = list;
                    }).ConfigureAwait(false);
                }
                // ---------- identity governance: access packages and PIM for Groups
                Step("Reading access packages and PIM for Groups…");
                await TryStep("Access packages (needs Entra ID Governance)", async () =>
                {
                    foreach (var p in await _api.GraphListAsync("/identityGovernance/entitlementManagement/accessPackages?$expand=catalog", ct).ConfigureAwait(false))
                    {
                        var pid = S(p, "id");
                        var groupsOf = new JsonArray();
                        var full = await _api.GraphGetAsync($"/identityGovernance/entitlementManagement/accessPackages/{pid}?$expand=resourceRoleScopes($expand=role,scope)", ct).ConfigureAwait(false);
                        foreach (var rrs in (full?["resourceRoleScopes"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
                        {
                            var scope = rrs["scope"] as JsonObject;
                            if (string.Equals(S(scope, "originSystem"), "AadGroup", StringComparison.OrdinalIgnoreCase) && S(scope, "originId").Length > 0)
                                groupsOf.Add(S(scope, "originId"));
                        }
                        packages.Add(new JsonObject { ["id"] = pid, ["name"] = S(p, "displayName"), ["catalog"] = S(p["catalog"] as JsonObject, "displayName"), ["groups"] = groupsOf });
                    }
                }).ConfigureAwait(false);
                if (packages.Count > 0)
                {
                    await TryStep("Access package assignments", async () =>
                    {
                        foreach (var a in await _api.GraphListAsync("/identityGovernance/entitlementManagement/assignments?$expand=target,accessPackage", ct).ConfigureAwait(false))
                        {
                            if (!string.Equals(S(a, "state"), "delivered", StringComparison.OrdinalIgnoreCase)) continue;
                            var o = new JsonObject { ["p"] = S(a["target"] as JsonObject, "objectId"), ["pkg"] = S(a["accessPackage"] as JsonObject, "id") };
                            var until = Api.Str(a["schedule"]?["expiration"]?["endDateTime"]);
                            if (!string.IsNullOrEmpty(until)) o["until"] = until;
                            packageAssignments.Add(o);
                        }
                    }).ConfigureAwait(false);
                    await TryStep("Pending access package requests", async () =>
                    {
                        foreach (var r in await _api.GraphListAsync("/identityGovernance/entitlementManagement/assignmentRequests?$expand=requestor,accessPackage", ct).ConfigureAwait(false))
                        {
                            if (!string.Equals(S(r, "state"), "pendingApproval", StringComparison.OrdinalIgnoreCase)) continue;
                            packageRequests.Add(new JsonObject { ["p"] = S(r["requestor"] as JsonObject, "objectId"), ["pkg"] = S(r["accessPackage"] as JsonObject, "id"),
                                ["created"] = S(r, "createdDateTime") });
                        }
                    }).ConfigureAwait(false);
                }
                var pimGroupsOk = true;
                foreach (var g in groups.Take(_o.MaxGroupsExpanded))
                {
                    if (!pimGroupsOk) break;
                    var gid = Api.Str(g["id"]);
                    try
                    {
                        foreach (var e in await _api.GraphListAsync($"/identityGovernance/privilegedAccess/group/eligibilityScheduleInstances?$filter=groupId eq '{gid}'", ct).ConfigureAwait(false))
                        {
                            if (!string.Equals(S(e, "accessId"), "member", StringComparison.OrdinalIgnoreCase)) continue;
                            var o = new JsonObject { ["g"] = gid, ["p"] = S(e, "principalId") };
                            if (S(e, "endDateTime").Length > 0) o["until"] = S(e, "endDateTime");
                            groupEligible.Add(o);
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        pimGroupsOk = false;                    // same permission for every group: note it once
                        _warnings.Add("PIM for Groups skipped: " + ex.Message);
                    }
                }
                // identities that only appear in packages, requests or PIM for Groups
                var more = packageAssignments.Concat(packageRequests).Concat(groupEligible).Select(x => Api.Str(x["p"]))
                    .Where(x => !string.IsNullOrEmpty(x) && !principals.ContainsKey(x)).Distinct().ToList();
                if (more.Count > 0)
                    await TryStep("Identity names (governance)", async () =>
                    {
                        for (var i = 0; i < more.Count; i += 1000)
                        {
                            var body = new JsonObject
                            {
                                ["ids"] = new JsonArray(more.Skip(i).Take(1000).Select(x => (JsonNode)JsonValue.Create(x)).ToArray()),
                                ["types"] = new JsonArray(JsonValue.Create("user"), JsonValue.Create("group"), JsonValue.Create("servicePrincipal"))
                            };
                            var r = await _api.GraphPostAsync("/directoryObjects/getByIds", body, ct).ConfigureAwait(false);
                            if (r?["value"] is JsonArray v) foreach (var o in v.OfType<JsonObject>()) AddPrincipal(o);
                        }
                    }).ConfigureAwait(false);

                Step("Reading Conditional Access policies…");
                await TryStep("Conditional Access policies", async () =>
                {
                    foreach (var p in await _api.GraphListAsync("/identity/conditionalAccess/policies?$select=id,displayName,state", ct).ConfigureAwait(false))
                        ca.Add(new JsonObject { ["name"] = S(p, "displayName"), ["state"] = S(p, "state") });
                }).ConfigureAwait(false);
            }
            else
            {
                _warnings.Add("Entra data not collected: identities show as object IDs and group members are not expanded.");
            }
            foreach (var a in azure)                    // anything Graph couldn't resolve keeps its object ID
            {
                var p = Api.Str(a["p"]);
                if (principals.ContainsKey(p)) continue;
                principalKinds.TryGetValue(p, out var t);
                var kind = t == "User" ? "user" : t == "Group" ? "group" : t == "ServicePrincipal" ? "sp" : "other";
                principals[p] = new JsonObject { ["id"] = p, ["name"] = p, ["kind"] = kind, ["unresolved"] = true };
            }

            Step("Done.");
            var nodes = new JsonArray();
            foreach (var id in _order) nodes.Add(_nodes[id]);
            return new JsonObject
            {
                ["meta"] = new JsonObject
                {
                    ["title"] = "Rootline", ["generated"] = DateTime.UtcNow.ToString("o"), ["scope"] = _o.Scope.Describe(),
                    ["tenantId"] = _tenant, ["tenantName"] = tenantName, ["entra"] = _o.Entra,
                    ["warnings"] = new JsonArray(_warnings.Select(w => (JsonNode)JsonValue.Create(w)).ToArray())
                },
                ["nodes"] = nodes,
                ["edges"] = _edges,
                ["access"] = new JsonObject
                {
                    ["principals"] = new JsonArray(principals.Values.Cast<JsonNode>().ToArray()),
                    ["azure"] = azure, ["entra"] = entra, ["members"] = members, ["policies"] = policies, ["ca"] = ca,
                    ["packages"] = packages, ["packageAssignments"] = packageAssignments, ["packageRequests"] = packageRequests, ["groupEligible"] = groupEligible
                }
            };
        }
    }
}
