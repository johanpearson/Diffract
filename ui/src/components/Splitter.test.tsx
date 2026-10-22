import { afterEach, describe, expect, it } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { KeyStep, Splitter, useStoredSize, type SizeSpec } from "./Splitter";

afterEach(cleanup);

const spec: SizeSpec = { key: "test.size", initial: 300, min: 200, max: 500 };

function memory(entries: Record<string, string> = {}) {
  const map = new Map(Object.entries(entries));
  return { map, get: (key: string) => map.get(key) ?? null, set: (key: string, value: string) => void map.set(key, value) };
}

function Sized({ storage, growsForward = true }: { storage: ReturnType<typeof memory>; growsForward?: boolean }) {
  const [size, setSize] = useStoredSize(storage, spec);
  return <Splitter label="Resize panel" orientation="vertical" spec={spec} size={size} onSize={setSize} growsForward={growsForward} />;
}

const bar = () => screen.getByRole("separator", { name: "Resize panel" });
const size = () => Number(bar().getAttribute("aria-valuenow"));

describe("Splitter", () => {
  it("starts at the initial size, then at the size remembered from last time", () => {
    render(<Sized storage={memory()} />);
    expect(size()).toBe(300);
    cleanup();

    render(<Sized storage={memory({ [spec.key]: "420" })} />);
    expect(size()).toBe(420);
  });

  it("ignores a remembered size that is not a number or out of range", () => {
    render(<Sized storage={memory({ [spec.key]: "wide" })} />);
    expect(size()).toBe(300);
    cleanup();

    render(<Sized storage={memory({ [spec.key]: "9000" })} />);
    expect(size()).toBe(500);
  });

  it("moves with the arrow keys and remembers the size", () => {
    const storage = memory();
    render(<Sized storage={storage} />);

    fireEvent.keyDown(bar(), { key: "ArrowRight" });
    fireEvent.keyDown(bar(), { key: "ArrowRight" });
    fireEvent.keyDown(bar(), { key: "ArrowLeft" });

    expect(size()).toBe(300 + KeyStep);
    expect(storage.map.get(spec.key)).toBe(String(300 + KeyStep));
  });

  it("grows the other way for a panel on the right", () => {
    render(<Sized storage={memory()} growsForward={false} />);

    fireEvent.keyDown(bar(), { key: "ArrowLeft" });

    expect(size()).toBe(300 + KeyStep);
  });

  it("goes to the smallest and largest size with Home and End, and never past them", () => {
    render(<Sized storage={memory()} />);

    fireEvent.keyDown(bar(), { key: "End" });
    fireEvent.keyDown(bar(), { key: "ArrowRight" });
    expect(size()).toBe(500);
    fireEvent.keyDown(bar(), { key: "Home" });
    expect(size()).toBe(200);
  });

  it("follows a drag, and goes back to the initial size on a double-click", () => {
    render(<Sized storage={memory()} />);

    fireEvent.pointerDown(bar(), { clientX: 100, pointerId: 1 });
    fireEvent.pointerMove(bar(), { clientX: 160, pointerId: 1 });
    fireEvent.pointerUp(bar(), { clientX: 160, pointerId: 1 });
    expect(size()).toBe(360);
    fireEvent.pointerMove(bar(), { clientX: 400, pointerId: 1 });
    expect(size()).toBe(360);

    fireEvent.doubleClick(bar());
    expect(size()).toBe(300);
  });
});
