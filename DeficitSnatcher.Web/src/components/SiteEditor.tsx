"use client";

import { useEffect, useState } from "react";
import {
  api,
  SiteKinds,
  StepActions,
  type BuyOutcome,
  type CheckoutStep,
  type HistoryPoint,
  type ProbeResponse,
  type AiStatus,
  type RunnerStatus,
  type RestockForecast,
  type SetupSuggestion,
  type Product,
  type Settings,
  type Site,
  type SiteInput,
  type SiteKind,
} from "@/lib/api";
import { ago, dateTime, money } from "@/lib/format";
import { Check, ErrorNote, Field, NumberInput, StockBadge, useAction, useData, useNow } from "./ui";
import { go } from "./App";
import { Logs } from "./Logs";

const kindHelp: Record<SiteKind, string> = {
  Auto: "Shopify product JSON if it's a Shopify /products/ page, otherwise the page's structured data, then your selectors.",
  Shopify: "A Shopify store's /products/… page: exact price and stock per variant. The most reliable.",
  Html: "Reads the page's HTML: structured data (JSON-LD, meta tags) and your selectors.",
  Browser: "Opens the page in Chromium first. For sites that build the page in script or block plain requests. Slower.",
};

const templates: Record<string, CheckoutStep[]> = {
  "Shopify checkout": [
    { action: "goto", value: "{{origin}}/cart/{{variantId}}:{{quantity}}", note: "Cart link: straight to checkout with the item" },
    { action: "click", selector: "button:has-text('Accept')", optional: true, timeoutMs: 2000 },
    { action: "fill", selector: "input[name='email']", value: "{{email}}" },
    { action: "fill", selector: "input[name='firstName']", value: "{{firstName}}" },
    { action: "fill", selector: "input[name='lastName']", value: "{{lastName}}" },
    { action: "fill", selector: "input[name='address1']", value: "{{address1}}" },
    { action: "fill", selector: "input[name='city']", value: "{{city}}" },
    { action: "select", selector: "select[name='zone']", value: "{{province}}", optional: true },
    { action: "fill", selector: "input[name='postalCode']", value: "{{postalCode}}" },
    { action: "fill", selector: "input[name='phone']", value: "{{phone}}", optional: true },
    { action: "fill", frame: "iframe[name^='card-fields-number']", selector: "input[name='number']", value: "{{card.number}}", timeoutMs: 30000 },
    { action: "fill", frame: "iframe[name^='card-fields-expiry']", selector: "input[name='expiry']", value: "{{card.expiryMonth}}/{{card.expiryYear2}}" },
    { action: "fill", frame: "iframe[name^='card-fields-verification_value']", selector: "input[name='verification_value']", value: "{{card.cvv}}" },
    { action: "fill", frame: "iframe[name^='card-fields-name']", selector: "input[name='name']", value: "{{card.name}}", optional: true },
    { action: "click", selector: "#checkout-pay-button", final: true },
    { action: "waitForUrl", value: "thank", timeoutMs: 60000, note: "The order confirmation page" },
  ],
  "Generic (signed-in store)": [
    { action: "goto", value: "{{url}}" },
    { action: "click", selector: "button:has-text('Accept')", optional: true, timeoutMs: 2000 },
    { action: "click", selector: "button:has-text('Add to cart')" },
    { action: "wait", value: "1500" },
    { action: "goto", value: "{{origin}}/checkout" },
    { action: "fill", selector: "input[name='cvv']", value: "{{card.cvv}}", optional: true, note: "Saved cards often still ask for the CVV" },
    { action: "click", selector: "button:has-text('Place order')", final: true },
    { action: "expectText", value: "Thank you", timeoutMs: 60000 },
  ],
};

function toInput(s: Site): SiteInput {
  return {
    productId: s.productId,
    name: s.name,
    url: s.url,
    kind: s.kind,
    enabled: s.enabled,
    intervalSeconds: s.intervalSeconds,
    shippingCost: s.shippingCost,
    priceSelector: s.priceSelector,
    titleSelector: s.titleSelector,
    inStockSelector: s.inStockSelector,
    outOfStockText: s.outOfStockText,
    variantId: s.variantId,
    autoBuy: s.autoBuy,
    checkoutSteps: s.checkoutSteps,
    cookies: null,
    runOnPc: s.runOnPc,
  };
}

export function SiteEditor({ id, productId }: { id: number | null; productId?: number }) {
  const products = useData(() => api.get<Product[]>("/api/products"), []);
  const settings = useData(() => api.get<Settings>("/api/settings"), []);
  const saved = useData(() => (id ? api.get<Site>(`/api/sites/${id}`) : Promise.resolve(null)), [id], id ? 5000 : undefined);
  const [form, setForm] = useState<SiteInput | null>(null);
  const [dirty, setDirty] = useState(false);
  const [probe, setProbe] = useState<ProbeResponse | null>(null);
  const [notice, setNotice] = useState<{ tone: "good" | "bad" | "warn"; text: string } | null>(null);
  const save = useAction();
  const test = useAction();
  const action = useAction();
  const aiAction = useAction();
  const aiStatus = useData(() => api.get<AiStatus>("/api/ai/status"), []);
  const runner = useData(() => api.get<RunnerStatus>("/api/runner/status"), [], 10000);
  const [suggestion, setSuggestion] = useState<SetupSuggestion | null>(null);
  const aiOn = !!aiStatus.data?.hasKey && !!aiStatus.data?.enabled;
  const aiOff = aiStatus.data && !aiOn ? (aiStatus.data.hasKey ? "The AI is turned off in Settings." : "The AI is off: add the OPENAI_API_KEY secret and redeploy.") : null;
  const currency = settings.data?.currency ?? "CAD";
  const site = saved.data;

  // The form starts from the saved site, or blank for a new one (once the products are known).
  useEffect(() => {
    if (form) return;
    if (id && site) {
      // eslint-disable-next-line react-hooks/set-state-in-effect -- filled once from what's loaded
      setForm(toInput(site));
    } else if (!id && products.data) {
      setForm({
        productId: productId ?? products.data[0]?.id ?? 0,
        name: "",
        url: "",
        kind: "Auto",
        enabled: true,
        intervalSeconds: null,
        shippingCost: 0,
        priceSelector: null,
        titleSelector: null,
        inStockSelector: null,
        outOfStockText: null,
        variantId: null,
        autoBuy: false,
        checkoutSteps: [],
        cookies: null,
        runOnPc: false,
      });
    }
  }, [id, site, products.data, productId, form]);

  if (!form) return <ErrorNote error={saved.error || products.error} />;

  const set = (patch: Partial<SiteInput>) => {
    setForm({ ...form, ...patch });
    setDirty(true);
  };

  const submit = async () => {
    const result = await save.run(() => (id ? api.put<Site>(`/api/sites/${id}`, form) : api.post<Site>("/api/sites", form)));
    if (!result) return;
    setDirty(false);
    setForm({ ...form, cookies: null });
    setNotice({ tone: "good", text: "Saved. It'll be checked in a moment." });
    if (!id) go(`site/${result.id}`);
    else void saved.reload();
  };

  // The AI reads the page and fills in the form (checked before it's offered; nothing is saved until Save).
  const aiSetup = async () => {
    setSuggestion(null);
    const r = await aiAction.run(() =>
      api.post<SetupSuggestion>("/api/ai/setup", { url: form.url, productId: form.productId, siteId: id, cookies: form.cookies || null, runOnPc: form.runOnPc }),
    );
    if (!r) return;
    setSuggestion(r);
    // Blocked: nothing was read, so nothing to fill in (only the store's name).
    if (!r.result && r.error) {
      if (!form.name && r.name) set({ name: r.name });
      return;
    }
    setForm({
      ...form,
      name: form.name || r.name || form.name,
      kind: r.kind,
      priceSelector: r.priceSelector,
      titleSelector: r.titleSelector,
      inStockSelector: r.inStockSelector,
      outOfStockText: r.outOfStockText,
    });
    setDirty(true);
    if (r.result) setProbe({ result: r.result, verdict: r.verdict, error: r.error, buyMessage: null });
  };

  // The AI goes through the checkout (dry run) and saves the steps it takes.
  const aiRecord = async () => {
    if (dirty) return setNotice({ tone: "warn", text: "Save first: the AI records the checkout of the saved page." });
    const r = await aiAction.run(() => api.post<BuyOutcome>(`/api/ai/record-checkout/${id}`));
    if (r?.started && r.purchaseId) return go(`purchase/${r.purchaseId}`);
    if (r) setNotice({ tone: "bad", text: r.message });
  };

  const runTest = async () => {
    setProbe(null);
    const r = await test.run(() => api.post<ProbeResponse>(`/api/sites/test${id ? `?id=${id}` : ""}`, form));
    if (r) setProbe(r);
  };

  const checkNow = async () => {
    const r = await action.run(() => api.post<ProbeResponse>(`/api/sites/${id}/check`));
    if (r) {
      setProbe(r);
      void saved.reload();
    }
  };

  const buy = async (dryRun: boolean) => {
    if (dirty) return setNotice({ tone: "warn", text: "Save first: the checkout runs the saved steps." });
    if (!dryRun && !confirm(`Run the checkout for REAL at ${site?.name}? This places an order at ${money((site?.lastPrice ?? 0) + (site?.shippingCost ?? 0), currency)}.`)) return;
    const r = await action.run(() => api.post<BuyOutcome>(`/api/sites/${id}/buy`, { dryRun }));
    if (r?.started && r.purchaseId) return go(`purchase/${r.purchaseId}`);
    if (r)
      setNotice(
        r.started
          ? { tone: "good", text: `${r.message} Opening it…` }
          : { tone: "bad", text: r.message },
      );
  };

  const remove = async () => {
    if (!confirm(`Delete ${site?.name}, its price history, scans and checkouts?`)) return;
    const ok = await action.run(async () => (await api.del(`/api/sites/${id}`), true));
    if (ok) go(`product/${site?.productId ?? ""}`);
  };

  return (
    <div className="stack" style={{ gap: 16 }}>
      <div className="page-head">
        <div className="row">
          <a href={`#product/${form.productId}`} className="btn ghost small">
            ← {products.data?.find((p) => p.id === form.productId)?.name ?? "Product"}
          </a>
          <h1>{id ? site?.name : "New page"}</h1>
          {site && <StockBadge site={site} />}
          {site?.match && <span className="badge good">BUY ZONE</span>}
        </div>
        <div className="row">
          {id && (
            <button className="btn" disabled={action.busy} onClick={checkNow}>
              Check now
            </button>
          )}
          <button className="btn primary" disabled={save.busy || (!dirty && !!id)} onClick={submit}>
            {save.busy ? "Saving…" : id ? "Save" : "Add page"}
          </button>
        </div>
      </div>

      <ErrorNote error={save.error || action.error || saved.error} />
      {notice && <div className={`notice ${notice.tone}`}>{notice.text}</div>}

      {site && <Status site={site} currency={currency} />}

      <div className="card stack">
        <h2>Page</h2>
        <div className="fields">
          <Field label="Product">
            <select value={form.productId} onChange={(e) => set({ productId: Number(e.target.value) })}>
              {products.data?.map((p) => (
                <option key={p.id} value={p.id}>
                  {p.name} ({money(p.minPrice, currency)}–{money(p.maxPrice, currency)})
                </option>
              ))}
            </select>
          </Field>
          <Field label="Name">
            <input type="text" value={form.name} placeholder="Best Buy" onChange={(e) => set({ name: e.target.value })} />
          </Field>
          <Field label="Kind" hint={kindHelp[form.kind]}>
            <select value={form.kind} onChange={(e) => set({ kind: e.target.value as SiteKind })}>
              {SiteKinds.map((k) => (
                <option key={k}>{k}</option>
              ))}
            </select>
          </Field>
        </div>
        <Field label="Product page URL">
          <div className="row" style={{ flexWrap: "nowrap" }}>
            <input type="url" value={form.url} placeholder="https://store.example.com/products/ps5-pro" onChange={(e) => set({ url: e.target.value })} />
            <button className="btn primary" style={{ flex: "none" }} disabled={!aiOn || aiAction.busy || !form.url} title={aiOff ?? undefined} onClick={aiSetup}>
              {aiAction.busy ? "AI is looking…" : "✨ Auto-fill with AI"}
            </button>
          </div>
        </Field>
        {aiOff && <div className="faint small">{aiOff}</div>}
        <ErrorNote error={aiAction.error} />
        {suggestion && (
          <div className={`notice ${suggestion.rightProduct === false || (!suggestion.result && suggestion.error) ? "bad" : suggestion.works ? "good" : "warn"}`}>
            <b>
              {!suggestion.result && suggestion.error
                ? "The store blocked the page, so nothing could be filled in."
                : suggestion.rightProduct === false
                  ? "The AI thinks this isn't the right product."
                  : suggestion.works
                    ? "Filled in by the AI, and a check with these settings worked."
                    : "Filled in by the AI, but a check with these settings didn't find everything."}
            </b>{" "}
            {!suggestion.result && suggestion.error ? suggestion.error : `${suggestion.notes} Review, then Save.`}
          </div>
        )}
        <div className="fields">
          <Field label="Check every (seconds)" hint={`Empty: the default (${settings.data?.defaultIntervalSeconds ?? 60} s).`}>
            <NumberInput value={form.intervalSeconds} min={5} placeholder={String(settings.data?.defaultIntervalSeconds ?? 60)} onChange={(v) => set({ intervalSeconds: v })} />
          </Field>
          <Field label={`Shipping (${currency})`} hint="Added to the price before it's compared with the range.">
            <NumberInput value={form.shippingCost} min={0} step={0.01} onChange={(v) => set({ shippingCost: v ?? 0 })} />
          </Field>
        </div>
        <div className="row" style={{ gap: 24 }}>
          <Check label="Enabled (checked on schedule)" checked={form.enabled} onChange={(enabled) => set({ enabled })} />
          <Check
            label={
              <>
                <b>Auto-buy</b> when it&apos;s a match (off: alerts only; the alert&apos;s store link opens in your own browser to buy it yourself)
              </>
            }
            checked={form.autoBuy}
            onChange={(autoBuy) => set({ autoBuy })}
          />
          <Check
            label={
              <>
                <b>Run on my PC</b> (stores that block servers, like Walmart)
              </>
            }
            checked={form.runOnPc}
            onChange={(runOnPc) => set({ runOnPc })}
          />
        </div>
        {form.runOnPc && runner.data && (
          <div className={`notice small ${runner.data.connected ? "good" : "warn"}`}>
            {runner.data.connected ? (
              <>
                Checked and bought in Chrome on the PC helper{runner.data.connectedCount > 1 ? "s" : ""} (
                {runner.data.connectedCount > 1 ? `${runner.data.connectedCount} connected, taking turns` : <b>{runner.data.machine}</b>}). If the store asks you to prove you&apos;re human,
                you get an alert: do it in that window and the bot carries on.
              </>
            ) : (
              <>
                The PC helper isn&apos;t connected, so this page is checked from the server for now (the store sometimes answers). Start the
                helper on your computer: <a href="#helpers">Helpers</a>.
              </>
            )}
          </div>
        )}
        {form.autoBuy && settings.data && <AutoBuyNotice settings={settings.data} product={products.data?.find((p) => p.id === form.productId)} />}
      </div>

      <div className="card stack">
        <div className="card-head" style={{ marginBottom: 0 }}>
          <h2>Reading the page</h2>
          <button className="btn" disabled={test.busy || !form.url} onClick={runTest}>
            {test.busy ? "Testing…" : "Test"}
          </button>
        </div>
        <p className="muted small">
          Usually nothing to set: price, stock and title come from the page&apos;s structured data. If the test can&apos;t find them, point it at the
          page&apos;s elements with CSS selectors (right-click the price in your browser → Inspect).
        </p>
        <div className="fields">
          <Field label="Price selector">
            <input type="text" value={form.priceSelector ?? ""} placeholder=".price, [data-testid=price]" onChange={(e) => set({ priceSelector: e.target.value })} />
          </Field>
          <Field label="Title selector">
            <input type="text" value={form.titleSelector ?? ""} placeholder="h1" onChange={(e) => set({ titleSelector: e.target.value })} />
          </Field>
          <Field label="In stock when this exists" hint="And isn't disabled, e.g. the Add to cart button.">
            <input type="text" value={form.inStockSelector ?? ""} placeholder="button.add-to-cart" onChange={(e) => set({ inStockSelector: e.target.value })} />
          </Field>
          <Field label="Out of stock when page says" hint="Comma-separated.">
            <input type="text" value={form.outOfStockText ?? ""} placeholder="Sold out, Coming soon" onChange={(e) => set({ outOfStockText: e.target.value })} />
          </Field>
          <Field label="Shopify variant id" hint="Empty: the first one in stock.">
            <input type="text" value={form.variantId ?? ""} onChange={(e) => set({ variantId: e.target.value })} />
          </Field>
        </div>
        {probe && <ProbeView probe={probe} currency={currency} />}
      </div>

      <StepsEditor steps={form.checkoutSteps} onChange={(checkoutSteps) => set({ checkoutSteps })} />
      {id && (
        <div className="card stack">
          <h2>Try the checkout</h2>
          <p className="muted small">
            A dry run goes through every step except the final ones (marked red), so you can see each step works without buying. The log and a
            screenshot of every step open as soon as it starts. <b>Record checkout with AI</b>: ChatGPT goes through this store&apos;s checkout
            itself (dry run, stopping before the order is placed) and the steps it takes are saved here.
            {aiOn && settings.data && (
              <>
                {" "}
                {settings.data.aiAutofixCheckout && "If a saved step breaks, the AI takes over from that screen and saves the fix. "}
                {settings.data.aiCheckoutWithoutSteps && "Without steps, the AI does the checkout itself."}
              </>
            )}
          </p>
          <div className="row">
            <button className="btn" disabled={action.busy || (form.checkoutSteps.length === 0 && !aiOn)} onClick={() => buy(true)}>
              Dry-run checkout
            </button>
            <button className="btn primary" disabled={!aiOn || aiAction.busy} title={aiOff ?? undefined} onClick={aiRecord}>
              ✨ Record checkout with AI
            </button>
            <button className="btn danger" disabled={action.busy || (form.checkoutSteps.length === 0 && !aiOn) || site?.lastPrice == null} onClick={() => buy(false)}>
              Buy now for real
            </button>
          </div>
        </div>
      )}

      <div className="card stack">
        <h2>Cookies</h2>
        <p className="muted small">
          Optional: a signed-in session for this store, so checks see member prices and the checkout skips signing in (and uses your saved
          address and card). Export them with a cookie extension (JSON), or paste a <code>name=value; …</code> Cookie header. Kept encrypted.
        </p>
        <textarea
          value={form.cookies ?? ""}
          placeholder={site?.hasCookies ? "Saved. Paste new cookies to replace them." : '[{"name": "session", "value": "…", "domain": ".store.com"}]'}
          onChange={(e) => set({ cookies: e.target.value })}
        />
        {site?.hasCookies && (
          <div className="row">
            <span className="badge good">Cookies saved</span>
            <button className="btn small" onClick={() => set({ cookies: "" })}>
              Remove on save
            </button>
          </div>
        )}
      </div>

      {id && <RestockCard siteId={id} aiOn={aiOn} />}

      {id && <History siteId={id} min={products.data?.find((p) => p.id === form.productId)?.minPrice} max={products.data?.find((p) => p.id === form.productId)?.maxPrice} currency={currency} />}

      {id && <Logs key={id} siteId={id} />}

      {id && (
        <div className="row">
          <button className="btn danger" disabled={action.busy} onClick={remove}>
            Delete page
          </button>
        </div>
      )}
    </div>
  );
}

function Status({ site, currency }: { site: Site; currency: string }) {
  const now = useNow();
  return (
    <div className="card">
      <div className="grid stats">
        <div className="stat">
          <div className="label">Price</div>
          <div className="value">{money(site.lastPrice, currency)}</div>
          {site.shippingCost > 0 && <div className="faint small">+ {money(site.shippingCost, currency)} shipping</div>}
        </div>
        <div className="stat">
          <div className="label">Checked</div>
          <div className="value" style={{ fontSize: 18 }}>
            {site.checking ? "checking…" : ago(site.lastCheckedAt, now)}
          </div>
          <div className="faint small">changed {ago(site.lastChangedAt, now)}</div>
        </div>
        <div className="stat" style={{ gridColumn: "span 2" }}>
          <div className="label">Verdict</div>
          <div style={{ marginTop: 4 }}>{site.match ? <span className="badge good">In range: {site.verdict}</span> : site.verdict}</div>
          {site.lastTitle && <div className="faint small ellipsis">{site.lastTitle}</div>}
        </div>
      </div>
      {site.lastError && (
        <div className="notice bad small" style={{ marginTop: 12 }}>
          {site.lastError}
        </div>
      )}
    </div>
  );
}

function ProbeView({ probe, currency }: { probe: ProbeResponse; currency: string }) {
  if (probe.error) return <div className="notice bad">{probe.error}</div>;
  const r = probe.result!;
  return (
    <div className={`notice ${probe.verdict?.match ? "good" : ""}`}>
      <div className="row" style={{ gap: 16 }}>
        <span>
          Price <b>{money(r.price, r.currency ?? currency)}</b>
        </span>
        <span>
          Stock <b>{r.inStock === true ? "in stock" : r.inStock === false ? "out of stock" : "unknown"}</b>
        </span>
        {r.variantId && <span>Variant {r.variantId}</span>}
      </div>
      <div className="small">Title: {r.title ?? "—"}</div>
      <div className="small">
        <b>{probe.verdict?.match ? "MATCH: " : "Not a match: "}</b>
        {probe.verdict?.reason}
      </div>
      {probe.buyMessage && <div className="small">Auto-buy: {probe.buyMessage}</div>}
      <div className="faint small">{r.notes.join(" · ")}</div>
    </div>
  );
}

function StepsEditor({ steps, onChange }: { steps: CheckoutStep[]; onChange: (s: CheckoutStep[]) => void }) {
  const update = (i: number, patch: Partial<CheckoutStep>) => onChange(steps.map((s, j) => (j === i ? { ...s, ...patch } : s)));
  const move = (i: number, d: number) => {
    const next = [...steps];
    const [s] = next.splice(i, 1);
    next.splice(i + d, 0, s);
    onChange(next);
  };
  const needsSelector = (a: string) => !["goto", "wait", "waitForUrl", "expectText"].includes(a);
  const needsValue = (a: string) => !["click", "check", "waitFor"].includes(a);
  const valueHint: Record<string, string> = {
    goto: "URL, e.g. {{origin}}/checkout",
    fill: "Text, e.g. {{email}}",
    type: "Text, typed key by key",
    select: "Option value or label",
    press: "Key, e.g. Enter",
    wait: "Milliseconds",
    waitForUrl: "Part of the URL",
    expectText: "Text that must show",
  };

  return (
    <div className="card stack">
      <div className="card-head" style={{ marginBottom: 0 }}>
        <h2>Checkout steps</h2>
        <div className="row">
          <select
            value=""
            onChange={(e) => {
              const t = templates[e.target.value];
              if (t && (steps.length === 0 || confirm("Replace the steps with the template?"))) onChange(t.map((s) => ({ ...s })));
            }}
            style={{ width: "auto" }}
          >
            <option value="">Start from a template…</option>
            {Object.keys(templates).map((k) => (
              <option key={k}>{k}</option>
            ))}
          </select>
          <button className="btn small" onClick={() => onChange([...steps, { action: "click", selector: "" }])}>
            Add step
          </button>
        </div>
      </div>
      <p className="muted small">
        Run in Chromium, top to bottom. Selectors are Playwright&apos;s: CSS, <code>button:has-text(&apos;Add to cart&apos;)</code>,{" "}
        <code>text=Place order</code>. Values can use <code>{"{{url}}"}</code> <code>{"{{origin}}"}</code> <code>{"{{variantId}}"}</code>{" "}
        <code>{"{{quantity}}"}</code> and your checkout profile: <code>{"{{email}}"}</code> <code>{"{{firstName}}"}</code>{" "}
        <code>{"{{lastName}}"}</code> <code>{"{{fullName}}"}</code> <code>{"{{phone}}"}</code> <code>{"{{address1}}"}</code> <code>{"{{address2}}"}</code>{" "}
        <code>{"{{city}}"}</code> <code>{"{{province}}"}</code> <code>{"{{postalCode}}"}</code> <code>{"{{country}}"}</code>{" "}
        <code>{"{{card.number}}"}</code> <code>{"{{card.name}}"}</code> <code>{"{{card.expiryMonth}}"}</code> <code>{"{{card.expiryYear}}"}</code>{" "}
        <code>{"{{card.expiryYear2}}"}</code> <code>{"{{card.expiry}}"}</code> <code>{"{{card.cvv}}"}</code>. Mark the step that places the order as{" "}
        <b>final</b>: dry runs stop before it, and real buys need one. Card fields are usually inside an iframe: put the iframe&apos;s selector in
        &quot;in frame&quot;.
      </p>
      {steps.length === 0 && (
        <p className="faint">
          No steps yet. Record them with AI below, start from a template, or add them by hand. Without steps, the AI does the checkout itself
          when it&apos;s on (Settings).
        </p>
      )}
      {steps.map((s, i) => (
        <div className={`step ${s.final ? "final" : ""}`} key={i}>
          <span className="idx">{i + 1}</span>
          <select value={s.action} onChange={(e) => update(i, { action: e.target.value as CheckoutStep["action"] })}>
            {StepActions.map((a) => (
              <option key={a}>{a}</option>
            ))}
          </select>
          {needsSelector(s.action) ? (
            <input type="text" placeholder="Selector" value={s.selector ?? ""} onChange={(e) => update(i, { selector: e.target.value })} />
          ) : (
            <span />
          )}
          {needsValue(s.action) ? (
            <input type="text" placeholder={valueHint[s.action] ?? "Value"} value={s.value ?? ""} onChange={(e) => update(i, { value: e.target.value })} />
          ) : (
            <span />
          )}
          <div className="row" style={{ gap: 2, flexWrap: "nowrap" }}>
            <button className="btn ghost small" disabled={i === 0} onClick={() => move(i, -1)} aria-label="Up">
              ↑
            </button>
            <button className="btn ghost small" disabled={i === steps.length - 1} onClick={() => move(i, 1)} aria-label="Down">
              ↓
            </button>
            <button className="btn ghost small" onClick={() => onChange(steps.filter((_, j) => j !== i))} aria-label="Remove">
              ✕
            </button>
          </div>
          <div className="step-extra small">
            <Check label="final (places the order)" checked={!!s.final} onChange={(final) => update(i, { final })} />
            <Check label="optional" checked={!!s.optional} onChange={(optional) => update(i, { optional })} />
            {needsSelector(s.action) && (
              <input type="text" placeholder="in frame (iframe selector)" value={s.frame ?? ""} onChange={(e) => update(i, { frame: e.target.value || undefined })} />
            )}
            <input
              type="number"
              placeholder="timeout ms (15000)"
              value={s.timeoutMs ?? ""}
              onChange={(e) => update(i, { timeoutMs: e.target.value === "" ? undefined : Number(e.target.value) })}
            />
            {s.note && <span className="faint">{s.note}</span>}
          </div>
        </div>
      ))}
    </div>
  );
}

function History({ siteId, min, max, currency }: { siteId: number; min?: number; max?: number; currency: string }) {
  const [days, setDays] = useState(14);
  const { data } = useData(() => api.get<HistoryPoint[]>(`/api/sites/${siteId}/history?days=${days}`), [siteId, days], 30000);
  const points = (data ?? []).filter((p) => p.price !== null) as (HistoryPoint & { price: number })[];
  const now = useNow(60000);

  const W = 800;
  const H = 120;
  let chart: React.ReactNode = <p className="faint">No price changes seen in this time.</p>;
  if (points.length > 0) {
    const t0 = now - days * 86400000;
    const t1 = now;
    const prices = points.map((p) => p.price).concat(min ?? [], max ?? []);
    const lo = Math.min(...prices) * 0.95;
    const hi = Math.max(...prices) * 1.05;
    const x = (iso: string) => ((new Date(iso).getTime() - t0) / (t1 - t0)) * W;
    const y = (v: number) => H - ((v - lo) / (hi - lo || 1)) * H;
    // Step line: a price holds until the next change, then up to now.
    let d = `M ${x(points[0].at)} ${y(points[0].price)}`;
    for (let i = 1; i < points.length; i++) d += ` H ${x(points[i].at)} V ${y(points[i].price)}`;
    d += ` H ${W}`;
    chart = (
      <>
        <svg className="spark" viewBox={`0 0 ${W} ${H}`} preserveAspectRatio="none">
          {min !== undefined && max !== undefined && <rect x={0} width={W} y={y(max)} height={Math.max(0, y(min) - y(max))} fill="var(--success-soft)" />}
          <path d={d} fill="none" stroke="var(--accent)" strokeWidth={2} vectorEffect="non-scaling-stroke" />
          {points.map((p, i) => (
            <circle key={i} cx={x(p.at)} cy={y(p.price)} r={3} fill={p.inStock ? "var(--success)" : "var(--text-3)"} vectorEffect="non-scaling-stroke">
              <title>
                {dateTime(p.at)}: {money(p.price, currency)} {p.inStock ? "in stock" : "out of stock"}
              </title>
            </circle>
          ))}
        </svg>
        <div className="row small faint" style={{ justifyContent: "space-between" }}>
          <span>{days} days ago</span>
          <span>green band: your price range · green dots: in stock</span>
          <span>now</span>
        </div>
      </>
    );
  }

  return (
    <div className="card stack">
      <div className="card-head" style={{ marginBottom: 0 }}>
        <h2>Price history</h2>
        <select value={days} onChange={(e) => setDays(Number(e.target.value))} style={{ width: "auto" }}>
          {[1, 7, 14, 30, 90].map((d) => (
            <option key={d} value={d}>
              {d} day{d === 1 ? "" : "s"}
            </option>
          ))}
        </select>
      </div>
      {chart}
    </div>
  );
}

/** What auto-buy on this page will do right now: real orders, dry runs, or nothing (follows Settings and the Dashboard switch). */
function AutoBuyNotice({ settings, product }: { settings: Settings; product?: Product }) {
  const limits = product
    ? `at most ${product.maxQuantity - product.bought} more ${product.name} (${product.bought} of ${product.maxQuantity} bought, ${product.quantityPerOrder} per order)` +
      (settings.totalBudget > 0 ? `, within the total budget of ${money(settings.totalBudget, settings.currency)}` : "")
    : "up to the product's units in all and the total budget";
  if (!settings.autoBuyArmed)
    return (
      <div className="notice small">
        <b>Auto-buy is off on the Dashboard:</b> when this page is in stock in range you&apos;re notified, but nothing is bought. Arm it on the
        Dashboard to buy.
      </div>
    );
  if (settings.dryRun)
    return (
      <div className="notice warn small">
        <b>Armed, in dry run:</b> when this page is in stock in range, the checkout runs up to placing the order, then stops. Nothing is
        bought. Turn dry run off in Settings to buy for real.
      </div>
    );
  if (product && product.bought >= product.maxQuantity)
    return (
      <div className="notice good small">
        <b>Armed for real, but done:</b> {product.bought} of {product.maxQuantity} {product.name} already bought, so nothing more is bought.
        Raise &quot;Units to buy in all&quot; on the product to buy more.
      </div>
    );
  return (
    <div className="notice bad small">
      <b>Armed for real:</b> when this page is in stock in range, it&apos;s ordered and paid for right away, {limits}.
      {settings.aiEnabled && settings.aiCheckOrder && " The AI checks the order (only this product, the right quantity, the total) before it's placed."}
    </div>
  );
}

/** The page's restock forecast: when, how sure, why, and what's reported online (with sources). */
function RestockCard({ siteId, aiOn }: { siteId: number; aiOn: boolean }) {
  const { data, error, setData } = useData(() => api.get<RestockForecast>(`/api/restock/site/${siteId}`), [siteId], 60000);
  const refresh = useAction();
  const now = useNow(60000);
  if (!data) return <ErrorNote error={error} />;
  const f = data;
  const look = async () => {
    const r = await refresh.run(() => api.post<RestockForecast>(`/api/restock/site/${siteId}/refresh`));
    if (r) setData(r);
  };
  return (
    <div className="card stack">
      <div className="card-head" style={{ marginBottom: 0 }}>
        <h2>Next restock</h2>
        <button className="btn small" disabled={!aiOn || refresh.busy} onClick={look} title={aiOn ? "One AI web search" : "The AI is off"}>
          {refresh.busy ? (
            <>
              <span className="spin" /> Looking online…
            </>
          ) : (
            "✨ Look online now"
          )}
        </button>
      </div>
      <div className="row" style={{ gap: 12 }}>
        <div style={{ fontSize: 22, fontWeight: 700 }}>{f.inStockNow ? "In stock now" : (f.when ?? "Not enough to go on yet")}</div>
        {!f.inStockNow && f.when && (
          <span className={`badge ${{ low: "", medium: "info", high: "good" }[f.confidence]}`}>
            {f.confidence} confidence · from {f.basis === "both" ? "history and online" : f.basis}
          </span>
        )}
      </div>
      <div className="muted small">{f.explanation}</div>
      <ErrorNote error={refresh.error} />
      <dl className="kv">
        <dt>Seen here</dt>
        <dd>
          {f.restocks.length === 0
            ? "No restocks seen yet."
            : `${f.restocks.length} restock${f.restocks.length === 1 ? "" : "s"}${f.historyPattern ? `: ${f.historyPattern}` : ""}. Last: ${dateTime(f.restocks[0])} (${ago(f.restocks[0], now)}).`}
        </dd>
        <dt>Online</dt>
        <dd>
          {f.onlineError ? (
            <span style={{ color: "var(--danger)" }}>{f.onlineError}</span>
          ) : f.onlineCheckedAt ? (
            <>
              {f.onlinePattern && <b>{f.onlinePattern}. </b>}
              {f.onlineSummary}
              <span className="faint"> (looked {ago(f.onlineCheckedAt, now)})</span>
            </>
          ) : (
            <span className="faint">Not looked up yet{aiOn ? ": it's done once a week by itself, or now with the button." : " (the AI is off)."}</span>
          )}
        </dd>
        {f.sources.length > 0 && (
          <>
            <dt>Sources</dt>
            <dd>
              {f.sources.map((s, i) => (
                <div key={i} className="ellipsis" style={{ maxWidth: 640 }}>
                  <a href={s.url} target="_blank" rel="noreferrer noopener">
                    {s.title}
                  </a>
                </div>
              ))}
            </dd>
          </>
        )}
      </dl>
      <div className="faint small">A guess from patterns, never a promise: stores restock when they choose. Times in {f.timeZone}.</div>
    </div>
  );
}
