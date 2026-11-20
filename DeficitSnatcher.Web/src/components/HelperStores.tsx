"use client";

import { Fragment, useState } from "react";
import { api, type StoreAccount, type StoreLogin, type WorkerInfo } from "@/lib/api";
import { ago } from "@/lib/format";
import { ErrorNote, useAction, useData, useNow } from "./ui";

export const loginBadge: Record<string, [string, string]> = {
  signedIn: ["good", "Signed in"],
  signedOut: ["bad", "Signed out"],
  humanCheck: ["warn", "Captcha"],
  error: ["warn", "Couldn't check"],
  unknown: ["", "Not checked yet"],
};

export function loginStatus(l: StoreLogin): [string, string] {
  return l.status === "unknown" && l.checkedAt ? ["warn", "Can't tell"] : (loginBadge[l.status] ?? loginBadge.unknown);
}

/** A store's domain as the helpers list it: the host without www. */
export function storeOf(url: string): string {
  try {
    const host = new URL(url).host.toLowerCase();
    return host.startsWith("www.") ? host.slice(4) : host;
  } catch {
    return "";
  }
}

/**
 * A product's stores on the PC helpers: whether each helper's Chrome is signed in to them, with Log in, Sign in for me and
 * Reset, whether that helper checks the store, and your saved sign-in for it.
 */
export function HelperStores({ urls }: { urls: string[] }) {
  const list = useData(() => api.get<WorkerInfo[]>("/api/runners"), [], 10000);
  const accounts = useData(() => api.get<StoreAccount[]>("/api/store-accounts"), []);
  const [editing, setEditing] = useState<string | null>(null);
  const helpers = list.data?.filter((w) => w.id != null) ?? [];
  const stores = [...new Set(urls.map(storeOf).filter(Boolean))].sort();
  if (!list.data || stores.length === 0) return null;

  return (
    <div className="card stack">
      <div className="card-head" style={{ marginBottom: 0 }}>
        <h2>Stores on your PC helpers</h2>
        <a className="btn small ghost" href="#helpers">
          Helpers →
        </a>
      </div>
      {helpers.length === 0 ? (
        <div className="small muted">
          No PC helper yet. <a href="#helpers">Add one</a> for stores that block the server.
        </div>
      ) : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Store</th>
                {helpers.length > 1 && <th>Helper</th>}
                <th>Signed in?</th>
                <th title="Checked and bought on this helper">Checks</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {stores.map((d) => {
                const account = accounts.data?.find((a) => a.domain === d);
                return (
                  <Fragment key={d}>
                    {helpers.map((h, i) => (
                      <StoreRow
                        key={h.id}
                        domain={d}
                        h={h}
                        first={i === 0}
                        rows={helpers.length}
                        saved={!!account}
                        onEditSignIn={() => setEditing(editing === d ? null : d)}
                        reload={list.reload}
                      />
                    ))}
                    {editing === d && (
                      <tr>
                        <td colSpan={helpers.length > 1 ? 5 : 4}>
                          <AccountForm domain={d} account={account} onDone={() => (setEditing(null), void accounts.reload())} />
                        </td>
                      </tr>
                    )}
                  </Fragment>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
      <div className="small faint">
        A saved sign-in lets a helper sign itself back in when it finds itself signed out. A captcha or a code from the store opens on that
        computer for you, with an alert.
      </div>
    </div>
  );
}

function StoreRow({ domain, h, first, rows, saved, onEditSignIn, reload }: {
  domain: string;
  h: WorkerInfo;
  first: boolean;
  rows: number;
  saved: boolean;
  onEditSignIn: () => void;
  reload: () => Promise<void>;
}) {
  const act = useAction();
  const now = useNow(60000);
  const [note, setNote] = useState<string | null>(null);
  const id = h.id!;
  const l = h.logins.find((x) => x.domain === domain);
  const [cls, text] = l ? loginStatus(l) : loginBadge.unknown;
  const on = h.checksEnabled && !h.disabledDomains.includes(domain);
  const off = !h.connected || act.busy;

  const run = async (f: () => Promise<unknown>, done: string) => {
    setNote(null);
    let ok = false;
    await act.run(async () => {
      await f();
      ok = true;
    });
    if (ok) setNote(done);
  };
  const reset = () => {
    if (
      !confirm(
        `Reset ${domain} in ${h.name}'s Chrome? Its cookies, site data and cache for that store are wiped, so the store sees a browser it hasn't met. You'll be signed out of ${domain}.`,
      )
    )
      return;
    void run(() => api.post(`/api/runners/${id}/reset`, { domain }), `Reset: ${domain} sees a new browser. Log in to it again.`);
  };

  return (
    <>
      <tr>
        {first && (
          <td rowSpan={rows} style={{ verticalAlign: "top" }}>
            <a href={l?.accountUrl ?? `https://${domain}`} target="_blank" rel="noreferrer">
              {domain}
            </a>
            <div>
              <button className="linklike small faint" onClick={onEditSignIn} title="Your email and password for this store, for signing in by itself">
                {saved ? "Saved sign-in ✓" : "Save sign-in"}
              </button>
            </div>
          </td>
        )}
        {rows > 1 && (
          <td className="small">
            {h.name}
            {!h.connected && <span className="faint"> (offline)</span>}
          </td>
        )}
        <td>
          <span className={`badge ${cls}`} title={l?.detail ?? undefined}>
            {text}
          </span>
          {l?.checkedAt && <span className="faint small"> {ago(l.checkedAt, now)}</span>}
        </td>
        <td>
          <button
            className={`toggle small go ${on ? "on" : ""}`}
            aria-label={`Check ${domain} on ${h.name}`}
            title={!h.checksEnabled ? "Checks are off for this helper (Helpers tab)" : on ? "Checked here: click to stop" : "Not checked here: click to start"}
            disabled={act.busy || !h.checksEnabled}
            onClick={() => void act.run(() => api.put(`/api/runners/${id}/checks`, { on: !on, domain })).then(reload)}
          />
        </td>
        <td style={{ textAlign: "right", whiteSpace: "nowrap" }}>
          {saved && l?.status !== "signedIn" && (
            <button
              className="btn small primary"
              style={{ marginRight: 6 }}
              disabled={off}
              title="Signs in with your saved sign-in, in the background"
              onClick={() => void run(() => api.post(`/api/runners/${id}/sign-in`, { domain }), "Signing in: this updates in a minute.")}
            >
              Sign in for me
            </button>
          )}
          <button
            className={`btn small ${(l?.status === "signedOut" || l?.status === "humanCheck") && !saved ? "primary" : ""}`}
            style={{ marginRight: 6 }}
            disabled={off}
            title={h.connected ? "Opens the store in this helper's Chrome, in front, to sign in or do a captcha" : "Not connected"}
            onClick={() => void run(() => api.post(`/api/runners/${id}/login`, { domain }), `Open in ${h.name}'s Chrome: sign in there.`)}
          >
            Log in
          </button>
          <button
            className="btn small ghost"
            disabled={off}
            title="Wipes this store's cookies, site data and cache in this helper's Chrome: a fresh start if the store has flagged it"
            onClick={reset}
          >
            Reset
          </button>
        </td>
      </tr>
      {(note || act.error) && (
        <tr>
          <td colSpan={rows > 1 ? 4 : 3} className="small">
            {note && <span className="faint">{note}</span>}
            <ErrorNote error={act.error} />
          </td>
        </tr>
      )}
    </>
  );
}

/** Your email and password for a store (encrypted; the password is never shown again). */
function AccountForm({ domain, account, onDone }: { domain: string; account?: StoreAccount; onDone: () => void }) {
  const act = useAction();
  const [email, setEmail] = useState(account?.email ?? "");
  const [password, setPassword] = useState("");
  const save = async () => {
    let ok = false;
    await act.run(async () => {
      await api.put(`/api/store-accounts/${encodeURIComponent(domain)}`, { email, password: password || null });
      ok = true;
    });
    if (ok) onDone();
  };
  const forget = async () => {
    if (!confirm(`Forget your sign-in for ${domain}?`)) return;
    let ok = false;
    await act.run(async () => {
      await api.del(`/api/store-accounts/${encodeURIComponent(domain)}`);
      ok = true;
    });
    if (ok) onDone();
  };
  return (
    <div className="stack" style={{ gap: 4 }}>
      <div className="row" style={{ gap: 8, flexWrap: "wrap" }}>
        <input type="email" autoComplete="off" placeholder={`Email for ${domain}`} value={email} onChange={(e) => setEmail(e.target.value)} style={{ maxWidth: 240 }} />
        <input
          type="password"
          autoComplete="new-password"
          placeholder={account?.hasPassword ? "Password (saved; type to change)" : "Password"}
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          style={{ maxWidth: 240 }}
        />
        <button className="btn small primary" disabled={act.busy || !email || (!password && !account?.hasPassword)} onClick={save}>
          Save
        </button>
        {account && (
          <button className="btn small ghost" disabled={act.busy} onClick={forget}>
            Forget
          </button>
        )}
        <span className="faint small">Encrypted; the password is never shown again.</span>
      </div>
      <ErrorNote error={act.error} />
    </div>
  );
}
