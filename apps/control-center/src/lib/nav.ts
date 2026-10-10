export interface NavItem {
  href: string;
  key: string;
  icon: string;
  /** Any of these permissions grants the entry (empty = always shown). */
  permissions: readonly string[];
}

export interface NavSection {
  key: string;
  items: readonly NavItem[];
}

/** Permission codes mirror Veltrix.SharedKernel.Permissions (platform.*). */
export const NAV: readonly NavSection[] = [
  {
    key: "nav.section.overview",
    items: [{ href: "/", key: "nav.dashboard", icon: "M3 13h8V3H3zm10 8h8V11h-8zM3 21h8v-6H3zm10-18v6h8V3z", permissions: ["platform.tenants"] }],
  },
  {
    key: "nav.section.customers",
    items: [
      { href: "/tenants", key: "nav.tenants", icon: "M4 21V7l8-4 8 4v14M9 21v-6h6v6M8 10h.01M12 10h.01M16 10h.01", permissions: ["platform.tenants"] },
      { href: "/licenses", key: "nav.licensing", icon: "M15 7a4 4 0 1 1-3.9 4.9L3 20v-3l2-2v-2h2l1.1-1.1A4 4 0 0 1 15 7z", permissions: ["platform.licenses"] },
      { href: "/devices", key: "nav.devices", icon: "M4 5h16v10H4zM2 19h20M9 15v4M15 15v4", permissions: ["platform.licenses"] },
      { href: "/subscriptions", key: "nav.subscriptions", icon: "M3 7h18v12H3zM3 11h18M7 15h4", permissions: ["platform.subscriptions"] },
      { href: "/plans", key: "nav.plans", icon: "M4 4h16v4H4zM4 10h16v4H4zM4 16h16v4H4z", permissions: ["platform.subscriptions"] },
      { href: "/invoices", key: "nav.invoices", icon: "M6 2h9l5 5v15H6zM14 2v6h6M9 13h6M9 17h6", permissions: ["platform.subscriptions"] },
      { href: "/support", key: "nav.support", icon: "M21 12a8 8 0 0 1-11.5 7.2L3 21l1.8-6.5A8 8 0 1 1 21 12z", permissions: ["platform.support"] },
    ],
  },
  {
    key: "nav.section.security",
    items: [
      { href: "/admins", key: "nav.admins", icon: "M12 3l8 3v6c0 5-3.5 8-8 9-4.5-1-8-4-8-9V6z", permissions: ["platform.admins"] },
      { href: "/audit", key: "nav.audit", icon: "M9 5h11M9 12h11M9 19h11M4 5h.01M4 12h.01M4 19h.01", permissions: ["platform.audit"] },
      { href: "/security", key: "nav.security", icon: "M12 9v4m0 4h.01M10.3 3.9L1.8 18a2 2 0 0 0 1.7 3h17a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0z", permissions: ["platform.audit"] },
    ],
  },
  {
    key: "nav.section.operations",
    items: [
      { href: "/health", key: "nav.health", icon: "M3 12h4l3-8 4 16 3-8h4", permissions: ["platform.config"] },
      { href: "/releases", key: "nav.releases", icon: "M12 3v12m0 0l-4-4m4 4l4-4M4 17v4h16v-4", permissions: ["platform.releases"] },
      { href: "/announcements", key: "nav.announcements", icon: "M3 11v2l13 5V6zM16 8a4 4 0 0 1 0 8M7 13l1 6h3l-1-5", permissions: ["platform.config"] },
      { href: "/settings", key: "nav.settings", icon: "M12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6zM19.4 15a1.7 1.7 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-2.9 1.2V21a2 2 0 1 1-4 0v-.1A1.7 1.7 0 0 0 7 19.4l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1A1.7 1.7 0 0 0 3 13.7H3a2 2 0 1 1 0-4h.1A1.7 1.7 0 0 0 4.6 7l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1A1.7 1.7 0 0 0 10.3 3V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 2.9 1.2l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0 1.2 2.9H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1z", permissions: ["platform.config"] },
    ],
  },
];

/** True when the sidebar entry should be highlighted for the current pathname. */
export function isActivePath(href: string, pathname: string): boolean {
  if (href === "/") return pathname === "/";
  return pathname === href || pathname.startsWith(`${href}/`);
}

/** Filters the navigation by the signed-in administrator's permissions (the API enforces them regardless). */
export function visibleNav(permissions: readonly string[], nav: readonly NavSection[] = NAV): NavSection[] {
  const has = new Set(permissions);
  const all = has.has("*") || has.has("platform.*");
  return nav
    .map((s) => ({ ...s, items: s.items.filter((i) => all || i.permissions.length === 0 || i.permissions.some((p) => has.has(p))) }))
    .filter((s) => s.items.length > 0);
}
