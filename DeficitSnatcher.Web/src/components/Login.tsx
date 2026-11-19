"use client";

import { useState } from "react";
import { api, setToken } from "@/lib/api";
import { ErrorNote, Field, useAction } from "./ui";

export function Login({ onSignedIn }: { onSignedIn: () => void }) {
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const { busy, error, run } = useAction();

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    const result = await run(() => api.post<{ token: string }>("/api/auth/login", { username, password }));
    if (result) {
      setToken(result.token);
      onSignedIn();
    }
  };

  return (
    <div className="login">
      <form className="card stack" onSubmit={submit}>
        <div className="brand" style={{ padding: 0 }}>
          <i className="brand-dot armed" />
          <span>Deficit Snatcher</span>
        </div>
        <Field label="Username">
          <input type="text" autoComplete="username" value={username} onChange={(e) => setUsername(e.target.value)} autoFocus />
        </Field>
        <Field label="Password">
          <input type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} />
        </Field>
        <ErrorNote error={error} />
        <button className="btn primary" disabled={busy || !username || !password}>
          {busy ? "Signing in…" : "Sign in"}
        </button>
      </form>
    </div>
  );
}
