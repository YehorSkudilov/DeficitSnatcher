"use client";

import { Fragment, useState } from "react";
import { api, query, type AiCall } from "@/lib/api";
import { dateTime } from "@/lib/format";
import { ErrorNote, useData } from "./ui";

const purposes: Record<string, string> = {
  setup: "Set up page",
  checkout: "Checkout (no steps)",
  "record-checkout": "Record checkout",
  "autofix-checkout": "Fix broken checkout",
  "autofix-reading": "Fix reading the page",
};

/** Every call to the AI for a product or a page: what for, how big, how long, and its answer. */
export function AiCalls({ productId, siteId }: { productId?: number; siteId?: number }) {
  const { data, error } = useData(() => api.get<AiCall[]>(`/api/ai/calls${query({ productId, siteId, limit: 200 })}`), [productId, siteId], 10000);
  const [open, setOpen] = useState<number | null>(null);
  const tokens = (data ?? []).reduce((n, c) => n + c.inputTokens + c.outputTokens, 0);
  return (
    <div className="stack" style={{ gap: 12 }}>
      <p className="muted">
        Every question sent to ChatGPT{siteId ? " for this page" : " for this product's pages"}, with its answer.{" "}
        {data && data.length > 0 && `${data.length} calls, ${tokens.toLocaleString()} tokens.`}
      </p>
      <ErrorNote error={error} />
      <div className="card">
        {data?.length === 0 && <p className="muted">The AI hasn&apos;t been used here yet.</p>}
        {!!data?.length && (
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>When</th>
                  <th>For</th>
                  {!siteId && <th>Page</th>}
                  <th>Result</th>
                  <th>Tokens</th>
                  <th>Took</th>
                </tr>
              </thead>
              <tbody>
                {data.map((c) => (
                  <Fragment key={c.id}>
                    <tr className="clickable" onClick={() => setOpen(open === c.id ? null : c.id)}>
                      <td className="small" style={{ whiteSpace: "nowrap" }}>
                        {dateTime(c.at)}
                      </td>
                      <td>
                        {purposes[c.purpose] ?? c.purpose}
                        {c.purchaseId && (
                          <>
                            {" "}
                            <a className="small" href={`#purchase/${c.purchaseId}`} onClick={(e) => e.stopPropagation()}>
                              #{c.purchaseId}
                            </a>
                          </>
                        )}
                      </td>
                      {!siteId && <td>{c.siteName ?? "—"}</td>}
                      <td>{c.ok ? <span className="badge good">ok</span> : <span className="badge bad">failed</span>}</td>
                      <td className="num small faint">
                        {c.inputTokens.toLocaleString()} + {c.outputTokens.toLocaleString()}
                      </td>
                      <td className="num small faint">{(c.durationMs / 1000).toFixed(1)}s</td>
                    </tr>
                    {open === c.id && (
                      <tr>
                        <td colSpan={6} style={{ background: "var(--surface-2)" }}>
                          <div className="faint small" style={{ marginBottom: 6 }}>
                            {c.model} · {c.ok ? "the AI's answer" : "what went wrong"}:
                          </div>
                          <pre className="log">{pretty(c.summary)}</pre>
                        </td>
                      </tr>
                    )}
                  </Fragment>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </div>
  );
}

function pretty(s: string) {
  try {
    return JSON.stringify(JSON.parse(s), null, 2);
  } catch {
    return s;
  }
}
