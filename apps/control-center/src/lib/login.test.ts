import { describe, expect, it } from "vitest";
import { loginErrorKey, parseLoginResponse, validateLoginInput } from "./login";

describe("validateLoginInput", () => {
  it("requires email and password", () => {
    expect(validateLoginInput({ email: "", password: "x", mfaCode: null })).toMatchObject({ ok: false, error: "err.required" });
    expect(validateLoginInput({ email: "nope", password: "x", mfaCode: null })).toMatchObject({ ok: false, error: "err.invalidEmail" });
  });

  it("accepts the password step without an MFA code", () => {
    expect(validateLoginInput({ email: " admin@veltrix.dz ", password: "pw", mfaCode: null })).toEqual({ ok: true, email: "admin@veltrix.dz", password: "pw" });
  });

  it("validates the MFA code format on the second step", () => {
    expect(validateLoginInput({ email: "a@b.dz", password: "pw", mfaCode: "12a" })).toMatchObject({ ok: false, mfaStep: true, error: "login.mfaFormat" });
    expect(validateLoginInput({ email: "a@b.dz", password: "pw", mfaCode: "123 456" })).toEqual({ ok: true, email: "a@b.dz", password: "pw", mfaCode: "123456" });
  });
});

describe("parseLoginResponse", () => {
  it("detects the MFA challenge", () => {
    expect(parseLoginResponse({ mfaRequired: true })).toEqual({ kind: "mfa" });
  });

  it("accepts a token response", () => {
    const body = { accessToken: "a", refreshToken: "r", accessTokenExpiresAt: "2026-01-01T00:00:00Z", refreshTokenExpiresAt: "2026-02-01T00:00:00Z", user: { isPlatformAdmin: true } };
    expect(parseLoginResponse(body).kind).toBe("tokens");
  });

  it("rejects anything else", () => {
    expect(parseLoginResponse(null)).toEqual({ kind: "invalid" });
    expect(parseLoginResponse({ accessToken: 1 })).toEqual({ kind: "invalid" });
  });
});

it("maps API error codes to dictionary keys", () => {
  expect(loginErrorKey("auth.invalid_credentials", "x")).toBe("auth.invalid");
  expect(loginErrorKey("auth.invalid_mfa", "x")).toBe("login.mfaInvalid");
  expect(loginErrorKey("something.else", "Raw message")).toBe("Raw message");
});
