import "server-only";
import { redirect } from "next/navigation";
import { cache } from "react";
import type { UserProfile } from "../types";
import { apiGet } from "./api";

/** Current administrator profile (one API call per request). Non platform admins are signed out. */
export const getCurrentUser = cache(async (): Promise<UserProfile> => {
  const res = await apiGet<UserProfile>("/api/v1/me");
  if (!res.ok) {
    if (res.error.status === 401 || res.error.status === 403) redirect("/api/auth/signout?reason=session");
    throw new Error(res.error.message);
  }
  if (!res.data.isPlatformAdmin) redirect("/api/auth/signout?reason=forbidden");
  return res.data;
});
