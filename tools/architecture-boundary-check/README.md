# Architecture boundary checker

`check.py` enforces the module boundary rules described in
[`docs/architecture.md`](../../docs/architecture.md). It checks Collector Domain C# imports,
qualified references to Collector and known package namespaces, references to types declared
outside Domain in a namespace that encloses Domain's own (`MentorRecorder.Collector`,
`MentorRecorder`, the global namespace), which Domain code can name with neither a using nor a
qualifier (rule `domain-root-namespace-reference`), project-wide global and
recursively imported MSBuild files, plus direct and transitive dependencies of
`HistoryController` and `StatisticsController`.

For `domain-root-namespace-reference`, a type is any namespace-level class, struct, interface,
enum, record or delegate, and an attribute class is also matched by its short name in an
attribute list (`[RootMarker]` for `RootMarkerAttribute`). A Domain type of the same name hides
an enclosing one only when it is declared directly in the referencing file's namespace or in a
namespace enclosing it; a type nested in another type, or declared in a sibling Domain
namespace, hides nothing. A type declared in a `MentorRecorder.Collector.Domain` namespace by a
file outside `src/Collector/Domain/` is reported as `domain-namespace-outside-domain`: Domain
code could name it without a using, yet its own dependencies would never be checked.

A C# file whose braces the checker cannot follow - a `}` that closes nothing, a block never
closed, or an `#if` / `#elif` / `#else` branch that opens or closes a block it does not also
close or open itself - ends the run with exit code `2`.

Run it from the repository root:

```powershell
python -B tools/architecture-boundary-check/check.py --json
python -B tools/architecture-boundary-check/selftest.py
```

Exit code `0` means the inspected scope passed, `1` means a forbidden dependency was
found, and `2` means inspection was incomplete or unreliable. The checker uses only the
Python standard library. It has no inline waiver mechanism. It is a source convention
gate; compilation and review remain responsible for complete language semantics.
