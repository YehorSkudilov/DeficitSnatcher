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
  type Product,
  type Settings,
  type Site,
  type SiteInput,
  type SiteKind,
} from "@/lib/api";
import { ago, dateTime, money } from "@/lib/format";
import { Check, ErrorNote, Field, NumberInput, StockBadge, useAction, useData, useNow } from "./ui";
import { go } from "./App";

const kindHelp: Record<SiteKind, string> = {
  Auto: "Shopify product JSON if it's a Shopify /products/ page, otherwise the page's structured data, then your selectors.",
  Shopify: "A Shopify store's /products/… page: exact price and stock per variant. The most reliable.",
  Html: "Reads the page's HTML: structured data (JSON-LD, meta tags) and your selectors.",
  Browser: "Opens the page in headless Chromium first. For sites that build the page in script or block plain requests. Slower.",
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
          {id && (
            <a className="small" href={`#logs/scans?site=${id}`}>
              Scan history
            </a>
          )}
          {id && (
            <a className="small" href={`#logs/checkouts?site=${id}`}>
              Checkouts
            </a>
          )}
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
          <input type="url" value={form.url} placeholder="https://store.example.com/products/ps5-pro" onChange={(e) => set({ url: e.target.value })} />
        </Field>
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
                <b>Auto-buy</b> when it&apos;s a match
              </>
            }
            checked={form.autoBuy}
            onChange={(autoBuy) => set({ autoBuy })}
          />
        </div>
        {form.autoBuy && (
          <div className="notice warn small">
            Buys only while auto-buy is armed on the Dashboard{settings.data?.dryRun ? " (dry run is on, so checkouts stop before placing the order)" : ""}, up to the
            product&apos;s units in all and the total budget. Set up the checkout below and test it with a dry run first.
          </div>
        )}
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
            screenshot of every step open as soon as it starts.
          </p>
          <div className="row">
            <button className="btn" disabled={action.busy || form.checkoutSteps.length === 0} onClick={() => buy(true)}>
              Dry-run checkout
            </button>
            <button className="btn danger" disabled={action.busy || form.checkoutSteps.length === 0 || site?.lastPrice == null} onClick={() => buy(false)}>
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

      {id && <History siteId={id} min={products.data?.find((p) => p.id === form.productId)?.minPrice} max={products.data?.find((p) => p.id === form.productId)?.maxPrice} currency={currency} />}

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
        Run in headless Chromium, top to bottom. Selectors are Playwright&apos;s: CSS, <code>button:has-text(&apos;Add to cart&apos;)</code>,{" "}
        <code>text=Place order</code>. Values can use <code>{"{{url}}"}</code> <code>{"{{origin}}"}</code> <code>{"{{variantId}}"}</code>{" "}
        <code>{"{{quantity}}"}</code> and your checkout profile: <code>{"{{email}}"}</code> <code>{"{{firstName}}"}</code>{" "}
        <code>{"{{lastName}}"}</code> <code>{"{{fullName}}"}</code> <code>{"{{phone}}"}</code> <code>{"{{address1}}"}</code> <code>{"{{address2}}"}</code>{" "}
        <code>{"{{city}}"}</code> <code>{"{{province}}"}</code> <code>{"{{postalCode}}"}</code> <code>{"{{country}}"}</code>{" "}
        <code>{"{{card.number}}"}</code> <code>{"{{card.name}}"}</code> <code>{"{{card.expiryMonth}}"}</code> <code>{"{{card.expiryYear}}"}</code>{" "}
        <code>{"{{card.expiryYear2}}"}</code> <code>{"{{card.expiry}}"}</code> <code>{"{{card.cvv}}"}</code>. Mark the step that places the order as{" "}
        <b>final</b>: dry runs stop before it, and real buys need one. Card fields are usually inside an iframe: put the iframe&apos;s selector in
        &quot;in frame&quot;.
      </p>
      {steps.length === 0 && <p className="faint">No steps: this site can be watched, but not bought automatically.</p>}
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
