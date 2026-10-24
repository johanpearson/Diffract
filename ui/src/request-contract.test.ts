import { describe, expect, it } from "vitest";
import { paramShapes } from "./testing/paramShapes";
import { check } from "./testing/shapes";

// Written by RequestContractTests in the sidecar's tests, from the sidecar's own params types and serializer.
const files = import.meta.glob<unknown>("../../tests/Diffract.Sidecar.Tests/Contract/requests/*.json", { eager: true, import: "default" });
const samples = new Map(Object.entries(files).map(([path, json]) => [path.split("/").pop()!.replace(/\.json$/, ""), json]));
const methods = Object.keys(paramShapes) as (keyof typeof paramShapes)[];

describe("request samples from the sidecar", () => {
  it("cover every method the UI sends params with, and no other", () => {
    expect([...samples.keys()].sort()).toEqual([...methods].sort());
  });

  it.each(methods)("%s: the sidecar reads the params the UI sends", (method) => {
    expect(check(paramShapes[method], samples.get(method))).toEqual([]);
  });
});
