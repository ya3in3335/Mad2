import { describe, expect, it } from "vitest";
import { isActivePath, NAV, visibleNav } from "./nav";
import { humanize, statusKey, toneFor } from "./status";
import { latestSubscriptionId } from "./tenant-detail";
import type { BillingInvoice } from "./types";

describe("navigation", () => {
  it("matches the dashboard only exactly and sections by prefix", () => {
    expect(isActivePath("/", "/")).toBe(true);
    expect(isActivePath("/", "/tenants")).toBe(false);
    expect(isActivePath("/tenants", "/tenants/123")).toBe(true);
    expect(isActivePath("/plans", "/plansx")).toBe(false);
  });

  it("filters entries by permission and drops empty sections", () => {
    const nav = visibleNav(["platform.audit"]);
    expect(nav.flatMap((s) => s.items.map((i) => i.href))).toEqual(["/audit", "/security"]);
    expect(visibleNav(["*"]).flatMap((s) => s.items).length).toBe(NAV.flatMap((s) => s.items).length);
    expect(visibleNav([])).toEqual([]);
  });
});

describe("status helpers", () => {
  it("maps API enum values to tones", () => {
    expect(toneFor("Active")).toBe("success");
    expect(toneFor("PastDue")).toBe("warning");
    expect(toneFor("Revoked")).toBe("danger");
    expect(toneFor("something-new")).toBe("neutral");
    expect(toneFor(null)).toBe("neutral");
  });

  it("humanizes identifiers", () => {
    expect(humanize("PastDue")).toBe("Past due");
    expect(humanize("auth.login_failed")).toBe("Auth login failed");
    expect(statusKey("Past_Due")).toBe("status.pastdue");
  });
});

describe("latestSubscriptionId", () => {
  const inv = (subscriptionId: string, issuedAt: string) => ({ subscriptionId, issuedAt }) as BillingInvoice;
  it("returns the subscription of the newest invoice", () => {
    expect(latestSubscriptionId([inv("a", "2026-01-01T00:00:00Z"), inv("b", "2026-03-01T00:00:00Z"), inv("c", "2026-02-01T00:00:00Z")])).toBe("b");
    expect(latestSubscriptionId([])).toBeNull();
  });
});
