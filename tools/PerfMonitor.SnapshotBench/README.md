# Synthetic snapshot measurements

This development tool measures `SnapshotAssembler` with deterministic generated
100/500/1000-row process arrays. It does not enumerate Windows processes or run
Agent, Desktop, IPC, SQLite, hardware Worker, Broker, or privileged actions. These
results locate allocation and latency in this path; they are not a whole-product
resource baseline or proof of an improvement to a user workload.

Use the repository-pinned SDK and a new artifacts directory for each build and
measurement. Coordinate an exclusive build/test/measurement window when other
agents are using this workspace. Do not overwrite a baseline after changing code.

```powershell
& 'C:\Users\Administrator\AppData\Local\PerfMonitor\dotnet-sdk-10.0.302\dotnet.exe' restore tools/PerfMonitor.SnapshotBench/PerfMonitor.SnapshotBench.csproj --artifacts-path artifacts/wave03-wp01-before-build
& 'C:\Users\Administrator\AppData\Local\PerfMonitor\dotnet-sdk-10.0.302\dotnet.exe' build tools/PerfMonitor.SnapshotBench/PerfMonitor.SnapshotBench.csproj -c Release --no-restore --artifacts-path artifacts/wave03-wp01-before-build
& 'C:\Users\Administrator\AppData\Local\PerfMonitor\dotnet-sdk-10.0.302\dotnet.exe' artifacts/wave03-wp01-before-build/bin/PerfMonitor.SnapshotBench/release/PerfMonitor.SnapshotBench.dll --self-check
& 'C:\Users\Administrator\AppData\Local\PerfMonitor\dotnet-sdk-10.0.302\dotnet.exe' artifacts/wave03-wp01-before-build/bin/PerfMonitor.SnapshotBench/release/PerfMonitor.SnapshotBench.dll --repository D:\GitHub\windows-performance-monitor --output D:\GitHub\windows-performance-monitor\artifacts\wave03-wp01-before-samples --label before --iterations 1000 --warmup 100 --trials 3
```

After building once, run the saved DLL directly to keep builds outside timed
samples. The tool stores every raw duration and allocation in `samples.csv`, plus
per-trial mean allocation, P50/P95/P99, extrema and GC deltas in `summary.json`.
Percentiles use nearest rank with no outlier removal. Timing includes timer and
delegate overhead; `timer-control` reports this without correcting other values.
No forced GC is performed. The fixed synthetic clock intentionally keeps quality
stable; unit tests cover freshness and publication ordering independently.

`read-envelope` reads metadata without accessing `Data`; `read-payload-once`
traverses all rows once, and `read-payload-three` independently obtains and
traverses the process payload three times. `serialize-existing` isolates JSON
writing, and `read-serialize` combines the real read and JSON path.
`watched-projection` exercises the production diagnostic process projection for
two fixed names. Publishing a small CPU update keeps the process group held;
publishing the process result measures replacing the large group. Fixture/result
construction for publication is included in those operations and unchanged
between before/after runs. Each scenario has its own assembler and warmup; trial
order rotates to reduce persistent ordering bias.

Source HEAD, dirty status, relevant source hashes, input hashes, binary hashes,
runtime, OS, architecture, CPU count, clock frequency, UTC and elapsed time are
recorded. The tool fails if relevant source or HEAD changes during measurement.
Keep other product tests and performance runs stopped during a sample. User or
OS activity is not controlled and short serial samples do not establish
statistical significance or real-workload causation.
