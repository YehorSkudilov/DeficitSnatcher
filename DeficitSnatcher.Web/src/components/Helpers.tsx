"use client";

import { useState } from "react";
import { api, type StoreLogin, type WorkerInfo } from "@/lib/api";
import { ago, dateTime } from "@/lib/format";
import { ErrorNote, useAction, useData, useNow } from "./ui";

const release = "https://github.com/YehorSkudilov/DeficitSnatcher/releases";
const downloads = [
  ["Windows", "win-x64"],
  ["Mac (Apple chip)", "osx-arm64"],
  ["Mac (Intel)", "osx-x64"],
  ["Linux", "linux-x64"],
] as const;

/** This computer's helper download, by its browser. */
function myDownload(): (typeof downloads)[number] {
  const ua = typeof navigator === "undefined" ? "" : navigator.userAgent;
  if (/Windows/i.test(ua)) return downloads[0];
  if (/Mac OS X|Macintosh/i.test(ua)) return downloads[1];
  if (/Linux/i.test(ua)) return downloads[3];
  return downloads[0];
}

/** The site's address and the helper's key in one code, pasted into the helper. */
function setupCode(site: string, key: string) {
  const json = JSON.stringify({ site, key });
  const b64 = btoa(unescape(encodeURIComponent(json))).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
  return `DSH1.${b64}`;
}

/** Every PC helper and the server: who's connected, what each has checked, and which stores each Chrome is signed in to. */
export function Helpers() {
  const list = useData(() => api.get<WorkerInfo[]>("/api/runners"), [], 5000);
  const helpers = list.data?.filter((w) => w.id != null) ?? [];
  const server = list.data?.find((w) => w.id == null);
  const connected = helpers.filter((h) => h.connected).length;
  return (
    <div className="stack" style={{ gap: 16 }}>
      <div className="page-head">
        <h1>Helpers</h1>
      </div>
      <p className="muted small" style={{ margin: 0 }}>
        Every page is checked by every connected helper and the server in turn, spaced out so two never check it at once. Helpers get two
        turns for the server&apos;s one; pages set to <b>Run on my PC</b> skip the server while a helper is connected. A buy runs where the
        item was seen in stock.
      </p>
      <ErrorNote error={list.error} />
      {list.data && (
        <div className="small muted">
          {connected} of {helpers.length} helper{helpers.length === 1 ? "" : "s"} connected
        </div>
      )}
      {helpers.map((h) => (
        <HelperCard key={h.id} h={h} reload={list.reload} />
      ))}
      {server && <ServerCard s={server} />}
      <AddHelper first={helpers.length === 0} reload={list.reload} />
    </div>
  );
}

function Stats({ w }: { w: WorkerInfo }) {
  const now = useNow(5000);
  return (
    <div className="kv small">
      <span className="faint">Checks</span>
      <span>
        {w.checksLastHour} in the last hour ({w.pagesLastHour} page{w.pagesLastHour === 1 ? "" : "s"}) · {w.checksToday} in 24 h
        {w.errorsLastHour > 0 && <span className="badge bad" style={{ marginLeft: 8 }}>{w.errorsLastHour} failed this hour</span>}
      </span>
      <span className="faint">Last check</span>
      <span>{w.lastCheck ? `${ago(w.lastCheck, now)} (${dateTime(w.lastCheck)})` : "None yet"}</span>
    </div>
  );
}

function ServerCard({ s }: { s: WorkerInfo }) {
  return (
    <div className="card stack">
      <div className="card-head" style={{ marginBottom: 0 }}>
        <h2>Server</h2>
        <span className="badge good">Always on</span>
      </div>
      <div className="small muted">
        {s.machine} · {s.browser} · takes its turn after the helpers
      </div>
      <Stats w={s} />
    </div>
  );
}

const loginBadge: Record<string, [string, string]> = {
  signedIn: ["good", "Signed in"],
  signedOut: ["bad", "Signed out"],
  humanCheck: ["warn", "Captcha"],
  error: ["warn", "Couldn't check"],
  unknown: ["", "Not checked yet"],
};

function HelperCard({ h, reload }: { h: WorkerInfo; reload: () => Promise<void> }) {
  const act = useAction();
  const [opened, setOpened] = useState<string | null>(null);
  const [checking, setChecking] = useState(false);
  const id = h.id!;

  const rename = async () => {
    const name = prompt("Name this helper", h.name);
    if (!name || name.trim() === h.name) return;
    await act.run(() => api.put(`/api/runners/${id}`, { name: name.trim() }));
    await reload();
  };
  const revoke = async () => {
    if (!confirm(`Remove ${h.name}? Its setup code stops working and it's disconnected now. To use that computer again, make a new code.`)) return;
    await act.run(() => api.del(`/api/runners/${id}`));
    await reload();
  };
  const login = async (l: StoreLogin) => {
    setOpened(null);
    let ok = false;
    await act.run(async () => {
      await api.post(`/api/runners/${id}/login`, { domain: l.domain });
      ok = true;
    });
    if (ok) setOpened(l.domain);
  };
  const checkNow = async () => {
    let ok = false;
    await act.run(async () => {
      await api.post(`/api/runners/${id}/logins/check`);
      ok = true;
    });
    setChecking(ok);
  };

  return (
    <div className="card stack">
      <div className="card-head" style={{ marginBottom: 0 }}>
        <h2>{h.name}</h2>
        {h.connected ? <span className="badge good">Connected</span> : <span className="badge warn">Not connected</span>}
      </div>
      <div className="small muted">
        {[h.machine, h.browser].filter(Boolean).join(" · ") || "Hasn't connected yet"}
        {h.connected && h.since && <> · connected {dateTime(h.since)}</>}
        {!h.connected && h.lastSeen && <> · last seen {dateTime(h.lastSeen)}</>}
      </div>
      <Stats w={h} />

      <div className="stack" style={{ gap: 8 }}>
        <div className="row" style={{ justifyContent: "space-between" }}>
          <b className="small">Store sign-ins</b>
          <button className="btn small ghost" disabled={!h.connected || act.busy} onClick={checkNow}>
            Check now
          </button>
        </div>
        {!h.connected && (
          <div className="notice warn small">
            Sign-ins are checked in this helper&apos;s Chrome, so only while it&apos;s connected; until then they say &quot;Not checked yet&quot; and
            its pages are checked from the server. On that computer, run <code>deficitsnatcher status</code> to see why (or{" "}
            <code>deficitsnatcher start</code>). If it says the key was refused, make a new setup code below and run{" "}
            <code>deficitsnatcher setup &lt;code&gt;</code>.
          </div>
        )}
        {checking && <div className="small faint">Checking in the next minute: it opens each store&apos;s account page in that Chrome.</div>}
        {h.logins.length === 0 ? (
          <div className="small faint">No pages yet.</div>
        ) : (
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Store</th>
                  <th>Status</th>
                  <th>Checked</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {h.logins.map((l) => {
                  const [cls, text] = loginBadge[l.status] ?? loginBadge.unknown;
                  return (
                    <tr key={l.domain}>
                      <td>
                        <a href={l.accountUrl} target="_blank" rel="noreferrer">
                          {l.domain}
                        </a>
                      </td>
                      <td>
                        <span className={`badge ${cls}`} title={l.detail ?? undefined}>
                          {text}
                        </span>
                      </td>
                      <td className="small faint">{l.checkedAt ? dateTime(l.checkedAt) : ""}</td>
                      <td style={{ textAlign: "right" }}>
                        <button
                          className={`btn small ${l.status === "signedOut" || l.status === "humanCheck" ? "primary" : ""}`}
                          disabled={!h.connected || act.busy}
                          title={h.connected ? "Opens the store in this helper's Chrome, in front" : "Not connected"}
                          onClick={() => login(l)}
                        >
                          Log in
                        </button>
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}
        {opened && (
          <div className="notice good small">
            {opened} is open in {h.name}&apos;s Chrome on that computer. Sign in there (or do the captcha); this updates by itself once you&apos;re
            in.
          </div>
        )}
        <div className="small faint">
          The helper stays connected all the time; only this sign-in check runs now and then: when it connects, then every 6 hours (or
          Check now). Signed out or a captcha sends you an alert (email too, if set up).
        </div>
      </div>

      <ErrorNote error={act.error} />
      <div className="row">
        <button className="btn small" onClick={rename} disabled={act.busy}>
          Rename
        </button>
        <button className="btn small danger" onClick={revoke} disabled={act.busy}>
          Remove
        </button>
      </div>
    </div>
  );
}

/** Add a helper: download it, make its setup code, paste. */
function AddHelper({ first, reload }: { first: boolean; reload: () => Promise<void> }) {
  const make = useAction();
  const [name, setName] = useState("");
  const [code, setCode] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);
  const site = typeof window === "undefined" ? "" : window.location.origin;
  const [label, rid] = myDownload();

  const newCode = async () => {
    const r = await make.run(() => api.post<{ id: number; key: string }>("/api/runner/key", { name: name.trim() || null }));
    if (r) {
      setCode(setupCode(site, r.key));
      setCopied(false);
      setName("");
      await reload();
    }
  };
  const copy = async () => {
    if (!code) return;
    try {
      await navigator.clipboard.writeText(code);
      setCopied(true);
    } catch {
      // Select it by hand.
    }
  };

  return (
    <div className="card stack">
      <div className="card-head" style={{ marginBottom: 0 }}>
        <h2>{first ? "Add a PC helper" : "Add another helper"}</h2>
      </div>
      <p className="muted small" style={{ margin: 0 }}>
        For stores that block servers (Walmart, Costco, Amazon): a small program on a computer lends the bot its Chrome, on that home
        internet, with your sign-ins. It runs in the background and starts with the computer. Each computer gets its own setup code.
      </p>
      <ol className="small" style={{ margin: 0, paddingLeft: 18, lineHeight: 2 }}>
        <li>
          <a className="btn small primary" href={`${release}/download/pc-helper/DeficitSnatcher-PC-helper-${rid}.zip`}>
            Download for {label}
          </a>{" "}
          <span className="faint">
            or{" "}
            {downloads
              .filter(([, r]) => r !== rid)
              .map(([l, r], i) => (
                <span key={r}>
                  {i > 0 && ", "}
                  <a href={`${release}/download/pc-helper/DeficitSnatcher-PC-helper-${r}.zip`}>{l}</a>
                </span>
              ))}
          </span>
          , and unzip it.
        </li>
        <li>
          <div className="row" style={{ flexWrap: "nowrap", maxWidth: 420 }}>
            <input type="text" placeholder="Name, e.g. Gaming PC" value={name} onChange={(e) => setName(e.target.value)} maxLength={100} />
            <button className="btn small" style={{ flex: "none" }} disabled={make.busy} onClick={newCode}>
              Make a setup code
            </button>
          </div>
        </li>
        <li>
          Open <code>DeficitSnatcher.Runner</code> (double-click) and paste the code. That&apos;s it: it installs itself and keeps running in the
          background. Then sign in to the stores from its card here (Log in).
        </li>
      </ol>
      <ErrorNote error={make.error} />
      {code && (
        <div className="notice warn small stack" style={{ gap: 8 }}>
          <div>The setup code (copy it now, it isn&apos;t shown again):</div>
          <div className="row" style={{ flexWrap: "nowrap" }}>
            <input type="text" readOnly value={code} onFocus={(e) => e.target.select()} className="mono" />
            <button className="btn small" style={{ flex: "none" }} onClick={copy}>
              {copied ? "Copied" : "Copy"}
            </button>
          </div>
        </div>
      )}
      <details className="small">
        <summary>Commands</summary>
        <pre className="log" style={{ marginTop: 8 }}>{`deficitsnatcher status        running? connected?
deficitsnatcher stop | start | restart
deficitsnatcher window show   show its Chrome (to sign in to a store)
deficitsnatcher window hide   minimise it again
deficitsnatcher autostart off don't start with the computer
deficitsnatcher setup <code>  connect with a new setup code
deficitsnatcher logs          what it's been doing
deficitsnatcher uninstall     remove it`}</pre>
        <div className="faint">In a new terminal after setup (Windows: Command Prompt or PowerShell; Mac: Terminal).</div>
        <div className="faint" style={{ marginTop: 4 }}>
          First start: Windows may say &quot;Windows protected your PC&quot; (More info → Run anyway); a Mac may refuse it (right-click → Open).
        </div>
      </details>
    </div>
  );
}
