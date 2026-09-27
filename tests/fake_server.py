"""Fake Azure Resource Graph + Azure Resource Manager + Microsoft Graph, as a real HTTP server.

Serves the fake tenant from raw.json (made by fake_tenant.py) so Rootline's real HTTP code can be tested:
  - ARM calls must carry the ARM token and Graph calls the Graph token (else 401)
  - Resource Graph pages at most 1,000 rows with $skipToken; ARM pages with nextLink; Graph with @odata.nextLink
  - every 9th request is throttled once with 429 + Retry-After, like the real services under load
  - one management group answers 403; getByIds rejects more than 1,000 ids
  - anything it doesn't recognise is answered 404 and counted, so the test fails on unknown calls
GET /stats returns the counters.

Usage: python tests/fake_server.py <raw.json> <port>
"""
import json, re, sys, threading, urllib.parse
from http.server import ThreadingHTTPServer, BaseHTTPRequestHandler

RAW = json.load(open(sys.argv[1]))
PORT = int(sys.argv[2])
BASE = f"http://127.0.0.1:{PORT}"
ARM_TOKEN, GRAPH_TOKEN = "fake-arm-token", "fake-graph-token"
GRAPH_PAGE = 25
STATS = {"requests": 0, "arg": 0, "arm": 0, "graph": 0, "throttled": 0, "unauthorized": 0, "denied": 0, "unknown": [], "badRequests": []}
LOCK = threading.Lock()

ROUTES = [  # (key in raw.json "arg", pattern in the KQL query)
    ("mgs", r"microsoft\.management/managementgroups'"),
    ("subs", r"microsoft\.resources/subscriptions'\s*\n\| project subscriptionId"),
    ("rgs", r"microsoft\.resources/subscriptions/resourcegroups'"),
    ("resources", r"\| where tolower\(type\) !in \("),
    ("subnets", r"mv-expand sn = properties\.subnets"),
    ("nic", r"networkinterfaces'\s*\n\| extend owner"),
    ("peer", r"virtualNetworkPeerings"),
    ("pip", r"publicipaddresses'\s*\n\| project"),
    ("pe", r"privateendpoints'"),
    ("disk", r"compute/disks' and isnotempty\(managedBy\)"),
    ("web", r"microsoft\.web/sites'"),
    ("aks", r"containerservice/managedclusters'"),
    ("vmss", r"virtualmachinescalesets'"),
    ("dns", r"privatednszones/virtualnetworklinks'"),
    ("conn", r"microsoft\.network/connections'"),
    ("routes", r"microsoft\.network/routetables'"),
    ("cfg-ipConfigurations", r"isnotnull\(properties\.ipConfigurations\)"),
    ("cfg-gatewayIPConfigurations", r"isnotnull\(properties\.gatewayIPConfigurations\)"),
    ("cfg-frontendIPConfigurations", r"isnotnull\(properties\.frontendIPConfigurations\)"),
    ("roledefs_custom", r"authorization/roledefinitions'"),
    ("roleassign", r"authorization/roleassignments'"),
    ("policyassign", r"authorization/policyassignments'"),
    ("policystates", r"policyinsights/policystates'"),
]


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"   # keep-alive, like the real services

    def log_message(self, *a):  # quiet
        pass

    def send(self, status, obj, headers=None):
        body = json.dumps(obj).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        for k, v in (headers or {}).items():
            self.send_header(k, v)
        self.end_headers()
        self.wfile.write(body)

    def error(self, status, code, message):
        self.send(status, {"error": {"code": code, "message": message}})

    def unknown(self):
        with LOCK:
            STATS["unknown"].append(f"{self.command} {self.path}")
        self.error(404, "FakeNotFound", f"no fake data for {self.command} {self.path}")

    def gate(self, token):
        """auth + occasional throttling; returns False if the request was already answered"""
        with LOCK:
            STATS["requests"] += 1
            n = STATS["requests"]
        if self.headers.get("Authorization") != f"Bearer {token}":
            with LOCK:
                STATS["unauthorized"] += 1
            self.error(401, "InvalidAuthenticationToken", "wrong or missing token for this service")
            return False
        if n % 9 == 0:
            with LOCK:
                STATS["throttled"] += 1
            self.send(429, {"error": {"code": "TooManyRequests", "message": "slow down"}}, {"Retry-After": "0"})
            return False
        return True

    def do_GET(self):
        u = urllib.parse.urlparse(self.path)
        q = urllib.parse.parse_qs(u.query)
        if u.path == "/stats":
            return self.send(200, STATS)
        if u.path.startswith("/arm/"):
            if not self.gate(ARM_TOKEN):
                return
            STATS["arm"] += 1
            return self.arm(u.path[4:], q, u.query)
        if u.path.startswith("/graph/v1.0/"):
            if not self.gate(GRAPH_TOKEN):
                return
            STATS["graph"] += 1
            return self.graph_get(u.path[len("/graph/v1.0"):], q, u)
        self.unknown()

    def do_POST(self):
        u = urllib.parse.urlparse(self.path)
        body = json.loads(self.rfile.read(int(self.headers.get("Content-Length", 0))) or b"{}")
        if u.path == "/arm/providers/Microsoft.ResourceGraph/resources":
            if not self.gate(ARM_TOKEN):
                return
            STATS["arg"] += 1
            return self.arg(body, urllib.parse.parse_qs(u.query))
        if u.path == "/graph/v1.0/directoryObjects/getByIds":
            if not self.gate(GRAPH_TOKEN):
                return
            STATS["graph"] += 1
            ids = body.get("ids", [])
            if len(ids) > 1000:
                return self.error(400, "Request_BadRequest", "getByIds accepts at most 1000 ids")
            objs = RAW["graph"]["objects"]
            return self.send(200, {"value": [objs[i] for i in ids if i in objs]})
        self.unknown()

    # ---------------- Resource Graph
    def arg(self, body, q):
        if q.get("api-version") != ["2022-10-01"]:
            STATS["badRequests"].append("ARG api-version " + str(q.get("api-version")))
        query, opts = body.get("query", ""), body.get("options", {})
        top = int(opts.get("$top", 100))
        if top > 1000:
            return self.error(400, "BadRequest", "$top cannot exceed 1000")
        if opts.get("resultFormat") != "objectArray":
            STATS["badRequests"].append("ARG resultFormat " + str(opts.get("resultFormat")))
        key = next((k for k, pat in ROUTES if re.search(pat, query)), None)
        if key is None:
            with LOCK:
                STATS["unknown"].append("ARG query: " + query[:120].replace("\n", " "))
            return self.error(400, "BadRequest", "no fake data for this query")
        rows = RAW["arg"][key]
        if key == "resources":
            excl = re.findall(r"'([^']+)'", query.split("!in (")[1].split(")")[0])
            rows = [r for r in rows if r["type"] not in excl]
        start = int(opts.get("$skipToken") or 0)
        page = rows[start:start + top]
        resp = {"totalRecords": len(rows), "count": len(page), "resultTruncated": "false", "data": page}
        if start + top < len(rows):
            resp["$skipToken"] = str(start + top)
        self.send(200, resp)

    # ---------------- Azure Resource Manager
    def arm(self, path, q, query):
        a = RAW["arm"]
        if path == "/providers/Microsoft.Authorization/roleDefinitions":
            start, size = int(q.get("fakePage", ["0"])[0]), 100
            items = a["roledefs_builtin"]
            resp = {"value": items[start:start + size]}
            if start + size < len(items):
                resp["nextLink"] = f"{BASE}/arm/providers/Microsoft.Authorization/roleDefinitions?api-version=2022-04-01&fakePage={start + size}"
            return self.send(200, resp)
        m = re.match(r"^/providers/Microsoft\.Management/managementGroups/([^/]+)/providers/Microsoft\.Authorization/(roleAssignments|policyAssignments)$", path)
        if m:
            mg, kind = urllib.parse.unquote(m.group(1)), m.group(2)
            if q.get("$filter") != ["atScope()"]:
                STATS["badRequests"].append(f"ARM {kind} at {mg} without atScope()")
            if mg in a["denied"]:
                STATS["denied"] += 1
                return self.error(403, "AuthorizationFailed", f"The client does not have authorization over scope {mg}.")
            entry = a["mg"].get(mg)
            if entry is None:
                return self.error(404, "ManagementGroupNotFound", mg)
            return self.send(200, {"value": entry[kind]})
        m = re.match(r"^/providers/Microsoft\.Management/managementGroups/([^/]+)/providers/Microsoft\.Authorization/roleEligibilityScheduleInstances$", path)
        if m:
            mg = urllib.parse.unquote(m.group(1))
            if q.get("$filter") != ["atScope()"] or q.get("api-version") != ["2020-10-01"]:
                STATS["badRequests"].append(f"ARM eligibility at {mg}: {query}")
            if mg in a["denied"]:
                STATS["denied"] += 1
                return self.error(403, "AuthorizationFailed", f"The client does not have authorization over scope {mg}.")
            return self.send(200, {"value": a["mg"][mg]["eligibility"]})
        m = re.match(r"^/subscriptions/([^/]+)/providers/Microsoft\.Authorization/roleEligibilityScheduleInstances$", path)
        if m and m.group(1) in a["subElig"]:
            items = a["subElig"][m.group(1)]
            start = int(q.get("fakePage", ["0"])[0]); size = 2        # tiny pages to exercise nextLink
            resp = {"value": items[start:start + size]}
            if start + size < len(items):
                resp["nextLink"] = f"{BASE}/arm{path}?api-version=2020-10-01&fakePage={start + size}"
            return self.send(200, resp)
        self.unknown()

    # ---------------- Microsoft Graph
    def graph_get(self, path, q, u):
        g = RAW["graph"]
        def paged(items):
            start = int(q.get("$skiptoken", ["0"])[0])
            resp = {"value": items[start:start + GRAPH_PAGE]}
            if start + GRAPH_PAGE < len(items):
                qs = dict((k, v[0]) for k, v in q.items())
                qs["$skiptoken"] = str(start + GRAPH_PAGE)
                resp["@odata.nextLink"] = f"{BASE}/graph/v1.0{path}?{urllib.parse.urlencode(qs)}"
            return self.send(200, resp)
        if path == "/organization":
            return self.send(200, {"value": [g["org"]]})
        if path == "/roleManagement/directory/roleDefinitions":
            return paged(g["roleDefinitions"])
        if path == "/roleManagement/directory/roleAssignments":
            return paged(g["roleAssignments"])
        if path == "/roleManagement/directory/roleEligibilitySchedules":
            return paged(g["roleEligibilitySchedules"])
        if path == "/identity/conditionalAccess/policies":
            return paged(g["ca"])
        EM = "/identityGovernance/entitlementManagement"
        if path == EM + "/accessPackages":
            if "catalog" not in q.get("$expand", [""])[0]:
                STATS["badRequests"].append("accessPackages without $expand=catalog")
            return paged(g["accessPackages"])
        m = re.match(r"^/identityGovernance/entitlementManagement/accessPackages/([^/]+)$", path)
        if m and m.group(1) in g["accessPackageDetail"]:
            if "resourceRoleScopes" not in q.get("$expand", [""])[0]:
                STATS["badRequests"].append("accessPackage without $expand=resourceRoleScopes")
            return self.send(200, g["accessPackageDetail"][m.group(1)])
        if path == EM + "/assignments":
            return paged(g["pkgAssignments"])
        if path == EM + "/assignmentRequests":
            return paged(g["pkgRequests"])
        if path == "/identityGovernance/privilegedAccess/group/eligibilityScheduleInstances":
            f = re.match(r"^groupId eq '([^']+)'$", q.get("$filter", [""])[0])
            if not f:   # the real API requires a groupId or principalId filter
                return self.error(400, "BadRequest", "filter on groupId or principalId is required")
            return paged(g["groupElig"].get(f.group(1), []))
        m = re.match(r"^/groups/([^/]+)/transitiveMembers$", path)
        if m:
            return paged(g["members"].get(m.group(1), []))
        self.unknown()


ThreadingHTTPServer(("127.0.0.1", PORT), Handler).serve_forever()
