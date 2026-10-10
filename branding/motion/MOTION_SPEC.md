# VELTRIX OS — Motion specification

Implemented natively in each client (no video files):

| Platform | Implementation |
|---|---|
| Windows (WPF) | `apps/windows/Veltrix.Desktop/Views/SplashWindow.xaml` — vector paths, `Storyboard` animations on opacity/transforms |
| Android (Compose) | `apps/android/.../ui/splash/AnimatedSplash.kt` — `Canvas` + `Animatable` |
| Control Center (web) | `apps/control-center/src/components/BrandIntro.tsx` — SVG + CSS keyframes |
| Standalone export | `branding/motion/veltrix-intro.html` (SVG/CSS) and `branding/motion/veltrix-intro.lottie.json` (Lottie/Bodymovin 5.x) |

## Startup sequence (≈ 3.0 s, never longer than actual startup work requires)

| Phase | Time | What happens | Easing |
|---|---|---|---|
| 1 Initial appearance | 0 – 450 ms | Navy background fades in; 14 faint cyan particles drift (opacity ≤ 0.35) | ease-out |
| 2 Logo construction | 300 – 1500 ms | Left blade slides up 24 px & fades in; right blade follows with 150 ms delay; circuit trace draws (stroke-dashoffset); nodes pop (scale 0.6→1) | cubic-bezier(0.2, 0.8, 0.2, 1) |
| 3 Brand reveal | 1300 – 2100 ms | Core node glows once (blur 0→12→6, no flashing); "VELTRIX OS" fades in with 12 px upward movement, then tagline | ease-out |
| 4 Initialization | 2100 ms → ready | A thin indeterminate line appears **only while real startup tasks are running** (database open, migrations, license check). No fake percentages. | linear |
| 5 Transition | on ready | Splash cross-fades (250 ms) into activation, login or dashboard | ease-in-out |

If the application finishes starting before 2.1 s the sequence still completes phase 3 (minimum brand moment
≈ 2.1 s on first launch, 1.2 s on later launches). If startup takes longer, phase 4 continues until ready.

### Reduced motion / static fallback
When the OS requests reduced motion (Windows *Show animations* off, Android *Remove animations*, web
`prefers-reduced-motion`), the static vertical logo is shown and the transition is a 150 ms fade.

### Performance budgets
* ≤ 2 % CPU average on a 4-core desktop during the intro; GPU-composited opacity/transform only.
* No allocations per frame on Android (pre-built paths).
* No network access during the intro.

## Micro-interactions (shared tokens)

| Interaction | Duration | Effect |
|---|---|---|
| Login → dashboard | 300 ms | Fade + 8 px slide |
| Navigation change | 180 ms | Content fade + 6 px slide (direction follows RTL/LTR) |
| Dialog open / close | 160 / 120 ms | Scale 0.96→1 + fade / reverse |
| Successful payment | 600 ms | Check-mark path draw + success colour pulse once |
| License activated | 900 ms | Symbol blades assemble + check mark |
| Loading | — | Skeleton shimmer 1.2 s loop (disabled with reduced motion) |
| Empty state | 400 ms | Symbol outline fade-in |
| Notification toast | 220 ms | Slide from edge + fade; auto-dismiss 5 s |
| Report transition | 200 ms | Cross-fade of chart/table |
| Sync running | 1.0 s loop | Two-arrow rotation (paused when idle) |
| Error feedback | 300 ms | 2 × 4 px horizontal shake (no red flashing) |
| Subscription renewed | 700 ms | Calendar → check morph |
