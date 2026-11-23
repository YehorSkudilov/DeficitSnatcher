"use client";

import { useState } from "react";
import { api, query, type AiCall, type BotEvent, type Product, type Purchase, type Scan, type ScansPage, type Site } from "@/lib/api";
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
      <button className="btn small" disabled={action.busy} onClick={() => void copy()} title="Copies the settings, pages, scans, activity, checkouts and AI calls as text">
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
  fields.filter((f) => f !== null && f !== undefined && f !== "").join(" | ");

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
  const [products, sites, recentScans, inStockScans, events, purchases, aiCalls] = await Promise.all([
    api.get<Product[]>("/api/products"),
    api.get<Site[]>(`/api/sites${query({ productId })}`),
    api.get<ScansPage>(`/api/scans${query({ ...scope, limit: 100 })}`),
    api.get<ScansPage>(`/api/scans${query({ ...scope, outcome: "InStock", limit: 60 })}`),
    api.get<BotEvent[]>(`/api/events${query({ ...scope, limit: 150 })}`),
    api.get<Purchase[]>(`/api/purchases${query({ ...scope, limit: 50 })}`),
    api.get<AiCall[]>(`/api/ai/calls${query({ ...scope, limit: 100 })}`),
  ]);
  const shownSites = sites.filter((s) => (siteId ? s.id === siteId : productId ? s.productId === productId : true));
  const shownProducts = products.filter((p) => (productId ? p.id === productId : siteId ? shownSites.some((s) => s.productId === p.id) : true));

  const scans = new Map<number, Scan>();
  for (const s of [...recentScans.scans, ...inStockScans.scans]) scans.set(s.id, s);
  const scanList = [...scans.values()].sort((a, b) => b.id - a.id);

  const out: string[] = [];
  out.push(`Deficit Snatcher logs, copied ${new Date().toISOString()}${productId ? `, product ${productId}` : ""}${siteId ? `, page ${siteId}` : ""}`);
  out.push(`Scan counts: ${JSON.stringify(recentScans.counts)}`);

  out.push("", "== Products ==");
  for (const p of shownProducts)
    out.push(line([`#${p.id} ${p.name}`, p.enabled ? "enabled" : "off", `range ${p.minPrice}–${p.maxPrice}`, `bought ${p.bought}/${p.maxQuantity}`,
      `per order ${p.quantityPerOrder}`, `must contain: ${p.requiredKeywords || "-"}`, `must not contain: ${p.excludedKeywords || "-"}`,
      p.autoBuyEnabled && "auto-buy", p.checkingPaused && "checks paused"]));

  out.push("", "== Pages ==");
  for (const s of shownSites) {
    out.push(line([`#${s.id} ${s.name}`, s.url, s.kind, s.enabled ? "enabled" : "off", `every ${s.intervalSeconds ?? "default"}s`, `shipping ${s.shippingCost}`,
      s.variantId && `variant ${s.variantId}`, s.autoBuy && "auto-buy"]));
    out.push(`  selectors: price=${s.priceSelector ?? "-"} title=${s.titleSelector ?? "-"} inStock=${s.inStockSelector ?? "-"} outOfStockText=${s.outOfStockText ?? "-"}`);
    if (s.channels) out.push(`  channels: ${JSON.stringify(s.channels)}`);
    out.push(`  last: ${line([s.lastCheckedAt, s.lastInStock == null ? "stock ?" : s.lastInStock ? "IN STOCK" : "out of stock", s.lastPrice, s.lastTitle,
      s.match && "MATCH", s.verdict, s.lastError && `error: ${s.lastError}`, s.consecutiveErrors > 0 && `${s.consecutiveErrors} errors in a row`])}`);
  }

  out.push("", `== Activity (${events.length}, newest first) ==`);
  for (const e of events) out.push(line([e.at, e.level, e.siteName, e.message]));

  out.push("", `== Checkouts (${purchases.length}) ==`);
  for (const p of purchases)
    out.push(line([`#${p.id}`, p.startedAt, p.siteName, p.status, p.dryRun && "dry run", p.manual && "manual", `price ${p.price}`, `qty ${p.quantity}`, p.message]));

  out.push("", `== AI calls (${aiCalls.length}, newest first) ==`);
  for (const c of aiCalls) {
    out.push(line([`#${c.id}`, c.at, c.purpose, c.siteName, c.ok ? "ok" : "FAILED", `${c.inputTokens}+${c.outputTokens} tokens`, `${c.durationMs}ms`]));
    out.push(`  ${c.summary.replace(/\n/g, "\n  ")}`);
  }
  // What the page itself said in the latest in-stock scan that kept its source: the stock wording, to see why it read as in stock.
  const latest = scanList.find((s) => s.outcome === "InStock" && s.hasSnapshot);
  if (latest) {
    out.push("", `== Page wording of in-stock scan #${latest.id} (${latest.siteName}, ${latest.at}) ==`);
    try {
      const blob = await api.get<Blob>(`/api/scans/${latest.id}/source`);
      const site = shownSites.find((x) => x.id === latest.siteId);
      out.push(...stockWording(await blob.text(), site));
    } catch (e) {
      out.push(`(couldn't load its source: ${e instanceof Error ? e.message : String(e)})`);
    }
  }

  out.push("", `== Scans (${scanList.length}: the latest 100 and the latest 60 in stock, newest first) ==`);
  for (const s of scanList) {
    out.push(line([`#${s.id}`, s.at, s.siteName, s.outcome, s.match && "MATCH", s.price != null && `price ${s.price}`, s.totalPrice != null && `total ${s.totalPrice}`,
      s.title && `title "${s.title}"`, s.verdict, s.action && `action: ${s.action}`, s.error && `error: ${s.error}`, s.manual && "manual",
      `${s.durationMs}ms`, s.worker, s.hasSnapshot && "snapshot"]));
    if (s.notes) out.push(`  notes: ${s.notes.replace(/\n/g, "\n  ")}`);
  }

  return out.join("\n");
}
