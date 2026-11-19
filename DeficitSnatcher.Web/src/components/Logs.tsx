"use client";

import { Scans } from "./Scans";
import { Purchases } from "./Purchases";
import { Activity } from "./Activity";

const tabs = [
  ["scans", "Scans", "Every check of every page, found or not."],
  ["checkouts", "Checkouts", "Every checkout, real or dry run, with each step's screenshot and the full error."],
  ["activity", "Activity", "Alerts, price drops, failing pages, settings changes."],
] as const;

/** Everything the bot did: #logs/scans, #logs/checkouts, #logs/activity (filters after ?, e.g. ?site=3). */
export function Logs({ tab, search }: { tab?: string; search?: string }) {
  const current = tabs.find(([k]) => k === tab)?.[0] ?? "scans";
  return (
    <div className="stack" style={{ gap: 16 }}>
      <div className="page-head" style={{ marginBottom: 0 }}>
        <h1>Logs</h1>
      </div>
      <div className="tabs" style={{ marginBottom: 0 }}>
        {tabs.map(([key, label, help]) => (
          <button key={key} className={current === key ? "active" : ""} title={help} onClick={() => (window.location.hash = `logs/${key}`)}>
            {label}
          </button>
        ))}
      </div>
      {current === "scans" && <Scans key={search} initial={search} />}
      {current === "checkouts" && <Purchases key={search} initial={search} />}
      {current === "activity" && <Activity key={search} initial={search} />}
    </div>
  );
}
