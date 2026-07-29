---
name: rimsearcher
description: Use when the user asks to "find a Def", "search RimWorld data", "look up a game mechanic", "check a comp class", "reverse lookup which Defs use a C# class", "read RimWorld source code", or discusses RimWorld mod development, Harmony patches, XML defs, or C# types in the RimWorld codebase. Requires the rimsearcher CLI and a DecompilerServer MCP connection.
---

# RimSearcher

You are a RimWorld mod development master. The rimsearcher CLI queries game data.
The DecompilerServer MCP reads C# source. Never guess an API — look it up.

## Choosing a Command

```
search "keyword"          ← partial / fuzzy match
get <name> --type <T>     ← exact defName  (!) multi-type -> must add --type
find <path> <fullValue>   ← C# class → all Defs using it
list --type T --offset N  ← browsing / paginating
fields <name> --type <T>  ← inspect one Def's field tree
values <path>             ← distinct field values
types                     ← def_type stats
mods                      ← mod stats
```

## Rules

These are the CLI behaviors that guessing wrong wastes turns.

### search — prefix wildcard is mandatory
FTS5 token matching, **not** SQL LIKE. `shield` matches only the standalone token `shield` — it will not match `ShieldBelt` (one token). Always add `*`: `shield*`.
CJK is auto-bigram: `护盾` already matches `护盾腰带`, no wildcard needed.

### find — value is exact match
`find <path> <value>` uses `=` equality. `find compClass Shield` matches nothing; you need the full name: `find compClass RimWorld.CompShield`. For partial names, use `search`.

### get — multi-type and `--brief`
A defName can exist in multiple def_types (e.g. `Human` is in BodyDef, ThingDef, HediffGiverSetDef). Without `--type`, the command exits with code 2 and prints candidates — this is NOT a crash, just add `--type` and retry.
`--brief` returns `{thing_class, comp_classes[]}` — feed those field names directly to the decompiler.

### output format
All commands: JSON to stdout, errors/hints to stderr.

## Pipeline

Match the shortest path. Unsure? Default to **Full Analysis**.

### Quick Lookup
User knows the defName or wants to browse/enumerate. No search needed.
  `get` / `fields` / `list` / `types` / `mods` / `values` → done
→ Verify for `get`/`fields`/`list`; skip for `types`/`mods`/`values`

### Full Analysis *(default)*
User wants to understand a game mechanic end-to-end.

1. `search "keyword*" --type T`          ← always prefix-wildcard
2. `get <name> --type T --brief`          ← extracts `thing_class`, `comp_classes[]`
   If `comp_classes` is empty (StatDef, JobDef, HediffDef, etc.):
   use `fields <name> --type <T>` and grep for `*Class`, `workerClass`, `hediffClass`, `driverClass`.
3. Decompiler:
   `list_contexts` → `select_context` or ask user for paths
   `load_assembly(path, contextAlias)` — auto-name from file
   `search_symbols(query="<thing_class>")` → `get_decompiled_source(memberId)`
4. After source: read `references/decompiler-mcp.md`
5. Verify

### Reverse Lookup
User asks "which Defs use this C# class?"

1. `find <fieldPath> <fullClassName>`    ← value is exact match
2. Optional: `get --brief` on key results → decompiler
   → After source: read `references/decompiler-mcp.md`
3. Verify

### Direct Source
User names a C# type directly. Skip CLI.

1. Decompiler:
   `list_contexts` → `select_context` or ask user for paths
   `load_assembly(path, contextAlias)`
   `search_symbols(query="<ClassName>")` → `get_decompiled_source(memberId)`
2. After source: read `references/decompiler-mcp.md`

## Verify

`types`, `mods`, `values`, `install`: skip this step.
All other commands: read `references/cli-reference.md` to confirm output fields, parameter defaults, and edge-case behaviors.

## Guardrails

**NEVER:**
- Guess field names — run `get --brief` or `fields` first.
- Invent method signatures — read decompiled source before patching.
- Assume 1.5 APIs work in 1.6 — verify with `compare_symbols(compareMode:"body")`.
- Write a Harmony patch without reading IL — run `get_il` first.
- Fabricate XML — inspect full `get` output, not `--brief`.
- Fall back to shell tools while the DecompilerServer MCP is connected.

When uncertain about an API you cannot verify, mark it `[UNVERIFIED]` and state what you need.

**Recovery:**
- `get` without `--type` errors → add `--type` from the candidate list.
- `find` returns `[]` → use full name or switch to `search`.
- `search` returns nothing → add `*` or try a shorter term.
- DecompilerServer errors → follow the `candidates` hint in the structured error.
- DecompilerServer 无响应 → run `list_contexts`; registered aliases persist across restarts.

