"use client";

import { useState } from "react";
import { api, type WorkerInfo } from "@/lib/api";
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
  const versions = useData(() => api.get<{ latest: string | null; error: string | null }>("/api/runners/latest-version"), [], 600000);
  const helpers = list.data?.filter((w) => w.id != null) ?? [];
  const server = list.data?.find((w) => w.id == null);
  return (
    <div className="stack" style={{ gap: 16 }}>
      <div className="page-head">
        <h1>Helpers</h1>
      </div>
      <p className="muted small" style={{ margin: 0 }}>
        Computers that lend the bot their Chrome and home internet, for stores that block the server. Store sign-ins are on each
        product&apos;s page.
      </p>
      <ErrorNote error={list.error} />
      {helpers.map((h) => (
        <HelperCard key={h.id} h={h} reload={list.reload} latest={versions.data?.latest ?? null} />
      ))}
      {list.data && <AddHelper first={helpers.length === 0} reload={list.reload} />}
      {server && <ServerCard s={server} />}
    </div>
  );
}

function Stats({ w }: { w: WorkerInfo }) {
  const now = useNow(5000);
  return (
    <div className="small muted">
      {w.checksLastHour} checks in the last hour ({w.pagesLastHour} page{w.pagesLastHour === 1 ? "" : "s"}) · {w.checksToday} in 24 h · last{" "}
      {w.lastCheck ? ago(w.lastCheck, now) : "never"}
      {w.errorsLastHour > 0 && <span className="badge bad" style={{ marginLeft: 8 }}>{w.errorsLastHour} failed this hour</span>}
    </div>
  );
}

function ServerCard({ s }: { s: WorkerInfo }) {
  return (
    <div className="card stack" style={{ gap: 6 }}>
      <div className="card-head" style={{ marginBottom: 0 }}>
        <h2>Server</h2>
        <span className="badge good">Always on</span>
      </div>
      <div className="small muted">
        {s.machine} · {s.browser} · checks pages that aren&apos;t on a helper, and takes over while helpers are offline
      </div>
      <Stats w={s} />
    </div>
  );
}

/** -1, 0, 1 for "1.0.12" style versions. */
function compareVersions(a: string, b: string) {
  const pa = a.split(".").map(Number);
  const pb = b.split(".").map(Number);
  for (let i = 0; i < Math.max(pa.length, pb.length); i++) {
    const d = (pa[i] ?? 0) - (pb[i] ?? 0);
    if (d !== 0) return Math.sign(d);
  }
  return 0;
}

/** One computer: connected or not, its version, checks on or off, and the things that apply to all of it. */
function HelperCard({ h, reload, latest }: { h: WorkerInfo; reload: () => Promise<void>; latest: string | null }) {
  const act = useAction();
  const [note, setNote] = useState<string | null>(null);
  const [updateAsked, setUpdateAsked] = useState<string | null>(null);
  const id = h.id!;
  const behind = h.connected && h.version != null && latest != null && compareVersions(h.version, latest) < 0;
  // Asked to update and still on the same version: in progress.
  const updating = updateAsked !== null && updateAsked === (h.version ?? "");
  const signedOut = h.logins.filter((l) => l.status === "signedOut" || l.status === "humanCheck").map((l) => l.domain);

  const run = async (f: () => Promise<unknown>, done: string) => {
    setNote(null);
    let ok = false;
    await act.run(async () => {
      await f();
      ok = true;
    });
    if (ok) setNote(done);
    return ok;
  };
  const update = async () => {
    if (await run(() => api.post(`/api/runners/${id}/update`), "Updating: it downloads the new version and restarts (a minute or two).")) {
      setUpdateAsked(h.version ?? "");
      setTimeout(() => setUpdateAsked(null), 180000);
    }
  };
  const rename = async () => {
    const name = prompt("Name this helper", h.name);
    if (!name || name.trim() === h.name) return;
    await act.run(() => api.put(`/api/runners/${id}`, { name: name.trim() }));
    await reload();
  };
  const remove = async () => {
    if (!confirm(`Remove ${h.name}? Its setup code stops working and it's disconnected now. (To remove the program from that computer: deficitsnatcher uninstall.)`))
      return;
    await act.run(() => api.del(`/api/runners/${id}`));
    await reload();
  };
  const resetBrowser = () => {
    if (!confirm(`Reset ${h.name}'s whole Chrome? Its profile is wiped and a clean one starts: you'll be signed out of every store.`)) return;
    void run(() => api.post(`/api/runners/${id}/reset`, { domain: null }), "Resetting: its Chrome restarts with a clean profile. Sign in to the stores again.");
  };

  return (
    <div className="card stack" style={{ gap: 8 }}>
      <div className="card-head" style={{ marginBottom: 0 }}>
        <div className="row">
          <h2>{h.name}</h2>
          {h.connected ? <span className="badge good">Connected</span> : <span className="badge warn">Offline</span>}
          {h.connected && h.version && (behind ? <span className="badge warn">v{h.version} · {latest} available</span> : <span className="badge">v{h.version}</span>)}
        </div>
        <label className="row small" style={{ gap: 8 }} title="Off: this helper gets no checks or buys (it stays connected)">
          Checks
          <button
            className={`toggle small go ${h.checksEnabled ? "on" : ""}`}
            aria-label={`Checks on ${h.name}`}
            disabled={act.busy}
            onClick={() => void act.run(() => api.put(`/api/runners/${id}/checks`, { on: !h.checksEnabled, domain: null })).then(reload)}
          />
        </label>
      </div>
      <div className="small muted">
        {[h.machine, h.browser].filter(Boolean).join(" · ") || "Hasn't connected yet"}
        {h.connected && h.since && <> · since {dateTime(h.since)}</>}
        {!h.connected && h.lastSeen && <> · last seen {dateTime(h.lastSeen)}</>}
      </div>
      <Stats w={h} />
      {!h.connected && (
        <div className="notice warn small">
          Its pages are checked from the server meanwhile. On that computer: <code>deficitsnatcher status</code> (or start it from the Start
          menu). Key refused? Make a new setup code below.
        </div>
      )}
      {signedOut.length > 0 && (
        <div className="notice warn small">Signed out of {signedOut.join(", ")}: sign in from those products&apos; pages.</div>
      )}
      {note && <div className="small faint">{note}</div>}
      <ErrorNote error={act.error} />
      <div className="row">
        {h.connected && h.canUpdate && (
          <button className={`btn small ${behind ? "primary" : ""}`} disabled={act.busy || updating} onClick={update}>
            {updating ? "Updating…" : behind ? "Update now" : "Check for update"}
          </button>
        )}
        <button
          className="btn small"
          disabled={!h.connected || act.busy}
          title="Opens each store's account page in its Chrome to see if it's still signed in (it also does this every 6 hours)"
          onClick={() => void run(() => api.post(`/api/runners/${id}/logins/check`), "Checking its sign-ins in the next minute.")}
        >
          Check sign-ins
        </button>
        <button className="btn small" disabled={!h.connected || act.busy} title="Wipes its whole Chrome profile and starts a clean one" onClick={resetBrowser}>
          Reset browser
        </button>
        <button className="btn small ghost" onClick={rename} disabled={act.busy}>
          Rename
        </button>
        <button className="btn small ghost danger" onClick={remove} disabled={act.busy}>
          Remove
        </button>
      </div>
    </div>
  );
}

/** Add a helper: download it, make its setup code, paste. */
function AddHelper({ first, reload }: { first: boolean; reload: () => Promise<void> }) {
  const [open, setOpen] = useState(first);
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

  const windows = rid === "win-x64";
  const command = (c: string | null) =>
    windows
      ? `& ([scriptblock]::Create((irm ${site}/api/runner/install.ps1)))${c ? ` ${c}` : ""}`
      : `curl -fsSL ${site}/api/runner/install.sh | sh${c ? ` -s -- ${c}` : ""}`;

  if (!open)
    return (
      <div className="row">
        <button className="btn small" onClick={() => setOpen(true)}>
          + Add a helper, update or uninstall
        </button>
      </div>
    );

  return (
    <div className="card stack">
      <div className="card-head" style={{ marginBottom: 0 }}>
        <h2>{first ? "Add a PC helper" : "Add a helper"}</h2>
        {!first && (
          <button className="btn small ghost" onClick={() => setOpen(false)}>
            Close
          </button>
        )}
      </div>
      <p className="muted small" style={{ margin: 0 }}>
        For stores that block servers (Walmart, Costco, Amazon): a small program on a computer lends the bot its Chrome, on that home
        internet, with your sign-ins. It runs in the background and starts with the computer. Each computer gets its own setup code.
      </p>
      <ol className="small" style={{ margin: 0, paddingLeft: 18, lineHeight: 2 }}>
        <li>
          <div className="row" style={{ flexWrap: "nowrap", maxWidth: 420 }}>
            <input type="text" placeholder="Name, e.g. Gaming PC" value={name} onChange={(e) => setName(e.target.value)} maxLength={100} />
            <button className="btn small primary" style={{ flex: "none" }} disabled={make.busy} onClick={newCode}>
              Make a setup code
            </button>
          </div>
        </li>
        <li>
          On that computer ({label}):
          <ul style={{ margin: 0, paddingLeft: 18 }}>
            {windows && (
              <li>
                <a className="btn small" href={`${release}/download/pc-helper/DeficitSnatcher-PC-helper-Setup.exe`}>
                  Download the installer
                </a>{" "}
                and run it; paste the setup code when it asks. Or:
              </li>
            )}
            <li>
              paste this into {windows ? "PowerShell" : "Terminal"}: it downloads, installs and connects it in one go.
              <CopyLine text={command(code)} placeholder={!code} />
            </li>
          </ul>
        </li>
        <li>Then sign in to the stores from its card here (Log in).</li>
      </ol>
      <ErrorNote error={make.error} />
      {code && (
        <div className="notice warn small stack" style={{ gap: 8 }}>
          <div>The setup code (copy it now, it isn&apos;t shown again; the command above has it in):</div>
          <div className="row" style={{ flexWrap: "nowrap" }}>
            <input type="text" readOnly value={code} onFocus={(e) => e.target.select()} className="mono" />
            <button className="btn small" style={{ flex: "none" }} onClick={copy}>
              {copied ? "Copied" : "Copy"}
            </button>
          </div>
        </div>
      )}
      <details className="small">
        <summary>Update, other computers, uninstall</summary>
        <div className="stack" style={{ gap: 6, marginTop: 8 }}>
          <div>
            <b>Update</b> (keeps its setup): it updates itself; or {windows ? "run the installer again, or " : ""}run:
            <CopyLine text={command(null)} />
          </div>
          <div>
            <b>Other computers:</b>{" "}
            {downloads.map(([l, r], i) => (
              <span key={r}>
                {i > 0 && ", "}
                <a href={`${release}/download/pc-helper/DeficitSnatcher-PC-helper-${r}.zip`}>{l}</a>
              </span>
            ))}{" "}
            (zip: unzip, open <code>DeficitSnatcher.Runner</code>, paste the code) ·{" "}
            <a href={`${release}/download/pc-helper/DeficitSnatcher-PC-helper-Setup.exe`}>Windows installer</a>
          </div>
          <div>
            <b>Uninstall</b> (the program, its settings and its Chrome profile):{" "}
            {windows ? "Settings → Apps → Deficit Snatcher PC helper, or " : ""}
            <code>deficitsnatcher uninstall</code>. Then remove it here too.
          </div>
        </div>
      </details>
      <details className="small">
        <summary>Commands</summary>
        <pre className="log" style={{ marginTop: 8 }}>{`deficitsnatcher status        running? connected?
deficitsnatcher stop | start | restart
deficitsnatcher window show   show its Chrome (to sign in to a store)
deficitsnatcher window hide   minimise it again
deficitsnatcher autostart off don't start with the computer
deficitsnatcher setup <code>  connect with a new setup code
deficitsnatcher logs          what it's been doing
deficitsnatcher uninstall     remove it completely`}</pre>
        <div className="faint">In a new terminal after setup (Windows: Command Prompt or PowerShell; Mac: Terminal).</div>
        <div className="faint" style={{ marginTop: 4 }}>
          First start: Windows may say &quot;Windows protected your PC&quot; (More info → Run anyway); a Mac may refuse it (right-click → Open).
        </div>
      </details>
    </div>
  );
}

/** A command to paste, with a Copy button. */
function CopyLine({ text, placeholder }: { text: string; placeholder?: boolean }) {
  const [copied, setCopied] = useState(false);
  return (
    <div className="row" style={{ flexWrap: "nowrap", margin: "4px 0" }}>
      <input
        type="text"
        readOnly
        value={placeholder ? "Make a setup code first: the command will have it in." : text}
        onFocus={(e) => e.target.select()}
        className="mono"
        disabled={placeholder}
      />
      <button
        className="btn small"
        style={{ flex: "none" }}
        disabled={placeholder}
        onClick={async () => {
          try {
            await navigator.clipboard.writeText(text);
            setCopied(true);
          } catch {
            // Select it by hand.
          }
        }}
      >
        {copied ? "Copied" : "Copy"}
      </button>
    </div>
  );
}
