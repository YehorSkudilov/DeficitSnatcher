"use client";

import { useState } from "react";
import { api, type Product, type ProductInput, type Settings } from "@/lib/api";
import { money } from "@/lib/format";
import { Check, ErrorNote, Field, NumberInput, useAction, useData } from "./ui";

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

/** What to buy and the price range to buy it in. */
export function Products() {
  const products = useData(() => api.get<Product[]>("/api/products"), []);
  const settings = useData(() => api.get<Settings>("/api/settings"), []);
  const [adding, setAdding] = useState(false);
  const currency = settings.data?.currency ?? "CAD";

  return (
    <div className="stack" style={{ gap: 16 }}>
      <div className="page-head">
        <h1>Price ranges</h1>
        {!adding && (
          <button className="btn primary" onClick={() => setAdding(true)}>
            Add product
          </button>
        )}
      </div>
      <p className="muted" style={{ marginTop: -8 }}>
        A site is a match when it&apos;s in stock, its title fits the keywords, and its price plus the site&apos;s shipping is within the range. Only
        matches are ever bought.
      </p>
      <ErrorNote error={products.error} />
      {adding && <ProductForm initial={blank} currency={currency} onDone={() => (setAdding(false), void products.reload())} />}
      {products.data?.map((p) => <ProductForm key={p.id} initial={p} id={p.id} bought={p.bought} sites={p.sites} currency={currency} onDone={() => void products.reload()} />)}
    </div>
  );
}

function ProductForm({
  initial,
  id,
  bought,
  sites,
  currency,
  onDone,
}: {
  initial: ProductInput;
  id?: number;
  bought?: number;
  sites?: number;
  currency: string;
  onDone: () => void;
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
    const r = await save.run(() => (id ? api.put(`/api/products/${id}`, p) : api.post("/api/products", p)));
    if (r) {
      setSaved(true);
      onDone();
    }
  };

  const remove = async () => {
    if (!id || !confirm(`Delete ${p.name}, its ${sites ?? 0} sites and their history?`)) return;
    const ok = await save.run(async () => (await api.del(`/api/products/${id}`), true));
    if (ok) onDone();
  };

  return (
    <div className="card stack">
      <div className="card-head" style={{ marginBottom: 0 }}>
        <div className="row">
          <h2>{id ? p.name || "Product" : "New product"}</h2>
          {id !== undefined && (
            <span className="badge">
              Bought {bought}/{p.maxQuantity} · {sites} site{sites === 1 ? "" : "s"}
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
        <Field label={`Maximum price (${currency})`} hint="Including the site's shipping. Never bought above this.">
          <NumberInput value={p.maxPrice} min={0} step={0.01} onChange={(v) => set({ maxPrice: v ?? 0 })} />
        </Field>
        <Field label="Units to buy in all" hint="Over every site. Auto-buy stops once reached.">
          <NumberInput value={p.maxQuantity} min={0} onChange={(v) => set({ maxQuantity: v ?? 0 })} />
        </Field>
        <Field label="Units per order">
          <NumberInput value={p.quantityPerOrder} min={1} onChange={(v) => set({ quantityPerOrder: v ?? 1 })} />
        </Field>
      </div>

      <RangeBar min={p.minPrice} max={p.maxPrice} currency={currency} />

      <div className="grid two">
        <Field label="Title must contain" hint={'Comma-separated, all required; "a|b" means either. E.g. Pro, PS5|PlayStation 5'}>
          <input type="text" value={p.requiredKeywords} onChange={(e) => set({ requiredKeywords: e.target.value })} />
        </Field>
        <Field label="Title must not contain" hint="Comma-separated. E.g. controller, refurbished, case">
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
          <button className="btn ghost" onClick={onDone}>
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
