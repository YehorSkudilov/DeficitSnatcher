# Deficit Snatcher

Watches stores for a PS5 Pro (or anything else) and buys it automatically when it's in stock within your price range.

- **DeficitSnatcher.Api**: ASP.NET Core (.NET 11) + Postgres. Checks the sites, decides, buys (in Camoufox or Chromium, via
  Playwright), sends notifications, and runs deploys.
- **DeficitSnatcher.Web**: Next.js exported as a static site, served by nginx. nginx also passes `/api` to the API, so
  the browser only ever talks to one address.
- **DeficitSnatcher.Camoufox**: Camoufox (a Firefox that hides automation), the app's browser by default.
- **DeficitSnatcher.Android**: the Android app (.NET MAUI): rings loudly when something is in stock in your range; turning
  the alarm off opens the store's page. It updates itself.
- **docker-compose.yml**: all four (database, API, site, Camoufox) in one `docker compose up`.

One account, no sign-ups: the username and password come from the environment (GitHub secrets on the server).

## What's on the site

| Tab | |
|---|---|
| **Products** → a product, or one of its pages | Its **Logs**: scans (with a screenshot and the page's source when something changed), checkouts, activity, and AI calls. |
| **Dashboard** | The auto-buy switch, each product's sites with price, stock and verdict, recent activity and checkouts. |
| **Price ranges** | Per product: min and max price (shipping included), units to buy in all and per order, title keywords. |
| **Sites** | Store pages to watch: Test, Check now, selectors, checkout steps, cookies, price history, buy now. |
| **Scans** | Every check of every site, found or not: price, stock, verdict, error, and what was done (checkout started, or why not). Filter by product, site, result (in stock / out / in range / checkout ran / errors), time, price, search. |
| **Checkouts** | Every checkout. Each one has: why it ran (the scan, price, limits at that moment), a step-by-step timeline with timing and a screenshot after each step, the full error (Playwright's call log), the browser's console and network errors, a full-page screenshot of where it ended, the page's HTML, and the raw log. |
| **Activity** | Everything that happened (alerts, price drops, checkouts, failing sites, settings changes), filterable. |
| **Settings** | The Android app (download, Ring my phone), how often pages are checked, email alerts, checkout profile (address, card). Everything else is fixed in code. |
| **Deploy** | What's running (commit, build time), commits on `main` not deployed yet, Deploy button, live progress of each step, cancel, links to the full GitHub logs. |

## AI (ChatGPT)

Turned on by the `OPENAI_API_KEY` secret (its options are fixed in code: BotSettings). Every call is in each page's Logs → AI.

- **Auto-fill with AI** (adding a page): reads the page and fills in the store name, how to read price and stock, and
  whether it really is the PS5 Pro; the settings are checked before they're offered.
- **Record checkout with AI**: ChatGPT goes through the store's checkout itself, stopping before the order, and the steps it
  takes are saved.
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

## The browser

Pages, checkouts, scan screenshots and the AI's setup run in **Camoufox** ([camoufox.com](https://camoufox.com)), a
Firefox that hides automation and keeps a consistent fingerprint. `DeficitSnatcher.Camoufox` runs it as a Playwright
server; the API connects to `ws://camoufox:4444/camoufox` (Playwright's `Firefox.ConnectAsync`). While it can't be
reached, the API's own Chromium (with a window on a virtual screen, Xvfb) takes over. **Camoufox's Playwright and the
API's must be the same version** (1.62.0 in both `DeficitSnatcher.Api.csproj` and `DeficitSnatcher.Camoufox/requirements.txt`).

**Sessions.** Every page keeps its own browser session: its cookies and local storage are saved (encrypted, in the
database) after each check or checkout and put back into the next browser, so the store sees one returning visitor and a
checkout carries on where the checks left off. A session starts afresh after 3 hours, when the store blocks it, or with
**Reset** on the page.

**Blocks.** A page the store blocked rests: it isn't checked again for 15 minutes, doubling with each block in a row (up
to 6 hours), until a check goes through. Checking again right away keeps a flag on; resting lets it wear off.

## Android app

`DeficitSnatcher.Android` (.NET MAUI, Android 8+). Settings → **Download the APK** (the `android` release), open it on the
phone and allow installing from your browser; sign in with the site's address, username and password.

- It keeps a connection to the site in the background (a small permanent notification) and starts again after a reboot
  or an update. The site holds each request until there's an alarm (`GET /api/alarms?after=…&wait=…`), so it rings
  within a second or two.
- A **match** (in stock, title fits, price + shipping in range) rings: the phone's alarm sound at full alarm volume,
  looping, with vibration, and a full-screen page even on the lock screen. **Stop & open the store** silences it and opens
  the store's product page to buy it. It stops by itself after 15 minutes.
- **Test alarm** (in the app, or Settings → Ring my phone) rings once. **Allow running in the background** keeps Android
  from putting it to sleep.
- It updates itself from the `android` release (`version.txt` there is the newest build). The first update asks you to
  confirm and to allow installing from the app; after that it updates by itself.
- Built by `.github/workflows/Android.yml` on every push that changes it. The APK is signed with the key in
  `DeficitSnatcher.Android/signing` (every build must be signed with the same key for updates to install).

## Restock forecast

Each page shows its **next restock**: from its own history (the times it came back into stock: usual weekday and time,
or the usual gap) and from what ChatGPT finds online about the store (web search across several kinds of source:
restock trackers such as NowInStock and StockTrack with their stock history logs, restock alert accounts, deal forums
like RedFlagDeals, Reddit, the store's own announcements; with links). The search is localised to your city, province and
postal area from the checkout profile (never the street), so it also looks at the chain's stores near you and regional
patterns, shown as *Near you*. Looked up about once a week per store (Settings → AI) or with *Look online now*. When the two disagree the
sooner one is shown and the other mentioned. A guess, labelled with how sure it is; times in Settings → Time zone.

## Email alerts

Email (SMTP; IONOS by default: `smtp.ionos.com`, port 587, the mailbox's full address and password), set in Settings:
matches, purchases, failed checkouts (with what went wrong) and pages that keep failing. Never for going out of stock.

**Amazon** has built-in settings (its buy box price, Add to Cart for stock, "See All Buying Options" counts as out of
stock so resellers' markups aren't bought); the AI is only asked when those don't read the page.

## How buying works

A site is a **match** when it's in stock, the title fits the keywords, and price + shipping is within the range. A match
is reported once (the app rings), and if the site has **auto-buy** on and auto-buy is **on** (the Dashboard switch), its
**checkout steps** run in the browser (goto, click, fill, select, type, wait, expectText…), filling in the checkout profile (`{{email}}`,
`{{card.number}}`, …). The step that places the order is marked **final**.

- Before every purchase, under one lock: auto-buy on, units left for the product, a 10-minute wait per site after a
  failed try.
- Before the saved steps place a real order, the AI reads the order review page: only
  this product, at most the units per order, total within the maximum plus tax. A cart still holding the console from
  an earlier attempt is refused instead of paying for two. The AI's own checkouts check the same before placing.
- A real checkout cut off by a restart is **Interrupted** and keeps counting against the limits until you mark it
  as not bought, so a restart can never cause a second order.
- Card number and CVV are masked in every log, error and saved page; screenshots, page HTML, the card, cookies and
  notification tokens are stored encrypted (AES-GCM, `Secrets__Key`).

**Bot protection.** Big retailers (Best Buy, Walmart, Amazon) use bot protection (Akamai, Cloudflare, HUMAN, DataDome),
captchas and 3-D Secure. A blocked check says "Blocked by …" in its scan, and the page rests (see The browser). Some
block servers' addresses outright, however real the browser looks: those can't be checked from the server; the app still
rings for the stores that can. Checking less often (Settings) is what helps most. A checkout can still stop at a captcha
or bank prompt; its screenshots and error show where, and you're emailed. Shopify and smaller stores work best.

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
