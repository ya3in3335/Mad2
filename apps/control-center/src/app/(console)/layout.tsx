import type { ReactNode } from "react";
import { Sidebar } from "@/components/sidebar";
import { StepUpProvider } from "@/components/step-up";
import { visibleNav } from "@/lib/nav";
import { getTheme } from "@/lib/server/i18n";
import { getCurrentUser } from "@/lib/server/user";

export const dynamic = "force-dynamic";

export default async function ConsoleLayout({ children }: { children: ReactNode }) {
  const user = await getCurrentUser();
  const theme = await getTheme();
  return (
    <StepUpProvider>
      <div className="lg:flex">
        <Sidebar nav={visibleNav(user.permissions)} user={{ fullName: user.fullName, email: user.email, mfaEnabled: user.mfaEnabled }} theme={theme} />
        <main id="main" className="min-w-0 flex-1 px-4 py-6 sm:px-6 lg:px-10 lg:py-8">
          <div className="mx-auto max-w-7xl">{children}</div>
        </main>
      </div>
    </StepUpProvider>
  );
}
