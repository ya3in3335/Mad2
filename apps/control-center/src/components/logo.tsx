/* eslint-disable @next/next/no-img-element -- static SVG brand asset; next/image adds nothing here. */
export function Logo({ theme, className }: { theme: "dark" | "light"; className?: string }) {
  const src = theme === "dark" ? "/brand/veltrix-logo-horizontal-dark.svg" : "/brand/veltrix-logo-horizontal-light.svg";
  return <img src={src} alt="VELTRIX" width={1080} height={340} className={className} />;
}
