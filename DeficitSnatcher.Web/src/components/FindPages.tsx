"use client";

import { useState } from "react";
import { api, type AiStatus } from "@/lib/api";
import { money } from "@/lib/format";
import { Check, ErrorNote, useAction, useData } from "./ui";

type Found = { store: string; url: string; title: string | null; price: string | null; note: string | null; alreadyAdded: boolean };
type Probed = {
  url: string;
  name: string;
  status: "reads" | "blocked" | "unread" | "error" | "foreign";
  price: number | null;
  inStock: boolean | null;
  title: string | null;
  titleMatches: boolean | null;
  inRange: boolean;
  message: string;
  alreadyAdded: boolean;
  /** The store (e.g. bestbuy.ca): one page per store. */
  domain: string;
};
type Row = Probed & { pick: boolean; runOnPc: boolean; note?: string | null };

/** Finds a product's pages (the AI searches the web, or links are pasted), checks each, and adds the chosen ones at once. */
export function FindPages({ productId, productName, currency, onAdded, onClose }: { productId: number; productName: string; currency: string; onAdded: () => void; onClose: () => void }) {
  const ai = useData(() => api.get<AiStatus>("/api/ai/status"), []);
  const aiOn = !!ai.data?.hasKey && !!ai.data?.enabled;
  const [mode, setMode] = useState<"ai" | "paste">("ai");
  const [hint, setHint] = useState("");
  const [pasted, setPasted] = useState("");
  const [rows, setRows] = useState<Row[] | null>(null);
  const [setupWithAi, setSetupWithAi] = useState(true);
  const [done, setDone] = useState<string | null>(null);
  const search = useAction();
  const probe = useAction();
  const add = useAction();

  const check = async (urls: string[], notes: Record<string, string | null> = {}, names: Record<string, string> = {}) => {
    setRows(null);
    setDone(null);
    const r = await probe.run(() => api.post<Probed[]>("/api/find-pages/probe", { productId, urls }));
    const seen = new Set<string>();
    if (r)
      setRows(
        r.map((p) => {
          // Already watched, or a second page of a store in this list: shown, but not added.
          const sameStore = p.alreadyAdded || seen.has(p.domain);
          seen.add(p.domain);
          return {
          ...p,
          // The store's name as the AI found it, when it searched.
          name: names[p.url] ?? p.name,
          note: notes[p.url] ?? null,
          alreadyAdded: sameStore,
          message: p.alreadyAdded ? `You already watch ${p.domain}.` : sameStore ? `Another ${p.domain} page is above.` : p.message,
          // Picked: a new store, works (or the AI can set it up), and not another product.
          pick: !sameStore && p.status !== "error" && p.status !== "foreign" && p.titleMatches !== false,
          runOnPc: p.status === "blocked",
          };
        }),
      );
  };

  const runSearch = async () => {
    const found = await search.run(() => api.post<Found[]>("/api/find-pages/search", { productId, hint }));
    if (!found) return;
    if (found.length === 0) return setDone("The AI didn't find any new pages.");
    await check(
      found.map((f) => f.url),
      Object.fromEntries(found.map((f) => [f.url, [f.price, f.note].filter(Boolean).join(" · ") || null])),
      Object.fromEntries(found.map((f) => [f.url, f.store])),
    );
  };

  const runPaste = () => {
    const urls = pasted.split(/[\s,]+/).filter((u) => /^https?:\/\//i.test(u));
    if (urls.length) void check(urls);
  };

  const set = (i: number, patch: Partial<Row>) => setRows((r) => r && r.map((x, j) => (j === i ? { ...x, ...patch } : x)));
  const picked = rows?.filter((r) => r.pick) ?? [];

  const addPicked = async () => {
    const r = await add.run(() =>
      api.post<{ added: number[]; skipped: string[]; settingUpWithAi: boolean }>("/api/find-pages/add", {
        productId,
        setupWithAi: setupWithAi && aiOn,
        pages: picked.map((p) => ({ name: p.name, url: p.url, runOnPc: p.runOnPc })),
      }),
    );
    if (!r) return;
    setDone(
      `Added ${r.added.length} page${r.added.length === 1 ? "" : "s"}.` +
        (r.skipped.length ? ` Skipped: ${r.skipped.join("; ")}.` : "") +
        (r.settingUpWithAi ? " The AI is setting up the ones that didn't read on their own (see Activity)." : "") +
        " They're checked in a moment; auto-buy is off on each until you turn it on.",
    );
    setRows(null);
    onAdded();
  };

  const busy = search.busy || probe.busy || add.busy;
  const statusBadge = (r: Row) =>
    ({
      reads: <span className="badge good">Reads</span>,
      unread: <span className="badge warn">Needs setup</span>,
      blocked: <span className="badge bad">Blocked</span>,
      error: <span className="badge bad">Error</span>,
      foreign: <span className="badge bad">Other country</span>,
    })[r.status];

  return (
    <div className="card stack">
      <div className="card-head" style={{ marginBottom: 0 }}>
        <h2>Find pages for {productName}</h2>
        <button className="btn ghost small" onClick={onClose}>
          Close
        </button>
      </div>
      <div className="tabs" style={{ marginBottom: 0 }}>
        <button className={mode === "ai" ? "active" : ""} onClick={() => setMode("ai")}>
          ✨ Search with AI
        </button>
        <button className={mode === "paste" ? "active" : ""} onClick={() => setMode("paste")}>
          Paste links
        </button>
      </div>

      {mode === "ai" ? (
        <div className="stack" style={{ gap: 8 }}>
          <p className="muted small">
            ChatGPT searches the web for stores that sell it new and ship to you (the country and province of your checkout profile, or
            your currency&apos;s country), leaving out stores you already watch and stores of other countries. One AI call with web search.
          </p>
          <div className="row" style={{ flexWrap: "nowrap" }}>
            <input type="text" value={hint} placeholder="Optional: e.g. include Costco and London Drugs, skip marketplaces" onChange={(e) => setHint(e.target.value)} />
            <button className="btn primary" style={{ flex: "none" }} disabled={busy || !aiOn} onClick={runSearch}>
              {search.busy ? (
                <>
                  <span className="spin" /> Searching…
                </>
              ) : (
                "Search"
              )}
            </button>
          </div>
          {ai.data && !aiOn && <div className="faint small">{ai.data.hasKey ? "The AI is turned off in Settings." : "The AI is off: add the OPENAI_API_KEY secret."}</div>}
        </div>
      ) : (
        <div className="stack" style={{ gap: 8 }}>
          <textarea value={pasted} placeholder={"https://www.bestbuy.ca/en-ca/product/…\nhttps://www.costco.ca/…"} onChange={(e) => setPasted(e.target.value)} style={{ minHeight: 120 }} />
          <div className="row">
            <button className="btn primary" disabled={busy || !pasted.trim()} onClick={runPaste}>
              Check links
            </button>
            <span className="faint small">One per line (or separated by spaces). Each is checked before anything is added.</span>
          </div>
        </div>
      )}

      <ErrorNote error={search.error || probe.error || add.error} />
      {probe.busy && (
        <div className="muted small">
          <span className="spin" /> Checking each page…
        </div>
      )}
      {done && <div className="notice good small">{done}</div>}

      {rows && (
        <>
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th />
                  <th>Store</th>
                  <th>Check</th>
                  <th>Price</th>
                  <th>What it found</th>
                  <th>On PC</th>
                </tr>
              </thead>
              <tbody>
                {rows.map((r, i) => (
                  <tr key={r.url} style={{ opacity: r.alreadyAdded ? 0.5 : 1 }}>
                    <td>
                      <input type="checkbox" checked={r.pick} disabled={r.alreadyAdded} onChange={(e) => set(i, { pick: e.target.checked })} />
                    </td>
                    <td style={{ minWidth: 200 }}>
                      <input type="text" value={r.name} onChange={(e) => set(i, { name: e.target.value })} />
                      <div className="faint small ellipsis" style={{ maxWidth: 320 }}>
                        <a href={r.url} target="_blank" rel="noreferrer noopener">
                          {r.url.replace(/^https?:\/\/(www\.)?/, "")}
                        </a>
                      </div>
                    </td>
                    <td>
                      {r.alreadyAdded ? <span className="badge">Store already watched</span> : statusBadge(r)}
                      {r.inRange && <div style={{ marginTop: 4 }}><span className="badge good">IN RANGE</span></div>}
                    </td>
                    <td className="num">{r.price != null ? money(r.price, currency) : "—"}</td>
                    <td className="small" style={{ maxWidth: 360 }}>
                      {r.title && <div className="ellipsis" style={{ maxWidth: 360 }}>{r.title}</div>}
                      <div className={r.titleMatches === false || r.status === "error" ? "" : "muted"} style={{ color: r.titleMatches === false ? "var(--danger)" : undefined }}>
                        {r.message}
                      </div>
                      {r.note && <div className="faint">AI: {r.note}</div>}
                    </td>
                    <td>
                      <input type="checkbox" checked={r.runOnPc} title="Check and buy it in Chrome on your PC (PC helper)" onChange={(e) => set(i, { runOnPc: e.target.checked })} />
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <Check
            label={aiOn ? "Set up the ones that don't read on their own with AI, after adding (uses tokens, within your daily limit)" : "Set up with AI (the AI is off)"}
            checked={setupWithAi && aiOn}
            onChange={setSetupWithAi}
          />
          <div className="row">
            <button className="btn primary" disabled={busy || picked.length === 0} onClick={addPicked}>
              {add.busy ? "Adding…" : `Add ${picked.length} page${picked.length === 1 ? "" : "s"}`}
            </button>
            <span className="faint small">Added with auto-buy off; turn it on per page once a dry run works.</span>
          </div>
        </>
      )}
    </div>
  );
}
