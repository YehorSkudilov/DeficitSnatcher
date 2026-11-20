"use client";

import { HelperStores } from "./HelperStores";
import { useState } from "react";
import { api, type Product, type ProductInput, type RestockForecast, type Settings, type Site } from "@/lib/api";
import { money } from "@/lib/format";
import { Check, ErrorNote, Field, NumberInput, useAction, useCollapsed, useData, useNow, CollapseButton, PagesSummary } from "./ui";
import { CheckAllButton, rowResults, SiteTable } from "./Dashboard";
import { go } from "./App";
import { Logs } from "./Logs";
import { FindPages } from "./FindPages";

const blank: ProductInput = {
  name: "",
  enabled: true,
  minPrice: 0,
  maxPrice: 0,
  maxQuantity: 1,
  quantityPerOrder: 1,
  requiredKeywords: "",
  excludedKeywords: "",
};

/** Every product, each with its range and pages: #products. */
export function Products() {
  const products = useData(() => api.get<Product[]>("/api/products"), []);
  const sites = useData(() => api.get<Site[]>("/api/sites"), [], 5000);
  const settings = useData(() => api.get<Settings>("/api/settings"), []);
  const now = useNow();
  const fold = useCollapsed();
  const currency = settings.data?.currency ?? "CAD";
  const [checked, setChecked] = useState<Record<number, { ok: boolean; text: string }>>({});
  const forecasts = useData(() => api.get<Record<number, RestockForecast>>("/api/restock/product/0"), [], 60000);

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
      {products.data?.length === 0 && <div className="card muted">No products yet.</div>}
      {(products.data?.length ?? 0) > 1 && (
        <div className="row" style={{ justifyContent: "flex-end" }}>
          <button className="btn small ghost" onClick={() => fold.setAll((products.data ?? []).map((p) => p.id), !fold.any)}>
            {fold.any ? "Expand all" : "Collapse all"}
          </button>
        </div>
      )}
      {products.data?.map((p) => (
        <div className="card" key={p.id}>
          <div className="card-head" style={fold.isCollapsed(p.id) ? { marginBottom: 0 } : undefined}>
            <a className="row" href={`#product/${p.id}`} style={{ color: "inherit", textDecoration: "none" }}>
              <CollapseButton collapsed={fold.isCollapsed(p.id)} onClick={() => fold.toggle(p.id)} />
              <h2>{p.name}</h2>
              {!p.enabled && <span className="badge">Off</span>}
              <span className="badge info">
                {money(p.minPrice, currency)} – {money(p.maxPrice, currency)}
              </span>
              <span className="badge">
                Bought {p.bought}/{p.maxQuantity}
              </span>
            </a>
            <div className="row">
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
          {fold.isCollapsed(p.id) ? (
            <PagesSummary sites={(sites.data ?? []).filter((s) => s.productId === p.id)} />
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

      {p && sites.data && <HelperStores urls={sites.data.map((s) => s.url)} />}

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
  });
  const [saved, setSaved] = useState(false);
  const save = useAction();
  const set = (patch: Partial<ProductInput>) => (setP({ ...p, ...patch }), setSaved(false));

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
      </div>

      <div className="fields">
        <Field label="Name">
          <input type="text" value={p.name} onChange={(e) => set({ name: e.target.value })} placeholder="PS5 Pro" />
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
