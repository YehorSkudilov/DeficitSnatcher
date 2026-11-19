"use client";

import { useState } from "react";
import { api, query, type BotEvent, type Site } from "@/lib/api";
import { dateTime } from "@/lib/format";
import { ErrorNote, Field, LevelDot, useAction, useData } from "./ui";

const iso = (local: string) => (local ? new Date(local).toISOString() : "");

/** Everything the bot did and saw, newest first (a product's or a page's own, in its Logs). */
export function Activity({ initial, productId, siteId: fixedSite }: { initial?: string; productId?: number; siteId?: number }) {
  const start = new URLSearchParams(initial ?? "");
  const [level, setLevel] = useState(start.get("level") ?? "");
  const [siteId, setSiteId] = useState(fixedSite ? String(fixedSite) : (start.get("site") ?? ""));
  const [search, setSearch] = useState("");
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  const [older, setOlder] = useState<BotEvent[]>([]);
  const [done, setDone] = useState(false);
  const sites = useData(() => api.get<Site[]>(`/api/sites${query({ productId })}`), [productId]);
  const action = useAction();

  const params = { level, siteId, productId, search: search.trim(), from: iso(from), to: iso(to) };
  const key = JSON.stringify(params);
  const latest = useData(() => api.get<BotEvent[]>(`/api/events${query({ ...params, limit: 100 })}`), [key], 5000);
  const reset = () => (setOlder([]), setDone(false));

  const all = [...(latest.data ?? []), ...older.filter((o) => !(latest.data ?? []).some((l) => l.id === o.id))];

  const more = async () => {
    const last = all[all.length - 1];
    if (!last) return;
    const page = await action.run(() => api.get<BotEvent[]>(`/api/events${query({ ...params, limit: 200, before: last.id })}`));
    if (page) {
      setOlder([...older, ...page]);
      if (page.length < 200) setDone(true);
    }
  };

  const clear = async () => {
    if (!confirm("Clear the whole activity log?")) return;
    const ok = await action.run(async () => (await api.del("/api/events"), true));
    if (ok) {
      reset();
      void latest.reload();
    }
  };

  return (
    <div className="stack" style={{ gap: 16 }}>
      <div className="page-head" style={{ marginBottom: 0 }}>
        <p className="muted">What happened: in stock alerts, price drops, checkouts, AI fixes, failing pages, settings changes.</p>
        {!productId && !fixedSite && (
          <button className="btn danger small" disabled={action.busy} onClick={clear}>
            Clear
          </button>
        )}
      </div>
      <ErrorNote error={latest.error || action.error} />
      <div className="card filters">
        <Field label="Kind">
          <select value={level} onChange={(e) => (setLevel(e.target.value), reset())}>
            <option value="">Everything</option>
            <option value="Good">Good news</option>
            <option value="Info">Info</option>
            <option value="Warning">Warnings</option>
            <option value="Error">Errors</option>
          </select>
        </Field>
        {!fixedSite && (
        <Field label="Page">
          <select value={siteId} onChange={(e) => (setSiteId(e.target.value), reset())}>
            <option value="">All</option>
            {sites.data?.map((s) => (
              <option key={s.id} value={s.id}>
                {s.name}
              </option>
            ))}
          </select>
        </Field>
        )}
        <Field label="From">
          <input type="datetime-local" value={from} onChange={(e) => (setFrom(e.target.value), reset())} />
        </Field>
        <Field label="To">
          <input type="datetime-local" value={to} onChange={(e) => (setTo(e.target.value), reset())} />
        </Field>
        <Field label="Search">
          <input type="text" value={search} onChange={(e) => (setSearch(e.target.value), reset())} />
        </Field>
      </div>
      <div className="card">
        {latest.data && all.length === 0 && <p className="muted">Nothing here.</p>}
        <div className="events">
          {all.map((e) => (
            <div className="event" key={e.id}>
              <LevelDot level={e.level} />
              <span>
                <Linked text={e.message} />
                {e.siteId && (
                  <>
                    {" "}
                    <a href={`#site/${e.siteId}`} className="small">
                      {e.siteName ?? "site"}
                    </a>
                  </>
                )}
              </span>
              <span className="faint small" style={{ whiteSpace: "nowrap" }}>
                {dateTime(e.at)}
              </span>
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

/** "checkout #12" in a message links to it. */
function Linked({ text }: { text: string }) {
  const parts = text.split(/(checkout #\d+|Checkout #\d+|Dry run #\d+)/);
  return (
    <>
      {parts.map((p, i) => {
        const m = /#(\d+)$/.exec(p);
        return m && i % 2 === 1 ? (
          <a key={i} href={`#purchase/${m[1]}`}>
            {p}
          </a>
        ) : (
          <span key={i}>{p}</span>
        );
      })}
    </>
  );
}
