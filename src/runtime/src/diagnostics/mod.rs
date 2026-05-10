//! Z#### VM runtime diagnostic catalog.
//!
//! Cross-language source of truth: [`docs/error-codes/Z.json`] —
//! this module loads the same JSON the C# compiler embeds, so
//! `z42vm explain Z0905` and `z42c explain Z0905` always render
//! identical content.
//!
//! Adding a new code: edit `Z.json`, emit it via `anyhow!("Zxxxx: ...")`,
//! and the drift-guard test (`registry_audit`) ensures the catalog and
//! emit sites stay in sync.

use std::sync::OnceLock;

use serde::Deserialize;

/// Embedded JSON SoT. Path is relative to this source file (src/runtime/src/diagnostics/mod.rs).
const Z_CATALOG_JSON: &str = include_str!("../../../../docs/error-codes/Z.json");

#[derive(Debug, Clone, Deserialize)]
pub struct DiagnosticEntry {
    pub code: String,
    pub title: String,
    pub description: String,
    #[serde(default)]
    pub example: String,
}

#[derive(Debug, Deserialize)]
struct Catalog {
    entries: Vec<DiagnosticEntry>,
}

fn catalog() -> &'static Vec<DiagnosticEntry> {
    static CATALOG: OnceLock<Vec<DiagnosticEntry>> = OnceLock::new();
    CATALOG.get_or_init(|| {
        let parsed: Catalog = serde_json::from_str(Z_CATALOG_JSON)
            .expect("Z.json failed to parse — fix docs/error-codes/Z.json");
        parsed.entries
    })
}

/// Look up a code (case-insensitive on the leading 'Z' but the rest is exact).
pub fn explain(code: &str) -> Option<&'static DiagnosticEntry> {
    catalog().iter().find(|e| e.code.eq_ignore_ascii_case(code))
}

/// All entries, in the order declared in Z.json.
pub fn list_all() -> &'static [DiagnosticEntry] {
    catalog().as_slice()
}

/// Format an entry for terminal display (mirrors the C# DiagnosticCatalog
/// rendering: header + horizontal rule + description + optional example).
pub fn format(entry: &DiagnosticEntry) -> String {
    let mut out = String::new();
    out.push_str(&format!("error[{}]: {}\n", entry.code, entry.title));
    out.push_str(&"─".repeat(60));
    out.push('\n');
    out.push('\n');
    out.push_str(&entry.description);
    out.push('\n');
    if !entry.example.is_empty() {
        out.push('\n');
        out.push_str("Example:\n");
        for line in entry.example.lines() {
            out.push_str("  ");
            out.push_str(line);
            out.push('\n');
        }
    }
    out.trim_end().to_string()
}

/// Compact list output (one line per code, grouped header).
pub fn format_list_all() -> String {
    let mut out = String::new();
    out.push_str("z42vm runtime diagnostic codes (Z####):\n\n");
    for entry in list_all() {
        out.push_str(&format!("  {}  {}\n", entry.code, entry.title));
    }
    out.push_str("\nUse `z42vm explain <code>` for full details.");
    out
}

#[cfg(test)]
mod tests;
