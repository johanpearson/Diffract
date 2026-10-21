// No console window next to the app in release builds.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod framing;
mod restart;
mod sidecar_log;

use restart::RestartPolicy;
use sidecar_log::SidecarLog;
use std::sync::Mutex;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::{Duration, Instant};
use tauri::{AppHandle, Emitter, Manager, RunEvent, State, WindowEvent};
use tauri_plugin_shell::ShellExt;
use tauri_plugin_shell::process::{CommandChild, CommandEvent};

/// How long the sidecar gets to exit after `app.shutdown` before it is killed.
const SHUTDOWN_GRACE: Duration = Duration::from_secs(2);

/// The running sidecar, if any, and what the core needs to look after it.
#[derive(Default)]
struct Sidecar {
    child: Mutex<Option<CommandChild>>,
    restarts: Mutex<RestartPolicy>,
    /// Set once the window is closing, so the sidecar's exit is expected and not restarted.
    shutting_down: AtomicBool,
    log: Mutex<Option<SidecarLog>>,
}

impl Sidecar {
    fn log(&self, line: &str) {
        eprintln!("{line}");
        if let Some(log) = self.log.lock().unwrap().as_mut() {
            let _ = log.write_line(line);
        }
    }
}

/// Writes one JSON-RPC message from the UI to the sidecar's stdin.
#[tauri::command]
fn sidecar_send(sidecar: State<Sidecar>, message: String) -> Result<(), String> {
    let bytes = framing::frame(&message)?;
    let mut child = sidecar.child.lock().map_err(|e| e.to_string())?;
    match child.as_mut() {
        Some(child) => child.write(&bytes).map_err(|e| e.to_string()),
        None => Err("The sidecar is not running".into()),
    }
}

/// Starts the sidecar again after the core gave up on it, when the user asks to try again.
#[tauri::command]
fn sidecar_restart(app: AppHandle) -> Result<(), String> {
    let sidecar = app.state::<Sidecar>();
    sidecar.restarts.lock().unwrap().reset();
    if sidecar.child.lock().unwrap().is_some() {
        return Ok(());
    }
    start_sidecar(&app)
}

/// Starts the bundled sidecar and relays every line it prints to stdout to the UI as a "sidecar-message" event.
/// Lines on stderr are its logs.
fn start_sidecar(app: &AppHandle) -> Result<(), String> {
    let (mut events, child) = app.shell().sidecar("diffract-sidecar").and_then(|c| c.spawn()).map_err(|e| e.to_string())?;
    let pid = child.pid();
    *app.state::<Sidecar>().child.lock().unwrap() = Some(child);

    let app = app.clone();
    tauri::async_runtime::spawn(async move {
        while let Some(event) = events.recv().await {
            match event {
                CommandEvent::Stdout(line) => {
                    let _ = app.emit("sidecar-message", framing::unframe(&line));
                }
                CommandEvent::Stderr(line) => app.state::<Sidecar>().log(&format!("sidecar: {}", framing::unframe(&line))),
                CommandEvent::Terminated(status) => {
                    let sidecar = app.state::<Sidecar>();
                    let mut slot = sidecar.child.lock().unwrap();
                    // A restarted sidecar may already be in the slot; only clear this one.
                    if slot.as_ref().map(CommandChild::pid) == Some(pid) {
                        *slot = None;
                    }
                    drop(slot);
                    sidecar_exited(&app, status.code);
                }
                _ => {}
            }
        }
    });
    Ok(())
}

/// Restarts a sidecar that stopped on its own, at most 3 times a minute, and tells the UI either way.
fn sidecar_exited(app: &AppHandle, code: Option<i32>) {
    let sidecar = app.state::<Sidecar>();
    if sidecar.shutting_down.load(Ordering::SeqCst) {
        return;
    }
    sidecar.log(&format!("core: the sidecar stopped unexpectedly (exit code {code:?})"));

    let allowed = sidecar.restarts.lock().unwrap().allow(Instant::now());
    match allowed.then(|| start_sidecar(app)) {
        Some(Ok(())) => {
            sidecar.log("core: the sidecar was restarted");
            let _ = app.emit("sidecar-restarted", ());
        }
        Some(Err(e)) => {
            sidecar.log(&format!("core: the sidecar could not be restarted: {e}"));
            let _ = app.emit("sidecar-stopped", ());
        }
        None => {
            sidecar.log("core: the sidecar stopped too often; not restarting it");
            let _ = app.emit("sidecar-stopped", ());
        }
    }
}

/// Asks the sidecar to shut down, waits up to 2 seconds for it to exit, then kills it if it is still there.
fn stop_sidecar(app: &AppHandle) {
    let sidecar = app.state::<Sidecar>();
    sidecar.shutting_down.store(true, Ordering::SeqCst);
    // A string id, so the UI's client, which only numbers its own requests, ignores the answer.
    if let Some(child) = sidecar.child.lock().unwrap().as_mut() {
        let _ = child.write(&framing::frame(r#"{"jsonrpc":"2.0","id":"core-shutdown","method":"app.shutdown"}"#).unwrap());
    }

    let deadline = Instant::now() + SHUTDOWN_GRACE;
    while Instant::now() < deadline && sidecar.child.lock().unwrap().is_some() {
        std::thread::sleep(Duration::from_millis(50));
    }
    if let Some(child) = sidecar.child.lock().unwrap().take() {
        sidecar.log("core: the sidecar did not exit within 2 seconds; killing it");
        let _ = child.kill();
    }
}

fn main() {
    tauri::Builder::default()
        .plugin(tauri_plugin_shell::init())
        .plugin(tauri_plugin_dialog::init())
        .manage(Sidecar::default())
        .setup(|app| {
            // Without a log file the app still works; the lines then only reach the console in dev builds.
            match app.path().app_log_dir().map(|dir| SidecarLog::open(&dir)) {
                Ok(Ok(log)) => *app.state::<Sidecar>().log.lock().unwrap() = Some(log),
                Ok(Err(e)) => eprintln!("Could not open the sidecar log: {e}"),
                Err(e) => eprintln!("No log folder for the sidecar log: {e}"),
            }
            start_sidecar(app.handle())?;
            Ok(())
        })
        .on_window_event(|window, event| {
            if let WindowEvent::CloseRequested { api, .. } = event {
                let app = window.app_handle().clone();
                if app.state::<Sidecar>().shutting_down.load(Ordering::SeqCst) {
                    return;
                }
                // Keep the window until the sidecar is gone, without blocking the UI thread meanwhile.
                api.prevent_close();
                std::thread::spawn(move || {
                    stop_sidecar(&app);
                    app.exit(0);
                });
            }
        })
        .invoke_handler(tauri::generate_handler![sidecar_send, sidecar_restart])
        .build(tauri::generate_context!())
        .expect("Diffract could not start")
        .run(|app, event| {
            if let RunEvent::Exit = event {
                app.state::<Sidecar>().shutting_down.store(true, Ordering::SeqCst);
                if let Some(child) = app.state::<Sidecar>().child.lock().unwrap().take() {
                    let _ = child.kill();
                }
            }
        });
}
