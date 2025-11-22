# dotnet performance guidelines

## Core Rules
- R1 Educate Yourself: understand stack/heap, allocator/collector roles before tuning.
- R2 Sequential over Random Access: design data structures and iterations to favor cache-line friendly, sequential scans.
- R3 Improve Locality: group related data, avoid pointer chasing; prefer arrays/structs with tight packing.
- R4 Advanced Possibilities: leverage hardware intrinsics, large pages, data-oriented design when justified by profiles.
- R5 Measure GC Early: baseline counters/traces in dev/CI to catch regressions before prod.
- R6 Measure Your Program: profile real workloads; synthetic benches can mislead allocation/GC behavior.
- R7 Don’t Assume (No) Leak: prove or disprove with growth curves and heap diffs; avoid gut feelings.
- R8 Consider Struct: for small immutable types in hot paths; avoid when copying would be expensive.
- R9 String Interning: intern hot literals to cut duplicates; beware unbounded interning of dynamic data.
- R10 Avoid Boxing: use generics/value types; watch for hidden boxing in interfaces, tuples, async state machines.
- R11 Monitor Generation Sizes: growth of gen2 indicates retention; gen0 churn is typically fine.
- R12 Avoid Unnecessary Heap References: clear caches, detach events, limit static singletons.
- R13 Monitor Segment Usage: watch number/size of GC segments/regions; many segments imply fragmentation or growth pressure.
- R14 Avoid Allocations in Hot Paths: refactor tight loops and high QPS endpoints to be allocation-free.
- R15 Avoid Excessive UOH/LOH: keep objects <85K; pool big buffers; avoid accidental huge strings/arrays.
- R16 Allocate on Stack When Appropriate: use `stackalloc`/ref structs for small temporaries; keep size modest.
- R17 Watch Runtime Suspensions: long stop-the-world pauses often stem from pinning, huge gen2, or heavy JIT/EE work.
- R18 Avoid Mid-Life Crisis: prevent medium-lived objects from promoting by reducing lifetimes or pooling.
- R19 Avoid Old-Gen & LOH Fragmentation: reuse large blocks, prefer contiguous layouts, compact only with care.
- R20 Avoid Explicit GC: replace with load shedding, throttling, or `TryStartNoGCRegion` for short windows.
- R21 Avoid Memory Leaks: routinely check roots; enforce cache limits; audit events/timers.
- R22 Use Pinning Carefully: pin minimal size/duration; batch external calls; avoid pinning LOH.
- R23 Choose GC Mode Consciously: match server/workstation and background/concurrent to workload and deployment.
- R24 Remember Latency Modes: set per operation where needed; revert after critical section.
- R25 Avoid Finalizers: rely on SafeHandle + IDisposable; finalizers add latency and unpredictability.
- R26 Prefer Explicit Cleanup: make disposal easy/automatic with `using`/`await using`; cover all paths.

## Must / Should / Could
- **Must**
  - Instrument allocations & GC from the start: counters (+alloc/sec, % time in GC, gen size), EventPipe traces, PerfView when deeper.
  - Keep hot-path allocations near zero; fix boxing, closures, hidden string formatting, async Task allocations.
  - Avoid explicit `GC.Collect()`; set hard memory limits only with measurement.
  - Use `IDisposable`/`IAsyncDisposable` with SafeHandle for native resources; ensure deterministic cleanup; avoid finalizers unless absolutely required.
  - Keep pinning rare/short-lived; prefer `Memory<T>`/`Span<T>`/`GCHandleType.Pinned` scoped.
  - Monitor LOH/UOH (pinned/large) usage; prevent fragmentation with pooling/slicing.
  - For services, pick GC flavor & latency mode based on workload (Server + SustainedLowLatency for steady high-throughput; Workstation + Interactive for UI).
  - For very large files, prefer memory-mapped I/O and parse with spans to avoid copies. citeturn2search0
- **Should**
  - Prefer structs for tiny, immutable, copy-cheap types used densely; otherwise stay with classes.
  - Use pooling: `ArrayPool<T>`, `ObjectPool<T>`, reusable buffers/streams (RecyclableMemoryStream) for bursts.
  - Use `ValueTask` for frequently-returned, already-completed async operations.
  - Use `stackalloc`/`Span<T>` for small temporary buffers; keep below a few KB to avoid guard-page hits.
  - Normalize string handling: intern hot constants, use `StringBuilder` or interpolated-string handlers to cut temps, choose UTF-8 where possible.
  - Keep data contiguous: arrays over linked lists; prefer SoA layouts when data-oriented.
  - Gate large allocations; move to streaming/chunking or mmap when possible.
- **Could**
  - Enable Large Pages for fixed high-load servers after benchmarking.
  - Use frozen segments/NonGC heap for read-only, long-lived data sets.
  - Adjust heap counts or thread affinity only after evidence they help.
  - Employ `no-GC region` for short, latency-critical bursts; measure post-burst collection cost.

## Optimization Tactics by Area
- **Allocation**: eliminate boxing (generics, `in` params), cache delegates, reuse closures, avoid LINQ on hot paths, prefer `ValueTuple`, use `MemoryPool`/`ArrayPool`, guard against implicit string/array copies, consolidate small allocations.
- **Data layout**: pack structs (beware of padding), align for cache, group hot fields together, prefer sequential traversal; move cold fields to separate types.
- **Strings**: use `string.Create`, interpolated-string handlers, pooled `StringBuilder`; avoid `string.Format` and `+` in loops; encode once, reuse bytes.
- **Strings**: use `string.Create`, interpolated-string handlers, pooled `StringBuilder`; avoid `string.Format` and `+` in loops; encode once, reuse bytes; for large text parsing prefer UTF-8 spans and delay UTF-16 decoding until output. citeturn2search0
- **Collections**: preset capacities; use `Span<T>`/`Memory<T>` to slice; prefer arrays for fixed-size, `ValueListBuilder<T>` for transient builds; avoid `ToArray()`/`ToList()` in hot paths; tune hash table load factor (<25%) and copy keys into a contiguous buffer for cache-friendly lookups. citeturn2search0
- **Collections**: in hot-path metadata plumbing (gRPC/HTTP headers), avoid LINQ pipelines or per-call `string.Join` over hash sets; walk the collection once, reuse cached header strings, and favor builders over concatenation to keep allocations near-zero and stay Native AOT friendly.
- **Collections**: when header keys can repeat (e.g., HTTP/gRPC), use indexed loops with dictionary indexers (`builder[key] = value`) instead of `Add` to avoid exceptions and branchy error paths; prefer first/last-wins semantics decided up-front to keep hot paths predictable.
- **Async/Tasks**: cache completed tasks, use `ValueTask`, avoid `async void`, configure awaits to reduce continuations when safe, prefer synchronous fast paths.
- **Data access (ASP.NET)**: call data APIs asynchronously; fetch only needed columns; favor no-tracking queries for read-only paths; cache hot data (MemoryCache/Distributed cache) with sensible TTLs; minimize round-trips and avoid N+1 queries; pool DbContexts when scaling. citeturn0search7
- **LOH/POH**: keep objects < 85K when possible, reuse large buffers, slice via `Memory<T>`/`Span<T>`, avoid pinning LOH blocks.
- **Pinning**: pin short and batch unmanaged calls; copy to stack-buffer for tiny payloads; prefer `MemoryHandle`/`fixed` inside minimal scope.
- **GC tuning**: pick workstation/server & concurrency mode; set latency mode per operation (Interactive, LowLatency, SustainedLowLatency, Batch, NoGCRegion); adjust hard limits/heap count only with traces.
- **Lifetime**: dispose on all control paths (including exceptions); use `using`/`await using`; weak references for caches; unsubscribe events; prefer safe handles over finalizers.
- **Diagnostics**: capture traces under load; compare snapshots (gcdump/dotnet-dump) across time to find growth; use allocation flame graphs; inspect roots for leaks (events, static caches, pinned handles).
- **File/IO hot paths**: memory-map very large inputs to avoid buffer copies; when vectorizing parsers, pad the tail copy so reads past the end are safe. citeturn2search0
- **File/IO hot paths**: when streaming JSON or newline-delimited payloads, write/read directly to `FileStream` with source-generated `JsonSerializerContext` and reuse transient buffers via `ArrayPool<T>` to avoid intermediate strings and keep Gen0 churn low.

## Case Study: 1BRC (.NET on Linux) Takeaways
- Memory-map big inputs to avoid copying; favor `MemoryMappedFile` on .NET or `mmap` via interop; copy tail padding to allow vector reads past end safely. citeturn2search0
- Represent keys as UTF-8 spans (no UTF-16 or allocations); keep structs like `Utf8Span { byte* ptr; int len; }` and delay decoding until final output. citeturn2search0
- Store keys contiguously (copy slices into a dedicated buffer) to improve cache locality of dictionary lookups; keep load factor low (~25%) to cut collisions. citeturn2search0
- Parse temperatures as scaled integers (-999..999) instead of doubles; branchless min/max updates reduce mispredictions in hot loops. citeturn2search0
- Use lightweight hash tuned to data (length + first 4 bytes, FNV-style tweaks); shave collisions without heavy compute. citeturn2search0
- Split each thread’s chunk into 2–3 subchunks to keep a single core’s pipeline busy (single-core “parallelism”); helps on non-HT CPUs. citeturn2search0
- Inline tiny helpers and keep rare paths out of line; prefer vectorized `IndexOf`/`Equals` for short keys; branchless first-vector compare. citeturn2search0
- Fast modulo: let JIT handle const divisors or use proven FastMod; avoid widening int→long in tight loops. citeturn2search0
- Example patterns:
  ```csharp
  // UTF8 key without allocations
  unsafe readonly struct Utf8Span
  {
      public readonly byte* Ptr;
      public readonly int Length;
      public bool Equals(Utf8Span other)
          => Length == other.Length && Vector128.Equals(Ptr, other.Ptr, Length);
      public int GetHashCode()
          => Length > 3 ? (Length * 820243) ^ *(int*)Ptr : *(byte*)Ptr;
  }

  // Branchless min/max with scaled int temperature
  static void Update(ref int min, ref int max, int t)
  {
      min = Math.Min(min, t);
      max = Math.Max(max, t);
  }
  ```
## Diagnostic Playbooks (Scenarios)
- Memory footprint & growth: Overall size; Native memory growth; Virtual memory growth; Assembly/plugin unload problems; Usage too big.
- Generation/segment health: Generation sizes over time; nopCommerce leak; LOH waste.
- Allocation spikes: Out of memory; Investigating allocations; Azure Functions burst allocations.
- GC behavior: GC usage; Allocation budget; Explicit GC calls; GC suspension times; Condemned generation analysis; Provisional mode shifts.
- Mark roots & leaks: nopCommerce leak (roots); Popular roots; Generational-aware analysis.
- Plan/pinning issues: Invalid structures in dump; Pinning investigation.
- Fragmentation: LOH fragmentation.
- Configuration: Checking GC settings; Benchmarking GC modes.
- Lifetime leaks: Finalization leak; Event leak.

## Tooling Shortlist & When to Use
- `dotnet-counters` (live): allocation rate, GC %, gen sizes; use first.
- `dotnet-trace` / EventPipe: GC pauses, reasons, allocation ticks; remote-trigger GC; combine with TraceEvent/PerfView for analysis.
- `dotnet-gcdump` / `dotnet-dump`: heap snapshots for leak/fragmentation comparisons.
- `dotnet-gcmon`: induced GC/suspension investigation.
- PerfView: deep GC/CPU, heap dumps, LOH/POH fragmentation.
- `BenchmarkDotNet`: micro/mezzo benchmarks with memory diagnosers.
- WinDbg/SOS/ClrMD: low-level dumps, root/pinning analysis, automation.
- Authors’ helpers: gcstats, fullgc, counters UI; use for quick visuals.

## Configuration Knobs (use with measurements)
- GC flavor: Workstation vs Server; concurrent/background on/off.
- Latency modes: Interactive, LowLatency, SustainedLowLatency, Batch, NoGCRegion.
- Limits: hard memory limit, heap count, GC thread affinity, memory load threshold.
- Features: large pages, conservative mode, dynamic adaptation, provisional mode, frozen segments/NonGC heap, region-based GC (.NET 7+).
- Heaps: LOH/POH thresholds, pinned object heap APIs, UOH considerations.

## Patterns & Anti-Patterns
- Prefer: pooling, spans, value types for tiny data, streaming large payloads, deterministic disposal, weak-event patterns.
- Prefer: pooling, spans, value types for tiny data, streaming large payloads, deterministic disposal, weak-event patterns.
- Prefer (ASP.NET Core): async end-to-end pipelines, streaming bodies, pagination for large sets, HttpClient reuse via IHttpClientFactory, small fast middleware. citeturn0search7
- Avoid: explicit `GC.Collect()`, long-lived pins, unbounded caches/statics, per-call `new` in hot paths, `string.Format` in loops, LINQ in tight loops, finalizers without SafeHandle, large temporary arrays, frequent `ToArray()`, high load-factor hash tables on hot paths, eager UTF-16 decoding of UTF-8 input streams, sync-over-async on `HttpRequest`/`HttpResponse`, per-request `HttpClient`, reading entire request bodies into memory, storing/using `HttpContext` across threads or after completion, exceptions for normal flow. citeturn0search7

## Use-Case Presets
- **High-throughput services**: Server GC + SustainedLowLatency; warm buffers via pools; minimize pins; monitor allocation rate & gen2/LOH sizes.
- **Low-latency bursts (trading/games)**: NoGCRegion or LowLatency around bursts; cap burst duration; preallocate buffers; reconcile after burst.
- **Background/batch jobs**: Batch latency mode; allow larger segments; optimize throughput over pause; fewer pools needed.
- **Plugins/add-ins**: Load via `AssemblyLoadContext` collectible; isolate static caches; track assembly unloadability; prevent event leaks.
- **Azure Functions / serverless**: Cold-start focus—avoid heavy JIT/alloc at first hit; pool buffers; prefer ValueTask; keep LOH minimal.
- **Interop-heavy**: Use SafeHandle; pin narrowly; marshal spans; avoid double buffering; consider `fixed`/stack buffers for tiny structs.

- **ASP.NET Core hot paths**: make request pipeline async end-to-end; avoid sync over async and blocking calls in middleware/controllers; prefer `ReadFormAsync`/async body reads; offload long-running work to background services/queues; paginate large responses and stream results; reuse HTTP connections via `IHttpClientFactory`; keep middleware light and early exits fast; minimize exceptions in normal flow; keep common responses compressed/minified; upgrade to latest ASP.NET Core for GC/runtime perf. citeturn0search7
  ```csharp
  // IHttpClientFactory registration
  builder.Services.AddHttpClient("api", c => c.BaseAddress = new Uri("https://api"));

  // Streaming response example
  [HttpGet("/data")]
  public async Task Stream(CancellationToken ct)
  {
      Response.ContentType = "application/json";
      await foreach (var batch in _svc.GetBatches(ct))
          await JsonSerializer.SerializeAsync(Response.Body, batch, cancellationToken: ct);
  }
  ```

## Metrics to Watch (alert thresholds are workload-specific)
- Allocation rate (MB/s), GC per second, % time in GC.
- Gen0/1/2 sizes and trends; LOH/POH size and free space; pinned bytes; fragmentation %.
- Pause time p50/p95; induced GCs; suspension reasons.
- Live object count of key types; top roots (events, static caches, timers, thread-locals).

## PR/Review Checklist
- Hot paths allocation-free? (boxing, closures, LINQ, string concat, async state machines)
- Disposal guaranteed on all paths (`using`/`await using`)? Any finalizers left?
- Pools sized and reused? Caps on cache size/entry TTL?
- Large buffers reused or chunked? LOH touched intentionally?
- Pinning minimized and scoped? Any long-lived pins on LOH objects?
- GC mode/latency set consciously for workload? Config discoverable via settings?
- Telemetry: counters/traces enabled and documented? Dashboards/alerts defined?

## Diagnostic Playbooks (expanded)
- **How Big Is My Program in Memory?**
  - Check process working set, private bytes, virtual size; correlate managed vs native using `dotnet-counters` (GC heap size, committed bytes) + OS tools (Task Manager/`ps`/`smem`).
  - Capture a `dotnet-dump` or `gcdump`; compare managed size to overall footprint to decide whether leak is managed, native, or memory-mapped.
  - Inspect module list and loaded assemblies; large native images or many DLLs may dominate.
  - Commands:
    ```bash
    dotnet-counters monitor System.Runtime --process-id <pid>
    dotnet-gcdump collect -p <pid> -o heap.gcdump
    dotnet-gcdump analyze heap.gcdump
    dotnet-dump collect -p <pid> -o full.dmp
    ```
- **Native Memory Keeps Growing**
  - Track native heaps via OS (`vmmap`/`procdump` on Windows, `pmap`/`smem` on Linux); watch for steady commit growth.
  - In dump, look at `!eeheap -gc` (Windows) or SOS `DumpHeap` to see if managed heap is stable; if so, suspect native owners: P/Invoke, COM, `Marshal.AllocHGlobal`, native libraries.
  - Add counters for `GC.GetGCMemoryInfo().TotalCommittedBytes` vs process commit to monitor gap.
  - Commands:
    ```bash
    pmap <pid> | sort -nrk2 | head
    dotnet-dump analyze full.dmp -c "eeheap -gc"
    ```
- **Virtual Memory Keeps Growing**
  - Use `vmmap`/`pmap` to list VADs/VMAs; look for many mappings (files, shared memory, JIT code, assembly load contexts).
  - Check for memory-mapped files, `MemoryMappedFile`, huge stacks, or many threads; trim threads and stacks, consolidate maps.
  - Commands:
    ```bash
    vmmap <pid>   # Windows Sysinternals
    pmap -X <pid> | head
    ```
- **Managed Memory Grows with Assembly Count**
  - Verify collectible `AssemblyLoadContext`; ensure PlugIns are loaded as collectible and all references cleared.
  - After unload attempt, force `GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();` once and check `AssemblyLoadContext.IsAlive`.
  - Hunt roots that keep assemblies alive (static caches, events, singletons referencing types).
  - Sample unload helper:
    ```csharp
    // after unloading plugins
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    Debug.WriteLine($"Context alive: {alc.IsAlive}");
    ```
- **Unable to Unload Plugins**
  - Scan for static/global references into plugin types; check for threads/timers/Task continuations from plugin that keep the context alive.
  - Dispose plugin services; cancel CTS; remove event handlers; ensure no unmanaged callbacks are pinned.
  - Commands:
    ```bash
    dotnet-dump analyze full.dmp -c "clrthreads"
    dotnet-dump analyze full.dmp -c "dumpheap -stat" -c "gcroot <addr>"
    ```
- **Memory Usage Too Big**
  - Establish baseline: allocation rate, live size, LOH share; filter top types in dump by size.
  - Look for large arrays/strings; consider compression/streaming/pooling; evaluate data structure choices (array vs list vs dictionary load factor).
  - Commands:
    ```bash
    dotnet-counters monitor System.Runtime --process-id <pid>
    dotnet-gcdump collect -p <pid> -o heap.gcdump
    dotnet-gcdump analyze heap.gcdump --type | head
    ```
- **Generation Sizes Over Time**
  - Continuously log gen0/1/2 sizes and promoted bytes; alert on gen2 growth slope.
  - Trigger dumps when gen2 crosses threshold; inspect long-lived roots.
  - Commands:
    ```bash
    dotnet-counters monitor System.Runtime --counters gc-heap-size,gen-0-size,gen-1-size,gen-2-size --process-id <pid>
    ```
- **nopCommerce Leak**
  - Classic web leak: cache/items pinned by lifetimes; analyze roots to HTTP/DI singletons.
  - Validate caching policy, eviction, and event subscriptions in web stack.
  - Commands:
    ```bash
    dotnet-gcdump collect -p <pid> -o leak.gcdump
    dotnet-gcdump analyze leak.gcdump --type | head
    ```
- **LOH Waste**
  - Compute LOH free vs used; detect many partially used chunks.
  - Fix by pooling/slicing large buffers, chunking payloads <85K, or enabling compaction when safe.
  - Commands:
    ```bash
    dotnet-dump analyze full.dmp -c "dumpheap -heapstat -largerthan 85000"
    ```
- **Out of Memory**
  - Distinguish managed OOM (allocation failure) vs address-space exhaustion.
  - Capture dump on OOM; check large arrays, pinned objects, or fragmented LOH; reduce allocation peaks.
  - Commands:
    ```bash
    dotnet-gcdump collect -p <pid> -o oom.gcdump
    dotnet-gcdump analyze oom.gcdump --type | head
    ```
- **Investigating Allocations**
  - Use `dotnet-trace` AllocationTick or PerfView allocation stack; rank call stacks by bytes/sec.
  - Fix high-traffic stacks: remove LINQ, cache delegates, reuse buffers.
  - Commands:
    ```bash
    dotnet-trace collect --providers Microsoft-Windows-DotNETRuntime:4c14fccbd:0x1c00008001:5 --process-id <pid> -o alloc.nettrace
    dotnet-trace ps
    ```
- **Azure Functions**
  - Target cold-start and per-trigger allocations; pre-JIT, cache clients (HttpClient, SDKs), reuse buffers.
  - Keep function outputs under LOH threshold; prefer `ValueTask` and pooled serializers.
  - Code sketch:
    ```csharp
    static readonly HttpClient client = new();
    [Function("Foo")]
    public static ValueTask<HttpResponseData> Run(...) => HandleAsync(...);
    ```
- **GC Usage**
  - Read GC reasons, pause durations, and allocation budget from EventPipe; chart pauses vs throughput.
  - Identify induced GCs; reduce forced collections and large ephemeral bursts.
  - Commands:
    ```bash
    dotnet-trace collect --profiles gc-cpu --process-id <pid> -o gc.nettrace
    dotnet-trace report gc --format csv gc.nettrace | head
    ```
- **Allocation Budget**
  - Inspect `GC.GetGCMemoryInfo().GenerationInfo[n].SizeBeforeBytes` and budget events to understand trigger thresholds.
  - Smooth bursts, reduce pinning that shrinks budget.
  - Code snippet:
    ```csharp
    var info = GC.GetGCMemoryInfo();
    foreach (var g in info.GenerationInfo)
        Console.WriteLine($"Gen{g.Generation}: budget={g.MemoryLoadBytes}");
    ```
- **Explicit GC Calls**
  - Search code/config for `GC.Collect`; instrument occurrences; remove or guard behind diagnostics.
  - Replace with proper backpressure (queues, rate limits) or `GC.TryStartNoGCRegion` when justified.
  - Commands:
    ```bash
    rg "GC\.Collect" src/
    ```
- **GC Suspension Times**
  - Measure `Suspension Time` events; correlate with stop-the-world phases.
  - Reduce large pinned regions, lower number of threads touching heap, avoid mega object graphs during suspend.
  - Commands:
    ```bash
    dotnet-trace collect --providers Microsoft-Windows-DotNETRuntime:4c14fccbd:0x1c0004000:5 --process-id <pid> -o suspend.nettrace
    dotnet-trace report counters suspend.nettrace | head
    ```
- **Condemned Generation Analysis**
  - Inspect which generation gets condemned and why; high gen1/2 promotions indicate tenured churn.
  - Cut mid-life objects: pool, cache appropriately, shrink per-request state.
  - Commands:
    ```bash
    dotnet-trace report gc gc.nettrace --show-gc-reasons | head
    ```
- **Provisional Mode**
  - When heap appears saturated, GC may switch modes; monitor mode changes.
  - Lower allocation rate, free LOH, or raise hard limit only after measuring impact.
  - Commands:
    ```bash
    dotnet-trace report gc gc.nettrace --show-heap-sizes | head
    ```
- **nopCommerce Leak (Roots)**
  - Compare successive dumps; identify growth types; trace roots (events/singletons) holding them.
  - Remove lingering event handlers and cache entries.
  - Commands:
    ```bash
    dotnet-gcdump analyze leak1.gcdump --type > snap1.txt
    dotnet-gcdump analyze leak2.gcdump --type > snap2.txt
    diff snap1.txt snap2.txt | head
    ```
- **Popular Roots**
  - Rank roots by fan-out; focus on few roots retaining the bulk of objects.
  - Refactor static caches; introduce weak references or size/TTL policies.
  - Commands (PerfView):
    ```bash
    PerfViewGui /HeapOnly /HeapSortByInclusiveSize /MaxDumpCount=200 leak.etl.zip
    ```
- **Generational-Aware Analysis**
  - Compare object age distribution; gen2 overweight points to retention.
  - Target objects surviving >2 collections; reduce longevity or pool.
  - Commands:
    ```bash
    dotnet-gcdump analyze heap.gcdump --gen-stats
    ```
- **Invalid Structures in Dump**
  - Corrupt dump may indicate overwrite/uninitialized memory; check unsafe/native interop.
  - Validate custom allocators; add guards; enable heap verification.
  - Commands:
    ```bash
    dotnet-dump analyze full.dmp -c "verifyheap"
    ```
- **Pinning Investigation**
  - List pinned objects (`!gcroot -all`, PerfView pinned statistics); map to call stacks.
  - Shorten pin lifetimes; copy to stack/temporary buffers; avoid pinning LOH.
  - Commands:
    ```bash
    dotnet-dump analyze full.dmp -c "dumpheap -pin" -c "gcroot <addr>"
    PerfViewGui /HeapOnly /ShowPinned
    ```
- **LOH Fragmentation**
  - Examine free list vs used; detect many gaps; measure compaction cost.
  - Pool big buffers; pack payloads; compact LOH if downtime allows.
  - Commands:
    ```bash
    dotnet-dump analyze full.dmp -c "dumpheap -stat -heapstat -largerthan 85000"
    ```
- **Checking GC Settings**
  - Dump runtime config: `GCSettings.IsServerGC`, `LatencyMode`, heap counts, hard limit.
  - Ensure settings align with workload; document defaults vs overrides.
  - Commands:
    ```csharp
    Console.WriteLine($"Server: {GCSettings.IsServerGC}, Mode: {GCSettings.LatencyMode}");
    var info = GC.GetGCMemoryInfo();
    Console.WriteLine($"Heap count: {info.HeapCount}, Hard limit: {info.TotalAvailableMemoryBytes}");
    ```
- **Benchmarking GC Modes**
  - Use BenchmarkDotNet or dedicated harness; vary server/workstation, concurrency, latency mode.
  - Measure both throughput and p95/p99 pause time; keep environment fixed.
  - Snippet:
    ```csharp
    [SimpleJob(RuntimeMoniker.Net10_0, Server = true)]
    [SimpleJob(RuntimeMoniker.Net10_0, Server = false)]
    public class GcModeBench { /* benchmark methods */ }
    ```
- **Finalization Leak**
  - Dump finalization queue length; long queue = leak.
  - Move cleanup to IDisposable; minimize finalizers; ensure `Dispose` is called.
  - Commands:
    ```bash
    dotnet-dump analyze full.dmp -c "finalizequeue"
    ```
- **Event Leak**
  - Trace event subscriptions; stale handlers keep publishers alive.
  - Implement weak-event pattern or unsubscribe in Dispose; watch for anonymous delegates.
  - Code pattern:
    ```csharp
    public void Dispose()
    {
        publisher.SomeEvent -= OnEvent;
        cts?.Cancel();
    }
    ```
- **Usage Scenarios**
  - Thread-safe counter implementations: compare interlocked vs TLS vs locks for memory/thread-local costs; choose per contention and allocation profile.
  - Code sketch:
    ```csharp
    // Low contention
    private long _counter;
    public void Increment() => Interlocked.Increment(ref _counter);
    ```
