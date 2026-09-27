"""Fake tenant for the Rootline round-trip test.

Builds a fictional Azure + Entra tenant ("Alpina Demo": CAF landing zones, hub-and-spoke in two regions,
~2,600 resources, ~3,000 connections, ~640 identities) and writes two files:
  raw.json       what Resource Graph, ARM and Microsoft Graph would return for it (served by FakeAzure.ps1)
  expected.json  what Rootline must reconstruct from those responses
Usage: python tests/fake_tenant.py <out-dir>
"""
import json, random, sys, uuid, os, datetime
random.seed(42)
T = "7a1b2c3d-0000-4000-8000-a1b2c3d4e5f6"
nodes, edges = {}, []
def add(id, name, type, cat, parent=None, **kw):
    id = id.lower()
    if id in nodes: return id
    n = {"id": id, "rid": id, "name": name, "type": type, "cat": cat}
    if parent: n["parent"] = parent.lower()
    n.update({k: v for k, v in kw.items() if v not in (None, "")})
    nodes[id] = n; return id
def edge(s, t, rel): edges.append({"s": s.lower(), "t": t.lower(), "rel": rel})
def gid(): return str(uuid.UUID(int=random.getrandbits(128)))

add("tenant", "Tenant Root Group", "tenant", "tenant")
MG = {}
def mg(key, name, parent): MG[key] = add(f"mg:{key}", name, "managementgroup", "mg", parent); return MG[key]
mg("alpina", "Alpina", "tenant")
mg("platform", "Platform", "mg:alpina"); mg("identity", "Identity", "mg:platform"); mg("management", "Management", "mg:platform"); mg("connectivity", "Connectivity", "mg:platform")
mg("landingzones", "Landing Zones", "mg:alpina"); mg("corp", "Corp", "mg:landingzones"); mg("online", "Online", "mg:landingzones"); mg("sap", "SAP", "mg:landingzones")
mg("sandbox", "Sandbox", "mg:alpina"); mg("decommissioned", "Decommissioned", "mg:alpina")

SUBS = [("connectivity-prod","connectivity"),("identity-prod","identity"),("management-prod","management"),
        ("erp-prod","corp"),("erp-dev","corp"),("hr-prod","corp"),("data-prod","corp"),("data-dev","corp"),("ropeway-iot-prod","corp"),
        ("web-prod","online"),("web-dev","online"),("api-prod","online"),("sap-prod","sap"),("sap-qa","sap"),
        ("sandbox-01","sandbox"),("sandbox-02","sandbox"),("sandbox-03","sandbox")]
sub = {}
for i,(n,m) in enumerate(SUBS):
    sid = f"{i+1:08d}-5ub0-4000-8000-{i+1:012d}"
    sub[n] = {"id": sid, "node": add(f"/subscriptions/{sid}", f"sub-{n}", "microsoft.resources/subscriptions", "sub", MG[m], sub=sid)}
REG = {"westeurope": "weu", "northeurope": "neu"}

def rg(s, name, loc="westeurope"):
    sid = sub[s]["id"]; return add(f"/subscriptions/{sid}/resourcegroups/{name}", name, "microsoft.resources/resourcegroups", "rg", sub[s]["node"], sub=sid, rg=name, loc=loc)
def res(s, g, t, name, cat=None, loc="westeurope", parent=None, **kw):
    sid = sub[s]["id"]; rgid = f"/subscriptions/{sid}/resourcegroups/{g}"
    rid = f"{parent}/{t.split('/')[-1]}/{name}" if parent else f"{rgid}/providers/{t}/{name}"
    c = cat or ("compute" if t.startswith(("microsoft.compute","microsoft.containerservice","microsoft.web")) else "network" if t.startswith("microsoft.network") else "data" if t.startswith(("microsoft.storage","microsoft.sql","microsoft.keyvault","microsoft.documentdb","microsoft.dbforpostgresql","microsoft.cache","microsoft.recoveryservices","microsoft.eventhub","microsoft.servicebus")) else "other")
    return add(rid, name, t, c, parent or rgid, sub=sid, rg=g, loc=loc, **kw)
def subnet(vnet, name, prefix, s, g, loc):
    return add(f"{vnet}/subnets/{name}", name, "subnet", "subnet", vnet, prefix=prefix, sub=sub[s]["id"], rg=g, loc=loc)

# ---------------- connectivity: hub per region
hubs, fw_ip, dnszones = {}, {}, []
rg("connectivity-prod", "rg-dns-global")
for z in ["privatelink.blob.core.windows.net","privatelink.database.windows.net","privatelink.vaultcore.azure.net","privatelink.azurewebsites.net","privatelink.postgres.database.azure.com","privatelink.westeurope.azmk8s.io"]:
    dnszones.append(res("connectivity-prod", "rg-dns-global", "microsoft.network/privatednszones", z, loc="global"))
for i,(loc,short) in enumerate(REG.items()):
    g = f"rg-hub-{short}"; rg("connectivity-prod", g, loc)
    v = res("connectivity-prod", g, "microsoft.network/virtualnetworks", f"vnet-hub-{short}", loc=loc); hubs[loc] = v
    sn_fw = subnet(v, "AzureFirewallSubnet", f"10.{i}.0.0/26", "connectivity-prod", g, loc)
    sn_gw = subnet(v, "GatewaySubnet", f"10.{i}.0.64/27", "connectivity-prod", g, loc)
    sn_bas = subnet(v, "AzureBastionSubnet", f"10.{i}.0.128/26", "connectivity-prod", g, loc)
    sn_dns = subnet(v, "snet-dns-resolver", f"10.{i}.1.0/28", "connectivity-prod", g, loc)
    fw = res("connectivity-prod", g, "microsoft.network/azurefirewalls", f"afw-hub-{short}", loc=loc, sku="AZFW_VNet"); edge(fw, sn_fw, "in subnet")
    for k in range(2):
        pip = res("connectivity-prod", g, "microsoft.network/publicipaddresses", f"pip-afw-{short}-{k+1}", loc=loc, sku="Standard"); edge(pip, fw, "public IP of")
    fwp = res("connectivity-prod", g, "microsoft.network/firewallpolicies", f"afwp-{short}", loc=loc)
    gw = res("connectivity-prod", g, "microsoft.network/virtualnetworkgateways", f"vgw-hub-{short}", loc=loc, sku="VpnGw2AZ"); edge(gw, sn_gw, "in subnet")
    pipg = res("connectivity-prod", g, "microsoft.network/publicipaddresses", f"pip-vgw-{short}", loc=loc, sku="Standard"); edge(pipg, gw, "public IP of")
    bas = res("connectivity-prod", g, "microsoft.network/bastionhosts", f"bas-hub-{short}", loc=loc, sku="Standard"); edge(bas, sn_bas, "in subnet")
    pipb = res("connectivity-prod", g, "microsoft.network/publicipaddresses", f"pip-bas-{short}", loc=loc, sku="Standard"); edge(pipb, bas, "public IP of")
    dnsr = res("connectivity-prod", g, "microsoft.network/dnsresolvers", f"dnspr-{short}", loc=loc); edge(dnsr, sn_dns, "in subnet")
    for site in (["Innsbruck","Wolfurt","Bolzano","Lana"] if short=="weu" else ["Dublin-DR","Zurich"]):
        lng = res("connectivity-prod", g, "microsoft.network/localnetworkgateways", f"lgw-{site.lower()}", loc=loc)
        con = res("connectivity-prod", g, "microsoft.network/connections", f"con-{site.lower()}-{short}", loc=loc)
        edge(con, gw, "connection of"); edge(con, lng, "connects to")
    for z in dnszones: edge(z, v, "DNS linked")
    fw_ip[loc] = fw
edge(hubs["westeurope"], hubs["northeurope"], "peered")
er = res("connectivity-prod", "rg-hub-weu", "microsoft.network/expressroutecircuits", "erc-wolfurt-weu", sku="Standard_MeteredData")
ergw = res("connectivity-prod", "rg-hub-weu", "microsoft.network/virtualnetworkgateways", "ergw-hub-weu", sku="ErGw1AZ")
edge(ergw, hubs["westeurope"] + "/subnets/gatewaysubnet", "in subnet")
cer = res("connectivity-prod", "rg-hub-weu", "microsoft.network/connections", "con-er-wolfurt"); edge(cer, ergw, "connection of"); edge(cer, er, "connects to")

# ---------------- management + identity
rg("management-prod", "rg-monitoring"); rg("management-prod", "rg-automation"); rg("identity-prod", "rg-identity")
law = res("management-prod", "rg-monitoring", "microsoft.operationalinsights/workspaces", "log-alpina-central", sku="PerGB2018")
res("management-prod", "rg-monitoring", "microsoft.insights/components", "appi-platform")
res("management-prod", "rg-automation", "microsoft.automation/automationaccounts", "aa-alpina-ops")
res("management-prod", "rg-monitoring", "microsoft.recoveryservices/vaults", "rsv-platform-weu", sku="Standard")
idv = res("identity-prod", "rg-identity", "microsoft.network/virtualnetworks", "vnet-identity-weu")
edge(idv, hubs["westeurope"], "peered")
sn_id = subnet(idv, "snet-dc", "10.10.0.0/27", "identity-prod", "rg-identity", "westeurope")
nsg_id = res("identity-prod", "rg-identity", "microsoft.network/networksecuritygroups", "nsg-dc"); edge(nsg_id, sn_id, "protects")
for k in range(4):
    vm = res("identity-prod", "rg-identity", "microsoft.compute/virtualmachines", f"vm-dc-{k+1:02d}", sku="Standard_D2s_v5", tags={"role": "domain-controller"})
    nic = res("identity-prod", "rg-identity", "microsoft.network/networkinterfaces", f"nic-vm-dc-{k+1:02d}"); edge(nic, vm, "NIC of"); edge(nic, sn_id, "in subnet")
    d = res("identity-prod", "rg-identity", "microsoft.compute/disks", f"vm-dc-{k+1:02d}-osdisk", sku="Premium_LRS"); edge(d, vm, "disk of")

# ---------------- landing zones
ENV_TAG = lambda s: "prod" if "prod" in s else "dev" if "dev" in s else "qa" if "qa" in s else "sandbox"
def pe_for(s, g, target, grp, sn_pe, zone):
    name = target.split("/")[-1][:40]
    pe = res(s, g, "microsoft.network/privateendpoints", f"pe-{name}-{grp}")
    pnic = res(s, g, "microsoft.network/networkinterfaces", f"pe-{name}-{grp}.nic.{random.randint(1000,9999)}")
    edge(pnic, pe, "NIC of"); edge(pnic, sn_pe, "in subnet"); edge(pe, sn_pe, "in subnet"); edge(pe, target, "private link to")
spoke_idx = 20
PLAN = {  # sub: (region, vms, aks, apps, sql, storages, vaults)
 "erp-prod": ("westeurope", 42, 1, 6, 3, 8, 3), "erp-dev": ("westeurope", 18, 0, 4, 2, 4, 2),
 "hr-prod": ("westeurope", 12, 0, 5, 2, 4, 2), "data-prod": ("westeurope", 20, 1, 3, 4, 14, 3), "data-dev": ("westeurope", 8, 0, 2, 2, 6, 1),
 "ropeway-iot-prod": ("westeurope", 16, 2, 8, 2, 10, 2),
 "web-prod": ("westeurope", 14, 2, 14, 3, 6, 3), "web-dev": ("westeurope", 6, 1, 8, 1, 3, 1), "api-prod": ("northeurope", 10, 1, 12, 2, 4, 2),
 "sap-prod": ("westeurope", 38, 0, 0, 0, 4, 2), "sap-qa": ("westeurope", 16, 0, 0, 0, 2, 1),
 "sandbox-01": ("westeurope", 4, 0, 2, 1, 2, 1), "sandbox-02": ("northeurope", 3, 0, 1, 0, 1, 0), "sandbox-03": ("westeurope", 2, 0, 1, 0, 1, 1),
}
SIZES = ["Standard_D2s_v5","Standard_D4s_v5","Standard_D8s_v5","Standard_E4s_v5","Standard_E8s_v5","Standard_B2ms","Standard_F4s_v2"]
app_names = ["portal","orders","billing","tickets","scheduler","telemetry","maintenance","inventory","reporting","auth","gateway","docs","search","notify","pricing","planner"]
for s,(loc,nvm,naks,napp,nsql,nst,nkv) in PLAN.items():
    nvm, nst = int(nvm * 1.8), int(nst * 1.8)
    short = REG[loc]; env = ENV_TAG(s); spoke_idx += 1
    gnet = f"rg-{s}-network"; gapp = f"rg-{s}-app"; gdata = f"rg-{s}-data"
    for g in (gnet, gapp, gdata): rg(s, g, loc)
    v = res(s, gnet, "microsoft.network/virtualnetworks", f"vnet-{s}-{short}", loc=loc)
    sandbox = s.startswith("sandbox")
    if not sandbox: edge(v, hubs[loc], "peered")
    rt = res(s, gnet, "microsoft.network/routetables", f"rt-{s}-to-afw", loc=loc)
    edge(rt, fw_ip[loc], "routes to") if not sandbox else None
    sn = {}
    for k,(nm,pref) in enumerate([("snet-app","0.0/24"),("snet-data","1.0/24"),("snet-pe","2.0/24"),("snet-aks","4.0/22"),("snet-integration","8.0/26")]):
        sn[nm] = subnet(v, nm, f"10.{spoke_idx}.{pref}", s, gnet, loc)
        nsg = res(s, gnet, "microsoft.network/networksecuritygroups", f"nsg-{s}-{nm[5:]}", loc=loc); edge(nsg, sn[nm], "protects")
        if not sandbox and nm != "snet-pe": edge(rt, sn[nm], "routes")
    # VMs (some behind load balancers)
    lbs = []
    for k in range(max(1, nvm // 12)):
        lb = res(s, gapp, "microsoft.network/loadbalancers", f"lbi-{s}-{k+1:02d}", loc=loc, sku="Standard"); lbs.append(lb)
    for k in range(nvm):
        prefix = "sap" if s.startswith("sap") else app_names[k % len(app_names)]
        vm = res(s, gapp, "microsoft.compute/virtualmachines", f"vm-{prefix}-{env}-{k+1:02d}", loc=loc, sku=random.choice(SIZES),
                 tags={"env": env, "owner": random.choice(["erp-team","web-team","data-team","sap-basis","iot-team","platform"]), "costcenter": f"CC{random.randint(1000,1099)}"})
        nic = res(s, gapp, "microsoft.network/networkinterfaces", f"nic-{vm.split('/')[-1]}", loc=loc)
        edge(nic, vm, "NIC of"); edge(nic, sn["snet-data" if prefix=="sap" or k % 5 == 0 else "snet-app"], "in subnet")
        if lbs and k % 3 == 0: edge(random.choice(lbs), nic, "balances")
        od = res(s, gapp, "microsoft.compute/disks", f"{vm.split('/')[-1]}-osdisk", loc=loc, sku="Premium_LRS"); edge(od, vm, "disk of")
        for dd in range(random.choice([0,1,1,2,3] if prefix=="sap" else [0,1,1,2])):
            d = res(s, gapp, "microsoft.compute/disks", f"{vm.split('/')[-1]}-data{dd+1}", loc=loc, sku=random.choice(["Premium_LRS","PremiumV2_LRS","StandardSSD_LRS"])); edge(d, vm, "disk of")
    for k in range(random.randint(1,4)):  # orphaned disks and PIPs
        res(s, gapp, "microsoft.compute/disks", f"disk-orphan-{k+1:02d}", loc=loc, sku="StandardSSD_LRS", tags={"note": "unattached"})
    if not sandbox and random.random() < .5:
        res(s, gnet, "microsoft.network/publicipaddresses", f"pip-unused-{s}", loc=loc, sku="Standard")
    # AKS
    for k in range(naks):
        aks = res(s, gapp, "microsoft.containerservice/managedclusters", f"aks-{s}-{k+1:02d}", loc=loc, sku="Standard"); edge(aks, sn["snet-aks"], "nodes in")
        ilb = res(s, gapp, "microsoft.network/loadbalancers", f"kubernetes-internal-{s}-{k+1}", loc=loc, sku="Standard")
        edge(ilb, sn["snet-aks"], "in subnet")
    # data services + private endpoints
    kvs, sts, sqls = [], [], []
    for k in range(nkv):
        kv = res(s, gdata, "microsoft.keyvault/vaults", f"kv-{s[:10]}-{short}-{k+1:02d}", loc=loc, sku="standard"); kvs.append(kv)
        pe_for(s, gdata, kv, "vault", sn["snet-pe"], "vault")
    for k in range(nst):
        st = res(s, gdata, "microsoft.storage/storageaccounts", f"st{s.replace('-','')[:12]}{k+1:02d}", loc=loc, sku=random.choice(["Standard_ZRS","Standard_LRS","Standard_GRS"])); sts.append(st)
        pe_for(s, gdata, st, "blob", sn["snet-pe"], "blob")
        if k % 3 == 0: pe_for(s, gdata, st, "file", sn["snet-pe"], "file")
    for k in range(nsql):
        srv = res(s, gdata, "microsoft.sql/servers", f"sql-{s}-{short}-{k+1:02d}", loc=loc); sqls.append(srv)
        pe_for(s, gdata, srv, "sqlServer", sn["snet-pe"], "sql")
        for d in range(random.randint(1,4)):
            res(s, gdata, "microsoft.sql/servers/databases", f"sqldb-{random.choice(app_names)}-{d+1}", loc=loc, parent=srv, sku=random.choice(["GP_Gen5_2","GP_Gen5_4","BC_Gen5_4","S1"]))
    if s in ("data-prod","ropeway-iot-prod"):
        pg = res(s, gdata, "microsoft.dbforpostgresql/flexibleservers", f"psql-{s}-{short}", loc=loc, sku="Standard_D4ds_v5"); pe_for(s, gdata, pg, "postgresqlServer", sn["snet-pe"], "pg")
        eh = res(s, gdata, "microsoft.eventhub/namespaces", f"evhns-{s}-{short}", loc=loc, sku="Standard"); pe_for(s, gdata, eh, "namespace", sn["snet-pe"], "eh")
    # app service
    if napp:
        plans = [res(s, gapp, "microsoft.web/serverfarms", f"asp-{s}-{short}-{k+1:02d}", loc=loc, sku=random.choice(["P1v3","P2v3","S1"])) for k in range(max(1, napp // 5))]
        for k in range(napp):
            app = res(s, gapp, "microsoft.web/sites", f"app-{app_names[k % len(app_names)]}-{s}", loc=loc, kind=random.choice(["app","app,linux","functionapp"]))
            edge(app, random.choice(plans), "hosted on"); edge(app, sn["snet-integration"], "integrated with")
            if not sandbox and k % 2 == 0: pe_for(s, gapp, app, "sites", sn["snet-pe"], "web")
    if s in ("web-prod","api-prod"):
        agsn = subnet(v, "snet-appgw", f"10.{spoke_idx}.9.0/26", s, gnet, loc)
        agw = res(s, gnet, "microsoft.network/applicationgateways", f"agw-{s}-{short}", loc=loc, sku="WAF_v2"); edge(agw, agsn, "in subnet")
        pipa = res(s, gnet, "microsoft.network/publicipaddresses", f"pip-agw-{s}", loc=loc, sku="Standard"); edge(pipa, agw, "public IP of")
        res(s, gnet, "microsoft.cdn/profiles", f"afd-{s}", loc="global", sku="Premium_AzureFrontDoor")
    res(s, gapp, "microsoft.insights/components", f"appi-{s}", loc=loc)
# an external partner peering
ext = "/subscriptions/99999999-9999-4999-8999-999999999999/resourcegroups/rg-partner/providers/microsoft.network/virtualnetworks/vnet-partner-ropeway-monitoring"
nodes[ext] = {"id": ext, "rid": ext, "name": "vnet-partner-ropeway-monitoring", "type": "microsoft.network/virtualnetworks", "cat": "network", "external": True}
edge(hubs["westeurope"], ext, "peered")

# ---------------- identities
FIRST = "Anna Lukas Sophie Elias Laura Jonas Lea Felix Julia Paul Sarah Noah Lisa Leon Hannah Maximilian Emma David Lena Simon Marie Tobias Katharina Florian Johanna Matteo Giulia Alessandro Chiara Luca Francesca Marco Elena Andrea Martina Stefan Petra Thomas Sabine Markus Nina Daniel Eva Martin Theresa Andreas Magdalena Christian Verena Michael".split()
LAST = "Gruber Huber Wagner Pichler Moser Mayer Hofer Steiner Berger Fischer Leitner Egger Rossi Bianchi Ferrari Mair Pircher Kofler Oberhofer Thaler Gasser Schwarz Weber Brunner Lechner Fuchs Eder Winkler Walder Kerschbaumer".split()
principals, azure, entra, members = [], [], [], {}
used = set()
def person():
    while True:
        f, l = random.choice(FIRST), random.choice(LAST)
        if (f,l) not in used: used.add((f,l)); break
    pid = gid(); principals.append({"id": pid, "name": f"{f} {l}", "upn": f"{f.lower()}.{l.lower()}@alpina-demo.example", "kind": "user"}); return pid
users = [person() for _ in range(520)]
guests = []
for k in range(24):
    pid = gid(); f,l = random.choice(FIRST), random.choice(LAST)
    principals.append({"id": pid, "name": f"{f} {l} (Partner)", "upn": f"{f.lower()}.{l.lower()}_partner.example#EXT#@alpina-demo.example", "kind": "user"}); guests.append(pid)
bg = []
for k in range(2):
    pid = gid(); principals.append({"id": pid, "name": f"Break Glass {k+1}", "upn": f"bg-admin-{k+1}@alpina-demo.example", "kind": "user"}); bg.append(pid)
def group(name, mems):
    pid = gid(); principals.append({"id": pid, "name": name, "kind": "group"}); members[pid] = mems; return pid
def sp(name):
    pid = gid(); principals.append({"id": pid, "name": name, "kind": "sp"}); return pid
admins = random.sample(users, 14); platform_team = admins[:8]
def az(p, role, scope, priv=None, cond=False, status="active", until=None):
    azure.append({"p": p, "role": role, "custom": role.endswith("(custom)"), "priv": role in ("Owner","Contributor","User Access Administrator","Role Based Access Control Administrator") if priv is None else priv,
                  "scope": scope, "cond": cond, "status": status, "until": until})
g_platform = group("grp-azure-platform-admins", platform_team)
g_netops = group("grp-azure-network-ops", random.sample(users, 9))
g_secops = group("grp-security-operations", random.sample(users, 11))
g_finops = group("grp-finops-readers", random.sample(users, 16))
g_all = group("grp-all-it-staff", random.sample(users, 120))
az(g_platform, "Owner", "mg:platform"); az(g_platform, "User Access Administrator", "mg:alpina"); az(g_platform, "Contributor", "mg:landingzones")
az(g_netops, "Network Contributor", sub["connectivity-prod"]["node"]); az(g_netops, "Reader", "mg:alpina")
az(g_secops, "Security Admin", "mg:alpina", priv=False); az(g_secops, "Reader", "mg:alpina")
az(g_finops, "Cost Management Reader", "mg:alpina", priv=False); az(g_finops, "Billing Reader", "mg:alpina", priv=False)
az(g_all, "Reader", "mg:sandbox")
az(bg[0], "Owner", "tenant"); az(bg[1], "User Access Administrator", "tenant")
for s in sub:
    if s in ("connectivity-prod","identity-prod","management-prod"): continue
    env = ENV_TAG(s)
    team = random.sample(users, random.randint(6, 30))
    go = group(f"grp-az-{s}-owners", random.sample(team, 2)); gc = group(f"grp-az-{s}-contributors", team[: max(3, len(team)//2)]); gr = group(f"grp-az-{s}-readers", team)
    node = sub[s]["node"]
    az(go, "Owner", node); az(gc, "Contributor", node); az(gr, "Reader", node)
    spn = sp(f"sp-gh-deploy-{s}"); az(spn, "Contributor", node)
    if env == "prod": az(sp(f"id-{s}-appgw-kv"), "Key Vault Secrets User", node, priv=False)
    # a few direct user assignments (the classic finding)
    for u in random.sample(users, random.randint(0, 3)):
        az(u, random.choice(["Contributor","Virtual Machine Contributor","Storage Blob Data Contributor","Owner"]), random.choice([node, node + f"/resourcegroups/rg-{s}-app"]))
    if s.startswith("sandbox"):
        for u in random.sample(users, 6): az(u, "Owner", node)
for g in ["rg-hub-weu","rg-hub-neu"]:
    az(sp("sp-terraform-connectivity"), "Contributor", sub["connectivity-prod"]["node"])
az(sp("sp-terraform-platform"), "Owner", "mg:alpina")
az(sp("sp-defender-scanner"), "Reader", "mg:alpina")
az(sp("sp-veeam-backup"), "Backup Contributor", sub["management-prod"]["node"], priv=False)
for u in random.sample(guests, 5): az(u, "Reader", random.choice([sub["erp-prod"]["node"], sub["ropeway-iot-prod"]["node"]]))
az(guests[0], "Contributor", sub["ropeway-iot-prod"]["node"])
az("0f3e2d1c-dead-4bee-8a11-000000000001", "Network Reader (custom)", sub["erp-prod"]["node"], priv=False)  # deleted identity
principals.append({"id": "0f3e2d1c-dead-4bee-8a11-000000000001", "name": "0f3e2d1c-dead-4bee-8a11-000000000001", "kind": "user", "unresolved": True})
for k in range(18): sp(f"app-{random.choice(app_names)}-{random.choice(['prod','dev'])}-{k+1:02d}")

# Entra roles
def NOW(days): return (datetime.datetime.now(datetime.timezone.utc).replace(microsecond=0) + datetime.timedelta(days=days)).strftime("%Y-%m-%dT%H:%M:%SZ")
ROLES_PRIV = ["Global Administrator","Privileged Role Administrator","Security Administrator","User Administrator","Exchange Administrator","Intune Administrator","Application Administrator","Conditional Access Administrator","Helpdesk Administrator","Authentication Administrator"]
def er(p, role, status="active", until=None): entra.append({"p": p, "role": role, "priv": role in ROLES_PRIV, "status": status, "scope": "/", "until": until})
for b in bg: er(b, "Global Administrator")
for u in admins[:3]: er(u, "Global Administrator", "eligible")
er(admins[3], "Global Administrator")                     # a standing GA: a finding
for u in admins[:5]: er(u, "Privileged Role Administrator", "eligible", NOW(365) if u == admins[0] else None)
for u in random.sample(users, 6): er(u, "Helpdesk Administrator")
for u in random.sample(users, 3): er(u, "User Administrator", "eligible")
for u in random.sample(users, 2): er(u, "Exchange Administrator", "eligible")
for u in random.sample(users, 2): er(u, "Intune Administrator")
er(g_secops, "Security Reader"); er(g_secops, "Security Administrator", "eligible")
er(admins[4], "Conditional Access Administrator", "eligible"); er(admins[5], "Application Administrator")
er(sp("sp-entra-provisioning"), "User Administrator")
for u in random.sample(users, 20): er(u, "Global Reader")
er(g_finops, "Billing Administrator", "eligible")

# ---------------- identity governance: PIM-eligible Azure roles, access packages, PIM for Groups
by_name = {p["name"]: p["id"] for p in principals}
for u in admins[:3]: az(u, "Owner", "mg:landingzones", status="eligible", until=NOW(365))
az(admins[5], "User Access Administrator", "mg:alpina", status="eligible")
az(admins[6], "Contributor", sub["erp-prod"]["node"], status="eligible", until=NOW(10))       # expires soon
az(admins[7], "Key Vault Administrator", sub["hr-prod"]["node"] + "/resourcegroups/rg-hr-prod-app", status="eligible", until=NOW(90))
b2b = gid(); principals.append({"id": b2b, "name": "Mario Rossi (Partner)", "upn": "mario.rossi@partner-ext.example", "kind": "user", "userType": "Guest"})
guests.append(b2b); az(b2b, "Reader", sub["ropeway-iot-prod"]["node"])
g_partner = group("grp-partner-ropeway-iot", random.sample(guests[1:-1], 6))
az(g_partner, "Reader", sub["ropeway-iot-prod"]["node"]); az(g_partner, "Storage Blob Data Reader", sub["ropeway-iot-prod"]["node"], priv=False)
g_erp_contrib = by_name["grp-az-erp-prod-contributors"]
packages = [
    {"id": gid(), "name": "Azure platform administrators", "catalog": "Azure platform", "groups": [g_platform]},
    {"id": gid(), "name": "ERP production contributors", "catalog": "Business applications", "groups": [g_erp_contrib]},
    {"id": gid(), "name": "Partner – Ropeway IoT (90 days)", "catalog": "Partners", "groups": [g_partner]},
    {"id": gid(), "name": "Intranet site members", "catalog": "Collaboration", "groups": []},     # SharePoint only: no group
]
pkg_assign = []   # delivered ones are expected; others must be filtered out
for u in platform_team[:6]: pkg_assign.append({"p": u, "pkg": packages[0]["id"], "state": "delivered", "until": NOW(180)})
for u in members[g_erp_contrib][:3]: pkg_assign.append({"p": u, "pkg": packages[1]["id"], "state": "delivered", "until": None})
for k, u in enumerate(members[g_partner]): pkg_assign.append({"p": u, "pkg": packages[2]["id"], "state": "delivered", "until": NOW(7 if k == 0 else 60)})
pkg_assign.append({"p": users[-1], "pkg": packages[2]["id"], "state": "expired", "until": NOW(-3)})
requester = users[-2]
pkg_requests = [{"p": requester, "pkg": packages[0]["id"], "state": "pendingApproval"},
                {"p": guests[-2], "pkg": packages[2]["id"], "state": "pendingApproval"},
                {"p": users[-3], "pkg": packages[1]["id"], "state": "delivered"}]
group_elig = [{"g": g_platform, "p": users[-4], "access": "member", "until": NOW(120)},
              {"g": g_platform, "p": users[-5], "access": "member", "until": None},
              {"g": g_secops, "p": users[-6], "access": "member", "until": NOW(30)},
              {"g": g_secops, "p": users[-7], "access": "owner", "until": None}]

# policies
policies = []
def pol(name, scope, init=False, enforce="Default", nc=0):
    policies.append({"id": gid(), "name": name, "scope": scope, "initiative": init, "enforce": enforce,
                     "nonCompliant": {s["id"]: random.randint(0, nc) for s in sub.values()} if nc else None})
pol("Microsoft cloud security benchmark", "tenant", True, "DoNotEnforce", 60)
pol("Allowed locations (West/North Europe)", "mg:alpina", nc=3); pol("Require Owner and CostCenter tags on resource groups", "mg:alpina", nc=8)
pol("Deploy diagnostic settings to Log Analytics", "mg:alpina", True, nc=20); pol("Deny public IP on NICs", "mg:landingzones", nc=2)
pol("Enforce private DNS zones for private endpoints", "mg:corp", True, nc=4); pol("Deny storage account public network access", "mg:corp", nc=5)
pol("Require TLS 1.2 on SQL and storage", "mg:landingzones", nc=6); pol("Kubernetes cluster pod security baseline", "mg:online", True, nc=12)
pol("Configure Azure Defender for servers", "mg:alpina", nc=0); pol("Deny subnet without NSG", "mg:landingzones", nc=1)
pol("Enable Azure Backup for VMs tagged prod", "mg:corp", nc=9); pol("Deny RDP/SSH from Internet", "mg:landingzones", nc=0)
pol("SAP: Allowed VM SKUs", "mg:sap", nc=2); pol("Sandbox budget and auto-shutdown", "mg:sandbox", enforce="DoNotEnforce", nc=4)
pol("Deny all resource creation", "mg:decommissioned")

ca = [{"name": n, "state": st} for n, st in [("CA001: Require MFA for all users","enabled"),("CA002: Block legacy authentication","enabled"),
      ("CA003: Require phishing-resistant MFA for admins","enabled"),("CA004: Require compliant device for Azure management","enabledForReportingButNotEnforced"),
      ("CA005: Block access from outside EU","enabled"),("CA006: Require MFA for guests","enabled"),("CA007: Sign-in risk policy","enabled"),
      ("CA008: Session lifetime 8h for admins","enabled"),("CA009: Block device code flow","enabledForReportingButNotEnforced"),("CA010: Legacy test policy","disabled")]]


# =====================================================================================================
# Turn the model into (1) raw API responses and (2) the result Rootline must produce.
# =====================================================================================================
HIER = ("tenant", "mg", "sub", "rg")
N = nodes
out_edges = {}
for e in edges:
    out_edges.setdefault(e["s"], []).append(e)
in_edges = {}
for e in edges:
    in_edges.setdefault(e["t"], []).append(e)
def outs(i, rel): return [e["t"] for e in out_edges.get(i, []) if e["rel"] == rel]
def ins(i, rel):  return [e["s"] for e in in_edges.get(i, []) if e["rel"] == rel]
def first(xs): return xs[0] if xs else ""
def ntype(i): return N[i]["type"] if i in N else ""

# private IPs: firewalls get .4 in their subnet, NICs get addresses in theirs
ip_of = {}
ip_counter = {}
for loc, fw in fw_ip.items():
    ip_of[fw] = f"10.{0 if loc == 'westeurope' else 1}.0.4"
    ip_counter[first(outs(fw, "in subnet"))] = 4                    # the firewall holds .4 in its subnet
import ipaddress
def next_ip(subnet_id):
    net = ipaddress.ip_network(N[subnet_id].get("prefix", "10.99.0.0/24"), strict=False)
    ip_counter[subnet_id] = ip_counter.get(subnet_id, 3) + 1      # Azure reserves the first four addresses
    return str(net.network_address + ip_counter[subnet_id])

mg_nodes = [n for n in N.values() if n["cat"] == "mg"]
def mg_name(i): return T if i == "tenant" else i[3:]
def mg_chain(i):  # immediate parent … tenant root
    chain = []
    while i:
        chain.append({"name": mg_name(i), "displayName": "Tenant Root Group" if i == "tenant" else N[i]["name"]})
        if i == "tenant": break
        i = N[i].get("parent", "tenant")
    return chain

raw = {"tenantId": T, "account": "admin@alpina-demo.example", "arg": {}, "arm": {"mg": {}, "denied": ["decommissioned"]}, "graph": {}}
A = raw["arg"]
A["mgs"] = [{"name": T, "displayName": "Tenant Root Group", "parent": ""}] + \
           [{"name": mg_name(n["id"]), "displayName": n["name"], "parent": mg_name(n.get("parent", "tenant"))} for n in mg_nodes]
A["subs"] = [{"subscriptionId": n["sub"], "name": n["name"], "chain": mg_chain(n["parent"])} for n in N.values() if n["cat"] == "sub"]
A["rgs"] = [{"id": n["id"], "rid": n["id"], "name": n["name"], "subscriptionId": n["sub"], "location": n.get("loc", ""), "tags": n.get("tags")}
            for n in N.values() if n["cat"] == "rg"]
res_nodes = [n for n in N.values() if n["cat"] not in HIER and n["cat"] != "subnet" and not n.get("external")]
A["resources"] = [{"id": n["id"], "rid": n["id"], "name": n["name"], "type": n["type"], "subscriptionId": n["sub"], "resourceGroup": n["rg"],
                   "location": n.get("loc", ""), "sku": n.get("sku", ""), "kind": n.get("kind", ""), "tags": n.get("tags")} for n in res_nodes]
# noise the app must filter out: VM extensions and private DNS VNet links (links are read separately as connections)
dns_rows = []
for n in [n for n in res_nodes if n["type"] == "microsoft.compute/virtualmachines"][:40]:
    A["resources"].append({"id": n["id"] + "/extensions/azuremonitoragent", "rid": n["id"] + "/extensions/AzureMonitorAgent", "name": "AzureMonitorAgent",
                           "type": "microsoft.compute/virtualmachines/extensions", "subscriptionId": n["sub"], "resourceGroup": n["rg"], "location": n["loc"], "sku": "", "kind": "", "tags": None})
for e in [e for e in edges if e["rel"] == "DNS linked"]:
    link = f"{e['s']}/virtualnetworklinks/link-{e['t'].split('/')[-1]}"
    A["resources"].append({"id": link, "rid": link, "name": link.split('/')[-1], "type": "microsoft.network/privatednszones/virtualnetworklinks",
                           "subscriptionId": N[e["s"]]["sub"], "resourceGroup": N[e["s"]]["rg"], "location": "global", "sku": "", "kind": "", "tags": None})
    dns_rows.append({"s": e["s"], "t": e["t"]})
A["dns"] = dns_rows
A["subnets"] = [{"vnet": n["parent"], "id": n["id"], "rid": n["id"], "name": n["name"], "prefix": n.get("prefix", ""),
                 "nsg": first(ins(n["id"], "protects")), "rt": first(ins(n["id"], "routes")), "nat": first(ins(n["id"], "NAT for")),
                 "subscriptionId": n["sub"], "resourceGroup": n["rg"], "location": n.get("loc", "")} for n in N.values() if n["cat"] == "subnet" and not n.get("external")]
A["nic"] = []
for n in [n for n in res_nodes if n["type"] == "microsoft.network/networkinterfaces"]:
    sn = first(outs(n["id"], "in subnet")); owner = first(outs(n["id"], "NIC of"))
    ip = next_ip(sn) if sn else ""
    lb = first([x for x in ins(n["id"], "balances")])
    A["nic"].append({"s": n["id"], "owner": owner, "nsg": first(ins(n["id"], "protects")), "subnet": sn,
                     "pip": first(ins(n["id"], "public IP of")), "lb": (lb + "/backendaddresspools/be-pool") if lb else "", "agw": "", "ip": ip})
seen_peer = set()
A["peer"] = []
for e in [e for e in edges if e["rel"] == "peered"]:
    A["peer"].append({"s": e["s"], "t": e["t"]})
    if not N.get(e["t"], {}).get("external"): A["peer"].append({"s": e["t"], "t": e["s"]})   # both sides report the peering
A["pip"] = [{"s": n["id"], "cfg": (first(outs(n["id"], "public IP of")) + "/ipconfigurations/ipconfig1") if outs(n["id"], "public IP of") else "", "nat": ""}
            for n in res_nodes if n["type"] == "microsoft.network/publicipaddresses"]
A["pe"] = [{"s": n["id"], "subnet": first(outs(n["id"], "in subnet")), "target": first(outs(n["id"], "private link to"))}
           for n in res_nodes if n["type"] == "microsoft.network/privateendpoints"]
A["disk"] = [{"s": e["s"], "t": e["t"]} for e in edges if e["rel"] == "disk of"]
A["web"] = [{"s": n["id"], "plan": first(outs(n["id"], "hosted on")), "subnet": first(outs(n["id"], "integrated with"))}
            for n in res_nodes if n["type"] == "microsoft.web/sites"]
A["aks"] = [{"s": e["s"], "t": e["t"]} for e in edges if e["rel"] == "nodes in"]
A["vmss"] = []
A["conn"] = [{"s": n["id"], "gw": first(outs(n["id"], "connection of")), "peer": first(outs(n["id"], "connects to"))}
             for n in res_nodes if n["type"] == "microsoft.network/connections"]
cfg = {"ipConfigurations": [], "gatewayIPConfigurations": [], "frontendIPConfigurations": []}
for e in [e for e in edges if e["rel"] == "in subnet"]:
    t = ntype(e["s"])
    if t in ("microsoft.network/networkinterfaces", "microsoft.network/privateendpoints"): continue
    prop = "gatewayIPConfigurations" if t == "microsoft.network/applicationgateways" else "frontendIPConfigurations" if t == "microsoft.network/loadbalancers" else "ipConfigurations"
    cfg[prop].append({"s": e["s"], "subnet": e["t"], "pip": "", "ip": ip_of.get(e["s"], next_ip(e["t"]))})
for k, v in cfg.items(): A["cfg-" + k] = v
A["routes"] = [{"s": e["s"], "hop": ip_of[e["t"]]} for e in edges if e["rel"] == "routes to" and ntype(e["s"]) == "microsoft.network/routetables"]

# ---------- RBAC: GUIDs for roles, scopes in ARM form, split between Resource Graph (subscriptions) and ARM (management groups)
role_guid = {}
def rguid(name):
    if name not in role_guid: role_guid[name] = str(uuid.UUID(int=random.getrandbits(128)))
    return role_guid[name]
def raw_scope(s):
    if s == "tenant": return "/"
    if s.startswith("mg:"): return f"/providers/Microsoft.Management/managementGroups/{s[3:]}"
    return s
pkind = {p["id"]: p["kind"] for p in principals}
ptype = {"user": "User", "group": "Group", "sp": "ServicePrincipal"}
A["roleassign"], mg_assign, elig_raw = [], [], []
for i, a in enumerate(azure):
    if a["status"] == "eligible":
        props = {"principalId": a["p"], "principalType": ptype.get(pkind.get(a["p"]), "User"), "scope": raw_scope(a["scope"]),
                 "roleDefinitionId": f"/providers/Microsoft.Authorization/roleDefinitions/{rguid(a['role'])}", "memberType": "Direct"}
        if a["until"]: props["endDateTime"] = a["until"]
        elig_raw.append((a["scope"], {"id": f"{raw_scope(a['scope'])}/providers/Microsoft.Authorization/roleEligibilityScheduleInstances/{uuid.UUID(int=random.getrandbits(128))}", "properties": props}))
        continue
    row = {"principalId": a["p"], "principalType": ptype.get(pkind.get(a["p"]), "User"), "scope": raw_scope(a["scope"]).lower(), "condition": ""}
    guid = rguid(a["role"])
    if a["scope"] == "tenant" or a["scope"].startswith("mg:"):
        row["roleDefinitionId"] = f"/providers/Microsoft.Authorization/roleDefinitions/{guid}"
        row["id"] = f"{raw_scope(a['scope'])}/providers/Microsoft.Authorization/roleAssignments/{uuid.UUID(int=random.getrandbits(128))}"
        mg_assign.append((a["scope"], row))
    else:
        row["roleDefinitionId"] = f"{a['scope']}/providers/microsoft.authorization/roledefinitions/{guid}".lower()
        row["id"] = f"{a['scope']}/providers/microsoft.authorization/roleassignments/{uuid.UUID(int=random.getrandbits(128))}"
        A["roleassign"].append(row)
custom = [r for r in role_guid if r.endswith("(custom)")]
A["roledefs_custom"] = [{"name": role_guid[r], "roleName": r, "roleType": "CustomRole"} for r in custom]
raw["arm"]["roledefs_builtin"] = [{"name": role_guid[r], "properties": {"roleName": r, "type": "BuiltInRole"}} for r in role_guid if r not in custom] + \
    [{"name": str(uuid.UUID(int=random.getrandbits(128))), "properties": {"roleName": f"Unused Built-in Role {k}", "type": "BuiltInRole"}} for k in range(140)]

def mg_ancestors(scope):  # scope and everything above it, for atScope()
    out = []
    i = scope
    while i:
        out.append(i)
        if i == "tenant": break
        i = N[i].get("parent", "tenant")
    return out
all_mgs = ["tenant"] + [n["id"] for n in mg_nodes]
policies_raw = []
def slug(s): return "".join(c if c.isalnum() else "-" for c in s.lower()).strip("-")[:40]
for p in policies:
    rid = f"{raw_scope(p['scope'])}/providers/Microsoft.Authorization/policyAssignments/{slug(p['name'])}"
    p["id"] = rid.lower()
    policies_raw.append((p["scope"], {"id": rid, "properties": {"displayName": p["name"], "scope": raw_scope(p["scope"]),
        "policyDefinitionId": f"/providers/Microsoft.Authorization/{'policySetDefinitions' if p['initiative'] else 'policyDefinitions'}/{slug(p['name'])}",
        "enforcementMode": p["enforce"]}}))
for m in all_mgs:
    name = mg_name(m)
    raw["arm"]["mg"][name] = {
        "roleAssignments": [{"id": r["id"], "properties": {k: r[k] for k in ("principalId", "principalType", "roleDefinitionId", "scope", "condition")}}
                            for sc, r in mg_assign if sc in mg_ancestors(m) or sc == "tenant"],
        "policyAssignments": [pr for sc, pr in policies_raw if (sc in mg_ancestors(m)) and (sc == "tenant" or sc.startswith("mg:"))],
        "eligibility": [e for sc, e in elig_raw if sc in mg_ancestors(m)]}
# per subscription: eligibility at, above and below the subscription (like the real API without a filter)
raw["arm"]["subElig"] = {}
for sname, sv in sub.items():
    node = sv["node"]; above = mg_ancestors(N[node]["parent"])
    raw["arm"]["subElig"][sv["id"]] = [e for sc, e in elig_raw if sc in above or sc == node or sc.startswith(node + "/")]
A["policyassign"] = [{"id": pr["id"].lower(), "name": pr["properties"]["displayName"], "scope": pr["properties"]["scope"].lower(),
                      "definitionId": pr["properties"]["policyDefinitionId"].lower(), "enforcement": pr["properties"]["enforcementMode"]}
                     for sc, pr in policies_raw if not (sc == "tenant" or sc.startswith("mg:"))]
A["policystates"] = []
for p in policies:
    nc = {k: v for k, v in (p["nonCompliant"] or {}).items() if v}
    for sid, n in nc.items(): A["policystates"].append({"a": p["id"], "subscriptionId": sid, "n": n})
    p["nonCompliant"] = nc or None

# ---------- Entra (Microsoft Graph)
G = raw["graph"]
G["org"] = {"id": T, "displayName": "Alpina Demo (fictional)"}
dir_role = {r: str(uuid.UUID(int=random.getrandbits(128))) for r in sorted({a["role"] for a in entra} | {"Global Reader", "Directory Readers", "Guest Inviter"})}
G["roleDefinitions"] = [{"id": i, "displayName": r, "isPrivileged": r in ROLES_PRIV} for r, i in dir_role.items()]
G["roleAssignments"] = [{"id": str(k), "principalId": a["p"], "roleDefinitionId": dir_role[a["role"]], "directoryScopeId": "/"} for k, a in enumerate(entra) if a["status"] == "active"]
G["roleEligibilitySchedules"] = [dict({"id": str(k), "principalId": a["p"], "roleDefinitionId": dir_role[a["role"]], "directoryScopeId": "/"},
                                    **({"scheduleInfo": {"startDateTime": NOW(-30), "expiration": {"type": "afterDateTime", "endDateTime": a["until"]}}} if a["until"] else
                                       {"scheduleInfo": {"startDateTime": NOW(-30), "expiration": {"type": "noExpiration"}}}))
                                  for k, a in enumerate(entra) if a["status"] == "eligible"]
odata = {"user": "#microsoft.graph.user", "group": "#microsoft.graph.group", "sp": "#microsoft.graph.servicePrincipal"}
def is_guest(p): return p["kind"] == "user" and ("#EXT#" in (p.get("upn") or "") or p.get("userType") == "Guest")
G["objects"] = {p["id"]: dict({"@odata.type": odata[p["kind"]], "id": p["id"], "displayName": p["name"], "userPrincipalName": p.get("upn")},
                              **({"userType": "Guest" if is_guest(p) else "Member"} if p["kind"] == "user" else {}))
                for p in principals if not p.get("unresolved")}
G["members"] = {g: [G["objects"][m] for m in ms] for g, ms in members.items()}
G["accessPackages"] = [{"id": k["id"], "displayName": k["name"], "catalog": {"id": gid(), "displayName": k["catalog"]}} for k in packages]
G["accessPackageDetail"] = {k["id"]: {"id": k["id"], "displayName": k["name"], "resourceRoleScopes":
    [{"id": gid(), "role": {"displayName": "Member", "originSystem": "AadGroup"}, "scope": {"originId": g, "originSystem": "AadGroup"}} for g in k["groups"]] +
    ([{"id": gid(), "role": {"displayName": "Members", "originSystem": "SharePointOnline"}, "scope": {"originId": "https://alpina.sharepoint.example/sites/intranet", "originSystem": "SharePointOnline"}}] if not k["groups"] else [])}
    for k in packages}
G["pkgAssignments"] = [dict({"id": gid(), "state": a["state"], "target": {"objectId": a["p"]}, "accessPackage": {"id": a["pkg"]}},
                            **({"schedule": {"expiration": {"type": "afterDateTime", "endDateTime": a["until"]}}} if a["until"] else {"schedule": {"expiration": {"type": "noExpiration"}}}))
                       for a in pkg_assign]
G["pkgRequests"] = [{"id": gid(), "state": r["state"], "createdDateTime": NOW(-2), "requestor": {"objectId": r["p"]}, "accessPackage": {"id": r["pkg"]}} for r in pkg_requests]
G["groupElig"] = {}
for e in group_elig:
    G["groupElig"].setdefault(e["g"], []).append(dict({"id": gid(), "groupId": e["g"], "principalId": e["p"], "accessId": e["access"], "memberType": "direct"},
                                                        **({"endDateTime": e["until"]} if e["until"] else {})))
G["ca"] = [{"id": str(k), "displayName": c["name"], "state": c["state"]} for k, c in enumerate(ca)]

# ---------- expected result
referenced = {a["p"] for a in azure} | {a["p"] for a in entra}
referenced |= {m for g, ms in members.items() if g in referenced for m in ms}
exp_pkg_assign = [a for a in pkg_assign if a["state"] == "delivered"]
exp_requests = [r for r in pkg_requests if r["state"] == "pendingApproval"]
exp_group_elig = [e for e in group_elig if e["access"] == "member" and e["g"] in referenced]
referenced |= {a["p"] for a in exp_pkg_assign} | {r["p"] for r in exp_requests} | {e["p"] for e in exp_group_elig}
exp_principals = [p for p in principals if p["id"] in referenced]
expected = {
    "tenantName": G["org"]["displayName"],
    "nodes": [{"id": n["id"], "type": n["type"], "parent": n.get("parent"), "name": n["name"], "external": bool(n.get("external"))} for n in N.values()],
    "edges": edges,
    "principals": [{"id": p["id"], "kind": p["kind"], "name": p["name"] if not p.get("unresolved") else p["id"]} for p in exp_principals],
    "guests": [p["id"] for p in exp_principals if is_guest(p)],
    "azure": [{"p": a["p"], "role": a["role"], "scope": a["scope"], "status": a["status"], "until": a["until"] or ""} for a in azure if a["scope"][3:] not in raw["arm"]["denied"]],
    "entra": [{"p": a["p"], "role": a["role"], "status": a["status"], "priv": a["role"] in ROLES_PRIV, "until": a["until"] or ""} for a in entra],
    "packages": [{"id": k["id"], "name": k["name"], "catalog": k["catalog"], "groups": sorted(k["groups"])} for k in packages],
    "packageAssignments": [{"p": a["p"], "pkg": a["pkg"], "until": a["until"] or ""} for a in exp_pkg_assign],
    "packageRequests": [{"p": r["p"], "pkg": r["pkg"]} for r in exp_requests],
    "groupEligible": [{"g": e["g"], "p": e["p"], "until": e["until"] or ""} for e in exp_group_elig],
    "members": {g: ms for g, ms in members.items() if g in referenced},
    "policies": [{"id": p["id"], "name": p["name"], "scope": p["scope"], "enforce": p["enforce"], "initiative": p["initiative"], "nonCompliant": p["nonCompliant"]}
                 for p in policies if p["scope"][3:] not in raw["arm"]["denied"]],
    "ca": [c["name"] for c in ca],
    "deniedWarning": "decommissioned",
}
out = sys.argv[1] if len(sys.argv) > 1 else "."
os.makedirs(out, exist_ok=True)
json.dump(raw, open(os.path.join(out, "raw.json"), "w"))
json.dump(expected, open(os.path.join(out, "expected.json"), "w"))
print(f"Fake tenant: {len(res_nodes)} resources, {len(edges)} connections, {len(exp_principals)} identities in use "
      f"({len(principals)} in directory), {len(azure)} Azure role assignments, {len(entra)} Entra role assignments, {len(policies)} policies -> {out}")
