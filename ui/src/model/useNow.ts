import { useEffect, useState } from "react";

/** The current time, updated every 30 seconds so "5 min ago" stays true. */
export function useNow(clock: () => Date): Date {
  const [now, setNow] = useState(clock);
  useEffect(() => {
    const timer = setInterval(() => setNow(clock()), 30_000);
    return () => clearInterval(timer);
  }, [clock]);
  return now;
}
