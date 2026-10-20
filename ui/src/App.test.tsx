import { afterEach, describe, expect, it } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import { App } from "./App";
import type { AppInfo } from "./protocol";
import { RpcClient } from "./rpc";
import { FakeTransport } from "./testing/fakeTransport";

afterEach(cleanup);

function renderWith(answer: (method: string) => unknown) {
  render(<App client={new RpcClient(new FakeTransport(answer))} />);
}

const supported: AppInfo = { sidecarVersion: "0.1.0", gitVersion: "2.47.1", gitSupported: true, gitProblem: null };

describe("App", () => {
  it("shows the sidecar and git versions", async () => {
    renderWith((method) => (method === "app.info" ? supported : undefined));

    expect(await screen.findByText("0.1.0")).toBeTruthy();
    expect(screen.getByText("2.47.1")).toBeTruthy();
    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("says why git cannot be used", async () => {
    renderWith(() => ({ ...supported, gitVersion: "2.39.5", gitSupported: false, gitProblem: "Diffract needs git 2.40.0 or newer, but found 2.39.5." }));

    expect((await screen.findByRole("alert")).textContent).toContain("needs git 2.40.0");
  });

  it("says when git is missing", async () => {
    renderWith(() => ({ ...supported, gitVersion: null, gitSupported: false, gitProblem: "Git was not found (git)." }));

    expect(await screen.findByText("not found")).toBeTruthy();
    expect(screen.getByRole("alert").textContent).toContain("Git was not found");
  });

  it("says when the sidecar does not answer", async () => {
    renderWith(() => {
      throw { code: -32603, message: "Something broke" };
    });

    expect((await screen.findByRole("alert")).textContent).toBe("The sidecar did not answer: Something broke");
  });
});
