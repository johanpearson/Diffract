import { describe, expect, it } from "vitest";
import {
  changeKinds,
  changedFileKinds,
  remoteOutcomes,
  refNameKinds,
  resetModes,
  gitOutputKinds,
  errorKinds,
  fileChangeKinds,
  fileContentKinds,
  gitUpdateOutcomes,
  gitUpdateStatuses,
  historyRefKinds,
  leftoverKinds,
  terminalKinds,
  verdicts,
  type HistoryPage,
  type Methods,
  type Notifications,
  type RepoChanged,
} from "./protocol";
import { RpcClient, RpcError } from "./rpc";
import { FakeTransport } from "./testing/fakeTransport";
import { check } from "./testing/shapes";
import { errorShape, notificationShape, notificationShapes, responseShape, resultShapes } from "./testing/protocolShapes";

// Written by ContractTests in the sidecar's tests, from the sidecar's own serializer.
const files = import.meta.glob<unknown>("../../tests/Diffract.Sidecar.Tests/Contract/*.json", { eager: true, import: "default" });
const samples = new Map(Object.entries(files).map(([path, json]) => [path.split("/").pop()!.replace(/\.json$/, ""), json]));

const methods = Object.keys(resultShapes) as (keyof Methods)[];
const notifications = Object.keys(notificationShapes) as (keyof Notifications)[];
const errors = [...samples.keys()].filter((name) => name.startsWith("error."));

function sample(name: string): unknown {
  const value = samples.get(name);
  if (value === undefined) throw new Error(`No contract sample ${name}.json. Run dotnet test to write it.`);
  return value;
}

describe("contract samples from the sidecar", () => {
  it("were found", () => {
    expect(samples.size).toBeGreaterThan(0);
  });

  it("are all known to the UI", () => {
    const known = new Set<string>([...methods, ...notifications, ...errors, "enums"]);
    expect([...samples.keys()].filter((name) => !known.has(name))).toEqual([]);
  });

  it.each(methods)("%s: the response matches the TypeScript types", (method) => {
    expect(check(responseShape(resultShapes[method]), sample(method))).toEqual([]);
  });

  it.each(notifications)("%s: the notification matches the TypeScript types", (method) => {
    expect(check(notificationShape(method, notificationShapes[method]), sample(method))).toEqual([]);
  });

  it.each(errors)("%s: the error matches the TypeScript types", (name) => {
    expect(check(errorShape, sample(name))).toEqual([]);
  });

  it("use the same enum values as the UI", () => {
    expect(sample("enums")).toEqual({
      verdict: [...verdicts],
      leftoverKind: [...leftoverKinds],
      errorKind: [...errorKinds],
      changeKind: [...changeKinds],
      terminalKind: [...terminalKinds],
      gitUpdateStatus: [...gitUpdateStatuses],
      gitUpdateOutcome: [...gitUpdateOutcomes],
      historyRefKind: [...historyRefKinds],
      fileChangeKind: [...fileChangeKinds],
      fileContentKind: [...fileContentKinds],
      changedFileKind: [...changedFileKinds],
      remoteOutcome: [...remoteOutcomes],
      refNameKind: [...refNameKinds],
      resetMode: [...resetModes],
      gitOutputKind: [...gitOutputKinds],
    });
  });

  it.each(methods)("%s: the client resolves with the result", async (method) => {
    const response = sample(method) as { result: unknown };
    const transport = new FakeTransport();
    const pending = new RpcClient(transport).request(method);

    transport.emit(JSON.stringify(response));

    expect(await pending).toEqual(response.result);
  });

  it.each(errors)("%s: the client rejects with the code, message and kind", async (name) => {
    const { error } = sample(name) as { error: { code: number; message: string; data?: { kind: string } } };
    const transport = new FakeTransport();
    const pending = new RpcClient(transport).request("repo.open").catch((e: unknown) => e);

    transport.emit(JSON.stringify(sample(name)));

    const rejected = await pending;
    expect(rejected).toBeInstanceOf(RpcError);
    expect(rejected).toMatchObject({ code: error.code, message: error.message, kind: error.data?.kind });
  });

  it("repo.changed: the client hands the params to the listener", () => {
    const transport = new FakeTransport();
    const received: RepoChanged[] = [];
    new RpcClient(transport).onNotification("repo.changed", (params) => received.push(params));

    transport.emit(JSON.stringify(sample("repo.changed")));

    expect(received).toEqual([(sample("repo.changed") as { params: RepoChanged }).params]);
  });

  it("history.page: the client hands the params to the listener", () => {
    const transport = new FakeTransport();
    const received: HistoryPage[] = [];
    new RpcClient(transport).onNotification("history.page", (params) => received.push(params));

    transport.emit(JSON.stringify(sample("history.page")));

    expect(received).toEqual([(sample("history.page") as { params: HistoryPage }).params]);
  });
});
