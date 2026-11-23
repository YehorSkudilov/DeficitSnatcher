"use client";

import { api, type Site } from "@/lib/api";
import { money } from "@/lib/format";
import { useData } from "./ui";

/**
 * Every page in stock right now, at the top: product, store, price, and one tap to open the store's page. Matches
 * (in range, right title) first; in stock but not a match (price or title off) after, with why. Nothing when none is.
 */
export function InStockNow({ currency }: { currency?: string }) {
  const sites = useData(() => api.get<Site[]>("/api/sites"), [], 5000);
  const inStock = (sites.data ?? [])
    .filter((s) => s.enabled && s.lastInStock === true)
    .sort((a, b) => Number(b.match) - Number(a.match) || (a.lastPrice ?? 0) - (b.lastPrice ?? 0));
  if (inStock.length === 0) return null;

  return (
    <div className="card stack in-stock-now" style={{ gap: 10, borderColor: "var(--success)" }}>
      <div className="row" style={{ justifyContent: "space-between" }}>
        <h2 style={{ color: "var(--success)" }}>In stock now · {inStock.length}</h2>
        <span className="small muted">Tap to open the store</span>
      </div>
      {inStock.map((s) => (
        <div key={s.id} className="row" style={{ gap: 10, justifyContent: "space-between", flexWrap: "wrap", paddingTop: 8, borderTop: "1px solid var(--stroke)" }}>
          <a href={`#site/${s.id}`} style={{ color: "inherit", textDecoration: "none", flex: "1 1 180px", minWidth: 0 }}>
            <div style={{ fontWeight: 600 }}>
              {s.productName} <span className="muted">at</span> {s.name}
            </div>
            <div className="small muted" style={{ overflowWrap: "anywhere" }}>
              {s.lastPrice != null && <strong style={{ color: "var(--text)" }}>{money(s.lastPrice + s.shippingCost, currency)}</strong>}
              {s.match ? " · a match" : ` · not bought: ${s.verdict}`}
              {s.lastChangedAt && ` · since ${new Date(s.lastChangedAt).toLocaleTimeString([], { hour: "numeric", minute: "2-digit" })}`}
            </div>
          </a>
          <a className={`btn ${s.match ? "primary" : ""}`} href={s.url} target="_blank" rel="noreferrer" style={{ flexShrink: 0 }}>
            Open store →
          </a>
        </div>
      ))}
    </div>
  );
}
