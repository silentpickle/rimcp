use serde::{Deserialize, Serialize};
use serde_json::{Map, Value};
use std::collections::HashSet;

#[derive(Deserialize)]
pub struct Manifest {
    pub version: String,
    pub tools: Vec<Tool>,
}

impl Manifest {
    pub fn load() -> Result<Self, serde_json::Error> {
        // The exporter validates Shared/ToolManifest.cs before producing this file.
        serde_json::from_str(include_str!(concat!(env!("OUT_DIR"), "/manifest.json")))
    }

    pub fn allowed_tools(
        &self,
        groups: Option<&str>,
        names: Option<&str>,
    ) -> Result<Option<HashSet<String>>, String> {
        if groups.is_none() && names.is_none() {
            return Ok(None);
        }
        let mut allowed = HashSet::new();
        for (key, setting, by_group) in [
            ("RIMCP_TOOL_GROUPS", groups, true),
            ("RIMCP_TOOLS", names, false),
        ] {
            let Some(setting) = setting else { continue };
            for value in setting.split(',').map(str::trim) {
                let matches = self.tools.iter().filter(|tool| {
                    if by_group {
                        tool.group == value
                    } else {
                        tool.name == value
                    }
                });
                let mut matches = matches.peekable();
                if matches.peek().is_none() {
                    return Err(format!("{key} has an unknown or empty entry: {value:?}"));
                }
                allowed.extend(matches.map(|tool| tool.name.clone()));
            }
        }
        Ok(Some(allowed))
    }
}

#[derive(Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Tool {
    pub name: String,
    #[serde(skip_serializing)]
    pub group: String,
    pub description: String,
    pub input_schema: Map<String, Value>,
    #[serde(default, skip_serializing_if = "Map::is_empty")]
    pub annotations: Map<String, Value>,
    #[serde(skip_serializing)]
    pub method: Method,
    #[serde(skip_serializing)]
    pub path_template: String,
    #[serde(skip_serializing)]
    pub query_keys: Vec<String>,
    #[serde(skip_serializing)]
    pub body_keys: Vec<String>,
}

#[derive(Deserialize)]
#[serde(rename_all = "UPPERCASE")]
pub enum Method {
    Get,
    Put,
    Post,
}

impl Method {
    pub fn as_str(&self) -> &'static str {
        match self {
            Self::Get => "GET",
            Self::Put => "PUT",
            Self::Post => "POST",
        }
    }
}
