"use client";

import { useState } from "react";
import { api } from "@/lib/api";
import { ErrorNote, useAction, useData, useNow } from "./ui";

type PendingCode = { runnerId: number; helper: string; domain: string; since: string; until: string };

/** On every page: a helper signing in to a store is waiting for the code the store sent you. Type it here, it carries on. */
export function PendingCodes() {
  const { data, reload } = useData(() => api.get<PendingCode[]>("/api/runners/codes"), [], 4000);
  if (!data || data.length === 0) return null;
  return (
    <div className="stack" style={{ gap: 8, marginBottom: 16 }}>
      {data.map((p) => (
        <CodeBox key={`${p.runnerId}/${p.domain}`} p={p} onSent={reload} />
      ))}
    </div>
  );
}

function CodeBox({ p, onSent }: { p: PendingCode; onSent: () => Promise<void> }) {
  const act = useAction();
  const [code, setCode] = useState("");
  const now = useNow(1000);
  const left = Math.max(0, Math.round((new Date(p.until).getTime() - now) / 1000));
  const send = async () => {
    let ok = false;
    await act.run(async () => {
      await api.post(`/api/runners/${p.runnerId}/code`, { domain: p.domain, code });
      ok = true;
    });
    if (ok) {
      setCode("");
      await onSent();
    }
  };
  return (
    <div className="notice warn stack" style={{ gap: 6 }}>
      <div>
        <b>{p.domain}</b> sent you a verification code ({p.helper} is signing in). Type it here and it carries on.{" "}
        <span className="faint small">
          {Math.floor(left / 60)}:{String(left % 60).padStart(2, "0")} left
        </span>
      </div>
      <form
        className="row"
        style={{ flexWrap: "nowrap", maxWidth: 360 }}
        onSubmit={(e) => {
          e.preventDefault();
          void send();
        }}
      >
        <input
          type="text"
          inputMode="numeric"
          autoComplete="one-time-code"
          placeholder="Code"
          value={code}
          onChange={(e) => setCode(e.target.value)}
          className="mono"
          autoFocus
        />
        <button className="btn small primary" style={{ flex: "none" }} disabled={act.busy || !code.trim()}>
          Send
        </button>
      </form>
      <ErrorNote error={act.error} />
    </div>
  );
}
