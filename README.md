# Deficit Snatcher

Watches stores for a PS5 Pro (or anything else) and buys it automatically when it's in stock within your price range.

- **DeficitSnatcher.Api**: ASP.NET Core (.NET 11) + Postgres. Checks the sites, decides, buys (in Camoufox or Chromium, via
  Playwright), sends notifications, and runs deploys.
- **DeficitSnatcher.Web**: Next.js exported as a static site, served by nginx. nginx also passes `/api` to the API, so
  the browser only ever talks to one address.
- **DeficitSnatcher.Camoufox**: Camoufox (a Firefox that hides automation), the app's browser by default.
- **docker-compose.yml**: all four (database, API, site, Camoufox) in one `docker compose up`.

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
- **Token use is capped.** The AI is only asked when needed (setting a page up, a step breaking, a page that stops
  reading, checking a real order). After it fails on a page, automatic runs leave it out for 6 hours (buttons still ask
  it), and it stops for the day after 300,000 tokens; checks and saved steps carry on without it. Both in Settings → AI,
  with today's usage.
- The AI never sees the card or address: it writes `{{card.number}}`-style placeholders that are filled in locally, and
  gets no screenshots once card details are typed. Before a real order it must read the order total, which must be within
  the maximum price plus the allowed tax (15% by default), or the order isn't placed.

## Finding pages

On a product: **✨ Find pages**. Either ChatGPT searches the web (OpenAI's web search, one call) for stores selling it new
in your country, or you paste links. Every link is checked first (reads / needs setup / blocked / wrong product / in
range), you tick the ones you want, and they're added together with auto-buy off. Optionally the AI then sets up the ones
that didn't read on their own, within the daily token limit; blocked ones are suggested for **Run on my PC**.
One page per store: a store (its domain, e.g. bestbuy.ca) that's already watched is never added again. Only stores for
your country are added (from the checkout profile's country and province, or the currency): amazon.com or a `/en-us/`
page is left out for someone in Canada, and so is a page priced in another currency.

## The app's browser

The server's browser (Browser-kind pages, checkouts, scan screenshots, the AI's setup) is **Settings → Checking → App
browser**. Pages on a PC helper always use its Chrome.

| Browser | What it is |
|---|---|
| **Camoufox** (default) | [Camoufox](https://camoufox.com), a Firefox that hides automation and keeps a consistent fingerprint. `DeficitSnatcher.Camoufox` runs it as a Playwright server; the API connects to `ws://camoufox:4444/camoufox` (Playwright's `Firefox.ConnectAsync`) and drives it like Chromium: checks, checkouts, the AI. Its container (`camoufox` in docker-compose.yml) always runs. Uses the proxy when one is set. **Its Playwright and the API's must be the same version** (1.62.0 in both `DeficitSnatcher.Api.csproj` and `DeficitSnatcher.Camoufox/requirements.txt`): a client only talks to its own version's server. |
| **Chromium** | Here in the API's container, with a window on a virtual screen (Xvfb). |

**Sessions.** Every page keeps its own browser session on the server: its cookies and local storage are saved (encrypted,
in the database, so they survive restarts) when a check, checkout or screenshot is done, and put back into the next
browser, so the store sees one returning visitor and a checkout carries on where the checks left off. Cookies pasted on
the page are added where the session doesn't have them. A session starts afresh when it's older than Settings → "Keep
browser sessions" (3 hours by default), when the store blocks it, when the app browser is changed (the fingerprint
wouldn't match), or with **Reset** on the page. Failures say which browser and why (not set up,
unreachable, version mismatch, timed out, blocked).

## Restock forecast

Each page shows its **next restock**: from its own history (the times it came back into stock: usual weekday and time,
or the usual gap) and from what ChatGPT finds online about the store (web search across several kinds of source:
restock trackers such as NowInStock and StockTrack with their stock history logs, restock alert accounts, deal forums
like RedFlagDeals, Reddit, the store's own announcements; with links). The search is localised to your city, province and
postal area from the checkout profile (never the street), so it also looks at the chain's stores near you and regional
patterns, shown as *Near you*. Looked up about once a week per store (Settings → AI) or with *Look online now*. When the two disagree the
sooner one is shown and the other mentioned. A guess, labelled with how sure it is; times in Settings → Time zone.

## Notifications

Discord, Telegram, any webhook (ntfy.sh…) and email (SMTP; IONOS by default: `smtp.ionos.com`, port 587, the mailbox's
full address and password). Settings → Notifications chooses what you're told about: in stock in range, in stock outside
the range, price drops while in stock, bought, not bought (with what went wrong), dry runs (off by default), and problems.
Never for going out of stock. A store's human check on your PC always alerts you.

## PC helper (Walmart and other stores that block servers)

`DeficitSnatcher.Runner` runs on your own computer and lends the site its Chrome: your home internet and your sign-ins.
Pages with **Run on my PC** are checked and bought there (checks, checkouts, screenshots, AI auto-fill all work the same);
while the helper isn't connected they're checked from the server as usual (Walmart sometimes answers).

1. Helpers tab → **Make a setup code** (the site's address and a key in one).
2. On that computer, either:
   - **Windows:** **Download the installer** (`DeficitSnatcher-PC-helper-Setup.exe`), run it and paste the code when it
     asks. It installs for your user only (no admin), with a Start menu entry and an entry in Settings → Apps.
   - **Any computer:** paste the one-line command shown there (it has the code in) into PowerShell (Windows) or Terminal
     (Mac, Linux): it downloads the newest helper, installs it and connects it.
     `& ([scriptblock]::Create((irm <site>/api/runner/install.ps1))) <code>` or
     `curl -fsSL <site>/api/runner/install.sh | sh -s -- <code>`.
   - Or the zip for your computer from the [pc-helper release](../../releases/tag/pc-helper): unzip, open
     `DeficitSnatcher.Runner`, paste the code.

   It starts in the background and again at every login; its Chrome opens once to sign in to the stores, then stays
   minimised.

Its Chrome is your installed Google Chrome, started as a normal program with its own profile (no automation switches,
nothing patched in the page), which the helper then connects to; so to a store it looks like an ordinary Chrome.

**Updating:** it updates itself (every few hours, or the Update button on its card). By hand: run the installer again, or
the same command without a code; its setup is kept.

**Uninstalling:** Settings → Apps → Deficit Snatcher PC helper (Windows installer), or `deficitsnatcher uninstall`, removes
everything: the program, the `deficitsnatcher` command, autostart, its settings and logs, and its Chrome profile (its
sign-ins; your own Chrome isn't touched). Then remove it on the Helpers tab.

Commands, in a new terminal: `deficitsnatcher status | stop | start | restart | window show | window hide |
autostart on|off | setup <code> | reset | logs | uninstall`.

**A store flagged the browser?** Helpers tab → **Reset** next to that store wipes its cookies, site data and cache in the
helper's Chrome (you're signed out of it); **Reset browser** (or `deficitsnatcher reset`) wipes the whole Chrome profile and
starts a clean one (signed out of every store). If the store still blocks it, it's flagging your internet address or
account, not the browser: wait a few hours, check less often (Settings → Checking → Stricter stores) or use another network.

It only connects out to the site (WebSocket through `/api`); nothing on the PC listens to the network. If a store shows
its human check (Walmart's Press & Hold), you get an alert, do it in that window, and the bot carries on (it waits 2
minutes). Uses Google Chrome when installed, otherwise downloads Chromium the first time.

**Amazon** has built-in settings (its buy box price, Add to Cart for stock, "See All Buying Options" counts as out of
stock so resellers' markups aren't bought); the AI is only asked when those don't read the page.

## How buying works

A site is a **match** when it's in stock, the title fits the keywords, and price + shipping is within the range. A match
is reported once, and if the site has **auto-buy** on and auto-buy is **armed**, its **checkout steps** run in
Chromium (goto, click, fill, select, type, wait, expectText…), filling in the checkout profile (`{{email}}`,
`{{card.number}}`, …). The step that places the order is marked **final**.

- **Dry run** (on by default) runs every step up to the final one, then stops. Test each store with it first.
- Before every purchase, under one lock: armed, units left for the product, total budget, retry wait per site.
- Before the saved steps place a real order, the AI reads the order review page (Settings → AI, on by default): only
  this product, at most the units per order, total within the maximum plus tax. A cart still holding the console from
  an earlier attempt is refused instead of paying for two. The AI's own checkouts check the same before placing.
- A real checkout cut off by a restart is **Interrupted** and keeps counting against the limits until you mark it
  as not bought, so a restart can never cause a second order.
- Card number and CVV are masked in every log, error and saved page; screenshots, page HTML, the card, cookies and
  notification tokens are stored encrypted (AES-GCM, `Secrets__Key`).

**Bot protection.** Big retailers (Best Buy, Walmart, Amazon) use bot protection (Akamai, Cloudflare, HUMAN, DataDome),
captchas and 3-D Secure. A blocked check says "Blocked by …" in its scan. Most of these block servers' addresses
outright, however real the browser looks (the browser always runs as full Chrome with a window, never headless: on the server
its window is on a virtual screen, Xvfb, which the API starts), so the fix is a **residential proxy**: Settings → Checking → Proxy (`http://user:password@host:port`); all
store traffic goes through it, ChatGPT's doesn't. Your own signed-in cookies on the page help too. A checkout can still
stop at a captcha or bank prompt; its screenshots and error show where. Shopify and smaller stores work best.

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
dotnet run -- install-browser   # once: Chromium
dotnet run                      # http://localhost:5210, reads ../.env   (Linux with no desktop: install xvfb, Chromium's window goes on a virtual screen)
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
