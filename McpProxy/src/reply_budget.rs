use crate::manifest::Tool;
use serde_json::{json, Value};

pub const MAX_BRIDGE_REPLY_BYTES: u64 = 64 * 1024;
pub const MAX_TOOL_RESULT_BYTES: usize = 128 * 1024;
const ARGUMENT: &str = "maxBytes";

pub fn requested(tool: &Tool, args: &Value) -> Result<u64, String> {
    let limit = &tool.input_schema.get("properties").unwrap_or(&Value::Null)[ARGUMENT];
    let Some(default) = limit["default"].as_u64() else {
        return Ok(MAX_BRIDGE_REPLY_BYTES);
    };
    let Some(value) = args.get(ARGUMENT).filter(|value| !value.is_null()) else {
        return Ok(default);
    };
    let min = limit["minimum"].as_u64().unwrap_or(default);
    let max = limit["maximum"].as_u64().unwrap_or(default);
    value
        .as_u64()
        .filter(|bytes| (min..=max).contains(bytes))
        .ok_or_else(|| format!("{ARGUMENT} must be a whole number between {min} and {max}."))
}

pub fn omitted(
    read_only: bool,
    success: bool,
    max_bytes: u64,
    scope: &str,
    source: &Value,
) -> Value {
    let accepted = success && !read_only;
    let message = if accepted && scope == "reply" {
        "The bridge accepted the command and its details exceeded maxBytes. If safe retries are supported, use the same requestId and a larger maxBytes. Otherwise check game or operation status before repeating."
    } else if accepted {
        "The bridge accepted the command, but its reply exceeded the proxy byte ceiling. Check the game or operation status before repeating the command."
    } else if success {
        "The read reply exceeded its byte budget. Retry with less detail, a smaller page, a narrower query, or a larger maxBytes."
    } else {
        "The bridge returned an error whose reply exceeded its byte budget."
    };
    let mut data = json!({
        "truncated": true,
        "budget": {"maxBytes": max_bytes, "scope": scope, "omittedFields": ["data"], "hint": message}
    });
    if accepted {
        copy_receipt(source, &mut data);
    }
    json!({"content": [{"type": "text", "text": message}], "structuredContent": data, "isError": !accepted})
}

fn copy_receipt(source: &Value, reduced: &mut Value) {
    for key in ["changed", "loadId", "tick"] {
        if let Some(value) = source.get(key) {
            copy_receipt_field(reduced, key, value);
        }
    }
    let Some(source_data) = source["data"].as_object() else {
        return;
    };
    let mut receipt = json!({});
    for key in [
        "operationId",
        "runId",
        "id",
        "requestId",
        "status",
        "state",
        "done",
        "endReason",
        "applied",
        "alreadySatisfied",
        "rejectedCount",
        "given",
        "refused",
    ] {
        if let Some(value) = source_data.get(key) {
            copy_receipt_field(&mut receipt, key, value);
        }
    }
    if receipt
        .as_object()
        .is_some_and(|receipt| !receipt.is_empty())
    {
        const MAX_OMITTED_FIELDS: usize = 16;
        const MAX_FIELD_NAME_BYTES: usize = 64;
        let omitted: Vec<_> = source_data
            .keys()
            .filter(|key| receipt.get(*key).is_none())
            .take(MAX_OMITTED_FIELDS + 1)
            .collect();
        if omitted.len() <= MAX_OMITTED_FIELDS
            && omitted.iter().all(|key| key.len() <= MAX_FIELD_NAME_BYTES)
        {
            reduced["budget"]["omittedFields"] = json!(omitted);
        }
        reduced["data"] = receipt;
    }
}

fn copy_receipt_field(target: &mut Value, key: &str, value: &Value) {
    const MAX_RECEIPT_FIELD_BYTES: usize = 256;
    if value.is_boolean()
        || value.is_number()
        || value
            .as_str()
            .is_some_and(|text| text.len() <= MAX_RECEIPT_FIELD_BYTES)
    {
        target[key] = value.clone();
    }
}
