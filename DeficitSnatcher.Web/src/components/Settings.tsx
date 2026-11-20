"use client";

import { useState } from "react";
import { api, type Profile, type Settings } from "@/lib/api";
import { ErrorNote, Field, NumberInput, useAction, useData } from "./ui";

export function SettingsPage() {
  const loaded = useData(() => api.get<Settings>("/api/settings"), []);
  return (
    <div className="stack" style={{ gap: 16 }}>
      <div className="page-head">
        <h1>Settings</h1>
      </div>
      <ErrorNote error={loaded.error} />
      <AndroidApp />
      {loaded.data && <SettingsForm initial={loaded.data} />}
      <ProfileForm />
    </div>
  );
}

function SettingsForm({ initial }: { initial: Settings }) {
  const [s, setS] = useState(initial);
  const [saved, setSaved] = useState(false);
  const save = useAction();
  const test = useAction();
  const [testResult, setTestResult] = useState<string | null>(null);
  const set = (patch: Partial<Settings>) => (setS({ ...s, ...patch }), setSaved(false));

  const submit = async () => {
    const r = await save.run(() => api.put<Settings>("/api/settings", s));
    if (r) {
      setS(r);
      setSaved(true);
    }
  };

  const sendTest = async () => {
    setTestResult(null);
    // Saved settings are what's used: save first.
    const savedNow = await save.run(() => api.put<Settings>("/api/settings", s));
    if (!savedNow) return;
    const r = await test.run(() => api.post<{ sent: boolean; problems: string[] }>("/api/settings/test-notification"));
    if (r) setTestResult(r.sent ? "Sent. Check your inbox." : r.problems.join("\n"));
  };

  return (
    <>
      <div className="card stack">
        <h2>Checking</h2>
        <Field
          label="Check each page every (minutes)"
          hint="At least 1. Every 3–5 minutes is plenty for restocks; checking more often is what gets you flagged and blocked."
        >
          <NumberInput value={Math.round(s.defaultIntervalSeconds / 60)} min={1} onChange={(v) => set({ defaultIntervalSeconds: Math.max(1, v ?? 3) * 60 })} />
        </Field>
        <div className="small muted">Pause or resume scheduled checks on each product card. Auto-buy is also set per product; the phone alarm still rings for in-range matches.</div>
      </div>

      <div className="card stack">
        <div className="card-head" style={{ marginBottom: 0 }}>
          <h2>Email alerts</h2>
          <button className="btn small" disabled={test.busy || save.busy} onClick={sendTest}>
            Save and send a test
          </button>
        </div>
        {testResult && <pre className="log">{testResult}</pre>}
        <p className="muted small" style={{ margin: 0 }}>
          Emailed: in-range stock matches and checkout outcomes. Set up for IONOS mail by default:
          the mailbox&apos;s full address as the username and its password.
        </p>
        <div className="fields">
          <Field label="Send alerts to" hint="Your email; several: comma-separated. Empty: no emails.">
            <input type="text" value={s.emailTo} placeholder="you@example.com" onChange={(e) => set({ emailTo: e.target.value })} />
          </Field>
          <Field label="SMTP username" hint="IONOS: the mailbox's full email address.">
            <input type="text" autoComplete="off" value={s.smtpUser} placeholder="alerts@yourdomain.com" onChange={(e) => set({ smtpUser: e.target.value })} />
          </Field>
          <Field label="SMTP password">
            <input type="password" autoComplete="new-password" value={s.smtpPassword} onChange={(e) => set({ smtpPassword: e.target.value })} />
          </Field>
          <Field label="SMTP server" hint="smtp.ionos.com (North America), smtp.ionos.co.uk, smtp.ionos.de…">
            <input type="text" value={s.smtpHost} onChange={(e) => set({ smtpHost: e.target.value })} />
          </Field>
          <Field label="Port" hint="587 (STARTTLS) or 465 (TLS).">
            <NumberInput value={s.smtpPort} min={1} onChange={(v) => set({ smtpPort: v ?? 587 })} />
          </Field>
          <Field label="Send from" hint="Empty: the username.">
            <input type="text" value={s.emailFrom} onChange={(e) => set({ emailFrom: e.target.value })} />
          </Field>
        </div>
      </div>

      <ErrorNote error={save.error || test.error} />
      <div className="row">
        <button className="btn primary" disabled={save.busy} onClick={submit}>
          {save.busy ? "Saving…" : "Save settings"}
        </button>
        {saved && <span className="badge good">Saved</span>}
      </div>
    </>
  );
}

function ProfileForm() {
  const loaded = useData(() => api.get<Profile>("/api/settings/profile"), []);
  if (!loaded.data) return <ErrorNote error={loaded.error} />;
  return <ProfileFields initial={loaded.data} />;
}

function ProfileFields({ initial }: { initial: Profile }) {
  const [p, setP] = useState<Profile>({ ...initial, cardNumber: "", cardCvv: "" });
  const [saved, setSaved] = useState<Profile>(initial);
  const [ok, setOk] = useState(false);
  const save = useAction();
  const set = (patch: Partial<Profile>) => (setP({ ...p, ...patch }), setOk(false));

  const submit = async (clearCard = false) => {
    if (clearCard && !confirm("Remove the saved card number and CVV?")) return;
    const r = await save.run(() => api.put<Profile>("/api/settings/profile", { ...p, clearCard }));
    if (r) {
      setSaved(r);
      setP({ ...r, cardNumber: "", cardCvv: "" });
      setOk(true);
    }
  };

  const text = (key: keyof Profile, label: string, type = "text", hint?: string) => (
    <Field label={label} hint={hint}>
      <input type={type} value={String(p[key] ?? "")} onChange={(e) => set({ [key]: e.target.value } as Partial<Profile>)} />
    </Field>
  );

  return (
    <div className="card stack">
      <h2>Checkout profile</h2>
      <p className="muted small">
        Filled into checkout steps (<code>{"{{email}}"}</code>, <code>{"{{card.number}}"}</code>, …). Kept encrypted on the server; the card
        number and CVV are never sent back to this page. Leave them empty to keep the saved ones. Stores where you&apos;re signed in (Cookies on
        the site) can use your saved address and card instead.
      </p>
      <div className="fields">
        {text("email", "Email", "email")}
        {text("phone", "Phone")}
        {text("firstName", "First name")}
        {text("lastName", "Last name")}
        {text("address1", "Address")}
        {text("address2", "Apartment, suite")}
        {text("city", "City")}
        {text("province", "Province / state", "text", "As the store's list shows it, e.g. Ontario or ON")}
        {text("postalCode", "Postal code")}
        {text("country", "Country", "text", "E.g. Canada or CA")}
      </div>
      <h3>Card</h3>
      <div className="fields">
        {text("cardName", "Name on card")}
        <Field label="Card number" hint={saved.cardNumber ? `Saved: •••• ${saved.cardNumber}` : "None saved"}>
          <input type="text" inputMode="numeric" autoComplete="off" value={p.cardNumber} placeholder={saved.cardNumber ? `•••• ${saved.cardNumber}` : ""} onChange={(e) => set({ cardNumber: e.target.value })} />
        </Field>
        {text("cardExpiryMonth", "Expiry month", "text", "MM")}
        {text("cardExpiryYear", "Expiry year", "text", "YYYY")}
        <Field label="CVV" hint={saved.hasCvv ? "Saved" : "None saved"}>
          <input type="password" inputMode="numeric" autoComplete="off" value={p.cardCvv} placeholder={saved.hasCvv ? "•••" : ""} onChange={(e) => set({ cardCvv: e.target.value })} />
        </Field>
      </div>
      <ErrorNote error={save.error} />
      <div className="row">
        <button className="btn primary" disabled={save.busy} onClick={() => submit()}>
          {save.busy ? "Saving…" : "Save profile"}
        </button>
        {ok && <span className="badge good">Saved</span>}
        <div style={{ flex: 1 }} />
        {(saved.cardNumber || saved.hasCvv) && (
          <button className="btn danger small" disabled={save.busy} onClick={() => submit(true)}>
            Remove card
          </button>
        )}
      </div>
    </div>
  );
}

/** The Android app: where to get it, and a test ring. */
function AndroidApp() {
  const ring = useAction();
  const [rung, setRung] = useState(false);
  const test = async () => {
    setRung(false);
    const r = await ring.run(() => api.post("/api/alarms/test"));
    if (r !== undefined) setRung(true);
  };
  return (
    <div className="card stack">
      <div className="card-head" style={{ marginBottom: 0 }}>
        <h2>Android app</h2>
        <button className="btn small" disabled={ring.busy} onClick={test}>
          Ring my phone
        </button>
      </div>
      <p className="muted small" style={{ margin: 0 }}>
        It stays connected to this site and rings loudly when something is in stock in your range; turning the alarm off opens the
        store&apos;s page so you can buy it. It updates itself.{" "}
        <a href="https://github.com/YehorSkudilov/DeficitSnatcher/releases/download/android/DeficitSnatcher.apk">Download the APK</a>, open
        it on the phone (allow installing from your browser), then sign in with this site&apos;s address, username and password.
      </p>
      {rung && <div className="notice good small">Rung: the app should be ringing now (within a few seconds).</div>}
      <ErrorNote error={ring.error} />
    </div>
  );
}
