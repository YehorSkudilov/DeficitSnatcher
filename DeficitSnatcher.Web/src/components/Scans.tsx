"use client";

import { Fragment, useState } from "react";
import { api, query, type Product, type Scan, type ScansPage, type Settings, type Site } from "@/lib/api";
import { dateTime, money } from "@/lib/format";
import { ErrorNote, Field, OutcomeBadge, useAction, useData } from "./ui";

type Filters = {
  productId: string;
  siteId: string;
  outcome: string;
  from: string;
  to: string;
  minPrice: string;
  maxPrice: string;
  manual: string;
  search: string;
};

const empty: Filters = { productId: "", siteId: "", outcome: "", from: "", to: "", minPrice: "", maxPrice: "", manual: "", search: "" };

/** datetime-local → ISO (local time to UTC). */
const iso = (local: string) => (local ? new Date(local).toISOString() : "");

/** Every check of every page, found or not: #logs/scans, or #logs/scans?site=3&outcome=InStock. */
export function Scans({ initial }: { initial?: string }) {
  const start = new URLSearchParams(initial ?? "");
  const [f, setF] = useState<Filters>({
    ...empty,
    productId: start.get("product") ?? "",
    siteId: start.get("site") ?? "",
    outcome: start.get("outcome") ?? "",
  });
  const [older, setOlder] = useState<Scan[]>([]);
  const [done, setDone] = useState(false);
  const [open, setOpen] = useState<number | null>(null);
  const products = useData(() => api.get<Product[]>("/api/products"), []);
  const sites = useData(() => api.get<Site[]>("/api/sites"), []);
  const settings = useData(() => api.get<Settings>("/api/settings"), []);
  const more = useAction();
  const clear = useAction();
  const currency = settings.data?.currency ?? "CAD";

  const params = {
    productId: f.productId,
    siteId: f.siteId,
    outcome: f.outcome,
    from: iso(f.from),
    to: iso(f.to),
    minPrice: f.minPrice,
    maxPrice: f.maxPrice,
    manual: f.manual,
    search: f.search.trim(),
  };
  const key = JSON.stringify(params);
  const page = useData(() => api.get<ScansPage>(`/api/scans${query({ ...params, limit: 100 })}`), [key], 10000);

  const set = (patch: Partial<Filters>) => {
    setF({ ...f, ...patch });
    setOlder([]);
    setDone(false);
  };

  const latest = page.data?.scans ?? [];
  const all = [...latest, ...older.filter((o) => !latest.some((l) => l.id === o.id))];
  const counts = page.data?.counts;

  const loadOlder = async () => {
    const last = all[all.length - 1];
    if (!last) return;
    const r = await more.run(() => api.get<ScansPage>(`/api/scans${query({ ...params, before: last.id, limit: 200 })}`));
    if (r) {
      setOlder([...older, ...r.scans]);
      if (r.scans.length < 200) setDone(true);
    }
  };

  const clearAll = async () => {
    const which = f.siteId ? sites.data?.find((s) => String(s.id) === f.siteId)?.name : null;
    if (!confirm(which ? `Delete every scan of ${which}?` : "Delete every scan of every site?")) return;
    const ok = await clear.run(async () => (await api.del(`/api/scans${query({ siteId: f.siteId })}`), true));
    if (ok) {
      setOlder([]);
      void page.reload();
    }
  };

  const chip = (value: string, label: string, n?: number) => (
    <button className={`chip ${f.outcome === value ? "active" : ""}`} onClick={() => set({ outcome: value })}>
      {label}
      {n !== undefined && <b>{n.toLocaleString()}</b>}
    </button>
  );

  const siteOptions = (sites.data ?? []).filter((s) => !f.productId || String(s.productId) === f.productId);

  return (
    <div className="stack" style={{ gap: 16 }}>
      <div className="page-head" style={{ marginBottom: 0 }}>
        <p className="muted">
          Every check of every page, whether it found the console or not: price, stock, the verdict against your range, and what was done
          about it. Kept {settings.data?.scanHistoryDays ? `${settings.data.scanHistoryDays} days` : "forever"} (Settings).
        </p>
        <div className="row">
          <button className="btn small" onClick={() => set(empty)}>
            Reset filters
          </button>
          <button className="btn small danger" disabled={clear.busy} onClick={clearAll}>
            Clear
          </button>
        </div>
      </div>
      <ErrorNote error={page.error || more.error || clear.error} />

      <div className="card stack">
        <div className="chips">
          {chip("", "All", counts?.all)}
          {chip("InStock", "In stock", counts?.inStock)}
          {chip("OutOfStock", "Out of stock", counts?.outOfStock)}
          {chip("Match", "In price range", counts?.match)}
          {chip("Bought", "Checkout ran", counts?.bought)}
          {chip("Unknown", "Stock unknown", counts?.unknown)}
          {chip("Error", "Errors", counts?.error)}
        </div>
        <div className="filters">
          <Field label="Product">
            <select value={f.productId} onChange={(e) => set({ productId: e.target.value, siteId: "" })}>
              <option value="">All</option>
              {products.data?.map((p) => (
                <option key={p.id} value={p.id}>
                  {p.name}
                </option>
              ))}
            </select>
          </Field>
          <Field label="Site">
            <select value={f.siteId} onChange={(e) => set({ siteId: e.target.value })}>
              <option value="">All</option>
              {siteOptions.map((s) => (
                <option key={s.id} value={s.id}>
                  {s.name}
                </option>
              ))}
            </select>
          </Field>
          <Field label="From">
            <input type="datetime-local" value={f.from} onChange={(e) => set({ from: e.target.value })} />
          </Field>
          <Field label="To">
            <input type="datetime-local" value={f.to} onChange={(e) => set({ to: e.target.value })} />
          </Field>
          <Field label={`Price from (${currency})`}>
            <input type="number" value={f.minPrice} min={0} onChange={(e) => set({ minPrice: e.target.value })} />
          </Field>
          <Field label="to">
            <input type="number" value={f.maxPrice} min={0} onChange={(e) => set({ maxPrice: e.target.value })} />
          </Field>
          <Field label="Started by">
            <select value={f.manual} onChange={(e) => set({ manual: e.target.value })}>
              <option value="">Anyone</option>
              <option value="false">Schedule</option>
              <option value="true">Check now</option>
            </select>
          </Field>
          <Field label="Search">
            <input type="text" value={f.search} placeholder="title, verdict, error…" onChange={(e) => set({ search: e.target.value })} />
          </Field>
        </div>
      </div>

      <div className="card">
        {page.data && all.length === 0 && <p className="muted">No scans match.</p>}
        {all.length > 0 && (
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>When</th>
                  <th>Site</th>
                  <th>Found</th>
                  <th>Price</th>
                  <th>Verdict</th>
                  <th>Action</th>
                  <th>Took</th>
                </tr>
              </thead>
              <tbody>
                {all.map((s) => (
                  <Fragment key={s.id}>
                    <tr className="clickable" onClick={() => setOpen(open === s.id ? null : s.id)}>
                      <td className="small" style={{ whiteSpace: "nowrap" }}>
                        {dateTime(s.at)}
                        {s.manual && <div className="faint">Check now</div>}
                      </td>
                      <td>
                        <b>{s.siteName}</b>
                        <div className="faint small">{s.productName}</div>
                      </td>
                      <td>
                        <OutcomeBadge outcome={s.outcome} />
                      </td>
                      <td className="num">
                        {money(s.totalPrice, currency)}
                        {s.price !== null && s.totalPrice !== null && s.totalPrice !== s.price && <div className="faint small">{money(s.price, currency)} + ship</div>}
                      </td>
                      <td className="small" style={{ maxWidth: 300 }}>
                        {s.match ? <span className="badge good">IN RANGE</span> : <span className={s.error ? "" : "muted"}>{s.error ?? s.verdict}</span>}
                      </td>
                      <td className="small" style={{ maxWidth: 260 }}>
                        {s.purchaseId ? (
                          <a href={`#purchase/${s.purchaseId}`} onClick={(e) => e.stopPropagation()}>
                            Checkout #{s.purchaseId} →
                          </a>
                        ) : (
                          <span className="muted">{s.action ?? ""}</span>
                        )}
                      </td>
                      <td className="small faint num">{(s.durationMs / 1000).toFixed(1)}s</td>
                    </tr>
                    {open === s.id && (
                      <tr>
                        <td colSpan={7} style={{ background: "var(--surface-2)" }}>
                          <dl className="kv">
                            <dt>Scan</dt>
                            <dd>
                              #{s.id} · {dateTime(s.at)} · {s.durationMs} ms · {s.manual ? "Check now" : "scheduled"}
                            </dd>
                            <dt>Listing</dt>
                            <dd>{s.title ?? "—"}</dd>
                            <dt>Verdict</dt>
                            <dd>{s.verdict}</dd>
                            {s.error && (
                              <>
                                <dt>Error</dt>
                                <dd style={{ color: "var(--danger)" }}>{s.error}</dd>
                              </>
                            )}
                            <dt>Read from</dt>
                            <dd>{s.notes ?? "—"}</dd>
                            <dt>Action</dt>
                            <dd>
                              {s.action ?? "None"}
                              {s.purchaseId && (
                                <>
                                  {" "}
                                  <a href={`#purchase/${s.purchaseId}`}>Open checkout #{s.purchaseId}</a>
                                </>
                              )}
                            </dd>
                            <dt>Site</dt>
                            <dd>
                              <a href={`#site/${s.siteId}`}>{s.siteName}</a>
                            </dd>
                          </dl>
                        </td>
                      </tr>
                    )}
                  </Fragment>
                ))}
              </tbody>
            </table>
          </div>
        )}
        {!done && all.length >= 100 && (
          <button className="btn small" style={{ marginTop: 12 }} disabled={more.busy} onClick={loadOlder}>
            {more.busy ? "Loading…" : "Older"}
          </button>
        )}
      </div>
    </div>
  );
}
