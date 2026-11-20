"use client";

import { useState } from "react";
import { api } from "@/lib/api";
import { ErrorNote, useData } from "./ui";

type DiagnosticLog = { at: string; source: string; level: string; message: string };
type DiagnosticReply = { entries: DiagnosticLog[] };

export function Diagnostics() {
  const loaded = useData(() => api.get<DiagnosticReply>("/api/diagnostics/logs?limit=3000"), [], 5000);
  const [copied, setCopied] = useState(false);
  const entries = loaded.data?.entries ?? [];
  const text = entries.map((e) => `${e.at} [${e.level}] [${e.source}] ${e.message}`).join("\n");

  const copyAll = async () => {
    try {
      if (navigator.clipboard?.writeText) {
        await navigator.clipboard.writeText(text);
      } else {
        const area = document.createElement("textarea");
        area.value = text;
        area.style.position = "fixed";
        area.style.opacity = "0";
        document.body.appendChild(area);
        area.select();
        document.execCommand("copy");
        area.remove();
      }
      setCopied(true);
      window.setTimeout(() => setCopied(false), 2000);
    } catch {
      setCopied(false);
    }
  };

  return (
    <div className="stack" style={{ gap: 16 }}>
      <div className="page-head">
        <div>
          <h1>Diagnostics</h1>
          <div className="muted small">Recent API and Camoufox container logs, refreshed every 5 seconds.</div>
        </div>
        <div className="row">
          <button className="btn" onClick={() => void loaded.reload()}>Refresh</button>
          <button className="btn primary" disabled={!text} onClick={() => void copyAll()}>{copied ? "Copied" : "Copy all"}</button>
        </div>
      </div>
      <div className="small muted">
        Keeps up to 3,000 recent lines in memory. A server restart clears them. Passwords, cookies, tokens, email addresses, URL query strings,
        and long card-like numbers are redacted before display and copy.
      </div>
      <ErrorNote error={loaded.error} />
      <div className="card" style={{ padding: 0, overflow: "hidden" }}>
        {entries.length ? (
          <pre className="log" style={{ margin: 0, maxHeight: "70vh", overflow: "auto", whiteSpace: "pre-wrap", overflowWrap: "anywhere" }}>
            {text}
          </pre>
        ) : (
          <div className="muted" style={{ padding: 18 }}>{loaded.error ? "Couldn't load diagnostics." : "Waiting for logs…"}</div>
        )}
      </div>
    </div>
  );
}
