import { useCallback, useEffect, useState, type RefObject } from "react";

/** Used until the element has a size, for example in tests, where nothing is laid out. */
const FallbackHeight = 600;

/** The scroll position and height of a scrolling element, kept current. */
export function useViewport(ref: RefObject<HTMLElement | null>) {
  const [scrollTop, setScrollTop] = useState(0);
  const [height, setHeight] = useState(FallbackHeight);

  useEffect(() => {
    const element = ref.current;
    if (!element) return;
    const measure = () => setHeight(element.clientHeight || FallbackHeight);
    measure();
    if (typeof ResizeObserver === "undefined") return;
    const observer = new ResizeObserver(measure);
    observer.observe(element);
    return () => observer.disconnect();
  }, [ref]);

  const onScroll = useCallback(() => setScrollTop(ref.current?.scrollTop ?? 0), [ref]);

  return { scrollTop, height, onScroll };
}
