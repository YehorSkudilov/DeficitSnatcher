"use client";

import { useState } from "react";
import { api, type Product, type ProductInput, type Settings, type Site } from "@/lib/api";
import { money } from "@/lib/format";
import { Check, ErrorNote, Field, NumberInput, useAction, useData, useNow } from "./ui";
import { SiteTable } from "./Dashboard";
import { go } from "./App";

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
  const check = useAction();
  const now = useNow();
  const currency = settings.data?.currency ?? "CAD";

  const checkNow = async (id: number) => {
    await check.run(() => api.post(`/api/sites/${id}/check`));
    void sites.reload();
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
      <ErrorNote error={products.error || check.error} />
      {products.data?.length === 0 && <div className="card muted">No products yet.</div>}
      {products.data?.map((p) => (
        <div className="card" key={p.id}>
          <div className="card-head">
            <a className="row" href={`#product/${p.id}`} style={{ color: "inherit", textDecoration: "none" }}>
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
              <a className="btn small" href={`#product/${p.id}`}>
                Edit
              </a>
              <a className="btn small primary" href={`#site/new?product=${p.id}`}>
                Add page
              </a>
            </div>
          </div>
          <SiteTable sites={(sites.data ?? []).filter((s) => s.productId === p.id)} currency={currency} now={now} onCheck={checkNow} busy={check.busy} />
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
  const check = useAction();
  const now = useNow();
  const currency = settings.data?.currency ?? "CAD";
  const p = id ? products.data?.find((x) => x.id === id) : undefined;

  const checkNow = async (siteId: number) => {
    await check.run(() => api.post(`/api/sites/${siteId}/check`));
    void sites.reload();
  };

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
          <div className="row">
            <a className="small" href={`#logs/scans?product=${p.id}`}>
              Scans
            </a>
            <a className="small" href={`#logs/checkouts?product=${p.id}`}>
              Checkouts
            </a>
          </div>
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
            <a className="btn small primary" href={`#site/new?product=${p.id}`}>
              Add page
            </a>
          </div>
          <ErrorNote error={check.error} />
          <SiteTable sites={sites.data ?? []} currency={currency} now={now} onCheck={checkNow} busy={check.busy} />
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
