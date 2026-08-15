use std::{
    fs,
    path::{Component, Path, PathBuf},
    sync::Mutex,
};

use base64::{engine::general_purpose::STANDARD, Engine as _};
use serde::{Deserialize, Serialize};
use tauri::{AppHandle, State};

#[derive(Default)]
struct KnowledgeState {
    root: Mutex<Option<PathBuf>>,
}

#[derive(Serialize, Deserialize)]
struct ReaderSettings {
    version: u32,
    last_knowledge_root: PathBuf,
}

#[tauri::command]
fn set_knowledge_root(path: String, state: State<'_, KnowledgeState>) -> Result<(), String> {
    let root = validate_knowledge_root(Path::new(&path))?;
    *state.root.lock().map_err(|_| "无法锁定知识库状态。".to_string())? = Some(root);
    Ok(())
}

#[tauri::command]
fn remember_knowledge_root(app: AppHandle, state: State<'_, KnowledgeState>) -> Result<(), String> {
    let root = state
        .root
        .lock()
        .map_err(|_| "无法锁定知识库状态。".to_string())?
        .clone()
        .ok_or_else(|| "请先打开离线知识库。".to_string())?;
    let directory = app
        .path_resolver()
        .app_config_dir()
        .ok_or_else(|| "无法确定阅读器配置目录。".to_string())?;
    fs::create_dir_all(&directory).map_err(|error| error.to_string())?;
    let settings = ReaderSettings {
        version: 1,
        last_knowledge_root: root,
    };
    let json = serde_json::to_vec_pretty(&settings).map_err(|error| error.to_string())?;
    fs::write(directory.join("reader-settings.json"), json).map_err(|error| error.to_string())
}

#[tauri::command]
fn try_load_default_knowledge(app: AppHandle, state: State<'_, KnowledgeState>) -> Result<bool, String> {
    if let Some(directory) = app.path_resolver().app_config_dir() {
        let settings_path = directory.join("reader-settings.json");
        if let Ok(json) = fs::read_to_string(settings_path) {
            if let Ok(settings) = serde_json::from_str::<ReaderSettings>(&json) {
                if settings.version == 1 {
                    if let Ok(root) = validate_knowledge_root(&settings.last_knowledge_root) {
                        *state.root.lock().map_err(|_| "无法锁定知识库状态。".to_string())? = Some(root);
                        return Ok(true);
                    }
                }
            }
        }
    }

    let executable = std::env::current_exe().map_err(|error| error.to_string())?;
    let Some(parent) = executable.parent() else {
        return Ok(false);
    };
    let candidate = parent.join("knowledge");
    if !candidate.join("manifest.json").is_file() {
        return Ok(false);
    }
    let root = validate_knowledge_root(&candidate)?;
    *state.root.lock().map_err(|_| "无法锁定知识库状态。".to_string())? = Some(root);
    Ok(true)
}

#[tauri::command]
fn read_knowledge_text(relative_path: String, state: State<'_, KnowledgeState>) -> Result<String, String> {
    let path = resolve_knowledge_path(&relative_path, &state)?;
    fs::read_to_string(path).map_err(|error| error.to_string())
}

#[tauri::command]
fn read_knowledge_asset(relative_path: String, state: State<'_, KnowledgeState>) -> Result<String, String> {
    let path = resolve_knowledge_path(&relative_path, &state)?;
    let bytes = fs::read(&path).map_err(|error| error.to_string())?;
    let mime = detect_mime(&bytes, &path);
    Ok(format!("data:{};base64,{}", mime, STANDARD.encode(bytes)))
}

#[tauri::command]
fn open_original(relative_path: String, state: State<'_, KnowledgeState>) -> Result<(), String> {
    let path = resolve_knowledge_path(&relative_path, &state)?;
    open::that(path).map_err(|error| error.to_string())
}

fn validate_knowledge_root(path: &Path) -> Result<PathBuf, String> {
    let root = path.canonicalize().map_err(|error| error.to_string())?;
    let manifest_path = root.join("manifest.json");
    let manifest = fs::read_to_string(&manifest_path).map_err(|error| error.to_string())?;
    let json: serde_json::Value = serde_json::from_str(&manifest).map_err(|error| error.to_string())?;
    if json.get("format").and_then(|value| value.as_str()) != Some("feishu-offline-knowledge") {
        return Err("所选目录不是可识别的飞书离线知识库。".to_string());
    }
    if !matches!(json.get("version").and_then(|value| value.as_u64()), Some(1..=3)) {
        return Err("离线知识库格式版本不受当前阅读器支持。".to_string());
    }
    for relative_path in ["tree.json", "index/search-index.json"] {
        let content = fs::read_to_string(root.join(relative_path)).map_err(|error| error.to_string())?;
        serde_json::from_str::<serde_json::Value>(&content).map_err(|error| error.to_string())?;
    }
    Ok(root)
}

fn resolve_knowledge_path(relative_path: &str, state: &State<'_, KnowledgeState>) -> Result<PathBuf, String> {
    let relative = Path::new(relative_path);
    if relative.is_absolute() || relative.components().any(|component| {
        matches!(component, Component::ParentDir | Component::RootDir | Component::Prefix(_))
    }) {
        return Err("知识库路径无效。".to_string());
    }

    let guard = state.root.lock().map_err(|_| "无法锁定知识库状态。".to_string())?;
    let root = guard.as_ref().ok_or_else(|| "请先打开离线知识库。".to_string())?;
    let candidate = root.join(relative).canonicalize().map_err(|error| error.to_string())?;
    if !candidate.starts_with(root) {
        return Err("知识库路径越界。".to_string());
    }
    Ok(candidate)
}

fn detect_mime(bytes: &[u8], path: &Path) -> &'static str {
    if bytes.starts_with(&[0x89, b'P', b'N', b'G']) { return "image/png"; }
    if bytes.starts_with(&[0xff, 0xd8, 0xff]) { return "image/jpeg"; }
    if bytes.starts_with(b"GIF8") { return "image/gif"; }
    if bytes.len() >= 12 && &bytes[0..4] == b"RIFF" && &bytes[8..12] == b"WEBP" { return "image/webp"; }
    match path.extension().and_then(|value| value.to_str()).unwrap_or_default().to_ascii_lowercase().as_str() {
        "svg" => "image/svg+xml",
        "pdf" => "application/pdf",
        _ => "application/octet-stream",
    }
}

fn main() {
    tauri::Builder::default()
        .manage(KnowledgeState::default())
        .invoke_handler(tauri::generate_handler![
            set_knowledge_root,
            remember_knowledge_root,
            try_load_default_knowledge,
            read_knowledge_text,
            read_knowledge_asset,
            open_original
        ])
        .run(tauri::generate_context!())
        .expect("error while running Feishu Wiki Reader WebKitGTK 4.0 compatibility build");
}
