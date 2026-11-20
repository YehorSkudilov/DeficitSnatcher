"use client";

import { useEffect, useState } from "react";
import { api } from "@/lib/api";
import { ErrorNote, useAction, useData, useNow } from "./ui";

type PendingCaptcha = { id: string; siteId: number; siteName: string; url: string; askedAt: string; until: string };

/** Captchas a check is waiting on (the server's Captchas): the puzzle and a box for the answer, on every screen. */
export function CaptchaPrompt() {
  const { data } = useData(() => api.get<PendingCaptcha[]>("/api/captchas"), [], 4000);
  if (!data || data.length === 0) return null;
  return (
    <div className="stack" style={{ marginBottom: 16 }}>
      {data.map((c) => (
        <CaptchaCard key={c.id} captcha={c} />
      ))}
    </div>
  );
}

function CaptchaCard({ captcha }: { captcha: PendingCaptcha }) {
  const [image, setImage] = useState<string | null>(null);
  const [answer, setAnswer] = useState("");
  const [sent, setSent] = useState(false);
  const send = useAction();
  const now = useNow(1000);
  const left = Math.max(0, Math.round((new Date(captcha.until).getTime() - now) / 1000));

  useEffect(() => {
    let url: string | null = null;
    api
      .get<Blob>(`/api/captchas/${captcha.id}/image`)
      .then((blob) => {
        url = URL.createObjectURL(blob);
        setImage(url);
      })
      .catch(() => {});
    return () => {
      if (url) URL.revokeObjectURL(url);
    };
  }, [captcha.id]);

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    const ok = await send.run(async () => {
      await api.post(`/api/captchas/${captcha.id}`, { answer });
      return true;
    });
    if (ok) setSent(true);
  };

  return (
    <form className="card stack" style={{ borderColor: "var(--warning)" }} onSubmit={submit}>
      <div className="card-head" style={{ marginBottom: 0 }}>
        <h2>{captcha.siteName} is asking for a captcha</h2>
        <span className="faint small">{left > 0 ? `${Math.floor(left / 60)}:${String(left % 60).padStart(2, "0")} left` : "expired"}</span>
      </div>
      {sent ? (
        <div className="muted">Sent. The check goes on with your answer.</div>
      ) : (
        <>
          <div className="muted small">Type the characters in the picture. The check is waiting and goes on as soon as you send it.</div>
          {image ? (
            // eslint-disable-next-line @next/next/no-img-element -- a one-off blob from the API, nothing to optimise
            <img src={image} alt="The store's captcha" style={{ maxWidth: "100%", width: 300, background: "#fff", borderRadius: 6 }} />
          ) : (
            <span className="faint">Loading the picture…</span>
          )}
          <div className="row" style={{ gap: 8 }}>
            <input
              value={answer}
              onChange={(e) => setAnswer(e.target.value)}
              placeholder="Characters"
              autoCapitalize="characters"
              autoComplete="off"
              autoFocus
              style={{ maxWidth: 220 }}
            />
            <button className="btn primary" disabled={send.busy || answer.trim().length === 0 || left === 0}>
              Send
            </button>
          </div>
          <ErrorNote error={send.error} />
        </>
      )}
    </form>
  );
}
