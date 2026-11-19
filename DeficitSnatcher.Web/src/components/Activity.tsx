"use client";

import { useState } from "react";
import { api, type BotEvent, type EventLevel } from "@/lib/api";
import { dateTime } from "@/lib/format";
import { ErrorNote, LevelDot, useAction, useData } from "./ui";

export function Activity() {
  const latest = useData(() => api.get<BotEvent[]>("/api/events?limit=100"), [], 5000);
  const [older, setOlder] = useState<BotEvent[]>([]);
  const [done, setDone] = useState(false);
  const [level, setLevel] = useState<EventLevel | "">("");
  const action = useAction();

  const all = [...(latest.data ?? []), ...older.filter((o) => !(latest.data ?? []).some((l) => l.id === o.id))];
  const shown = level ? all.filter((e) => e.level === level) : all;

  const more = async () => {
    const last = all[all.length - 1];
    if (!last) return;
    const page = await action.run(() => api.get<BotEvent[]>(`/api/events?limit=200&before=${last.id}`));
    if (page) {
      setOlder([...older, ...page]);
      if (page.length < 200) setDone(true);
    }
  };

  const clear = async () => {
    if (!confirm("Clear the whole activity log?")) return;
    const ok = await action.run(async () => (await api.del("/api/events"), true));
    if (ok) {
      setOlder([]);
      void latest.reload();
    }
  };

  return (
    <div className="stack" style={{ gap: 16 }}>
      <div className="page-head">
        <h1>Activity</h1>
        <div className="row">
          <select value={level} onChange={(e) => setLevel(e.target.value as EventLevel | "")} style={{ width: "auto" }}>
            <option value="">Everything</option>
            <option value="Good">Good news</option>
            <option value="Info">Info</option>
            <option value="Warning">Warnings</option>
            <option value="Error">Errors</option>
          </select>
          <button className="btn danger" disabled={action.busy} onClick={clear}>
            Clear
          </button>
        </div>
      </div>
      <ErrorNote error={latest.error || action.error} />
      <div className="card">
        {shown.length === 0 && <p className="muted">Nothing here.</p>}
        <div className="events">
          {shown.map((e) => (
            <div className="event" key={e.id}>
              <LevelDot level={e.level} />
              <span>
                {e.message}
                {e.siteId && (
                  <>
                    {" "}
                    <a href={`#site/${e.siteId}`} className="small">
                      {e.siteName ?? "site"}
                    </a>
                  </>
                )}
              </span>
              <span className="faint small">{dateTime(e.at)}</span>
            </div>
          ))}
        </div>
        {!done && all.length >= 100 && (
          <button className="btn small" style={{ marginTop: 12 }} disabled={action.busy} onClick={more}>
            Older
          </button>
        )}
      </div>
    </div>
  );
}
