"use client";

import { useState } from "react";
import { api, type RestockForecast, type CheckAllItem, type Dashboard as DashboardData, type ProbeResponse, type Product, type Settings, type Site } from "@/lib/api";
import { ago, dateTime, host, money, until } from "@/lib/format";
import { ErrorNote, LevelDot, PurchaseBadge, StockBadge, useAction, useData, useNow } from "./ui";
import { go } from "./App";

export function Dashboard() {
  const dash = useData(() => api.get<DashboardData>("/api/dashboard"), [], 5000);
  const sites = useData(() => api.get<Site[]>("/api/sites"), [], 5000);
  const products = useData(() => api.get<Product[]>("/api/products"), [], 15000);
  const arm = useAction();
  const now = useNow();
  const d = dash.data;
  const [checked, setChecked] = useState<Record<number, RowResult>>({});
  const forecasts = useData(() => api.get<Record<number, RestockForecast>>("/api/restock/product/0"), [], 60000);

  const setArmed = async (armed: boolean) => {
    if (armed && !d?.dryRun && !confirm("Arm auto-buy for REAL purchases? Matches in your price range will be ordered and paid for.")) return;
    const s = await arm.run(() => api.post<Settings>("/api/settings/arm", { armed }));
    if (s) void dash.reload();
  };

  const refresh = () => {
    void sites.reload();
    void dash.reload();
  };

  return (
    <div className="stack" style={{ gap: 16 }}>
      <div className="page-head">
        <h1>Dashboard</h1>
        <div className="row">
          {d && !d.trackingEnabled && <span className="badge warn">Tracking paused (Settings)</span>}
          <CheckAllButton count={sites.data?.length ?? 0} currency={d?.currency} onDone={(items) => (setChecked(rowResults(items, d?.currency)), refresh())} />
        </div>
      </div>
      <ErrorNote error={dash.error || arm.error} />

      {d && (
        <div className={`arm ${d.autoBuyArmed ? "on" : ""} ${d.dryRun ? "dry" : ""}`}>
          <div className="stack" style={{ gap: 4 }}>
            <h2>{d.autoBuyArmed ? (d.dryRun ? "Auto-buy armed: dry run" : "Auto-buy ARMED") : "Auto-buy off"}</h2>
            <div className="muted small">
              {d.autoBuyArmed
                ? d.dryRun
                  ? "Matches run the checkout up to the final step, then stop. Turn dry run off in Settings to buy for real."
                  : "A site in stock in your price range will be bought right away."
                : "Sites are watched and you're told about matches, but nothing is bought."}
            </div>
          </div>
          <button className={`toggle ${d.autoBuyArmed ? "on" : ""}`} aria-label="Arm auto-buy" disabled={arm.busy} onClick={() => setArmed(!d.autoBuyArmed)} />
        </div>
      )}

      {d && (
        <div className="grid stats">
          <Stat label="Sites watched" value={`${d.sitesEnabled}/${d.sites}`} />
          <Stat label="In stock now" value={d.sitesInStock} tone={d.sitesInStock > 0 ? "good" : undefined} />
          <Stat label="Failing" value={d.sitesFailing} tone={d.sitesFailing > 0 ? "bad" : undefined} />
          <Stat label="Checks, last hour" value={d.checksLastHour} />
          <Stat label="Spent" value={money(d.spent, d.currency)} sub={d.totalBudget > 0 ? `of ${money(d.totalBudget, d.currency)}` : undefined} />
        </div>
      )}

      {products.data?.map((p) => (
        <div className="card" key={p.id}>
          <div className="card-head">
            <div className="row">
              <h2>{p.name}</h2>
              {!p.enabled && <span className="badge">Off</span>}
              <span className="badge info">
                {money(p.minPrice, d?.currency)} – {money(p.maxPrice, d?.currency)}
              </span>
              <span className="badge">
                Bought {p.bought}/{p.maxQuantity}
              </span>
            </div>
            <div className="row">
              <a className="btn small" href={`#product/${p.id}`}>
                Edit range
              </a>
              <a className="btn small primary" href={`#site/new?product=${p.id}`}>
                Add page
              </a>
            </div>
          </div>
          <SiteTable sites={(sites.data ?? []).filter((s) => s.productId === p.id)} currency={d?.currency} now={now} onChanged={refresh} results={checked} forecasts={forecasts.data ?? undefined} />
        </div>
      ))}

      <div className="grid two">
        <div className="card">
          <div className="card-head">
            <h2>Recent activity</h2>
          </div>
          <div className="events">
            {d?.recentEvents.length === 0 && <p className="muted">Nothing yet.</p>}
            {d?.recentEvents.map((e) => (
              <div className="event" key={e.id}>
                <LevelDot level={e.level} />
                <span>{e.message}</span>
                <span className="faint small" title={dateTime(e.at)}>
                  {ago(e.at, now)}
                </span>
              </div>
            ))}
          </div>
        </div>
        <div className="card">
          <div className="card-head">
            <h2>Checkouts</h2>
          </div>
          {d?.recentPurchases.length === 0 && <p className="muted">No checkouts yet.</p>}
          <div className="events">
            {d?.recentPurchases.map((p) => (
              <div className="event clickable" key={p.id} style={{ gridTemplateColumns: "auto 1fr auto", cursor: "pointer" }} onClick={() => go(`purchase/${p.id}`)}>
                <PurchaseBadge status={p.status} />
                <span className="ellipsis">
                  {p.productName} at {p.siteName}, {money(p.price, d.currency)}
                </span>
                <span className="faint small">{ago(p.startedAt, now)}</span>
              </div>
            ))}
          </div>
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
                  <b className="ellipsis" style={{ maxWidth: 200 }}>
                    {s.name}
                  </b>
                  {s.autoBuy && <span className="badge bad">Auto-buy</span>}
                  {s.runOnPc && <span className="badge info">PC</span>}
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
                {s.match ? <span className="badge good">BUY ZONE</span> : <span className="muted">{s.lastError ?? s.verdict}</span>}
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

/** A page's next restock in a word: when, and how sure (details on its page). */
export function RestockCell({ f }: { f?: RestockForecast }) {
  if (!f) return <span className="faint">—</span>;
  if (f.inStockNow) return <span className="badge good">In stock now</span>;
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
