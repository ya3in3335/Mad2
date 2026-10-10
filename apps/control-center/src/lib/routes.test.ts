import { describe, expect, it } from "vitest";
import { findApiRule, requiresStepUp } from "./routes";

const ID = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";
const P = "/api/v1/platform";

describe("operations allowlist", () => {
  it("allows the fleet, incident, telegram and owner dashboard reads", () => {
    for (const path of [
      `${P}/dashboard/admin`,
      `${P}/fleet/devices`,
      `${P}/fleet/devices/${ID}`,
      `${P}/incidents`,
      `${P}/incidents/groups`,
      `${P}/incidents/${ID}`,
      `${P}/licenses/activation-failures`,
      `${P}/telegram/status`,
      `${P}/telegram/deliveries`,
    ]) {
      expect(findApiRule("GET", path), path).not.toBeNull();
    }
  });

  it("requires step-up for destructive fleet actions and the Telegram test only", () => {
    for (const a of ["deactivate", "replace", "reassign"]) expect(requiresStepUp("POST", `${P}/fleet/devices/${ID}/${a}`)).toBe(true);
    expect(requiresStepUp("POST", `${P}/telegram/test`)).toBe(true);
    expect(findApiRule("POST", `${P}/fleet/devices/${ID}/diagnostic-requests`)?.stepUp).toBe(false);
    expect(findApiRule("POST", `${P}/incidents/${ID}/status`)?.stepUp).toBe(false);
    expect(findApiRule("POST", `${P}/licenses/demo`)?.stepUp).toBe(false);
    expect(findApiRule("POST", `${P}/licenses/${ID}/seats`)?.stepUp).toBe(false);
  });

  it("rejects unknown fleet actions", () => {
    expect(findApiRule("POST", `${P}/fleet/devices/${ID}/wipe`)).toBeNull();
    expect(findApiRule("GET", `${P}/fleet/devices/not-a-guid`)).toBeNull();
  });
});
