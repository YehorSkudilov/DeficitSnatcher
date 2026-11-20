"use client";

import { useState } from "react";
import { api, type RestockForecast, type CheckAllItem, type Dashboard as DashboardData, type ProbeResponse, type Product, type Site } from "@/lib/api";
import { ago, dateTime, host, money, storeIconSources, until } from "@/lib/format";
import { Check, ErrorNote, LevelDot, PurchaseBadge, StockBadge, useAction, useCollapsed, useData, useNow, CollapseButton, PagesSummary } from "./ui";
import { go } from "./App";

export function Dashboard() {
  const dash = useData(() => api.get<DashboardData>("/api/dashboard"), [], 5000);
  const sites = useData(() => api.get<Site[]>("/api/sites"), [], 5000);
  const products = useData(() => api.get<Product[]>("/api/products"), [], 15000);
  const now = useNow();
  const fold = useCollapsed();
  const autoBuyAction = useAction();
  const d = dash.data;
  const [checked, setChecked] = useState<Record<number, RowResult>>({});
  const forecasts = useData(() => api.get<Record<number, RestockForecast>>("/api/restock/product/0"), [], 60000);

  const refresh = () => {
    void sites.reload();
    void dash.reload();
  };
  const setAutoBuy = async (id: number, enabled: boolean) => {
    const updated = await autoBuyAction.run(() => api.post<Product>(`/api/products/${id}/auto-buy`, { enabled }));
    if (updated) await products.reload();
  };

  return (
    <div className="stack" style={{ gap: 16 }}>
      <div className="page-head">
        <h1>Dashboard</h1>
        <div className="row">
          <CheckAllButton count={sites.data?.length ?? 0} currency={d?.currency} onDone={(items) => (setChecked(rowResults(items, d?.currency)), refresh())} />
        </div>
      </div>
      <ErrorNote error={dash.error || autoBuyAction.error} />

      {d && (
        <div className="grid stats">
          <Stat label="In stock now" value={d.sitesInStock} tone={d.sitesInStock > 0 ? "good" : undefined} />
          <Stat label="Failing" value={d.sitesFailing} tone={d.sitesFailing > 0 ? "bad" : undefined} />
          <Stat label="Checks, last hour" value={d.checksLastHour} />
          <Stat label="Spent" value={money(d.spent, d.currency)} sub={d.totalBudget > 0 ? `of ${money(d.totalBudget, d.currency)}` : undefined} />
        </div>
      )}

      {products.data?.map((p) => (
        <div className="card" key={p.id} onClick={fold.isCollapsed(p.id) ? () => go(`product/${p.id}`) : undefined} style={fold.isCollapsed(p.id) ? { cursor: "pointer" } : undefined}>
          <div className="card-head" style={fold.isCollapsed(p.id) ? { marginBottom: 0 } : undefined}>
            <div className="row" style={{ gap: 8 }}>
              <CollapseButton collapsed={fold.isCollapsed(p.id)} onClick={() => fold.toggle(p.id)} />
              <a className="row product-card-title" href={`#product/${p.id}`}>
              <h2>{p.name}</h2>
              {!p.enabled && <span className="badge">Off</span>}
              <span className="badge info">
                {money(p.minPrice, d?.currency)} – {money(p.maxPrice, d?.currency)}
              </span>
              <span className="badge">
                Bought {p.bought}/{p.maxQuantity}
              </span>
              {p.autoBuyEnabled && <span className="badge bad">Auto-buy</span>}
              {p.checkingPaused && <span className="badge warn">Checks paused</span>}
              </a>
            </div>
            <div className="row" onClick={(e) => e.stopPropagation()}>
              <Check label="Auto-buy" checked={p.autoBuyEnabled} disabled={autoBuyAction.busy || !p.enabled} onChange={(enabled) => void setAutoBuy(p.id, enabled)} />
              <a className="btn small primary" href={`#site/new?product=${p.id}`}>
                Add page
              </a>
            </div>
          </div>
          {fold.isCollapsed(p.id) ? (
            <a className="product-summary-link" href={`#product/${p.id}`}><PagesSummary sites={(sites.data ?? []).filter((s) => s.productId === p.id)} /></a>
          ) : (
            <SiteTable sites={(sites.data ?? []).filter((s) => s.productId === p.id)} currency={d?.currency} now={now} onChanged={refresh} results={checked} forecasts={forecasts.data ?? undefined} />
          )}
        </div>
      ))}

      <div className="card">
          <div className="card-head">
            <h2>Alerts</h2>
          </div>
          <div className="events">
            {d?.recentEvents.length === 0 && d?.recentPurchases.length === 0 && <p className="muted">No in-stock alerts or checkout runs yet.</p>}
            {d?.recentEvents.length ? <div className="small muted">In stock</div> : null}
            {d?.recentEvents.map((e) => (
              <div className="event clickable" key={e.id} onClick={() => {
                const checkoutId = Number(e.message.match(/checkout #(\d+)/i)?.[1]);
                if (checkoutId) go(`purchase/${checkoutId}`);
                else if (e.siteId) go(`site/${e.siteId}`);
              }} title="Open alert details and screenshots">
                <LevelDot level={e.level} />
                <span>{e.message}</span>
                <span className="faint small" title={dateTime(e.at)}>
                  {ago(e.at, now)}
                </span>
              </div>
            ))}
            {d?.recentPurchases.length ? <div className="small muted" style={{ marginTop: 8 }}>Checkout runs</div> : null}
            {d?.recentPurchases.map((p) => (
              <div className="event clickable" key={`purchase-${p.id}`} onClick={() => go(`purchase/${p.id}`)} title="Open checkout details and screenshots">
                <PurchaseBadge status={p.status} />
                <span>{p.productName} at {p.siteName}, {money(p.price, d.currency)} Â· {p.message}</span>
                <span className="faint small" title={dateTime(p.startedAt)}>{ago(p.startedAt, now)}</span>
              </div>
            ))}
          </div>
      </div>
    </div>
  );
}

function Stat({ label, value, sub, tone }: { label: string; value: React.ReactNode; sub?: string; tone?: "good" | "bad" }) {
  return (
    <div className="card stat">
      <div className="label">{label}</div>
      <div className="value" style={{ color: tone === "good" ? "var(--success)" : tone === "bad" ? "var(--danger)" : undefined }}>
        {value}
      </div>
      {sub && <div className="faint small">{sub}</div>}
    </div>
  );
}

export type RowResult = { ok: boolean; text: string };

function describe(r: ProbeResponse | CheckAllItem, currency?: string): RowResult {
  const error = r.error;
  if (error) return { ok: false, text: error.length > 90 ? error.slice(0, 90) + "…" : error };
  const price = "result" in r ? r.result?.price : r.price;
  const inStock = "result" in r ? r.result?.inStock : r.inStock;
  const match = "verdict" in r && typeof r.verdict === "object" ? r.verdict?.match : (r as CheckAllItem).match;
  return {
    ok: true,
    text: `${money(price ?? null, currency)} · ${inStock === true ? "in stock" : inStock === false ? "out of stock" : "stock unknown"}${match ? " · IN RANGE" : ""}`,
  };
}

/** Checks every page (of one product, or all) now, and says how it went. */
export function CheckAllButton({ productId, count, onDone }: { productId?: number; count: number; currency?: string; onDone: (items: CheckAllItem[]) => void }) {
  const run = useAction();
  const [summary, setSummary] = useState<string | null>(null);
  const checkAll = async () => {
    setSummary(null);
    const items = await run.run(() => api.post<CheckAllItem[]>(`/api/sites/check-all${productId ? `?productId=${productId}` : ""}`));
    if (!items) return;
    const inStock = items.filter((i) => i.inStock === true).length;
    const inRange = items.filter((i) => i.match).length;
    const failed = items.filter((i) => i.error).length;
    setSummary(`${items.length} checked: ${inStock} in stock${inRange ? `, ${inRange} in range` : ""}${failed ? `, ${failed} failed` : ""}.`);
    onDone(items);
  };
  return (
    <span className="row" style={{ gap: 8 }}>
      {summary && <span className="small muted">{summary}</span>}
      {run.error && <span className="small" style={{ color: "var(--danger)" }}>{run.error}</span>}
      <button className="btn small" disabled={run.busy || count === 0} onClick={checkAll}>
        {run.busy ? (
          <>
            <span className="spin" /> Checking {count}…
          </>
        ) : (
          "Check all"
        )}
      </button>
    </span>
  );
}

/** A product's pages with their latest check; each row's Check button checks that page now and shows what it found. */
export function SiteTable({
  sites,
  currency,
  now,
  onChanged,
  results: outside,
  forecasts,
}: {
  sites: Site[];
  currency?: string;
  now: number;
  onChanged: () => void;
  /** Results of a Check all, shown on the rows. */
  results?: Record<number, RowResult>;
  /** Each page's restock forecast. */
  forecasts?: Record<number, RestockForecast>;
}) {
  const [checking, setChecking] = useState<Set<number>>(new Set());
  const [results, setResults] = useState<Record<number, RowResult>>({});
  const shown = { ...outside, ...results };

  const check = async (id: number) => {
    setChecking((c) => new Set(c).add(id));
    try {
      const r = await api.post<ProbeResponse>(`/api/sites/${id}/check`);
      setResults((x) => ({ ...x, [id]: describe(r, currency) }));
    } catch (e) {
      setResults((x) => ({ ...x, [id]: { ok: false, text: e instanceof Error ? e.message : String(e) } }));
    } finally {
      setChecking((c) => {
        const n = new Set(c);
        n.delete(id);
        return n;
      });
      onChanged();
    }
  };

  if (sites.length === 0) return <p className="muted">No pages yet. Add the product pages of the stores that sell it.</p>;
  return (
    <div className="table-wrap">
      <table>
        <thead>
          <tr>
            <th>Page</th>
            <th>Price</th>
            <th>Stock</th>
            <th>Verdict</th>
            <th>Checked</th>
            <th>Next check</th>
            <th>Next restock</th>
            <th />
          </tr>
        </thead>
        <tbody>
          {sites.map((s) => (
            <tr key={s.id} className="clickable" onClick={() => go(`site/${s.id}`)}>
              <td>
                <div className="row" style={{ gap: 6, flexWrap: "nowrap" }}>
                  <StoreIcon key={s.url} name={s.name} url={s.url} />
                  <b className="ellipsis" style={{ maxWidth: 200 }}>
                    {s.name}
                  </b>
                  {!s.enabled && <span className="badge">Off</span>}
                </div>
                <div className="faint small">{host(s.url)}</div>
              </td>
              <td className="num">
                {money(s.lastPrice, currency)}
                {s.shippingCost > 0 && <div className="faint small">+{money(s.shippingCost, currency)} ship</div>}
              </td>
              <td>
                <StockBadge site={s} />
              </td>
              <td className="small" style={{ maxWidth: 280 }}>
                {s.match ? <span className="badge good">BUY ZONE</span> : <span className="muted clamp" title={s.lastError ?? s.verdict}>{s.lastError ?? s.verdict}</span>}
              </td>
              <td className="small muted">{s.checking || checking.has(s.id) ? "checking…" : ago(s.lastCheckedAt, now)}</td>
              <td className="small muted">{s.enabled ? until(s.nextCheckAt, now) : "—"}</td>
              <td className="small" style={{ minWidth: 130 }}>
                <RestockCell f={forecasts?.[s.id]} />
              </td>
              <td style={{ minWidth: 120 }}>
                <button
                  className="btn small"
                  disabled={checking.has(s.id)}
                  onClick={(e) => {
                    e.stopPropagation();
                    void check(s.id);
                  }}
                >
                  {checking.has(s.id) ? (
                    <>
                      <span className="spin" /> Checking
                    </>
                  ) : (
                    "Check"
                  )}
                </button>
                {shown[s.id] && (
                  <div className="small" style={{ marginTop: 4, color: shown[s.id].ok ? "var(--text-2)" : "var(--danger)", maxWidth: 220 }}>
                    {shown[s.id].text}
                  </div>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

/** Check all's results by page, for the rows. */
export function rowResults(items: CheckAllItem[], currency?: string): Record<number, RowResult> {
  return Object.fromEntries(items.map((i) => [i.siteId, describe(i, currency)]));
}

function StoreIcon({ name, url }: { name: string; url: string }) {
  const sources = storeIconSources(url);
  const [sourceIndex, setSourceIndex] = useState(0);
  return (
    <span className="store-icon" aria-label={`${name} store`}>
      {sourceIndex < sources.length ? <img src={sources[sourceIndex]} alt="" onError={() => setSourceIndex((i) => i + 1)} /> : <span>{name.trim().slice(0, 1).toUpperCase()}</span>}
    </span>
  );
}

/** A page's next restock in a word: when, and how sure (details on its page). */
export function RestockCell({ f }: { f?: RestockForecast }) {
  if (!f) return <span className="faint">—</span>;
  if (f.inStockNow) return <span className="badge good">In stock now</span>;
  if (!f.when && f.onlineCheckedAt) return <span className="faint" title={f.onlineError ?? f.explanation}>No estimate</span>;
  if (!f.when) return <span className="faint" title={f.explanation}>Learning…</span>;
  const cls = { low: "", medium: "info", high: "good" }[f.confidence];
  return (
    <span title={f.explanation}>
      {f.when}
      <div>
        <span className={`badge ${cls}`}>{f.confidence === "high" ? "likely" : f.confidence === "medium" ? "maybe" : "guess"}</span>
      </div>
    </span>
  );
}
