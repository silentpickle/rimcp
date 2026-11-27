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
    calls: u64,
}

pub struct Sample {
    calls: u64,
    reply_bytes: u64,
    rate: Option<u64>,
}

impl PlayMetrics {
    // Use both tick bounds so overlapping calls may finish in either order.
    pub fn record(&mut self, reply: &Value, bytes: u64) -> Option<Sample> {
        let id = reply.get("loadId")?.as_str()?;
        let tick = reply.get("tick")?.as_u64()?;
        let mut load = self.take(id).unwrap_or_else(|| LoadMetrics {
            id: id.into(),
            first_tick: tick,
            last_tick: tick,
            reply_bytes: 0,
            calls: 0,
        });
        load.first_tick = load.first_tick.min(tick);
        load.last_tick = load.last_tick.max(tick);
        load.reply_bytes = load.reply_bytes.saturating_add(bytes);
        load.calls = load.calls.saturating_add(1);
        let ticks = load.last_tick - load.first_tick;
        let rate = (ticks > 0)
            .then(|| (load.reply_bytes as f64 * TICKS_PER_HOUR / ticks as f64).round() as u64);
        let sample = Sample {
            calls: load.calls,
            reply_bytes: load.reply_bytes,
            rate,
        };
        self.loads.push_front(load);
        self.loads.truncate(RETAINED_LOADS);
        Some(sample)
    }

    fn take(&mut self, id: &str) -> Option<LoadMetrics> {
        let index = self.loads.iter().position(|load| load.id == id)?;
        self.loads.remove(index)
    }
}

pub fn annotate(reply: &mut Value, name: &str, sample: Option<Sample>, role: Option<&str>) -> bool {
    match name {
        "get_game_context" => reply
            .get_mut("data")
            .and_then(Value::as_object_mut)
            .is_some_and(|data| {
                let build = data.entry("build").or_insert_with(|| json!({}));
                let Some(build) = build.as_object_mut() else {
                    return add_sample(data.get_mut("playMetrics"), sample, role);
                };
                build.insert("proxyVersion".into(), json!(env!("CARGO_PKG_VERSION")));
                add_sample(data.get_mut("playMetrics"), sample, role);
                true
            }),
        "advance_time" => add_sample(reply.pointer_mut("/data/playMetrics"), sample, role),
        "get_advance" => add_sample(reply.pointer_mut("/data/run/playMetrics"), sample, role),
        _ => false,
    }
}

fn add_sample(metrics: Option<&mut Value>, sample: Option<Sample>, role: Option<&str>) -> bool {
    // Only annotate a live sample. Old runs, menus, and older mod builds have no playMetrics.
    let (Some(metrics), Some(sample)) = (metrics.and_then(Value::as_object_mut), sample) else {
        return false;
    };
    if let Some(rate) = sample.rate {
        metrics.insert("replyBytesPerGameHour".into(), json!(rate));
    }
    if let Some(role) = role {
        metrics.insert(
            "proxyRole".into(),
            json!({
                "role": role, "calls": sample.calls, "replyBytes": sample.reply_bytes
            }),
        );
    }
    sample.rate.is_some() || role.is_some()
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
        assert_eq!(
            metrics
                .record(&reply("a", HOUR_TICK), REPLY_BYTES)
                .unwrap()
                .rate,
            None
        );
        assert_eq!(
            metrics
                .record(&reply("b", START_TICK), REPLY_BYTES)
                .unwrap()
                .rate,
            None
        );
        assert_eq!(
            metrics
                .record(&reply("a", START_TICK), REPLY_BYTES)
                .unwrap()
                .rate,
            Some(REPLY_BYTES + REPLY_BYTES)
        );
        assert_eq!(
            metrics
                .record(&reply("b", HOUR_TICK), REPLY_BYTES)
                .unwrap()
                .rate,
            Some(REPLY_BYTES + REPLY_BYTES)
        );
        assert!(metrics.record(&json!({"tick": 0}), REPLY_BYTES).is_none());
    }

    #[test]
    fn role_totals_are_available_before_time_advances() {
        let mut metrics = PlayMetrics::default();
        let mut context = json!({"loadId": "a", "tick": START_TICK, "data": {"playMetrics": {}}});
        metrics.record(&context, REPLY_BYTES);
        let sample = metrics.record(&context, REPLY_BYTES);
        assert!(annotate(
            &mut context,
            "get_game_context",
            sample,
            Some("scribe")
        ));
        assert_eq!(
            context["data"]["playMetrics"],
            json!({
                "proxyRole": {"role": "scribe", "calls": 2, "replyBytes": 200}
            })
        );
    }
}
