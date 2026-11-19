# Deficit Snatcher

Watches stores for a PS5 Pro (or anything else) and buys it automatically when it's in stock within your price range.

- **DeficitSnatcher.Api**: ASP.NET Core (.NET 11) + Postgres. Does the checking, decides, buys (headless Chromium via
  Playwright) and sends notifications.
- **DeficitSnatcher.Web**: Next.js, exported as a static site and served by nginx. Talks only to the API.

Same layout as Gym Book: one `docker-compose.yml` per project, a reverse proxy in front doing TLS, and GitHub Actions
that deploy over SSH.

## How it works

1. **Price ranges** (products): a minimum and maximum price, how many units to buy in all and per order, and keywords
   the listing title must or must not contain. A new database starts with a "PS5 Pro" product at $600–$1,000.
2. **Sites**: the store pages to watch for a product. Each site is checked on its own interval (with jitter, at most
   one check per store every few seconds, and longer waits while a site keeps failing). The price, stock and title come
   from:
   - **Shopify** stores: the product's `.js` JSON (exact price and stock per variant);
   - other pages: structured data (JSON-LD `Product`/`Offer`, `og:`/`product:` meta tags, microdata);
   - or your own CSS selectors (price, title, an "in stock" element, "sold out" text);
   - **Browser** kind: the page is rendered in headless Chromium first, for sites that build it in script.
3. A site is a **match** when it's in stock, the title fits the keywords, and price + the site's shipping is within
   the range. A match is reported once (Discord, Telegram, or any webhook such as ntfy.sh), and if the site has
   **auto-buy** on and auto-buy is **armed**, it's bought.
4. **Buying** runs the site's **checkout steps** (goto, click, fill, select, type, wait, expectText…) in headless
   Chromium, filling in your checkout profile (`{{email}}`, `{{card.number}}`, …). The step that places the order is
   marked **final**:
   - **Dry run** (on by default) runs everything up to the final step, then stops. Use it to test a checkout.
   - Every checkout keeps a step-by-step log and a screenshot (Purchases page).
   - Limits are checked under one lock before each purchase: armed, units left for the product, total budget, and a
     retry wait per site.
   - Templates for a Shopify checkout and a generic signed-in store are built in. Store cookies (a signed-in session
     exported from your browser) can be added per site, so the checkout can use your saved address and card.

Card details, notification tokens and cookies are stored encrypted (AES-GCM, `Secrets__Key`). The card number and CVV are
never sent back to the web.

**Limits.** Big retailers (Best Buy, Walmart, Amazon) use bot protection, captchas and 3-D Secure. Checks can get
blocked (try Browser mode, a longer interval, or your cookies) and checkouts can stop on a captcha or a bank prompt.
Shopify stores and smaller shops are where this works best. Always do a dry run before arming it for real.

## Run locally

```bash
cd DeficitSnatcher.Api
cp .env.example .env            # set Auth__Password and Jwt__SigningKey
docker compose up -d db
dotnet run -- install-browser   # once: downloads headless Chromium
dotnet run
```

```bash
cd DeficitSnatcher.Web
cp .env.example .env            # NEXT_PUBLIC_API_URL=http://localhost:5210 (or wherever the API runs)
npm install
npm run dev
```

Migrations: `dotnet ef migrations add <Name> -o Data/Migrations` in `DeficitSnatcher.Api`.

## Deploy

On the server: `git clone` this repository to `/opt/DeficitSnatcher`, with Docker installed and a reverse proxy sending
the API's and web's domains to `API_PORT` and `WEB_PORT`. Pushing to `main` runs **Publish API** / **Publish Web**
(or run them by hand from Actions).

GitHub **secrets**: `SERVER_HOST`, `SERVER_USER`, `SERVER_SSH_KEY`, `POSTGRES_PASSWORD`, `AUTH_PASSWORD`,
`JWT_SIGNING_KEY` (`openssl rand -base64 48`), `SECRETS_KEY` (`openssl rand -base64 32`; never change it once set).

GitHub **variables**: `API_PORT`, `WEB_PORT`, `API_BASE_URL` (e.g. `https://api.snatcher.example.com`), `WEB_URL`
(e.g. `https://snatcher.example.com`), and optionally `AUTH_USERNAME` (default `owner`), `POSTGRES_USER`,
`POSTGRES_DB`, `DB_PORT`.
