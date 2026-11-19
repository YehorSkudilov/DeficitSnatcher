import type { Metadata, Viewport } from "next";
import "./globals.css";

export const metadata: Metadata = {
  title: "Deficit Snatcher",
  description: "Watches stores for a PS5 Pro and buys it in your price range.",
  // Private: nothing here for search engines.
  robots: { index: false, follow: false },
};

export const viewport: Viewport = {
  themeColor: "#0a0c10",
};

export default function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="en">
      <body>{children}</body>
    </html>
  );
}
