# Architecture boundary checker

`check.py` enforces the module boundary rules described in
[`docs/architecture.md`](../../docs/architecture.md). It checks Collector Domain C# imports,
qualified references to Collector and known package namespaces, project-wide global and
recursively imported MSBuild files, plus direct and transitive dependencies of
`HistoryController` and `StatisticsController`.

Run it from the repository root:

```powershell
python -B tools/architecture-boundary-check/check.py --json
python -B tools/architecture-boundary-check/selftest.py
```

Exit code `0` means the inspected scope passed, `1` means a forbidden dependency was
found, and `2` means inspection was incomplete or unreliable. The checker uses only the
Python standard library. It has no inline waiver mechanism. It is a source convention
gate; compilation and review remain responsible for complete language semantics.
