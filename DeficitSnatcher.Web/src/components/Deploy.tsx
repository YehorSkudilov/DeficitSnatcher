"use client";

import { useState } from "react";
import { api, type DeployJob, type DeployRun, type DeployStatus } from "@/lib/api";
import { ago, dateTime } from "@/lib/format";
import { ErrorNote, useAction, useData, useNow } from "./ui";

const short = (sha: string | null | undefined) => (sha ? sha.slice(0, 7) : "—");

function RunBadge({ run }: { run: Pick<DeployRun, "status" | "conclusion"> }) {
  if (run.status !== "completed")
    return (
      <span className="badge info">
        <span className="spin" /> {run.status.replace("_", " ")}
      </span>
    );
  const cls = run.conclusion === "success" ? "good" : run.conclusion === "cancelled" || run.conclusion === "skipped" ? "" : "bad";
  return <span className={`badge ${cls}`}>{run.conclusion ?? "done"}</span>;
}

/** Deploys the site (GitHub Actions: Deploy.yml) and shows how it's going: #deploy. */
export function Deploy() {
  const status = useData(() => api.get<DeployStatus>("/api/deploy"), [], 5000);
  const [openRun, setOpenRun] = useState<number | null>(null);
  const action = useAction();
  const now = useNow();
  const d = status.data;

  const active = d?.runs.find((r) => r.status !== "completed");
  const shownRun = openRun ?? active?.id ?? null;
  const pending = d?.commits.filter((c) => c.pending) ?? [];

  const deploy = async () => {
    const what = pending.length ? `${pending.length} new commit${pending.length === 1 ? "" : "s"}` : "the same version again";
    if (!confirm(`Deploy ${d?.branch} (${what})? The site restarts for a minute; a checkout running at that moment is cut off.`)) return;
    const ok = await action.run(async () => (await api.post("/api/deploy"), true));
    if (ok) setTimeout(() => void status.reload(), 2500);
  };

  const cancel = async (id: number) => {
    if (!confirm("Cancel this deploy?")) return;
    await action.run(() => api.post(`/api/deploy/runs/${id}/cancel`));
    void status.reload();
  };

  return (
    <div className="stack" style={{ gap: 16 }}>
      <div className="page-head">
        <h1>Deploy</h1>
        {d?.configured && (
          <button className="btn primary" disabled={action.busy || !!active} onClick={deploy}>
            {active ? "Deploying…" : pending.length ? `Deploy ${pending.length} new commit${pending.length === 1 ? "" : "s"}` : "Deploy again"}
          </button>
        )}
      </div>
      <ErrorNote error={action.error} />
      {status.error && (
        <div className="notice warn">
          Can&apos;t reach the server: {status.error}. During a deploy it restarts; this page picks it up again by itself.
        </div>
      )}

      {d && (
        <div className="card">
          <div className="grid stats">
            <div className="stat">
              <div className="label">Running version</div>
              <div className="value mono" style={{ fontSize: 22 }}>
                {short(d.serverSha)}
              </div>
              <div className="faint small">{d.serverSha ? "" : "built locally, not by the Deploy workflow"}</div>
            </div>
            <div className="stat">
              <div className="label">Built</div>
              <div className="value" style={{ fontSize: 18 }}>
                {d.builtAt ? ago(d.builtAt, now) : "—"}
              </div>
              <div className="faint small">{dateTime(d.builtAt)}</div>
            </div>
            <div className="stat">
              <div className="label">Server up since</div>
              <div className="value" style={{ fontSize: 18 }}>
                {ago(d.startedAt, now).replace(" ago", "")}
              </div>
              <div className="faint small">{dateTime(d.startedAt)}</div>
            </div>
            <div className="stat">
              <div className="label">Waiting to deploy</div>
              <div className="value" style={{ color: pending.length ? "var(--warning)" : undefined }}>
                {d.configured ? pending.length : "—"}
              </div>
              <div className="faint small">
                commits on {d.branch}
              </div>
            </div>
          </div>
        </div>
      )}

      {d && !d.configured && (
        <div className="card stack">
          <h2>Deploying from here is off</h2>
          <p className="muted">
            Add a <b>DEPLOY_GITHUB_TOKEN</b> secret to the repository: a fine-grained GitHub token for this repository with{" "}
            <b>Actions: read and write</b> and <b>Contents: read</b>. Then deploy once from GitHub (Actions → Deploy → Run workflow); after
            that this page runs it.
          </p>
        </div>
      )}
      {d?.error && <div className="notice bad">GitHub: {d.error}</div>}

      {d?.configured && (
        <div className="card">
          <div className="card-head">
            <h2>Deploys</h2>
            <span className="faint small">
              {d.repository} · {d.workflow}
            </span>
          </div>
          {d.runs.length === 0 && <p className="muted">No deploys yet.</p>}
          <div className="stack" style={{ gap: 8 }}>
            {d.runs.map((r) => (
              <div key={r.id}>
                <div className="row clickable" style={{ cursor: "pointer", justifyContent: "space-between" }} onClick={() => setOpenRun(shownRun === r.id ? -1 : r.id)}>
                  <div className="row" style={{ gap: 8 }}>
                    <RunBadge run={r} />
                    <b>#{r.runNumber}</b>
                    <span className="mono faint">{short(r.headSha)}</span>
                    {r.headSha === d.serverSha && <span className="badge good">running now</span>}
                    <span className="muted small ellipsis" style={{ maxWidth: 360 }}>
                      {r.title}
                    </span>
                  </div>
                  <div className="row small faint" style={{ gap: 10 }}>
                    <span>{r.actor}</span>
                    <span title={dateTime(r.createdAt)}>{ago(r.createdAt, now)}</span>
                    <span>{duration(r, now)}</span>
                    {r.status !== "completed" && (
                      <button
                        className="btn small danger"
                        onClick={(e) => {
                          e.stopPropagation();
                          void cancel(r.id);
                        }}
                      >
                        Cancel
                      </button>
                    )}
                    <a href={r.url} target="_blank" rel="noreferrer noopener" onClick={(e) => e.stopPropagation()}>
                      Logs ↗
                    </a>
                  </div>
                </div>
                {shownRun === r.id && <RunSteps id={r.id} live={r.status !== "completed"} />}
              </div>
            ))}
          </div>
        </div>
      )}

      {d?.configured && d.commits.length > 0 && (
        <div className="card">
          <h2 style={{ marginBottom: 10 }}>Commits on {d.branch}</h2>
          <div className="events">
            {d.commits.map((c) => (
              <div className="event" key={c.sha} style={{ gridTemplateColumns: "auto 1fr auto" }}>
                <a className="mono small" href={c.url} target="_blank" rel="noreferrer noopener">
                  {short(c.sha)}
                </a>
                <span className="ellipsis">
                  {c.deployed && <span className="badge good">deployed</span>} {c.pending && <span className="badge warn">not deployed</span>}{" "}
                  {c.message.split("\n")[0]}
                </span>
                <span className="faint small">
                  {c.author} · {ago(c.date, now)}
                </span>
              </div>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}

function duration(r: DeployRun, now: number) {
  const start = new Date(r.runStartedAt ?? r.createdAt).getTime();
  const end = r.status === "completed" ? new Date(r.updatedAt).getTime() : now;
  const s = Math.max(0, Math.round((end - start) / 1000));
  return `${Math.floor(s / 60)}m ${s % 60}s`;
}

function RunSteps({ id, live }: { id: number; live: boolean }) {
  const { data, error } = useData(() => api.get<DeployJob[]>(`/api/deploy/runs/${id}`), [id], live ? 3000 : undefined);
  if (!data) return <ErrorNote error={error} />;
  return (
    <div className="stack" style={{ gap: 6, margin: "10px 0 6px 12px", paddingLeft: 12, borderLeft: "2px solid var(--stroke)" }}>
      {data.map((j) => (
        <div key={j.id} className="stack" style={{ gap: 4 }}>
          {j.steps.map((s) => (
            <div key={s.number} className="row small" style={{ gap: 8 }}>
              <RunBadge run={s} />
              <span className={s.status === "completed" ? "" : "muted"}>{s.name}</span>
              {s.startedAt && s.completedAt && (
                <span className="faint">{Math.round((new Date(s.completedAt).getTime() - new Date(s.startedAt).getTime()) / 1000)}s</span>
              )}
            </div>
          ))}
          <a className="small" href={j.url} target="_blank" rel="noreferrer noopener">
            Full log of {j.name} on GitHub ↗
          </a>
        </div>
      ))}
    </div>
  );
}
