# Setting up a machine to build Diffract

Everything you need to build and test Diffract on Windows, from the libraries to the installer. Run the commands in PowerShell. Each tool has one `winget` command where one exists.

## What you need

| Tool | Why | Version |
|---|---|---|
| .NET SDK | the sidecar and its tests | 10.0.100 or newer (see `global.json`) |
| Node.js | the UI: Vite, React, Vitest, Playwright | 24 LTS or newer |
| Rust, MSVC toolchain | the Tauri core | current stable, `x86_64-pc-windows-msvc` |
| C++ build tools | linking the Rust core and the Native AOT sidecar | MSVC from Visual Studio 2022 or 2026 |
| WebView2 runtime | the app window | comes with Windows 11 |
| Git for Windows | Diffract itself drives it | 2.40 or newer |

## Install

### .NET SDK

```powershell
winget install --id Microsoft.DotNet.SDK.10 --exact
```

### Node.js

```powershell
winget install --id OpenJS.NodeJS.LTS --exact
```

### Rust

```powershell
winget install --id Rustlang.Rustup --exact
```

Open a new terminal afterwards, so `cargo` is on the PATH. On Windows, rustup picks the MSVC toolchain by default. If `rustc -vV` does not show `host: x86_64-pc-windows-msvc`, run:

```powershell
rustup default stable-x86_64-pc-windows-msvc
```

### C++ build tools

Rust on Windows and .NET Native AOT both need the MSVC linker. How you get it depends on whether Visual Studio is already installed.

**Visual Studio is installed** (Community, Professional or Enterprise): add the "Desktop development with C++" workload to it. Find the installation path with `vswhere`, then run the Visual Studio Installer:

```powershell
$vs = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -property installationPath
& "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\setup.exe" modify --installPath $vs `
    --add Microsoft.VisualStudio.Workload.NativeDesktop --includeRecommended --passive
```

You can also do the same by hand: open Visual Studio Installer, choose **Modify**, and tick **Desktop development with C++**.

**No Visual Studio:** install the Build Tools with the C++ workload only:

```powershell
winget install --id Microsoft.VisualStudio.BuildTools --exact `
    --override "--wait --passive --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended"
```

### WebView2

Windows 11 includes it. To check, this should print a version:

```powershell
(Get-ItemProperty "HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}").pv
```

If it prints nothing, install it with `winget install --id Microsoft.EdgeWebView2Runtime --exact`.

## Check that it works

Run these from the repo root in a new terminal.

```powershell
dotnet --list-sdks                 # a 10.0.x line
node --version                     # v24 or newer
cargo --version                    # any version
rustc -vV                          # host: x86_64-pc-windows-msvc
git --version                      # 2.40 or newer
dotnet test --solution Diffract.slnx
```

The last check proves the C++ linker works: it publishes the spike as a native binary, which fails without it. These two commands work in both PowerShell and Command Prompt. The output goes to `artifacts\`, which git ignores.

```powershell
dotnet publish tools/Diffract.Spike -c Release -r win-x64 -o artifacts\aot
artifacts\aot\diffract-spike.exe scan .
```

## Run the app

Once, and again after changing sidecar code: publish the sidecar to where Tauri bundles it from.

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build-sidecar.ps1
```

Then, from `ui\`:

```powershell
npm ci                    # once, and after package changes
npm run tauri dev         # the app, reloading the UI as you edit it
npm test                  # UI tests
npm run tauri build       # the installer, in src-tauri\target\release\bundle\nsis\
```

The Rust tests run with `cargo test` in `src-tauri\`. They need a built UI in `ui\dist`, so run `npm run build` in `ui\` first.

## Later in milestone 2

- **Playwright browsers.** Phase 4 adds end-to-end tests. Install the browser they use once, with `npx playwright install chromium` in `ui/`.
