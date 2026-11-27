use serde_json::{json, Value};
use std::collections::VecDeque;

const TICKS_PER_HOUR: f64 = 2500.0;
const RETAINED_LOADS: usize = 8;

#[derive(Default)]
pub struct PlayMetrics {
    loads: VecDeque<LoadMetrics>,
}

struct LoadMetrics {
    id: String,
    first_tick: u64,
    last_tick: u64,
    reply_bytes: u64,
}

impl PlayMetrics {
    // Use both tick bounds so overlapping calls may finish in either order.
    pub fn record(&mut self, reply: &Value, bytes: u64) -> Option<u64> {
        let id = reply.get("loadId")?.as_str()?;
        let tick = reply.get("tick")?.as_u64()?;
        let mut load = self.take(id).unwrap_or_else(|| LoadMetrics {
            id: id.into(),
            first_tick: tick,
            last_tick: tick,
            reply_bytes: 0,
        });
        load.first_tick = load.first_tick.min(tick);
        load.last_tick = load.last_tick.max(tick);
        load.reply_bytes = load.reply_bytes.saturating_add(bytes);
        let ticks = load.last_tick - load.first_tick;
        let rate = (ticks > 0)
            .then(|| (load.reply_bytes as f64 * TICKS_PER_HOUR / ticks as f64).round() as u64);
        self.loads.push_front(load);
        self.loads.truncate(RETAINED_LOADS);
        rate
    }

    fn take(&mut self, id: &str) -> Option<LoadMetrics> {
        let index = self.loads.iter().position(|load| load.id == id)?;
        self.loads.remove(index)
    }
}

pub fn annotate(reply: &mut Value, name: &str, rate: Option<u64>) -> bool {
    match name {
        "get_game_context" => reply
            .get_mut("data")
            .and_then(Value::as_object_mut)
            .is_some_and(|data| {
                let build = data.entry("build").or_insert_with(|| json!({}));
                let Some(build) = build.as_object_mut() else {
                    return add_rate(data.get_mut("playMetrics"), rate);
                };
                build.insert("proxyVersion".into(), json!(env!("CARGO_PKG_VERSION")));
                add_rate(data.get_mut("playMetrics"), rate);
                true
            }),
        "advance_time" => add_rate(reply.pointer_mut("/data/playMetrics"), rate),
        "get_advance" => add_rate(reply.pointer_mut("/data/run/playMetrics"), rate),
        _ => false,
    }
}

fn add_rate(metrics: Option<&mut Value>, rate: Option<u64>) -> bool {
    // Only annotate a live sample. Old runs, menus, and older mod builds have no playMetrics.
    let (Some(metrics), Some(rate)) = (metrics.and_then(Value::as_object_mut), rate) else {
        return false;
    };
    metrics.insert("replyBytesPerGameHour".into(), json!(rate));
    true
}

#[cfg(test)]
mod tests {
    use super::*;

    const START_TICK: u64 = 1_000;
    const HOUR_TICK: u64 = START_TICK + TICKS_PER_HOUR as u64;
    const REPLY_BYTES: u64 = 100;

    #[test]
    fn l1_load_accounting() {
        let mut metrics = PlayMetrics::default();
        let reply = |id, tick| json!({"loadId": id, "tick": tick});
        assert_eq!(metrics.record(&reply("a", HOUR_TICK), REPLY_BYTES), None);
        assert_eq!(metrics.record(&reply("b", START_TICK), REPLY_BYTES), None);
        assert_eq!(
            metrics.record(&reply("a", START_TICK), REPLY_BYTES),
            Some(REPLY_BYTES + REPLY_BYTES)
        );
        assert_eq!(
            metrics.record(&reply("b", HOUR_TICK), REPLY_BYTES),
            Some(REPLY_BYTES + REPLY_BYTES)
        );
        assert_eq!(metrics.record(&json!({"tick": 0}), REPLY_BYTES), None);
    }
}
