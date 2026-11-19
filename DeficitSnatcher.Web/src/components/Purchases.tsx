"use client";

import { useState } from "react";
import { api, PurchaseStatuses, query, type Product, type Purchase, type PurchaseDetail as Detail, type Settings, type Site, type StepRecord } from "@/lib/api";
import { dateTime, money } from "@/lib/format";
import { AuthImage, ErrorNote, Field, PurchaseBadge, download, useAction, useData } from "./ui";
import { go } from "./App";

/** Every checkout, real or dry run, with filters (a product's or a page's own, in its Logs). */
export function Purchases({ initial, productId: fixedProduct, siteId: fixedSite }: { initial?: string; productId?: number; siteId?: number }) {
  const start = new URLSearchParams(initial ?? "");
  const [status, setStatus] = useState(start.get("status") ?? "");
  const [siteId, setSiteId] = useState(fixedSite ? String(fixedSite) : (start.get("site") ?? ""));
  const [productId, setProductId] = useState(fixedProduct ? String(fixedProduct) : (start.get("product") ?? ""));
  const [kind, setKind] = useState("");
  const sites = useData(() => api.get<Site[]>("/api/sites"), []);
  const products = useData(() => api.get<Product[]>("/api/products"), []);
  const settings = useData(() => api.get<Settings>("/api/settings"), []);
  const params = { status, siteId, productId, dryRun: kind, limit: 300 };
  const list = useData(() => api.get<Purchase[]>(`/api/purchases${query(params)}`), [JSON.stringify(params)], 5000);
  const currency = settings.data?.currency ?? "CAD";

  return (
    <div className="stack" style={{ gap: 16 }}>
      <p className="muted">
        Every checkout, real or dry run. Open one for what started it, every step with its screenshot and timing, the full error, the
        browser&apos;s console and network errors, and the page&apos;s HTML where it ended.
      </p>
      <ErrorNote error={list.error} />
      <div className="card filters">
        <Field label="Result">
          <select value={status} onChange={(e) => setStatus(e.target.value)}>
            <option value="">All</option>
            {PurchaseStatuses.map((s) => (
              <option key={s} value={s}>
                {{ Succeeded: "Bought", Failed: "Failed", DryRun: "Dry run done", Running: "Running", Interrupted: "Interrupted" }[s]}
              </option>
            ))}
          </select>
        </Field>
        <Field label="Kind">
          <select value={kind} onChange={(e) => setKind(e.target.value)}>
            <option value="">Real and dry runs</option>
            <option value="false">Real only</option>
            <option value="true">Dry runs only</option>
          </select>
        </Field>
        {!fixedProduct && !fixedSite && (
        <Field label="Product">
          <select value={productId} onChange={(e) => setProductId(e.target.value)}>
            <option value="">All</option>
            {products.data?.map((p) => (
              <option key={p.id} value={p.id}>
                {p.name}
              </option>
            ))}
          </select>
        </Field>
        )}
        {!fixedSite && (
        <Field label="Page">
          <select value={siteId} onChange={(e) => setSiteId(e.target.value)}>
            <option value="">All</option>
            {sites.data?.filter((s) => !productId || String(s.productId) === productId).map((s) => (
              <option key={s.id} value={s.id}>
                {s.name}
              </option>
            ))}
          </select>
        </Field>
        )}
      </div>
      <div className="card">
        {list.data?.length === 0 && <p className="muted">No checkouts match.</p>}
        {!!list.data?.length && (
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>#</th>
                  <th>When</th>
                  <th>Product</th>
                  <th>Site</th>
                  <th>Price</th>
                  <th>Result</th>
                  <th>Message</th>
                </tr>
              </thead>
              <tbody>
                {list.data.map((p) => (
                  <tr key={p.id} className="clickable" onClick={() => go(`purchase/${p.id}`)}>
                    <td className="faint num">{p.id}</td>
                    <td className="small" style={{ whiteSpace: "nowrap" }}>
                      {dateTime(p.startedAt)}
                      <div className="faint">{p.manual ? "by hand" : "automatic"}</div>
                    </td>
                    <td>
                      {p.productName}
                      {p.quantity > 1 && <span className="faint"> ×{p.quantity}</span>}
                    </td>
                    <td>{p.siteName}</td>
                    <td className="num">{money(p.price, currency)}</td>
                    <td>
                      <div className="row" style={{ gap: 4 }}>
                        <PurchaseBadge status={p.status} />
                        {p.dryRun && p.status !== "DryRun" && <span className="badge">dry run</span>}
                      </div>
                    </td>
                    <td className="small muted" style={{ maxWidth: 380 }}>
                      {p.message}
                      {p.shots > 0 && <span className="faint"> · {p.shots} screenshots</span>}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </div>
  );
}

const tabs = ["Steps", "Screenshots", "Error", "Browser log", "Full log"] as const;

/** One checkout, everything about it: #purchase/12. */
export function PurchaseDetail({ id }: { id: number }) {
  const { data, error, reload } = useData(() => api.get<Detail>(`/api/purchases/${id}`), [id], 3000);
  const settings = useData(() => api.get<Settings>("/api/settings"), []);
  const [tab, setTab] = useState<(typeof tabs)[number] | null>(null);
  const action = useAction();
  const currency = settings.data?.currency ?? "CAD";

  if (!data) return <ErrorNote error={error} />;
  const p = data.purchase;
  const shown = tab ?? (p.status === "Failed" && data.error ? "Error" : "Steps");
  const failedStep = data.steps.find((s) => s.status === "failed");
  const took = p.finishedAt ? (new Date(p.finishedAt).getTime() - new Date(p.startedAt).getTime()) / 1000 : null;

  const markNotBought = async () => {
    if (!confirm("Mark this order as not bought (cancelled, refunded or never went through)? It then no longer counts against the units to buy or the budget."))
      return;
    const ok = await action.run(async () => (await api.post(`/api/purchases/${id}/void`), true));
    if (ok) void reload();
  };

  const remove = async () => {
    if (!confirm(`Delete checkout #${id} with its screenshots and logs?`)) return;
    const ok = await action.run(async () => (await api.del(`/api/purchases/${id}`), true));
    if (ok) go(`site/${data?.purchase.siteId}`);
  };

  return (
    <div className="stack" style={{ gap: 16 }}>
      <div className="page-head">
        <div className="row">
          <a href={`#site/${p.siteId}`} className="btn ghost small">
            ← {p.siteName}
          </a>
          <h1>
            Checkout #{p.id}
          </h1>
          <PurchaseBadge status={p.status} />
          {p.dryRun && <span className="badge warn">dry run</span>}
          {p.aiUsed && <span className="badge info">AI</span>}
        </div>
        <div className="row">
          {data.hasHtml && (
            <button className="btn small" onClick={() => action.run(() => download(`/api/purchases/${id}/html`, `checkout-${id}.html`))}>
              Page HTML
            </button>
          )}
          {(p.status === "Succeeded" || p.status === "Interrupted") && (
            <button className="btn small danger" disabled={action.busy} onClick={markNotBought}>
              Mark as not bought
            </button>
          )}
          {p.status !== "Succeeded" && p.status !== "Running" && p.status !== "Interrupted" && (
            <button className="btn small danger" disabled={action.busy} onClick={remove}>
              Delete
            </button>
          )}
        </div>
      </div>
      <ErrorNote error={action.error} />

      <div className={`notice ${p.status === "Succeeded" ? "good" : p.status === "DryRun" ? "warn" : p.status === "Running" ? "" : "bad"}`}>
        {p.status === "Running" && <span className="spin" />} <b>{p.message}</b>
        {failedStep && (
          <div className="small" style={{ marginTop: 4 }}>
            Failed at step {failedStep.index + 1}: {failedStep.description}
            {failedStep.url && <> on {failedStep.url}</>}
          </div>
        )}
      </div>

      <div className="grid two">
        <div className="card">
          <h2 style={{ marginBottom: 10 }}>Checkout</h2>
          <dl className="kv">
            <dt>Product</dt>
            <dd>
              {p.productName} ×{p.quantity}
            </dd>
            <dt>Site</dt>
            <dd>
              <a href={`#site/${p.siteId}`}>{p.siteName}</a> ·{" "}
              <a href={data.siteUrl} target="_blank" rel="noreferrer noopener">
                product page ↗
              </a>
            </dd>
            <dt>Price</dt>
            <dd>{money(p.price, currency)}</dd>
            <dt>Started</dt>
            <dd>
              {dateTime(p.startedAt)} · {p.manual ? "by hand from the site" : "automatically by a check"}
            </dd>
            <dt>Finished</dt>
            <dd>{p.finishedAt ? `${dateTime(p.finishedAt)} (${took?.toFixed(1)} s)` : "still running…"}</dd>
            <dt>Ended on</dt>
            <dd>{data.finalUrl ?? "—"}</dd>
            {data.scan && (
              <>
                <dt>Triggered by</dt>
                <dd>
                  <a href={`#site/${p.siteId}`}>Scan #{data.scan.id}</a> at {dateTime(data.scan.at)}
                </dd>
              </>
            )}
          </dl>
        </div>
        <div className="card">
          <h2 style={{ marginBottom: 10 }}>Why it ran</h2>
          <pre className="log">{data.context || "—"}</pre>
        </div>
      </div>

      <div className="card">
        <div className="tabs">
          {tabs.map((t) => (
            <button key={t} className={shown === t ? "active" : ""} onClick={() => setTab(t)}>
              {t}
              {t === "Screenshots" && ` (${data.shots.length})`}
              {t === "Error" && data.error && " ●"}
              {t === "Browser log" && data.browserLog && ` (${data.browserLog.split("\n").length})`}
            </button>
          ))}
        </div>

        {shown === "Steps" && <Timeline id={id} steps={data.steps} running={p.status === "Running"} />}

        {shown === "Screenshots" &&
          (data.shots.length === 0 ? (
            <p className="muted">No screenshots{p.status === "Running" ? " yet" : ""}.</p>
          ) : (
            <div className="shots-grid">
              {data.shots.map((s) => (
                <figure key={s.id}>
                  <AuthImage path={`/api/purchases/${id}/shots/${s.id}`} alt={s.label} />
                  <figcaption>{s.label}</figcaption>
                </figure>
              ))}
            </div>
          ))}

        {shown === "Error" && (data.error ? <pre className="log" style={{ color: "#ffc2c8" }}>{data.error}</pre> : <p className="muted">No error.</p>)}

        {shown === "Browser log" &&
          (data.browserLog ? (
            <>
              <p className="muted small" style={{ marginBottom: 8 }}>
                Console errors and warnings, page errors, failed requests and error responses while the checkout ran, and every page it
                went to.
              </p>
              <pre className="log">{data.browserLog}</pre>
            </>
          ) : (
            <p className="muted">Nothing logged.</p>
          ))}

        {shown === "Full log" && <pre className="log">{data.log || "…"}</pre>}
      </div>

      {/* Where it ended, big: the first thing to look at when it failed. */}
      {data.shots
        .filter((s) => s.stepIndex === null)
        .map((s) => (
          <div className="card stack" key={s.id}>
            <h2>{s.label}</h2>
            <AuthImage path={`/api/purchases/${id}/shots/${s.id}`} alt={s.label} className="screenshot" />
          </div>
        ))}
    </div>
  );
}

function Timeline({ id, steps, running }: { id: number; steps: StepRecord[]; running: boolean }) {
  if (steps.length === 0) return <p className="muted">{running ? "Running… the steps show when it's done." : "No steps ran."}</p>;
  return (
    <div className="timeline">
      {steps.map((s) => (
        <div key={s.index} className={`tl-step ${s.status}`}>
          <div className="tl-num">{s.status === "failed" ? "✕" : s.index + 1}</div>
          <div style={{ minWidth: 0 }}>
            <div className="row" style={{ gap: 6 }}>
              <b className="mono" style={{ overflowWrap: "anywhere" }}>
                {s.description}
              </b>
            </div>
            <div className="row small" style={{ gap: 8, marginTop: 4 }}>
              <span className={`badge ${{ ok: "good", failed: "bad", skipped: "warn", notRun: "" }[s.status]}`}>
                {{ ok: "ok", failed: "failed", skipped: "failed, optional", notRun: "not run" }[s.status]}
              </span>
              {s.ai && <span className="badge info">AI</span>}
              {s.final && <span className="badge bad">final</span>}
              {s.optional && <span className="badge">optional</span>}
              {s.ms !== null && <span className="faint">{s.ms} ms</span>}
              {s.url && <span className="faint ellipsis" style={{ maxWidth: 420 }}>{s.url}</span>}
            </div>
            {s.note && (
              <div className="small muted" style={{ marginTop: 4 }}>
                AI: {s.note}
              </div>
            )}
            {s.error && (s.status === "notRun" ? <div className="faint small" style={{ marginTop: 6 }}>{s.error}</div> : <pre className="log">{s.error}</pre>)}
          </div>
          <div>
            {s.shotId !== null && <AuthImage path={`/api/purchases/${id}/shots/${s.shotId}`} alt={`After step ${s.index + 1}: ${s.description}`} className="tl-shot" />}
          </div>
        </div>
      ))}
    </div>
  );
}
