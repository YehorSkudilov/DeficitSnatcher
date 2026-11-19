"use client";

import { useEffect, useState } from "react";
import { api, type Purchase, type PurchaseDetail, type Settings } from "@/lib/api";
import { dateTime, money } from "@/lib/format";
import { ErrorNote, PurchaseBadge, useAction, useData } from "./ui";

export function Purchases() {
  const list = useData(() => api.get<Purchase[]>("/api/purchases"), [], 5000);
  const settings = useData(() => api.get<Settings>("/api/settings"), []);
  const [open, setOpen] = useState<number | null>(null);
  const currency = settings.data?.currency ?? "CAD";

  return (
    <div className="stack" style={{ gap: 16 }}>
      <div className="page-head">
        <h1>Purchases</h1>
      </div>
      <p className="muted" style={{ marginTop: -8 }}>
        Every checkout, real or dry run. Open one for each step it took and a screenshot of the page it ended on.
      </p>
      <ErrorNote error={list.error} />
      <div className="card">
        {list.data?.length === 0 && <p className="muted">No checkouts yet.</p>}
        {!!list.data?.length && (
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>When</th>
                  <th>Product</th>
                  <th>Site</th>
                  <th>Price</th>
                  <th>Status</th>
                  <th>Result</th>
                </tr>
              </thead>
              <tbody>
                {list.data.map((p) => (
                  <tr key={p.id} className="clickable" onClick={() => setOpen(open === p.id ? null : p.id)}>
                    <td className="small">{dateTime(p.startedAt)}</td>
                    <td>
                      {p.productName}
                      {p.quantity > 1 && <span className="faint"> ×{p.quantity}</span>}
                    </td>
                    <td>{p.siteName}</td>
                    <td className="num">{money(p.price, currency)}</td>
                    <td>
                      <PurchaseBadge status={p.status} />
                    </td>
                    <td className="small muted" style={{ maxWidth: 360 }}>
                      {p.message}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
      {open !== null && <Detail key={open} id={open} onChanged={() => void list.reload()} />}
    </div>
  );
}

function Detail({ id, onChanged }: { id: number; onChanged: () => void }) {
  const { data, error, reload } = useData(() => api.get<PurchaseDetail>(`/api/purchases/${id}`), [id], 3000);
  const [shot, setShot] = useState<string | null>(null);
  const action = useAction();
  const hasShot = data?.purchase.hasScreenshot;

  // The screenshot needs the token, so it's fetched rather than linked.
  useEffect(() => {
    if (!hasShot) return;
    let url: string | null = null;
    api
      .get<Blob>(`/api/purchases/${id}/screenshot`)
      .then((b) => setShot((url = URL.createObjectURL(b))))
      .catch(() => {});
    return () => {
      if (url) URL.revokeObjectURL(url);
    };
  }, [id, hasShot]);

  const markNotBought = async () => {
    if (!confirm("Mark this order as not bought (cancelled or refunded)? It then no longer counts against the units to buy or the budget.")) return;
    const ok = await action.run(async () => (await api.post(`/api/purchases/${id}/void`), true));
    if (ok) {
      void reload();
      onChanged();
    }
  };

  if (!data) return <ErrorNote error={error} />;
  const p = data.purchase;
  return (
    <div className="card stack">
      <div className="card-head" style={{ marginBottom: 0 }}>
        <div className="row">
          <h2>
            {p.productName} at {p.siteName}
          </h2>
          <PurchaseBadge status={p.status} />
        </div>
        {p.status === "Succeeded" && (
          <button className="btn small danger" disabled={action.busy} onClick={markNotBought}>
            Mark as not bought
          </button>
        )}
      </div>
      <ErrorNote error={action.error} />
      <div className="muted">{p.message}</div>
      <pre className="log">{data.log || "…"}</pre>
      {/* eslint-disable-next-line @next/next/no-img-element -- a blob from the API, not a static asset */}
      {shot && <img className="screenshot" src={shot} alt="The page the checkout ended on" />}
    </div>
  );
}
