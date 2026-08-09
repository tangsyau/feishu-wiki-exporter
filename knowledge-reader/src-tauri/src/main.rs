#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

use std::{
    fs,
    path::{Component, Path, PathBuf},
    sync::Mutex,
};

use tauri::State;

#[derive(Default)]
struct KnowledgeState {
    root: Mutex<Option<PathBuf>>,
}

#[tauri::command]
fn set_knowledge_root(path: String, state: State<'_, KnowledgeState>) -> Result<(), String> {
    let root = validate_knowledge_root(Path::new(&path))?;
    *state.root.lock().map_err(|_| "无法锁定知识库状态。".to_string())? = Some(root);
    Ok(())
}

#[tauri::command]
fn try_load_default_knowledge(state: State<'_, KnowledgeState>) -> Result<bool, String> {
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

fn main() {
    tauri::Builder::default()
        .plugin(tauri_plugin_dialog::init())
        .manage(KnowledgeState::default())
        .invoke_handler(tauri::generate_handler![
            set_knowledge_root,
            try_load_default_knowledge,
            read_knowledge_text,
            open_original
        ])
        .run(tauri::generate_context!())
        .expect("error while running Feishu Wiki Reader");
}
