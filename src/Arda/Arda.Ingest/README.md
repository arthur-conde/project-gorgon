# Arda.Ingest

L0/L1 span-based log ingestion engine for Project Gorgon game logs. Reads raw log files, classifies lines, and emits a typed `IAsyncEnumerable<LogLine>` stream for consumption by the world simulation (L2–L4).

## Architecture

```
Arda.Ingest/
  Internal/           Internal value types (not visible outside assembly)
  Tailer/             L0: byte→char mechanical tailer
  Clock/              Timestamp grammar parsers (per-source)
  Coordinator/        Source coordinators implementing ILogLineSource
  Classification/     L1: line classification and promotion/discard
```

### Pipeline Layers

| Layer | Component | Responsibility |
|-------|-----------|---------------|
| L0 | `LogSourceTailer` | File I/O, byte-offset tracking, UTF-8 decoding to pooled `char[]`, residual buffering, rotation detection |
| L0 | `ILogSourceClock` | Timestamp prefix parsing on `ReadOnlySpan<char>`, returns `(DateTimeOffset, int consumedLength)` |
| L1 | `LineClassifier` | Span-based prefix check; promotes timestamped lines and known system patterns; discards engine noise at zero allocation cost |
| L1 | `PlayerLogSource` / `ChatLogSource` | Multi-file orchestration, IsReplay stamping, `ILogLineSource` implementation |

### Span-Based Processing Model

The pipeline avoids per-line string allocation for discarded lines:

1. **Tailer** reads bytes into `ArrayPool<byte>`, decodes to `ArrayPool<char>`, computes line boundaries as `(start, length)` pairs.
2. **Clock** inspects `ReadOnlySpan<char>` to extract timestamps and report prefix length — no string created.
3. **Classifier** checks prefix on span. Non-timestamped noise is discarded without any heap allocation.
4. Only lines that pass classification allocate a `string` (via `span.ToString()`) for the `LogLine.Log` payload.

### Multi-File Strategy

**Player logs:** `Player-prev.log` is read to completion first (all lines stamped `IsReplay = true`), then `Player.log` is tailed live.

**Chat logs:** Files matching `Chat-yy-mm-dd.log` are enumerated in lexicographic (chronological) order. Historical files are read to completion; the most recent file is tailed live with midnight-rollover detection for new files.

### IsReplay Semantics

`IsReplay` starts `true` for all historical data. When the coordinator observes an empty read at the tail of the live file (all existing content has been consumed), it flips to `false` and never reverts. Downstream consumers use this to suppress alarms during replay.

## Public Surface (Arda.Abstractions)

Consumers depend only on `Arda.Abstractions`:

- `ILogLineSource` — async enumerable of `LogLine` values
- `LogLine` — the stripped game-event text + metadata
- `LogLineMetadata` — `Timestamp`, `ReadOn`, `IsReplay`

No internal types (`TailedBatch`, `LogFileOrigin`, `ClockResult`) are visible outside this assembly.

## Design References

- [docs/design/arda/log-source.md](../../../docs/design/arda/log-source.md) — L0/L1 requirements, processing model, and rationale
- [docs/design/arda/dispatch-and-composition.md](../../../docs/design/arda/dispatch-and-composition.md) — full L0–L4 layer model
- [docs/design/arda/simulator-design.md](../../../docs/design/arda/simulator-design.md) — downstream frame structure
