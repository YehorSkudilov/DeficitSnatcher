"use client";

import { useState } from "react";
import { api, type AiStatus, type Profile, type Settings } from "@/lib/api";
import { Check, ErrorNote, Field, NumberInput, useAction, useData } from "./ui";

export function SettingsPage() {
  const loaded = useData(() => api.get<Settings>("/api/settings"), []);
  return (
    <div className="stack" style={{ gap: 16 }}>
      <div className="page-head">
        <h1>Settings</h1>
      </div>
      <ErrorNote error={loaded.error} />
      {loaded.data && <SettingsForm initial={loaded.data} />}
      <div className="card small muted">
        PC helpers (download, setup codes, sign-ins) are on the <a href="#helpers">Helpers</a> tab.
      </div>
      <ProfileForm />
    </div>
  );
}

function SettingsForm({ initial }: { initial: Settings }) {
  const [s, setS] = useState(initial);
  const [saved, setSaved] = useState(false);
  const save = useAction();
  const test = useAction();
  const [testResult, setTestResult] = useState<string | null>(null);
  const set = (patch: Partial<Settings>) => (setS({ ...s, ...patch }), setSaved(false));

  const submit = async () => {
    if (s.autoBuyArmed && !s.dryRun && (initial.dryRun || !initial.autoBuyArmed) && !confirm("Auto-buy will be armed with dry run OFF: matches will be bought for real. Save?"))
      return;
    const r = await save.run(() => api.put<Settings>("/api/settings", s));
    if (r) {
      setS(r);
      setSaved(true);
    }
  };

  const sendTest = async () => {
    setTestResult(null);
    // Saved settings are what's used: save first.
    const savedNow = await save.run(() => api.put<Settings>("/api/settings", s));
    if (!savedNow) return;
    const r = await test.run(() => api.post<{ sent: boolean; problems: string[] }>("/api/settings/test-notification"));
    if (r) setTestResult(r.sent ? "Sent. Check your phone." : r.problems.join("\n"));
  };

  return (
    <>
      <div className="card stack">
        <h2>Auto-buy</h2>
        <div className="row" style={{ gap: 24 }}>
          <Check label={<b>Armed</b>} checked={s.autoBuyArmed} onChange={(autoBuyArmed) => set({ autoBuyArmed })} />
          <Check label="Dry run (stop before placing the order)" checked={s.dryRun} onChange={(dryRun) => set({ dryRun })} />
        </div>
        <Check
          label="Screenshot after every checkout step (about 0.1 s each; failures and where it ended are always kept)"
          checked={s.stepScreenshots}
          onChange={(stepScreenshots) => set({ stepScreenshots })}
        />
        {s.autoBuyArmed && !s.dryRun && <div className="notice bad small">Armed for real: sites with auto-buy on will be ordered and paid for as soon as they match.</div>}
        <div className="fields">
          <Field label="Total budget" hint="Most to spend over all purchases. 0: no limit beyond each product's units.">
            <NumberInput value={s.totalBudget} min={0} step={0.01} onChange={(v) => set({ totalBudget: v ?? 0 })} />
          </Field>
          <Field label="Retry a site after (minutes)" hint="After a failed or dry-run checkout.">
            <NumberInput value={s.buyRetryMinutes} min={0} onChange={(v) => set({ buyRetryMinutes: v ?? 0 })} />
          </Field>
          <Field label="Time zone" hint="For restock days and times, e.g. America/Toronto, America/Vancouver, America/Edmonton.">
            <input type="text" value={s.timeZone} onChange={(e) => set({ timeZone: e.target.value })} />
          </Field>
          <Field label="Currency" hint="For showing prices; sites' prices are taken as they are.">
            <input type="text" value={s.currency} onChange={(e) => set({ currency: e.target.value })} />
          </Field>
        </div>
      </div>

      <div className="card stack">
        <h2>Checking</h2>
        <Check label="Tracking on (uncheck to pause every check)" checked={s.trackingEnabled} onChange={(trackingEnabled) => set({ trackingEnabled })} />
        <Check
          label="Check pages that keep failing less often (up to 5× their interval, so a store blocking you isn't hammered). Off: every page keeps its own interval."
          checked={s.slowDownFailingPages}
          onChange={(slowDownFailingPages) => set({ slowDownFailingPages })}
        />
        <div className="fields">
          <Field label="Check every (seconds)" hint="Default for sites without their own.">
            <NumberInput value={s.defaultIntervalSeconds} min={5} onChange={(v) => set({ defaultIntervalSeconds: v ?? 60 })} />
          </Field>
          <Field label="Jitter (%)" hint="Each wait is randomly this much shorter or longer, so checks don't land on a fixed beat. 0: exact.">
            <NumberInput value={s.jitterPercent} min={0} onChange={(v) => set({ jitterPercent: v ?? 0 })} />
          </Field>
          <Field label="Same store at most every (seconds)" hint="Spaces out checks of one host, so it doesn't block you.">
            <NumberInput value={s.minSecondsPerHost} min={0} onChange={(v) => set({ minSecondsPerHost: v ?? 0 })} />
          </Field>
          <Field
            label="Stricter stores"
            hint="Pages of these stores are checked at most every so many minutes, whatever their own interval. For stores that keep blocking you."
          >
            <div className="stack" style={{ gap: 6 }}>
              {s.storeIntervals.map((x, i) => (
                <div className="row" key={i} style={{ gap: 6, flexWrap: "nowrap" }}>
                  <input
                    type="text"
                    placeholder="walmart.ca"
                    value={x.domain}
                    onChange={(e) => set({ storeIntervals: s.storeIntervals.map((y, j) => (j === i ? { ...y, domain: e.target.value } : y)) })}
                  />
                  <span className="small faint" style={{ whiteSpace: "nowrap" }}>every</span>
                  <NumberInput
                    value={x.minutes}
                    min={1}
                    onChange={(v) => set({ storeIntervals: s.storeIntervals.map((y, j) => (j === i ? { ...y, minutes: v ?? 10 } : y)) })}
                  />
                  <span className="small faint">min</span>
                  <button className="btn small ghost" onClick={() => set({ storeIntervals: s.storeIntervals.filter((_, j) => j !== i) })}>
                    ×
                  </button>
                </div>
              ))}
              <button
                className="btn small"
                style={{ alignSelf: "flex-start" }}
                onClick={() => set({ storeIntervals: [...s.storeIntervals, { domain: "", minutes: 10 }] })}
              >
                Add a store
              </button>
            </div>
          </Field>
          <Field label="Checks at once">
            <NumberInput value={s.maxConcurrentChecks} min={1} onChange={(v) => set({ maxConcurrentChecks: v ?? 1 })} />
          </Field>
          <Field label="Timeout (seconds)">
            <NumberInput value={s.requestTimeoutSeconds} min={3} onChange={(v) => set({ requestTimeoutSeconds: v ?? 20 })} />
          </Field>
          <Field label="Keep price history and activity (days)" hint="0 keeps it all.">
            <NumberInput value={s.historyDays} min={0} onChange={(v) => set({ historyDays: v ?? 0 })} />
          </Field>
          <Field label="Scan snapshots" hint="The page's screenshot and source, kept with a scan.">
            <select value={s.scanSnapshots} onChange={(e) => set({ scanSnapshots: e.target.value as Settings["scanSnapshots"] })}>
              <option value="Changes">When something changes, it&apos;s in range, or it fails</option>
              <option value="Every">Every scan (slower, more storage)</option>
              <option value="Off">Off</option>
            </select>
          </Field>
          <Field label="Keep scans (days)" hint="Every check is a scan. 0 keeps them all.">
            <NumberInput value={s.scanHistoryDays} min={0} onChange={(v) => set({ scanHistoryDays: v ?? 0 })} />
          </Field>
          <Field label="Keep failed and dry-run checkouts (days)" hint="With their screenshots. Bought ones are always kept. 0: forever.">
            <NumberInput value={s.checkoutHistoryDays} min={0} onChange={(v) => set({ checkoutHistoryDays: v ?? 0 })} />
          </Field>
        </div>
        <Field
          label="Proxy"
          hint={
            <>
              All store traffic (checks and checkouts; not ChatGPT) goes through it. Big stores (Best Buy, Walmart…) often answer &quot;Access
              Denied&quot; to any server&apos;s address; a <b>residential</b> proxy gets past that. Format: <code>http://user:password@host:port</code>{" "}
              (or socks5://). Empty: direct.
            </>
          }
        >
          <input type="text" autoComplete="off" value={s.proxyUrl} placeholder="http://user:password@proxy.example.com:8000" onChange={(e) => set({ proxyUrl: e.target.value })} />
        </Field>
        <Field label="User agent" hint="What plain checks (not the browser) say they are.">
          <input type="text" value={s.userAgent} onChange={(e) => set({ userAgent: e.target.value })} />
        </Field>
      </div>

      <div className="card stack">
        <div className="card-head" style={{ marginBottom: 0 }}>
          <h2>Notifications</h2>
          <button className="btn small" disabled={test.busy || save.busy} onClick={sendTest}>
            Save and send a test
          </button>
        </div>
        {testResult && <pre className="log">{testResult}</pre>}
        <ErrorNote error={test.error} />
        <div className="grid two">
          <Field label="Discord webhook URL" hint="Server settings → Integrations → Webhooks → New webhook → Copy URL.">
            <input type="url" value={s.discordWebhookUrl} onChange={(e) => set({ discordWebhookUrl: e.target.value })} />
          </Field>
          <Field label="Webhook URL (plain text POST)" hint="E.g. an ntfy.sh topic: https://ntfy.sh/your-secret-topic">
            <input type="url" value={s.webhookUrl} onChange={(e) => set({ webhookUrl: e.target.value })} />
          </Field>
          <Field label="Telegram bot token" hint="From @BotFather.">
            <input type="password" value={s.telegramBotToken} autoComplete="off" onChange={(e) => set({ telegramBotToken: e.target.value })} />
          </Field>
          <Field label="Telegram chat id" hint="Message your bot, then open api.telegram.org/bot<token>/getUpdates.">
            <input type="text" value={s.telegramChatId} onChange={(e) => set({ telegramChatId: e.target.value })} />
          </Field>
        </div>
        <h3 style={{ marginTop: 6 }}>Email</h3>
        <p className="muted small" style={{ marginTop: -6 }}>
          Set up for IONOS mail: the mailbox&apos;s full address as the username and its password. Server{" "}
          <code>smtp.ionos.com</code> (North America), <code>smtp.ionos.co.uk</code> (UK) or <code>smtp.ionos.de</code>; port 587.
        </p>
        <div className="fields">
          <Field label="Send alerts to" hint="Your email; several: comma-separated.">
            <input type="text" value={s.emailTo} placeholder="you@example.com" onChange={(e) => set({ emailTo: e.target.value })} />
          </Field>
          <Field label="SMTP username" hint="IONOS: the mailbox's full email address.">
            <input type="text" autoComplete="off" value={s.smtpUser} placeholder="alerts@yourdomain.com" onChange={(e) => set({ smtpUser: e.target.value })} />
          </Field>
          <Field label="SMTP password">
            <input type="password" autoComplete="new-password" value={s.smtpPassword} onChange={(e) => set({ smtpPassword: e.target.value })} />
          </Field>
          <Field label="SMTP server">
            <select
              value={["smtp.ionos.com", "smtp.ionos.co.uk", "smtp.ionos.de"].includes(s.smtpHost) ? s.smtpHost : "other"}
              onChange={(e) => e.target.value !== "other" && set({ smtpHost: e.target.value, smtpPort: 587 })}
            >
              <option value="smtp.ionos.com">IONOS North America (smtp.ionos.com)</option>
              <option value="smtp.ionos.co.uk">IONOS UK (smtp.ionos.co.uk)</option>
              <option value="smtp.ionos.de">IONOS Germany (smtp.ionos.de)</option>
              <option value="other">Other…</option>
            </select>
          </Field>
          <Field label="Server address">
            <input type="text" value={s.smtpHost} onChange={(e) => set({ smtpHost: e.target.value })} />
          </Field>
          <Field label="Port" hint="587 (STARTTLS) or 465 (TLS).">
            <NumberInput value={s.smtpPort} min={1} onChange={(v) => set({ smtpPort: v ?? 587 })} />
          </Field>
          <Field label="Send from" hint="Empty: the username. IONOS only sends from your own addresses.">
            <input type="text" value={s.emailFrom} onChange={(e) => set({ emailFrom: e.target.value })} />
          </Field>
        </div>
        <h3 style={{ marginTop: 6 }}>Tell me when</h3>
        <p className="muted small" style={{ marginTop: -6 }}>
          Sent to every channel set above. Never for going out of stock. Everything is in the activity log either way.
        </p>
        <div className="stack" style={{ gap: 8 }}>
          <Check label={<><b>In stock in my price range</b> (a match: it&apos;s bought if auto-buy is on)</>} checked={s.notifyInStock} onChange={(notifyInStock) => set({ notifyInStock })} />
          <Check label={<><b>In stock, outside my range</b> (too expensive, suspiciously cheap, or not the right item)</>} checked={s.notifyInStockOutOfRange} onChange={(notifyInStockOutOfRange) => set({ notifyInStockOutOfRange })} />
          <Check label={<><b>Price drops</b> while in stock</>} checked={s.notifyPriceDrop} onChange={(notifyPriceDrop) => set({ notifyPriceDrop })} />
          <Check label={<><b>Bought</b></>} checked={s.notifyPurchase} onChange={(notifyPurchase) => set({ notifyPurchase })} />
          <Check label={<><b>Not bought</b>: a real checkout failed or was cut off (with what went wrong)</>} checked={s.notifyNotBought} onChange={(notifyNotBought) => set({ notifyNotBought })} />
          <Check label={<><b>Dry runs</b> finished (off: while armed in dry run they&apos;d come every few minutes)</>} checked={s.notifyDryRun} onChange={(notifyDryRun) => set({ notifyDryRun })} />
          <Check label={<><b>A page keeps failing</b> (see below for how many checks)</>} checked={s.notifyPageFailing ?? s.notifyErrors} onChange={(notifyPageFailing) => set({ notifyPageFailing })} />
          <Check label={<><b>A failing page works again</b></>} checked={s.notifyPageRecovered ?? s.notifyErrors} onChange={(notifyPageRecovered) => set({ notifyPageRecovered })} />
          <Check label={<><b>AI problems</b>: it couldn&apos;t read or fix a page, or thinks a page isn&apos;t the product any more</>} checked={s.notifyAiProblems ?? s.notifyErrors} onChange={(notifyAiProblems) => set({ notifyAiProblems })} />
          <Check label={<><b>A PC helper disconnected</b> (and stayed away 90 seconds)</>} checked={s.notifyHelperDisconnected ?? s.notifyErrors} onChange={(notifyHelperDisconnected) => set({ notifyHelperDisconnected })} />
          <Check label={<><b>A PC helper is signed out</b> of a store</>} checked={s.notifySignedOut} onChange={(notifySignedOut) => set({ notifySignedOut })} />
          <Check label={<><b>Prove you&apos;re human</b>: a store shows a captcha on a PC helper (the bot waits for you)</>} checked={s.notifyHumanCheck} onChange={(notifyHumanCheck) => set({ notifyHumanCheck })} />
        </div>
        <Field label="A page is failing after (checks in a row)">
          <NumberInput value={s.errorThreshold} min={1} onChange={(v) => set({ errorThreshold: v ?? 5 })} />
        </Field>
      </div>

      <AiSettings s={s} set={set} />

      <ErrorNote error={save.error} />
      <div className="row">
        <button className="btn primary" disabled={save.busy} onClick={submit}>
          {save.busy ? "Saving…" : "Save settings"}
        </button>
        {saved && <span className="badge good">Saved</span>}
      </div>
    </>
  );
}

function ProfileForm() {
  const loaded = useData(() => api.get<Profile>("/api/settings/profile"), []);
  if (!loaded.data) return <ErrorNote error={loaded.error} />;
  return <ProfileFields initial={loaded.data} />;
}

function ProfileFields({ initial }: { initial: Profile }) {
  const [p, setP] = useState<Profile>({ ...initial, cardNumber: "", cardCvv: "" });
  const [saved, setSaved] = useState<Profile>(initial);
  const [ok, setOk] = useState(false);
  const save = useAction();
  const set = (patch: Partial<Profile>) => (setP({ ...p, ...patch }), setOk(false));

  const submit = async (clearCard = false) => {
    if (clearCard && !confirm("Remove the saved card number and CVV?")) return;
    const r = await save.run(() => api.put<Profile>("/api/settings/profile", { ...p, clearCard }));
    if (r) {
      setSaved(r);
      setP({ ...r, cardNumber: "", cardCvv: "" });
      setOk(true);
    }
  };

  const text = (key: keyof Profile, label: string, type = "text", hint?: string) => (
    <Field label={label} hint={hint}>
      <input type={type} value={String(p[key] ?? "")} onChange={(e) => set({ [key]: e.target.value } as Partial<Profile>)} />
    </Field>
  );

  return (
    <div className="card stack">
      <h2>Checkout profile</h2>
      <p className="muted small">
        Filled into checkout steps (<code>{"{{email}}"}</code>, <code>{"{{card.number}}"}</code>, …). Kept encrypted on the server; the card
        number and CVV are never sent back to this page. Leave them empty to keep the saved ones. Stores where you&apos;re signed in (Cookies on
        the site) can use your saved address and card instead.
      </p>
      <div className="fields">
        {text("email", "Email", "email")}
        {text("phone", "Phone")}
        {text("firstName", "First name")}
        {text("lastName", "Last name")}
        {text("address1", "Address")}
        {text("address2", "Apartment, suite")}
        {text("city", "City")}
        {text("province", "Province / state", "text", "As the store's list shows it, e.g. Ontario or ON")}
        {text("postalCode", "Postal code")}
        {text("country", "Country", "text", "E.g. Canada or CA")}
      </div>
      <h3>Card</h3>
      <div className="fields">
        {text("cardName", "Name on card")}
        <Field label="Card number" hint={saved.cardNumber ? `Saved: •••• ${saved.cardNumber}` : "None saved"}>
          <input type="text" inputMode="numeric" autoComplete="off" value={p.cardNumber} placeholder={saved.cardNumber ? `•••• ${saved.cardNumber}` : ""} onChange={(e) => set({ cardNumber: e.target.value })} />
        </Field>
        {text("cardExpiryMonth", "Expiry month", "text", "MM")}
        {text("cardExpiryYear", "Expiry year", "text", "YYYY")}
        <Field label="CVV" hint={saved.hasCvv ? "Saved" : "None saved"}>
          <input type="password" inputMode="numeric" autoComplete="off" value={p.cardCvv} placeholder={saved.hasCvv ? "•••" : ""} onChange={(e) => set({ cardCvv: e.target.value })} />
        </Field>
      </div>
      <ErrorNote error={save.error} />
      <div className="row">
        <button className="btn primary" disabled={save.busy} onClick={() => submit()}>
          {save.busy ? "Saving…" : "Save profile"}
        </button>
        {ok && <span className="badge good">Saved</span>}
        <div style={{ flex: 1 }} />
        {(saved.cardNumber || saved.hasCvv) && (
          <button className="btn danger small" disabled={save.busy} onClick={() => submit(true)}>
            Remove card
          </button>
        )}
      </div>
    </div>
  );
}

function AiSettings({ s, set }: { s: Settings; set: (patch: Partial<Settings>) => void }) {
  const status = useData(() => api.get<AiStatus>("/api/ai/status"), []);
  return (
    <div className="card stack">
      <div className="card-head" style={{ marginBottom: 0 }}>
        <h2>AI (ChatGPT)</h2>
        {status.data &&
          (status.data.hasKey ? <span className="badge good">API key set</span> : <span className="badge warn">No API key: add the OPENAI_API_KEY secret</span>)}
      </div>
      <p className="muted small">
        Sets pages up from a link, goes through checkouts, and fixes them when a store changes its pages. The AI never sees your card or
        address: it writes placeholders like <code>{"{{card.number}}"}</code> that are filled in here, and it gets no screenshots once card
        details are typed. Every call is in each page&apos;s Logs → AI.
      </p>
      <Check label={<b>AI on</b>} checked={s.aiEnabled} onChange={(aiEnabled) => set({ aiEnabled })} />
      {status.data && (
        <div className="small">
          Used today: <b>{status.data.tokensToday.toLocaleString()}</b>
          {status.data.dailyTokenLimit > 0 && <> of {status.data.dailyTokenLimit.toLocaleString()}</>} tokens.{" "}
          <span className="muted">
            The AI is only asked when needed: setting a page up, a checkout step breaking, a page that stops reading (at most every{" "}
            {s.aiCooldownHours || 6} h), and checking a real order before it&apos;s placed.
          </span>
        </div>
      )}
      <div className="fields">
        <Field label="Model" hint="E.g. gpt-5-mini (fast, cheap) or gpt-5 (smarter).">
          <input type="text" value={s.aiModel} onChange={(e) => set({ aiModel: e.target.value })} />
        </Field>
        <Field label="Reasoning effort" hint="For gpt-5 models; empty for older models like gpt-4o.">
          <select value={s.aiReasoningEffort} onChange={(e) => set({ aiReasoningEffort: e.target.value })}>
            <option value="">(not sent)</option>
            <option value="minimal">minimal</option>
            <option value="low">low</option>
            <option value="medium">medium</option>
            <option value="high">high</option>
          </select>
        </Field>
        <Field label="Most AI actions per checkout">
          <NumberInput value={s.aiMaxActions} min={5} onChange={(v) => set({ aiMaxActions: v ?? 30 })} />
        </Field>
        <Field label="Most AI tokens per day" hint="The AI rests once they're used (until midnight UTC); everything else carries on. 0: no limit.">
          <NumberInput value={s.aiDailyTokenLimit} min={0} step={10000} onChange={(v) => set({ aiDailyTokenLimit: v ?? 0 })} />
        </Field>
        <Field label="Rest after a failure (hours)" hint="When the AI fails on a page, automatic runs don't ask it again for this long. Buttons always do.">
          <NumberInput value={s.aiCooldownHours} min={0} onChange={(v) => set({ aiCooldownHours: v ?? 6 })} />
        </Field>
        <Field label="Tax and fees allowed (%)" hint="Before the AI places a real order it reads the total: at most the maximum price plus this.">
          <NumberInput value={s.maxTaxPercent} min={0} onChange={(v) => set({ maxTaxPercent: v ?? 15 })} />
        </Field>
      </div>
      <div className="stack" style={{ gap: 8 }}>
        <Check
          label="Check the order before a real one is placed: only this product, no more than the units per order, total within the limit"
          checked={s.aiCheckOrder}
          onChange={(aiCheckOrder) => set({ aiCheckOrder })}
        />
        <Check label="Fix broken checkouts: when a step fails, the AI takes over from that screen" checked={s.aiAutofixCheckout} onChange={(aiAutofixCheckout) => set({ aiAutofixCheckout })} />
        <Check label="Save the fix: the AI's steps replace the broken ones, so next time runs without the AI" checked={s.aiSaveFixedSteps} onChange={(aiSaveFixedSteps) => set({ aiSaveFixedSteps })} />
        <Check label="Pages without checkout steps are bought by the AI going through the checkout itself" checked={s.aiCheckoutWithoutSteps} onChange={(aiCheckoutWithoutSteps) => set({ aiCheckoutWithoutSteps })} />
        <Check label="Fix reading: when a page keeps failing or showing no price or stock, the AI re-learns it (at most every 6 hours)" checked={s.aiAutofixReading} onChange={(aiAutofixReading) => set({ aiAutofixReading })} />
        <Check
          label="Restock forecasts: look up each store's restock pattern online about once a week (one web search per store: trackers like NowInStock, forums, Reddit, and stores near your city and postal area, never your street)"
          checked={s.aiRestockForecast}
          onChange={(aiRestockForecast) => set({ aiRestockForecast })}
        />
        <Check label="Send screenshots to the AI (never once card details are typed)" checked={s.aiSendScreenshots} onChange={(aiSendScreenshots) => set({ aiSendScreenshots })} />
      </div>
    </div>
  );
}
