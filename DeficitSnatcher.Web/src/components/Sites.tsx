"use client";

import { api, type Settings, type Site } from "@/lib/api";
import { ErrorNote, useAction, useData, useNow } from "./ui";
import { SiteTable } from "./Dashboard";

export function Sites() {
  const sites = useData(() => api.get<Site[]>("/api/sites"), [], 5000);
  const settings = useData(() => api.get<Settings>("/api/settings"), []);
  const check = useAction();
  const now = useNow();

  const checkNow = async (id: number) => {
    await check.run(() => api.post(`/api/sites/${id}/check`));
    void sites.reload();
  };

  const groups = new Map<string, Site[]>();
  for (const s of sites.data ?? []) groups.set(s.productName, [...(groups.get(s.productName) ?? []), s]);

  return (
    <div className="stack" style={{ gap: 16 }}>
      <div className="page-head">
        <h1>Sites</h1>
        <a className="btn primary" href="#site/new">
          Add site
        </a>
      </div>
      <ErrorNote error={sites.error || check.error} />
      {sites.data?.length === 0 && (
        <div className="card muted">
          No sites yet. Add a store&apos;s product page for the PS5 Pro: Shopify stores work out of the box, most others through the
          page&apos;s structured data, and any page with CSS selectors.
        </div>
      )}
      {[...groups].map(([product, list]) => (
        <div className="card" key={product}>
          <div className="card-head">
            <h2>{product}</h2>
          </div>
          <SiteTable sites={list} currency={settings.data?.currency} now={now} onCheck={checkNow} busy={check.busy} />
        </div>
      ))}
    </div>
  );
}
