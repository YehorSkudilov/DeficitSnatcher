"use client";

import { useState } from "react";
import { api, type AiStatus, type Product, type ProductInput, type RestockForecast, type Settings, type Site } from "@/lib/api";
import { money } from "@/lib/format";
import { Check, ErrorNote, Field, NumberInput, useAction, useCollapsed, useData, useNow, CollapseButton, PagesSummary } from "./ui";
import { CheckAllButton, rowResults, SiteTable } from "./Dashboard";
import { go } from "./App";
import { Logs } from "./Logs";
import { FindPages } from "./FindPages";
import { InStockNow } from "./InStockNow";

const blank: ProductInput = {
  name: "",
  enabled: true,
  minPrice: 0,
  maxPrice: 0,
  maxQuantity: 1,
  quantityPerOrder: 1,
  requiredKeywords: "",
  excludedKeywords: "",
  autoBuyEnabled: false,
};

/** Every product, each with its range and pages: #products. */
export function Products() {
  const products = useData(() => api.get<Product[]>("/api/products"), [], 5000);
  const sites = useData(() => api.get<Site[]>("/api/sites"), [], 5000);
  const settings = useData(() => api.get<Settings>("/api/settings"), []);
  const now = useNow();
  const fold = useCollapsed();
  const pauseAction = useAction();
  const [cooldowns, setCooldowns] = useState<Record<number, number>>({});
  const currency = settings.data?.currency ?? "CAD";
  const [checked, setChecked] = useState<Record<number, { ok: boolean; text: string }>>({});
  const forecasts = useData(() => api.get<Record<number, RestockForecast>>("/api/restock/product/0"), [], 60000);
  const setCheckingPaused = async (id: number, paused: boolean, cooldownMinutes?: number) => {
    const updated = await pauseAction.run(() => api.post<Product>(`/api/products/${id}/checking`, { paused, cooldownMinutes }));
    if (updated) await products.reload();
  };
  const setAutoBuy = async (id: number, enabled: boolean) => {
    const updated = await pauseAction.run(() => api.post<Product>(`/api/products/${id}/auto-buy`, { enabled }));
    if (updated) await products.reload();
  };

  return (
    <div className="stack" style={{ gap: 16 }}>
      <div className="page-head">
        <h1>Products</h1>
        <a className="btn primary" href="#product/new">
          Add product
        </a>
      </div>
      <p className="muted" style={{ marginTop: -8 }}>
        Each product has a price range and the store pages that sell it. A page is a match when it&apos;s in stock, its title fits the
        keywords, and its price plus shipping is within the range. Only matches are ever bought.
      </p>
      <ErrorNote error={products.error} />
      <InStockNow currency={currency} />
      {products.data?.length === 0 && <div className="card muted">No products yet.</div>}
      {(products.data?.length ?? 0) > 1 && (
        <div className="row" style={{ justifyContent: "flex-end" }}>
          <button className="btn small ghost" onClick={() => {
            const ids = (products.data ?? []).map((p) => p.id);
            fold.setAll(ids, fold.allExpanded(ids));
          }}>
            {fold.allExpanded((products.data ?? []).map((p) => p.id)) ? "Collapse all" : "Expand all"}
          </button>
        </div>
      )}
      {products.data?.map((p) => (
        <div className="card" key={p.id} onClick={fold.isCollapsed(p.id) ? () => go(`product/${p.id}`) : undefined} style={fold.isCollapsed(p.id) ? { cursor: "pointer" } : undefined}>
          <div className="card-head" style={fold.isCollapsed(p.id) ? { marginBottom: 0 } : undefined}>
            <div className="row" style={{ gap: 8 }}>
              <CollapseButton collapsed={fold.isCollapsed(p.id)} onClick={() => fold.toggle(p.id)} />
              <a className="row" href={`#product/${p.id}`} style={{ color: "inherit", textDecoration: "none" }}>
              <h2>{p.name}</h2>
              {!p.enabled && <span className="badge">Off</span>}
              <span className="badge info">
                {money(p.minPrice, currency)} – {money(p.maxPrice, currency)}
              </span>
              <span className="badge">
                Bought {p.bought}/{p.maxQuantity}
              </span>
              {p.autoBuyEnabled && <span className="badge bad">Auto-buy</span>}
              </a>
            </div>
            <div className="row" onClick={(e) => e.stopPropagation()}>
              <CheckAllButton
                productId={p.id}
                count={(sites.data ?? []).filter((s) => s.productId === p.id).length}
                currency={currency}
                onDone={(items) => (setChecked((c) => ({ ...c, ...rowResults(items, currency) })), void sites.reload())}
              />
              <a className="btn small" href={`#product/${p.id}`}>
                Edit
              </a>
              <a className="btn small primary" href={`#site/new?product=${p.id}`}>
                Add page
              </a>
            </div>
          </div>
          <div className="product-controls" onClick={(e) => e.stopPropagation()}>
            <Check label="Scheduled checks" checked={!p.checkingPaused} disabled={pauseAction.busy} onChange={(enabled) => void setCheckingPaused(p.id, !enabled)} />
            <Check label="Auto-buy this product" checked={p.autoBuyEnabled} disabled={pauseAction.busy || !p.enabled} onChange={(enabled) => void setAutoBuy(p.id, enabled)} />
            <div className="row product-pause-controls">
              <span className="small muted">Pause for</span>
              <NumberInput value={cooldowns[p.id] ?? 30} min={1} onChange={(v) => setCooldowns((old) => ({ ...old, [p.id]: Math.max(1, Math.min(10080, v ?? 30)) }))} />
              <span className="small muted">minutes</span>
              <button className="btn small" disabled={pauseAction.busy} onClick={() => void setCheckingPaused(p.id, true, cooldowns[p.id] ?? 30)}>Pause</button>
              <button className="btn small ghost" disabled={pauseAction.busy || p.checkingPaused && !p.checkingResumeAt} onClick={() => void setCheckingPaused(p.id, true)}>Turn off indefinitely</button>
              {p.checkingPaused && <span className="small muted">{p.checkingResumeAt ? `Paused until ${new Date(p.checkingResumeAt).toLocaleString()}` : "Paused indefinitely"}</span>}
            </div>
          </div>
          <ErrorNote error={pauseAction.error} />
          {fold.isCollapsed(p.id) ? (
            <a className="product-summary-link" href={`#product/${p.id}`}><PagesSummary sites={(sites.data ?? []).filter((s) => s.productId === p.id)} /></a>
          ) : (
            <SiteTable sites={(sites.data ?? []).filter((s) => s.productId === p.id)} currency={currency} now={now} onChanged={() => void sites.reload()} results={checked} forecasts={forecasts.data ?? undefined} />
          )}
        </div>
      ))}
    </div>
  );
}

/** One product: its range, limits, keywords and its pages: #product/3, #product/new. */
export function ProductPage({ id }: { id: number | null }) {
  const products = useData(() => api.get<Product[]>("/api/products"), [id]);
  const sites = useData(() => (id ? api.get<Site[]>(`/api/sites?productId=${id}`) : Promise.resolve([] as Site[])), [id], 5000);
  const settings = useData(() => api.get<Settings>("/api/settings"), []);
  const now = useNow();
  const currency = settings.data?.currency ?? "CAD";
  const p = id ? products.data?.find((x) => x.id === id) : undefined;
  const [checked, setChecked] = useState<Record<number, { ok: boolean; text: string }>>({});
  const forecasts = useData(() => api.get<Record<number, RestockForecast>>(`/api/restock/product/${id ?? 0}`), [id], 60000);
  const [finding, setFinding] = useState(false);

  if (id && !p)
    return products.data ? (
      <p className="muted">
        No such product. <a href="#products">Products</a>
      </p>
    ) : (
      <ErrorNote error={products.error} />
    );

  return (
    <div className="stack" style={{ gap: 16 }}>
      <div className="page-head">
        <div className="row">
          <a href="#products" className="btn ghost small">
            ← Products
          </a>
          <h1>{p ? p.name : "New product"}</h1>
          {p && !p.enabled && <span className="badge">Off</span>}
        </div>
        {p && (
          <button className="btn small" onClick={() => document.getElementById("logs")?.scrollIntoView({ behavior: "smooth" })}>
            Logs ↓
          </button>
        )}
      </div>

      <ProductForm
        key={p?.id ?? "new"}
        initial={p ?? blank}
        id={p?.id}
        bought={p?.bought}
        sites={p?.sites}
        currency={currency}
        onSaved={(saved) => (id ? void products.reload() : go(`product/${saved.id}`))}
        onDeleted={() => go("products")}
        onCancel={() => go("products")}
      />

      {p && (
        <div className="card">
          <div className="card-head">
            <h2>Pages</h2>
            <div className="row">
              <CheckAllButton
                productId={p.id}
                count={sites.data?.length ?? 0}
                currency={currency}
                onDone={(items) => (setChecked(rowResults(items, currency)), void sites.reload())}
              />
              <button className="btn small" onClick={() => setFinding(!finding)}>
                ✨ Find pages
              </button>
              <a className="btn small primary" href={`#site/new?product=${p.id}`}>
                Add page
              </a>
            </div>
          </div>
          {finding && (
            <div style={{ marginBottom: 14 }}>
              <FindPages productId={p.id} productName={p.name} currency={currency} onAdded={() => (void sites.reload(), void products.reload())} onClose={() => setFinding(false)} />
            </div>
          )}
          <SiteTable sites={sites.data ?? []} currency={currency} now={now} onChanged={() => void sites.reload()} results={checked} forecasts={forecasts.data ?? undefined} />
        </div>
      )}

      {p && (
        <div id="logs">
          <Logs key={p.id} productId={p.id} />
        </div>
      )}
    </div>
  );
}

function ProductForm({
  initial,
  id,
  bought,
  sites,
  currency,
  onSaved,
  onDeleted,
  onCancel,
}: {
  initial: ProductInput;
  id?: number;
  bought?: number;
  sites?: number;
  currency: string;
  onSaved: (p: Product) => void;
  onDeleted: () => void;
  onCancel: () => void;
}) {
  const [p, setP] = useState<ProductInput>({
    name: initial.name,
    enabled: initial.enabled,
    minPrice: initial.minPrice,
    maxPrice: initial.maxPrice,
    maxQuantity: initial.maxQuantity,
    quantityPerOrder: initial.quantityPerOrder,
    requiredKeywords: initial.requiredKeywords,
    excludedKeywords: initial.excludedKeywords,
    autoBuyEnabled: initial.autoBuyEnabled,
  });
  const [saved, setSaved] = useState(false);
  const save = useAction();
  const set = (patch: Partial<ProductInput>) => (setP((old) => ({ ...old, ...patch })), setSaved(false));
  const ai = useData(() => api.get<AiStatus>("/api/ai/status"), []);
  const aiOn = !!ai.data?.hasKey && !!ai.data?.enabled;
  const fill = useAction();
  const [fillNote, setFillNote] = useState<string | null>(null);

  const aiFill = async () => {
    setFillNote(null);
    const r = await fill.run(() => api.post<AiFill>("/api/ai/product-fill", { name: p.name }));
    if (!r) return;
    set({
      name: r.name || p.name,
      ...(r.minPrice != null ? { minPrice: r.minPrice } : {}),
      ...(r.maxPrice != null ? { maxPrice: r.maxPrice } : {}),
      ...(r.requiredKeywords ? { requiredKeywords: r.requiredKeywords } : {}),
      ...(r.excludedKeywords ? { excludedKeywords: r.excludedKeywords } : {}),
    });
    setFillNote(r.note ?? "Filled in. Check the values before saving.");
  };

  const submit = async () => {
    const r = await save.run(() => (id ? api.put<Product>(`/api/products/${id}`, p) : api.post<Product>("/api/products", p)));
    if (r) {
      setSaved(true);
      onSaved(r);
    }
  };

  const remove = async () => {
    if (!id || !confirm(`Delete ${p.name}, its ${sites ?? 0} page${sites === 1 ? "" : "s"}, their scans and checkouts?`)) return;
    const ok = await save.run(async () => (await api.del(`/api/products/${id}`), true));
    if (ok) onDeleted();
  };

  return (
    <div className="card stack">
      <div className="card-head" style={{ marginBottom: 0 }}>
        <div className="row">
          <h2>Price range and limits</h2>
          {id !== undefined && (
            <span className="badge">
              Bought {bought}/{p.maxQuantity} · {sites} page{sites === 1 ? "" : "s"}
            </span>
          )}
        </div>
        <Check label="Enabled" checked={p.enabled} onChange={(enabled) => set({ enabled })} />
        <Check label="Auto-buy this product" checked={p.autoBuyEnabled} onChange={(autoBuyEnabled) => set({ autoBuyEnabled })} />
      </div>
      <p className="small muted">When this product matches, checkout starts automatically. The phone alarm still rings at the same time; dismissing it opens the store or cart.</p>

      <div className="fields">
        <Field
          label="Name"
          hint={aiOn ? "Type the name, then ✨ AI fill searches the web and fills in the price range and keywords." : ai.data ? "AI fill needs the AI (Settings → AI)." : undefined}
        >
          <div className="row" style={{ flexWrap: "nowrap" }}>
            <input type="text" value={p.name} onChange={(e) => set({ name: e.target.value })} placeholder="PS5 Pro" style={{ flex: 1, minWidth: 0 }} />
            <button type="button" className="btn small" disabled={!aiOn || fill.busy || !p.name.trim()} onClick={aiFill} title="Fill the price range and keywords with AI">
              {fill.busy ? "Filling…" : "✨ AI fill"}
            </button>
          </div>
        </Field>
        <Field label={`Minimum price (${currency})`} hint="Anything cheaper is taken as a wrong listing (accessory, scam) and never bought.">
          <NumberInput value={p.minPrice} min={0} step={0.01} onChange={(v) => set({ minPrice: v ?? 0 })} />
        </Field>
        <Field label={`Maximum price (${currency})`} hint="Including the page's shipping. Never bought above this.">
          <NumberInput value={p.maxPrice} min={0} step={0.01} onChange={(v) => set({ maxPrice: v ?? 0 })} />
        </Field>
        <Field label="Units to buy in all" hint="Over every page. Auto-buy stops once reached.">
          <NumberInput value={p.maxQuantity} min={0} onChange={(v) => set({ maxQuantity: v ?? 0 })} />
        </Field>
        <Field label="Units per order">
          <NumberInput value={p.quantityPerOrder} min={1} onChange={(v) => set({ quantityPerOrder: v ?? 1 })} />
        </Field>
      </div>

      <ErrorNote error={fill.error} />
      {fillNote && <p className="small muted" style={{ margin: 0 }}>✨ {fillNote}</p>}

      <RangeBar min={p.minPrice} max={p.maxPrice} currency={currency} />

      <div className="grid two">
        <Field label="Title must contain" hint={'Comma-separated, all required; "a|b" means either. Spaces, case and ®/™ are ignored. E.g. PS5 Pro|PlayStation 5 Pro'}>
          <input type="text" value={p.requiredKeywords} onChange={(e) => set({ requiredKeywords: e.target.value })} />
        </Field>
        <Field label="Title must not contain" hint="Comma-separated. E.g. Slim, Digital Edition, controller, refurbished">
          <input type="text" value={p.excludedKeywords} onChange={(e) => set({ excludedKeywords: e.target.value })} />
        </Field>
      </div>

      <ErrorNote error={save.error} />
      <div className="row">
        <button className="btn primary" disabled={save.busy} onClick={submit}>
          {save.busy ? "Saving…" : id ? "Save" : "Add"}
        </button>
        {saved && <span className="badge good">Saved</span>}
        <div style={{ flex: 1 }} />
        {id ? (
          <button className="btn danger" disabled={save.busy} onClick={remove}>
            Delete
          </button>
        ) : (
          <button className="btn ghost" onClick={onCancel}>
            Cancel
          </button>
        )}
      </div>
    </div>
  );
}

type AiFill = { name: string; minPrice: number | null; maxPrice: number | null; requiredKeywords: string; excludedKeywords: string; note: string | null };

function RangeBar({ min, max, currency }: { min: number; max: number; currency: string }) {
  if (!(max > 0) || min > max) return null;
  const top = max * 1.25;
  return (
    <div>
      <div style={{ position: "relative", height: 10, borderRadius: 99, background: "var(--surface-3)" }}>
        <div
          style={{
            position: "absolute",
            left: `${(min / top) * 100}%`,
            width: `${((max - min) / top) * 100}%`,
            top: 0,
            bottom: 0,
            borderRadius: 99,
            background: "var(--success)",
          }}
        />
      </div>
      <div className="row small faint" style={{ justifyContent: "space-between", marginTop: 4 }}>
        <span>{money(0, currency)}</span>
        <span style={{ color: "var(--success)" }}>
          Buys between {money(min, currency)} and {money(max, currency)}
        </span>
        <span>{money(top, currency)}</span>
      </div>
    </div>
  );
}
