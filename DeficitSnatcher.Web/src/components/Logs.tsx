"use client";

import { useState } from "react";
import { Scans } from "./Scans";
import { Purchases } from "./Purchases";
import { Activity } from "./Activity";
import { AiCalls } from "./AiCalls";
import { Diagnostics } from "./Diagnostics";
import { CopyLogsButton } from "./CopyLogs";

const tabs = [
  ["scans", "Scans", "Every check, found or not, with the page's screenshot and source when something changed."],
  ["checkouts", "Checkouts", "Every checkout, with each step's screenshot and the full error."],
  ["activity", "Activity", "Alerts, price drops, AI fixes, failing pages."],
  ["ai", "AI", "Every question sent to ChatGPT and its answer."],
  ["system", "System", "Combined API and browser container output; copy all from here."],
] as const;

/** The logs of one product (all its pages) or one page, as tabs. */
export function Logs({ productId, siteId }: { productId?: number; siteId?: number }) {
  const [tab, setTab] = useState<(typeof tabs)[number][0]>("scans");
  return (
    <div className="stack" style={{ gap: 14 }}>
      <div className="row" style={{ justifyContent: "space-between" }}>
        {productId || siteId ? <h2>Logs{siteId ? " of this page" : " of all its pages"}</h2> : <h1>Logs</h1>}
        <CopyLogsButton productId={productId} siteId={siteId} />
      </div>
      <div className="tabs" style={{ marginBottom: 0 }}>
        {tabs.map(([key, label, help]) => (
          <button key={key} className={tab === key ? "active" : ""} title={help} onClick={() => setTab(key)}>
            {label}
          </button>
        ))}
      </div>
      {tab === "scans" && <Scans productId={productId} siteId={siteId} />}
      {tab === "checkouts" && <Purchases productId={productId} siteId={siteId} />}
      {tab === "activity" && <Activity productId={productId} siteId={siteId} />}
      {tab === "ai" && <AiCalls productId={productId} siteId={siteId} />}
      {tab === "system" && <Diagnostics />}
    </div>
  );
}
