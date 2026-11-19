# Deficit Snatcher

Watches stores for a PS5 Pro (or anything else) and buys it automatically when it's in stock within your price range.

- **DeficitSnatcher.Api**: ASP.NET Core (.NET 11) + Postgres. Checks the sites, decides, buys (headless Chromium via
  Playwright), sends notifications, and runs deploys.
- **DeficitSnatcher.Web**: Next.js exported as a static site, served by nginx. nginx also passes `/api` to the API, so
  the browser only ever talks to one address.
- **docker-compose.yml**: all three (database, API, site) in one `docker compose up`.

One account, no sign-ups: the username and password come from the environment (GitHub secrets on the server).

## What's on the site

| Tab | |
|---|---|
| **Products** → a product, or one of its pages | Its **Logs**: scans (with a screenshot and the page's source when something changed), checkouts, activity, and AI calls. |
| **Dashboard** | The auto-buy switch, each product's sites with price, stock and verdict, recent activity and checkouts. |
| **Price ranges** | Per product: min and max price (shipping included), units to buy in all and per order, title keywords. |
| **Sites** | Store pages to watch: Test, Check now, selectors, checkout steps, cookies, price history, dry-run / buy now. |
| **Scans** | Every check of every site, found or not: price, stock, verdict, error, and what was done (checkout started, or why not). Filter by product, site, result (in stock / out / in range / checkout ran / errors), time, price, search. |
| **Checkouts** | Every checkout, real or dry run. Each one has: why it ran (the scan, price, limits at that moment), a step-by-step timeline with timing and a screenshot after each step, the full error (Playwright's call log), the browser's console and network errors, a full-page screenshot of where it ended, the page's HTML, and the raw log. |
| **Activity** | Everything that happened (alerts, price drops, checkouts, failing sites, settings changes), filterable. |
| **Settings** | Auto-buy (armed, dry run, budget, retry wait), checking (interval, jitter, per-store spacing, timeouts), notifications (Discord, Telegram, any webhook such as ntfy.sh), history retention, checkout profile (address, card). |
| **Deploy** | What's running (commit, build time), commits on `main` not deployed yet, Deploy button, live progress of each step, cancel, links to the full GitHub logs. |

## AI (ChatGPT)

Turned on by the `OPENAI_API_KEY` secret; model and options in Settings. Every call is in each page's Logs → AI.

- **Auto-fill with AI** (adding a page): reads the page and fills in the store name, how to read price and stock, and
  whether it really is the PS5 Pro; the settings are checked before they're offered.
- **Record checkout with AI**: ChatGPT goes through the store's checkout itself (dry run) and the steps it takes are saved.
- **Fixes broken checkouts**: when a saved step fails (the store changed its page), the AI takes over from that screen,
  finishes, and saves the corrected steps. Pages without steps are bought by the AI directly.
- **Fixes reading**: when a page keeps failing or showing no price or stock, the AI re-learns it (at most every 6 hours).
- The AI never sees the card or address: it writes `{{card.number}}`-style placeholders that are filled in locally, and
  gets no screenshots once card details are typed. Before a real order it must read the order total, which must be within
  the maximum price plus the allowed tax (15% by default), or the order isn't placed.

## How buying works

A site is a **match** when it's in stock, the title fits the keywords, and price + shipping is within the range. A match
is reported once, and if the site has **auto-buy** on and auto-buy is **armed**, its **checkout steps** run in headless
Chromium (goto, click, fill, select, type, wait, expectText…), filling in the checkout profile (`{{email}}`,
`{{card.number}}`, …). The step that places the order is marked **final**.

- **Dry run** (on by default) runs every step up to the final one, then stops. Test each store with it first.
- Before every purchase, under one lock: armed, units left for the product, total budget, retry wait per site.
- A real checkout cut off by a restart is **Interrupted** and keeps counting against the limits until you mark it
  as not bought, so a restart can never cause a second order.
- Card number and CVV are masked in every log, error and saved page; screenshots, page HTML, the card, cookies and
  notification tokens are stored encrypted (AES-GCM, `Secrets__Key`).

**Limits.** Big retailers (Best Buy, Walmart, Amazon) use bot protection, captchas and 3-D Secure. Checks can get
blocked (try Browser mode, a longer interval, or your cookies) and a checkout can stop at a captcha or bank prompt;
the checkout's screenshots and error show exactly where. Shopify and smaller stores work best.

## Run it with Docker

```bash
cp .env.example .env        # set Auth__Username, Auth__Password, Jwt__SigningKey, POSTGRES_PASSWORD
docker compose up -d --build
```

The site is on `http://localhost:8090` (`WEB_PORT`).

## Develop

```bash
cp .env.example .env
docker compose -f docker-compose.yml -f docker-compose.dev.yml up -d db
cd DeficitSnatcher.Api
dotnet run -- install-browser   # once: headless Chromium
dotnet run                      # http://localhost:5210, reads ../.env
```

```bash
cd DeficitSnatcher.Web
npm install
NEXT_PUBLIC_API_URL=http://localhost:5210 npm run dev
```

Migrations: `dotnet ef migrations add <Name> -o Data/Migrations` in `DeficitSnatcher.Api`.

## Deploy

The **Deploy** workflow (`.github/workflows/Deploy.yml`) SSHes to the server, puts the repository at
`/opt/DeficitSnatcher` (cloning it the first time), writes `.env` from the secrets, and runs `docker compose up -d --build`.
The server needs Docker, and a reverse proxy (TLS) forwarding your domain to `WEB_PORT`.

Run it the first time from GitHub (Actions → Deploy → Run workflow); after that, from the site's Deploy tab.

**Secrets** (Settings → Secrets and variables → Actions):

| Secret | |
|---|---|
| `APP_USERNAME`, `APP_PASSWORD` | The one login. Change them and redeploy to change it. |
| `JWT_SIGNING_KEY` | `openssl rand -base64 48` |
| `SECRETS_KEY` | `openssl rand -base64 32`. Never change it once set: the card and screenshots are encrypted with it. |
| `POSTGRES_PASSWORD` | Any strong password. |
| `SERVER_HOST`, `SERVER_USER`, `SERVER_SSH_KEY` | SSH to the server. |
| `OPENAI_API_KEY` | Optional, turns on the AI (platform.openai.com → API keys). |
| `DEPLOY_GITHUB_TOKEN` | Optional, turns on the Deploy tab: a fine-grained token for this repository with Actions (read and write) and Contents (read). |

**Variables** (all optional): `WEB_PORT` (default 8090), `PUBLIC_URL` (the site's address, for links in
notifications), `SERVER_SSH_PORT`, `POSTGRES_USER`, `POSTGRES_DB`, `TOKEN_DAYS`. The database opens no port on the
server; only the site's `WEB_PORT` is used.
