"use client";

import { useState } from "react";
import {
  api,
  query,
  type AiCall,
  type AiStatus,
  type BotEvent,
  type DeployStatus,
  type Product,
  type Purchase,
  type PurchaseDetail,
  type Scan,
  type ScansPage,
  type Settings,
  type Site,
} from "@/lib/api";
import { ErrorNote, useAction } from "./ui";

/** Copies one text report of a product's (or a page's) setup and logs: its settings, pages, scans, activity, checkouts and AI calls. */
export function CopyLogsButton({ productId, siteId }: { productId?: number; siteId?: number }) {
  const action = useAction();
  const [copied, setCopied] = useState<string | null>(null);

  const copy = async () => {
    setCopied(null);
    const text = await action.run(() => buildReport(productId, siteId));
    if (text == null) return;
    try {
      await copyText(text);
      setCopied(`Copied ${Math.round(text.length / 1000)}k characters`);
      window.setTimeout(() => setCopied(null), 4000);
    } catch (e) {
      action.setError("Couldn't copy: " + (e instanceof Error ? e.message : String(e)));
    }
  };

  return (
    <div className="row" style={{ gap: 8 }}>
      <button className="btn small" disabled={action.busy} onClick={() => void copy()} title="Copies one report: server version, AI and settings, products, pages, what stands out, checkouts in detail, activity, AI calls, server errors and scans">
        {action.busy ? "Copying…" : copied ? "Copied" : "Copy all"}
      </button>
      {copied && <span className="small muted">{copied}</span>}
      <ErrorNote error={action.error} />
    </div>
  );
}

async function copyText(text: string) {
  if (navigator.clipboard?.writeText) {
    try {
      await navigator.clipboard.writeText(text);
      return;
    } catch {
      // Falls back below (e.g. the page lost focus while loading).
    }
  }
  const area = document.createElement("textarea");
  area.value = text;
  area.style.position = "fixed";
  area.style.opacity = "0";
  document.body.appendChild(area);
  area.select();
  const ok = document.execCommand("copy");
  area.remove();
  if (!ok) throw new Error("the browser refused");
}

const line = (fields: (string | number | boolean | null | undefined)[]) =>
  fields.filter((f) => f !== null && f !== undefined && f !== "" && f !== false).join(" | ");

const cut = (s: string | null | undefined, n: number) => {
  const t = (s ?? "").replace(/\s+/g, " ").trim();
  return t.length > n ? t.slice(0, n) + "…" : t;
};

/** Repeated lines (same text once numbers and times are left out) as one, with ×N. */
function collapse(lines: { key: string; text: string; at: string }[]): string[] {
  const out: string[] = [];
  for (let i = 0; i < lines.length; ) {
    let j = i + 1;
    while (j < lines.length && lines[j].key === lines[i].key) j++;
    out.push(j - i > 1 ? `${lines[i].text}  [×${j - i}, back to ${lines[j - 1].at.slice(0, 19)}]` : lines[i].text);
    i = j;
  }
  return out;
}

const shape = (s: string) => s.replace(/\d+([.,:]\d+)*/g, "#");

// Pastes get cut off around here: the report is kept under it, the most telling parts first.
const MAX_CHARS = 30000;

const safe = async <T,>(f: () => Promise<T>): Promise<T | null> => {
  try {
    return await f();
  } catch {
    return null;
  }
};

/** The page's boxes named by its stock rules, and every short bit of text about delivery, pickup, stock or the cart. */
function stockWording(source: string, site: Site | undefined): string[] {
  if (source.trimStart().startsWith("{")) return [`(product data, not a page) ${source.slice(0, 1500)}`];
  const doc = new DOMParser().parseFromString(source, "text/html");
  doc.querySelectorAll("script, style, noscript, svg, template").forEach((e) => e.remove());
  const squash = (t: string | null) => (t ?? "").replace(/\s+/g, " ").trim();
  const out: string[] = [];
  for (const [name, rule] of [["online", site?.channels?.online], ["pickup", site?.channels?.pickup]] as const) {
    if (!rule?.container) continue;
    try {
      const boxes = [...doc.querySelectorAll(rule.container)].map((e) => squash(e.textContent)).filter(Boolean);
      out.push(`${name} box (${rule.container}): ${boxes.length ? boxes.map((b) => `"${b.slice(0, 300)}"`).join(" + ") : "not on the page"}`);
    } catch {
      out.push(`${name} box: bad selector ${rule.container}`);
    }
  }
  const words = /deliver|ship|pick ?up|in stock|out of stock|sold out|unavailable|available|add to cart|notify|backorder|coming soon/i;
  const seen = new Set<string>();
  for (const el of doc.body?.querySelectorAll("*") ?? []) {
    if (el.children.length > 0) continue;
    const t = squash(el.textContent);
    if (t.length < 3 || t.length > 160 || !words.test(t) || seen.has(t)) continue;
    seen.add(t);
    const cls = typeof el.className === "string" && el.className ? "." + el.className.trim().split(/\s+/).slice(0, 2).join(".") : "";
    const parent = el.parentElement;
    const pcls = parent && typeof parent.className === "string" && parent.className ? "." + parent.className.trim().split(/\s+/)[0] : "";
    out.push(`  ${parent?.tagName.toLowerCase() ?? ""}${pcls} > ${el.tagName.toLowerCase()}${cls}: ${t}`);
    if (seen.size >= 60) break;
  }
  if (seen.size === 0) out.push("  (no stock wording found in the saved page)");
  return out;
}

async function buildReport(productId?: number, siteId?: number): Promise<string> {
  const scope = { productId, siteId };
  const [products, sites, recentScans, inStockScans, problemScans, events, purchases, aiCalls, settings, aiStatus, deploy, serverLog] = await Promise.all([
    api.get<Product[]>("/api/products"),
    api.get<Site[]>(`/api/sites${query({ productId })}`),
    api.get<ScansPage>(`/api/scans${query({ ...scope, limit: 150 })}`),
    api.get<ScansPage>(`/api/scans${query({ ...scope, outcome: "InStock", limit: 40 })}`),
    api.get<ScansPage>(`/api/scans${query({ ...scope, outcome: "Error", limit: 30 })}`),
    api.get<BotEvent[]>(`/api/events${query({ ...scope, limit: 200 })}`),
    api.get<Purchase[]>(`/api/purchases${query({ ...scope, limit: 20 })}`),
    api.get<AiCall[]>(`/api/ai/calls${query({ ...scope, limit: 60 })}`),
    safe(() => api.get<Settings>("/api/settings")),
    safe(() => api.get<AiStatus>("/api/ai/status")),
    safe(() => api.get<DeployStatus>("/api/deploy")),
    safe(() => api.get<{ entries: { at: string; source: string; level: string; message: string }[] }>("/api/diagnostics/logs?limit=3000")),
  ]);
  const shownSites = sites.filter((s) => (siteId ? s.id === siteId : productId ? s.productId === productId : true));
  const shownProducts = products.filter((p) => (productId ? p.id === productId : siteId ? shownSites.some((s) => s.productId === p.id) : true));
  const siteNames = new Set(shownSites.map((s) => s.name));
  const cur = settings?.currency ?? "";

  const scans = new Map<number, Scan>();
  for (const s of [...recentScans.scans, ...inStockScans.scans, ...problemScans.scans]) scans.set(s.id, s);
  const scanList = [...scans.values()].sort((a, b) => b.id - a.id);

  const out: string[] = [];
  const now = new Date();
  out.push(`Deficit Snatcher report, copied ${now.toISOString()} (browser time ${now.toString().slice(16, 33)})${productId ? `, product #${productId}` : ""}${siteId ? `, page #${siteId}` : ""}`);
  if (deploy)
    out.push(`Server: version ${deploy.serverSha?.slice(0, 7) ?? "?"} built ${deploy.builtAt ?? "?"}, up since ${deploy.startedAt}; newest on ${deploy.branch}: ${deploy.commits[0]?.sha.slice(0, 7) ?? "?"}${deploy.commits.some((c) => c.pending) ? " (not deployed yet)" : ""}; last deploy: ${deploy.runs[0] ? `${deploy.runs[0].status}/${deploy.runs[0].conclusion ?? "-"} ${deploy.runs[0].headSha.slice(0, 7)} at ${deploy.runs[0].createdAt}` : "none"}`);
  if (aiStatus)
    out.push(`AI: ${aiStatus.hasKey ? "key set" : "NO KEY"}, ${aiStatus.enabled ? "on" : "OFF"}, ${aiStatus.model}, tokens today ${aiStatus.tokensToday.toLocaleString()} of ${aiStatus.dailyTokenLimit > 0 ? aiStatus.dailyTokenLimit.toLocaleString() : "no limit"}${aiStatus.dailyTokenLimit > 0 && aiStatus.tokensToday >= aiStatus.dailyTokenLimit ? " (USED UP)" : ""}`);
  if (settings)
    out.push(
      "Settings: " +
        line([
          settings.dryRun ? "DRY RUN (never orders)" : "real orders",
          `budget ${settings.totalBudget} ${cur}`,
          `buy retry ${settings.buyRetryMinutes} min`,
          `checks every ${settings.defaultIntervalSeconds}s ±${settings.jitterPercent}%`,
          `${settings.maxConcurrentChecks} at once`,
          `timeout ${settings.requestTimeoutSeconds}s`,
          `browser ${settings.appBrowser}`,
          settings.proxyUrl ? "proxy set" : "no proxy",
          `snapshots ${settings.scanSnapshots}`,
          `AI checkout without steps ${settings.aiCheckoutWithoutSteps}`,
          `AI autofix checkout ${settings.aiAutofixCheckout}`,
          `AI autofix reading ${settings.aiAutofixReading}`,
          `AI max actions ${settings.aiMaxActions}`,
          `AI rests ${settings.aiCooldownHours} h after failing`,
          `AI checks order ${settings.aiCheckOrder}`,
          `max tax ${settings.maxTaxPercent}%`,
          `error alert after ${settings.errorThreshold}`,
          `alerts: in stock ${settings.notifyInStock}, not bought ${settings.notifyNotBought}, purchase ${settings.notifyPurchase}`,
        ]),
    );
  out.push(`Scan counts: ${JSON.stringify(recentScans.counts)}`);

  out.push("", "== Products ==");
  for (const p of shownProducts)
    out.push(line([`#${p.id} ${p.name}`, p.enabled ? "enabled" : "OFF", `range ${p.minPrice}–${p.maxPrice}`, `bought ${p.bought}/${p.maxQuantity}`,
      `per order ${p.quantityPerOrder}`, `must contain: ${p.requiredKeywords || "-"}`, `must not contain: ${p.excludedKeywords || "-"}`,
      p.autoBuyEnabled ? "AUTO-BUY on" : "auto-buy off", p.checkingPaused && `checks paused${p.checkingResumeAt ? ` until ${p.checkingResumeAt}` : ""}`]));

  // What stands out now, worked out here so it isn't missed further down.
  out.push("", "== Now ==");
  const notes: string[] = [];
  for (const s of shownSites) {
    if (!s.enabled) continue;
    if (s.lastInStock && s.match) notes.push(`IN STOCK + MATCH: ${s.productName} at ${s.name} (#${s.id}) ${s.lastPrice} — ${s.verdict}`);
    else if (s.lastInStock) notes.push(`in stock but NOT a match: ${s.productName} at ${s.name} (#${s.id}) ${s.lastPrice} — ${s.verdict}`);
    if (s.consecutiveErrors > 0) notes.push(`failing ${s.consecutiveErrors}× in a row: ${s.name} (#${s.id}) — ${cut(s.lastError, 200)}`);
    else if (s.lastInStock == null) notes.push(`stock unknown: ${s.name} (#${s.id}) — ${s.verdict}`);
    if (s.autoBuy && s.checkoutSteps.length === 0) notes.push(`auto-buy without saved checkout steps (AI does it): ${s.name} (#${s.id})`);
  }
  if (aiStatus && aiStatus.dailyTokenLimit > 0 && aiStatus.tokensToday >= aiStatus.dailyTokenLimit) notes.push("AI daily token limit used up");
  out.push(...(notes.length ? notes : ["nothing in stock, nothing failing"]));

  out.push("", "== Pages ==");
  for (const s of shownSites) {
    out.push(line([`#${s.id} ${s.name}`, `(${s.productName})`, s.url, s.kind, s.enabled ? "" : "OFF", s.intervalSeconds ? `every ${s.intervalSeconds}s` : "",
      s.shippingCost ? `shipping ${s.shippingCost}` : "", s.variantId && `variant ${s.variantId}`, s.autoBuy && "page auto-buy",
      `${s.checkoutSteps.length} checkout steps`, s.hasCookies && "has cookies"]));
    const sel = [s.priceSelector && `price=${s.priceSelector}`, s.titleSelector && `title=${s.titleSelector}`, s.inStockSelector && `inStock=${s.inStockSelector}`, s.outOfStockText && `outOfStockText=${s.outOfStockText}`].filter(Boolean);
    if (sel.length) out.push(`  selectors: ${cut(sel.join(" "), 400)}`);
    if (s.channels) out.push(`  channels: ${JSON.stringify(s.channels)}`);
    out.push(`  last: ${line([s.lastCheckedAt, s.lastInStock == null ? "stock ?" : s.lastInStock ? "IN STOCK" : "out of stock", s.lastPrice, cut(s.lastTitle, 100),
      s.match && "MATCH", s.verdict, s.lastChangedAt && `changed ${s.lastChangedAt}`, s.nextCheckAt && `next ${s.nextCheckAt}`,
      s.lastError && `error: ${cut(s.lastError, 200)}`, s.consecutiveErrors > 0 && `${s.consecutiveErrors} errors in a row`])}`);
  }

  out.push("", `== Checkouts (latest ${purchases.length}) ==`);
  for (const p of purchases)
    out.push(line([`#${p.id}`, p.startedAt, p.productName, p.siteName, p.status, p.dryRun && "dry run", p.manual && "manual", p.aiUsed && "AI",
      `price ${p.price}`, `qty ${p.quantity}`, cut(p.message, 300)]));
  // The latest few in full: each step, its error, and the end of the checkout's log.
  for (const p of purchases.slice(0, 3)) {
    const d = await safe(() => api.get<PurchaseDetail>(`/api/purchases/${p.id}`));
    if (!d) continue;
    out.push(`-- checkout #${p.id} in detail (${d.siteUrl}) --`);
    if (d.error) out.push(`  error: ${cut(d.error, 600)}`);
    for (const st of d.steps)
      out.push(`  ${st.index + 1}. ${line([st.status, st.ai && "AI", st.final && "final", st.action, cut(st.description, 120), st.ms != null && `${st.ms}ms`, st.url && cut(st.url, 100), st.error && `error: ${cut(st.error, 300)}`, st.note && `note: ${cut(st.note, 200)}`])}`);
    if (d.finalUrl) out.push(`  ended at: ${d.finalUrl}`);
    if (d.log) out.push(`  log (end): ${cut(d.log.slice(-1500), 1500)}`);
  }

  out.push("", "== Activity (newest first, repeats folded) ==");
  out.push(...collapse(events.map((e) => ({ key: `${e.level}|${e.siteName}|${shape(e.message)}`, text: line([e.at.slice(0, 19), e.level, e.siteName, cut(e.message, 400)]), at: e.at }))).slice(0, 120));

  out.push("", "== AI calls (newest first) ==");
  for (const c of aiCalls)
    out.push(line([`#${c.id}`, c.at.slice(0, 19), c.purpose, c.siteName, c.ok ? "ok" : "FAILED", `${c.inputTokens + c.outputTokens} tokens`, `${c.durationMs}ms`, cut(c.summary, c.ok ? 220 : 500)]));

  // What the page itself said in the latest in-stock scan that kept its source: the stock wording, to see why it read as in stock.
  const latest = scanList.find((s) => s.outcome === "InStock" && s.hasSnapshot);
  if (latest) {
    out.push("", `== Page wording of in-stock scan #${latest.id} (${latest.siteName}, ${latest.at}) ==`);
    const blob = await safe(() => api.get<Blob>(`/api/scans/${latest.id}/source`));
    if (blob) out.push(...stockWording(await blob.text(), shownSites.find((x) => x.id === latest.siteId)).slice(0, 50));
    else out.push("(couldn't load its source)");
  }

  if (serverLog) {
    // The server's own warnings and errors about these pages (or all of them, for the whole site), then its latest lines.
    const entries = serverLog.entries.filter((e) => !productId && !siteId ? true : [...siteNames].some((n) => e.message.includes(n)) || e.level === "Error");
    const bad = entries.filter((e) => /warn|error|crit|fail/i.test(e.level));
    out.push("", "== Server warnings and errors (newest last) ==");
    out.push(...collapse(bad.slice(-60).map((e) => ({ key: `${e.level}|${shape(e.message)}`, text: `${e.at.slice(0, 19)} [${e.level}] [${e.source}] ${cut(e.message, 400)}`, at: e.at }))));
  }

  // Plain "out of stock" scans as one line per page; every other scan (in stock, match, unknown, error) in full.
  const plainScans = scanList.filter((s) => s.outcome === "OutOfStock" && !s.match);
  const plainBySite = new Map<number, Scan[]>();
  for (const s of plainScans) plainBySite.set(s.siteId, [...(plainBySite.get(s.siteId) ?? []), s]);
  out.push("", `== Routine out-of-stock scans (of the latest ${scanList.length}) ==`);
  for (const list of plainBySite.values()) {
    const prices = [...new Set(list.map((s) => s.totalPrice).filter((p) => p != null))];
    const verdicts = [...new Set(list.map((s) => s.verdict))];
    const ms = list.map((s) => s.durationMs).sort((x, y) => x - y);
    out.push(line([list[0].siteName, `${list.length} scans ${list[list.length - 1].at.slice(0, 19)} → ${list[0].at.slice(0, 19)}`, `price ${prices.join("/") || "none"}`,
      verdicts.map((v) => cut(v, 80)).join(" / "), `median ${ms[Math.floor(ms.length / 2)]}ms`, `latest notes: ${cut(list[0].notes, 300)}`]));
  }
  out.push("", "== Other scans (newest first) ==");
  for (const s of scanList.filter((s) => !(s.outcome === "OutOfStock" && !s.match))) {
    out.push(line([`#${s.id}`, s.at.slice(0, 19), s.siteName, s.outcome, s.match && "MATCH", s.totalPrice != null && `${s.totalPrice}`,
      s.title && `"${cut(s.title, 80)}"`, s.verdict, s.action && `action: ${cut(s.action, 200)}`, s.error && `error: ${cut(s.error, 200)}`,
      s.manual && "manual", `${s.durationMs}ms`, s.hasSnapshot && "snapshot"]));
    if (s.notes) out.push(`  notes: ${cut(s.notes, 700)}`);
  }

  let report = out.join("\n");
  if (report.length > MAX_CHARS) report = report.slice(0, MAX_CHARS) + `\n… (cut at ${MAX_CHARS.toLocaleString()} characters; the rest is older scans)`;
  return report;
}
