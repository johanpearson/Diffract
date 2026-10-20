// No console window next to the app in release builds.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod framing;

use std::sync::Mutex;
use tauri::{Emitter, Manager, RunEvent, State};
use tauri_plugin_shell::ShellExt;
use tauri_plugin_shell::process::{CommandChild, CommandEvent};

/// The running sidecar, if any.
#[derive(Default)]
struct Sidecar(Mutex<Option<CommandChild>>);

/// Writes one JSON-RPC message from the UI to the sidecar's stdin.
#[tauri::command]
fn sidecar_send(sidecar: State<Sidecar>, message: String) -> Result<(), String> {
    let bytes = framing::frame(&message)?;
    let mut child = sidecar.0.lock().map_err(|e| e.to_string())?;
    match child.as_mut() {
        Some(child) => child.write(&bytes).map_err(|e| e.to_string()),
        None => Err("The sidecar is not running".into()),
    }
}

/// Starts the bundled sidecar and relays every line it prints to the UI as a "sidecar-message" event.
fn start_sidecar(app: &tauri::AppHandle) -> Result<(), Box<dyn std::error::Error>> {
    let (mut events, child) = app.shell().sidecar("diffract-sidecar")?.spawn()?;
    *app.state::<Sidecar>().0.lock().unwrap() = Some(child);

    let app = app.clone();
    tauri::async_runtime::spawn(async move {
        while let Some(event) = events.recv().await {
            match event {
                CommandEvent::Stdout(line) => {
                    let _ = app.emit("sidecar-message", framing::unframe(&line));
                }
                CommandEvent::Stderr(line) => eprintln!("sidecar: {}", framing::unframe(&line)),
                CommandEvent::Terminated(status) => {
                    *app.state::<Sidecar>().0.lock().unwrap() = None;
                    let _ = app.emit("sidecar-exited", status.code);
                }
                _ => {}
            }
        }
    });
    Ok(())
}

fn main() {
    tauri::Builder::default()
        .plugin(tauri_plugin_shell::init())
        .manage(Sidecar::default())
        .setup(|app| start_sidecar(app.handle()))
        .invoke_handler(tauri::generate_handler![sidecar_send])
        .build(tauri::generate_context!())
        .expect("Diffract could not start")
        .run(|app, event| {
            if let RunEvent::Exit = event {
                if let Some(child) = app.state::<Sidecar>().0.lock().unwrap().take() {
                    let _ = child.kill();
                }
            }
        });
}
