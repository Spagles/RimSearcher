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

Full parameter tables → [CLI Reference](references/cli-reference.md).

## Pipeline

Run these steps **before writing code**.

### 1. Search

```bash
rimsearcher search "shield*" --type ThingDef
rimsearcher search "护盾" --count
```

Always prefix-search (`shield*`). Without `*`, compound names like `Apparel_ShieldBelt` are missed.

### 2. Get C# Types

```bash
rimsearcher get Apparel_ShieldBelt --type ThingDef --brief
```

Returns `thing_class` and `comp_classes[]`. Feed these names directly to step 4.

If `comp_classes` is empty (non-ThingDef: StatDef, JobDef, HediffDef, etc.), use
`fields <name> --type <T>` and grep for `*Class`, `workerClass`, `hediffClass`, or `driverClass`.

Multi-type match without `--type` → error with a candidate list. Add `--type` and retry.

### 3. Reverse Lookup

```bash
rimsearcher find compClass RimWorld.CompShield
rimsearcher find thingClass RimWorld.Building_Turret
```

Path is suffix-matched, value is exact. `find compClass Shield` won't match `RimWorld.CompShield`.
Fuzzy value matching → use `search`.

### 4. Read Source

```
list_contexts                           ← check registered aliases first
  → activate by name if found
  → if not found, ask the user for paths
```

Ask: "请提供需要分析的程序集路径（例如 D:\\SteamLibrary\\...\\RimWorldWin64_Data\\Managed，或 Mods/XXX/Assemblies/XXX.dll）"
Then auto-name aliases from file names and load once per assembly:

```
load_assembly(assemblyPath="/path/to/Assembly-CSharp.dll", contextAlias="rw")
load_assembly(assemblyPath="/path/to/CombatExtended.dll", contextAlias="ce")
```

```
search_symbols(query="RimWorld.CompShield")
get_decompiled_source(memberId="<id>")
```

Once you have a `memberId`, drop `contextAlias` — it auto-routes.
Use `resolve_member_id` for fully-qualified guesses, `list_members(mode="signatures")` before guessing methods.

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

## DecompilerServer MCP

```
list_contexts / status                ← first: check registered
  → select_context("rw16")            ←    activate if found
  → ask user for paths                ←    if not: auto-name from file

search_symbols → list_members → get_decompiled_source
        ↓
find_callers / find_callees / get_il  (before patching)
compare_contexts / compare_symbols    (version diffs)
```

Registered aliases persist — ask only once. `select_context` loads on demand,
`load_assembly` is for new paths with auto-named aliases.
Full workflow → [DecompilerServer MCP](references/decompiler-mcp.md).
