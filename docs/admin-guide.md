# Admin guide

How to deploy, set up and look after the app. For a quick start, see the [README](../README.md).

## Deploying to a Linux VM

1. Create an Ubuntu Server 26.04 LTS VM (2–4 vCPU, 4–8 GB RAM, ~100 GB disk) with a static IP.
2. Ask the network team for an internal DNS A record, e.g. `pcserv.example.org` → VM IP.
3. Install Docker: https://docs.docker.com/engine/install/ubuntu/
4. Copy this repository to the VM, then:

   ```bash
   cd deploy
   cp .env.example .env   # set PCSERV_HOSTNAME and TZ
   docker compose up -d
   ```

5. Browse to `https://pcserv.example.org` and complete setup.

## Settings for your organization

The [README](../README.md#configuration) lists the settings. For local runs without Docker, use `src/PcServ.Web/district.json` instead, following [district.example.json](district.example.json). Restart the app after changing them.

Under **Manage → Repair lists**, add the device models you own before checking in repairs. The app starts with a
list of common repair reasons and no models.

## Getting back into an account

An Admin can reset anyone's password under **Manage → Users**. If nobody can sign in as an Admin, run these on the server, from the `deploy` folder:

```bash
docker compose exec web dotnet PcServ.Web.dll --list-admins
docker compose exec web dotnet PcServ.Web.dll --reset-password <username>
```

The second one prints a temporary password, which must be changed at sign-in. It also clears lockouts, re-enables the account if it was disabled, and turns off two-factor sign-in in case the phone is gone. Running locally, use `dotnet run --project src/PcServ.Web -- --reset-password <username>`.

## HTTPS certificate

The Caddyfile uses `tls internal` by default (Caddy's own certificate authority). Browsers
will warn until Caddy's root certificate is trusted on staff computers. Options:

- **Trust Caddy's root:** `docker compose cp caddy:/data/caddy/pki/authorities/local/root.crt .`
  and deploy it as a trusted root (e.g. via Group Policy or SCCM).
- **Use a district certificate:** replace `tls internal` with `tls <cert> <key>` and mount the files.
- **Let's Encrypt via DNS challenge:** works without opening ports; needs a Caddy build with
  your DNS provider's plugin.

Passkeys require a trusted HTTPS certificate.

## Importing many maps

**Manage → Import maps** (Admins only) brings in many maps at once from a CSV list you can edit in Excel. Keep
real maps and the list out of git. On a server, put them in `deploy/import/`, with the list named `map-import.csv`.

| Column | Meaning |
|---|---|
| `File` | Path to the PDF, PNG or JPEG, relative to the list |
| `Site` | Site name. It's created if it doesn't exist yet |
| `Code` | Optional short code for a new site |
| `SiteType` | `Elementary`, `Middle`, `High`, `Office`, `Warehouse` or `Other` |
| `MapName` | Name shown in the app |
| `Kind` | `SitePlan`, `FloorPlan` or `Other` |
| `Floor` | Optional floor number |
| `Visibility` | Lowest role that can open the map: `Viewer`, `Tech`, `Manager` or `Admin` |

Text boxes in a PDF become layers automatically. Running an import again skips anything already imported. Importing
is turned off while **Show test version** is on.

## Network layers from Wi-Fi maps

If your Wi-Fi maps are flat PDFs, `tools/extract-network-layers.cs` reads their access points and closets using OCR, on your own PC. It expects a particular drawing style: names in red ending in a three-digit model, and symbols in set colors. The top of the file explains its options and how to adjust it for other maps.

Put its output in a `Network_Layers` folder beside the import list, then use step 3 on **Manage → Import maps**. Each map's image is redrawn without its colored markings, so switching a layer off really removes them.

OCR makes mistakes, so anything uncertain is marked for checking: it's circled on the map and listed under **Find on this map**, where Techs and above can click to correct it. Not every misread gets flagged, so spot-check important maps. Keep the output out of git, because it describes your real network.

## Showing network layers on plans

**Align layers** (Managers and above) lines up each map that has network layers with the other maps at that site.
It only suggests: nothing shows on a plan until someone confirms it on the check page, where a plan is drawn in grey
with the other map's lines in red. If it doesn't line up, click matching points on both maps. Layers are linked rather
than copied, so a corrected name shows everywhere.

One alignment moves the whole drawing together. If a building is drawn in a different spot on each map, items in that
building won't land in the right place, so check portables closely.

## Backups

All app data is in the `pcserv_pcserv-data` Docker volume (database + sign-in keys). Back up
the VM with Proxmox Backup Server, and/or copy the volume nightly. Stop the app first: SQLite
keeps recent changes in a separate file, so copying it while running can give a broken backup.

```bash
cd deploy
docker compose stop web
docker run --rm -v pcserv_pcserv-data:/data:ro -v "$PWD":/backup alpine \
  tar czf /backup/pcserv-data-$(date +%F).tar.gz -C /data .
docker compose start web
```

To restore a backup (this replaces the current data):

```bash
cd deploy
docker compose stop web
docker run --rm -v pcserv_pcserv-data:/data -v "$PWD":/backup alpine \
  sh -c 'rm -rf /data/* && tar xzf /backup/pcserv-data-YYYY-MM-DD.tar.gz -C /data'
docker compose start web
```

Keep the volume on the VM's local disk. SQLite isn't reliable on network shares such as NFS.

## Updating

```bash
git pull            # or copy the new files over
cd deploy
docker compose pull
docker compose up -d
```

Database migrations apply automatically on startup. Take a Proxmox snapshot first.

To build the image yourself instead of using the published one, for example after changing the code, run `docker compose up -d --build`.

## Moving the Docker image to a machine without internet access

Save the image on one machine, and load it on the other:

```bash
docker save ghcr.io/hasibziai/tech-team-portal:latest | gzip > tech-team-portal.tar.gz   # on the first machine
docker load < tech-team-portal.tar.gz                                  # on the second machine
```
