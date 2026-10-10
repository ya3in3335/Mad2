export default function Loading() {
  return (
    <div aria-busy="true" aria-live="polite" className="animate-pulse space-y-6">
      <div className="h-8 w-64 rounded-md bg-raised" />
      <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-4">
        {Array.from({ length: 4 }, (_, i) => (
          <div key={i} className="h-24 rounded-lg bg-raised" />
        ))}
      </div>
      <div className="h-72 rounded-lg bg-raised" />
      <span className="sr-only">Loading…</span>
    </div>
  );
}
