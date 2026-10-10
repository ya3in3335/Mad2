import type { Metadata, Viewport } from "next";
import type { ReactNode } from "react";
import { I18nProvider } from "@/components/i18n-client";
import { isRtl } from "@/lib/i18n-core";
import { DICTIONARIES } from "@/lib/i18n-dictionaries";
import { getLocale, getTheme } from "@/lib/server/i18n";
import "./globals.css";

export const metadata: Metadata = {
  title: { default: "VELTRIX Control Center", template: "%s · VELTRIX Control Center" },
  description: "VELTRIX vendor administration portal",
  robots: { index: false, follow: false },
  icons: { icon: "/icon.svg" },
};

export const viewport: Viewport = {
  themeColor: "#07111F",
  width: "device-width",
  initialScale: 1,
};

export default async function RootLayout({ children }: { children: ReactNode }) {
  const locale = await getLocale();
  const theme = await getTheme();
  return (
    <html lang={locale} dir={isRtl(locale) ? "rtl" : "ltr"} className={theme === "dark" ? "dark" : undefined} suppressHydrationWarning>
      <body className="min-h-screen antialiased">
        <I18nProvider locale={locale} dict={DICTIONARIES[locale]} fallback={DICTIONARIES.en}>
          {children}
        </I18nProvider>
      </body>
    </html>
  );
}
