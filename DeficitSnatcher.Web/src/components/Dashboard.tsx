"use client";

import { api, type Dashboard as DashboardData, type Product, type Settings, type Site } from "@/lib/api";
import { ago, dateTime, host, money, until } from "@/lib/format";
import { ErrorNote, LevelDot, PurchaseBadge, StockBadge, useAction, useData, useNow } from "./ui";
import { go } from "./App";

export function Dashboard() {
  const dash = useData(() => api.get<DashboardData>("/api/dashboard"), [], 5000);
  const sites = useData(() => api.get<Site[]>("/api/sites"), [], 5000);
  const products = useData(() => api.get<Product[]>("/api/products"), [], 15000);
  const arm = useAction();
  const check = useAction();
  const now = useNow();
  const d = dash.data;

  const setArmed = async (armed: boolean) => {
    if (armed && !d?.dryRun && !confirm("Arm auto-buy for REAL purchases? Matches in your price range will be ordered and paid for.")) return;
    const s = await arm.run(() => api.post<Settings>("/api/settings/arm", { armed }));
    if (s) void dash.reload();
  };

  const checkNow = async (id: number) => {
    await check.run(() => api.post(`/api/sites/${id}/check`));
    void sites.reload();
    void dash.reload();
  };

  return (
    <div className="stack" style={{ gap: 16 }}>
      <div className="page-head">
        <h1>Dashboard</h1>
        {d && !d.trackingEnabled && <span className="badge warn">Tracking paused (Settings)</span>}
      </div>
      <ErrorNote error={dash.error || arm.error || check.error} />

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
              <a className="btn small" href="#products">
                Edit range
              </a>
              <a className="btn small primary" href={`#site/new?product=${p.id}`}>
                Add site
              </a>
            </div>
          </div>
          <SiteTable sites={(sites.data ?? []).filter((s) => s.productId === p.id)} currency={d?.currency} now={now} onCheck={checkNow} busy={check.busy} />
        </div>
      ))}

      <div className="grid two">
        <div className="card">
          <div className="card-head">
            <h2>Recent activity</h2>
            <a href="#activity" className="small">
              All
            </a>
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
            <h2>Purchases</h2>
            <a href="#purchases" className="small">
              All
            </a>
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

export function SiteTable({
  sites,
  currency,
  now,
  onCheck,
  busy,
}: {
  sites: Site[];
  currency?: string;
  now: number;
  onCheck: (id: number) => void;
  busy: boolean;
}) {
  if (sites.length === 0) return <p className="muted">No sites yet. Add the product pages of the stores that sell it.</p>;
  return (
    <div className="table-wrap">
      <table>
        <thead>
          <tr>
            <th>Site</th>
            <th>Price</th>
            <th>Stock</th>
            <th>Verdict</th>
            <th>Checked</th>
            <th>Next</th>
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
              <td className="small muted">{s.checking ? "checking…" : ago(s.lastCheckedAt, now)}</td>
              <td className="small muted">{s.enabled ? until(s.nextCheckAt, now) : "—"}</td>
              <td>
                <button
                  className="btn small"
                  disabled={busy}
                  onClick={(e) => {
                    e.stopPropagation();
                    onCheck(s.id);
                  }}
                >
                  Check
                </button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
