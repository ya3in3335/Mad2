import { describe, expect, it } from "vitest";
import { buildRequest, initialValues, isFormKind, resolvePath } from "./forms";

describe("buildRequest", () => {
  it("builds a tenant creation body and nulls empty optionals", () => {
    const r = buildRequest("tenant.create", { name: "  Acme SARL ", email: "", phone: "0550 00 00 00" });
    expect(r).toEqual({ ok: true, body: { name: "Acme SARL", email: null, phone: "0550 00 00 00", contactName: null, wilaya: null, notes: null } });
  });

  it("reports required and format errors as i18n keys", () => {
    const r = buildRequest("tenant.create", { name: "", email: "not-an-email" });
    expect(r.ok).toBe(false);
    if (!r.ok) expect(r.errors).toEqual({ name: "form.required", email: "form.invalidEmail" });
  });

  it("validates GUIDs, integer ranges and date conversion for licenses", () => {
    const ok = buildRequest("license.create", {
      tenantId: "3F2504E0-4F89-11D3-9A0C-0305E82C3301",
      type: "Annual",
      planCode: "professional",
      maxDevices: "3",
      startsAt: "2026-01-01",
      expiresAt: "2026-12-31",
      offlineGraceDays: "",
      entitlements: ["pos", "inventory"],
    });
    expect(ok.ok).toBe(true);
    if (ok.ok) {
      expect(ok.body).toMatchObject({
        tenantId: "3f2504e0-4f89-11d3-9a0c-0305e82c3301",
        maxDevices: 3,
        startsAt: "2026-01-01T00:00:00Z",
        expiresAt: "2026-12-31T23:59:59Z",
        offlineGraceDays: null,
        entitlements: ["pos", "inventory"],
      });
    }
    const bad = buildRequest("license.create", { tenantId: "x", type: "Annual", planCode: "pro", maxDevices: "0" });
    expect(bad.ok).toBe(false);
    if (!bad.ok) expect(bad.errors).toMatchObject({ tenantId: "form.invalidId", maxDevices: "form.outOfRange" });
  });

  it("parses decimals with a comma and upper-cases the currency for plans", () => {
    const r = buildRequest("plan.upsert", {
      code: "pro",
      name: "Pro",
      monthlyPrice: "4 500,50",
      annualPrice: "45000",
      currency: "dzd",
      maxDevices: "3",
      maxUsers: "10",
      maxBranches: "2",
      trialDays: "14",
      offlineGraceDays: "7",
      features: [],
      isActive: true,
      isPublic: false,
    });
    expect(r.ok).toBe(true);
    if (r.ok) expect(r.body).toMatchObject({ monthlyPrice: 4500.5, annualPrice: 45000, currency: "DZD", isActive: true, isPublic: false });
  });

  it("requires HTTPS download URLs and 64-hex SHA-256 for releases", () => {
    const r = buildRequest("release.upsert", {
      platform: "windows",
      version: "1.2.0",
      channel: "stable",
      minSupportedVersion: "1.0.0",
      downloadUrl: "http://example.com/x.msi",
      sha256: "abc",
    });
    expect(r.ok).toBe(false);
    if (!r.ok) expect(r.errors).toEqual({ downloadUrl: "form.invalidUrl", sha256: "form.invalidHash" });
  });

  it("enforces a minimum length for new administrator passwords", () => {
    const r = buildRequest("admin.create", { email: "a@b.dz", fullName: "A", password: "short" });
    expect(r.ok).toBe(false);
    if (!r.ok) expect(r.errors).toEqual({ password: "form.passwordShort" });
  });

  it("converts datetime-local values to UTC ISO strings", () => {
    const r = buildRequest("announcement.upsert", { title: "T", body: "B", severity: "Info", startsAt: "2026-05-01T10:30", published: true });
    expect(r.ok).toBe(true);
    if (r.ok) expect(r.body).toMatchObject({ startsAt: "2026-05-01T10:30:00Z", endsAt: null, tenantId: null, published: true });
  });

  it("rejects invalid setting keys", () => {
    const r = buildRequest("setting.set", { key: "bad key/../x", value: "v" });
    expect(r.ok).toBe(false);
  });
});

describe("form helpers", () => {
  it("initialValues falls back per field type", () => {
    expect(
      initialValues([
        { name: "a", label: "A", type: "text" },
        { name: "b", label: "B", type: "checkbox" },
        { name: "c", label: "C", type: "checkboxes" },
        { name: "d", label: "D", type: "number", defaultValue: "5" },
      ]),
    ).toEqual({ a: "", b: false, c: [], d: "5" });
  });

  it("resolvePath fills and encodes placeholders", () => {
    expect(resolvePath("/api/v1/platform/settings/{key}", { key: " billing.terms " })).toBe("/api/v1/platform/settings/billing.terms");
    expect(resolvePath("/x/{key}", { key: "a/b" })).toBe("/x/a%2Fb");
  });

  it("isFormKind only accepts known builders", () => {
    expect(isFormKind("plan.upsert")).toBe(true);
    expect(isFormKind("__proto__")).toBe(false);
    expect(isFormKind("nope")).toBe(false);
  });
});
