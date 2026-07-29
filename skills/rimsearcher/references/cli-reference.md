# CLI Command Reference

Full parameter defaults, SQL schema details, and edge-case behaviors. Load this only when you need internals beyond the [SKILL.md](../SKILL.md) summaries — everyday queries are covered there.

All commands output JSON to stdout and errors/hints to stderr. The database (`defs.db`) must be in
the same directory as `rimsearcher.exe`.

---

## Database Schema (Conceptual)

```
defs:        id, def_name, def_type, label, description, mod_name, package_id, source_file, full_data
field_values: def_id, field_path, field_value
defs_fts:    FTS5( def_name, label, description, full_text )  — tokenize='unicode61'
```

- `full_data` is the complete JSON serialization of the Def object
- FTS5 with CJK bigram expansion: `"护盾腰带"` is indexed as `"护盾腰带 护盾 盾腰 腰带"`
- Output varies based on loaded mods — never assume a fixed count or mod list

---

## search

FTS5 full-text search across def_name, label, description, and field values.

```
rimsearcher search <keyword> [--type T] [--mod M] [--limit N] [--count]
```

| Parameter | Default | Description |
|---|---|---|
| `keyword` | required | FTS5 MATCH expression. Supports `*` (prefix), `OR`, `NOT`, `"phrases"` |
| `--type` | null | Filter by def_type (exact match) |
| `--mod` | null | Filter by mod_name (exact match) |
| `--limit` | 20 | Max results |
| `--count` | false | Return `{"count": N}` instead of result array |

**Output** (default): Array of `{def_name, def_type, label, mod_name, package_id, rank}`, sorted by rank descending.

**Output** (--count): `{"count": N}`

**Semantics**: FTS5 token matching, not SQL LIKE. The unicode61 tokenizer splits on word boundaries.
`shield` matches the standalone token `shield` but not `ShieldBelt` (one token). Use `shield*` for prefix.

**CJK**: Chinese text is expanded into bigrams. `护盾` matches `护盾腰带`, `护盾发生器`, etc.

**Examples**:
```bash
rimsearcher search "shield*" --type ThingDef
rimsearcher search "护盾" --count
rimsearcher search "shield OR barrier" --limit 5
```

---

## list

Browse Defs with pagination, no search overhead.

```
rimsearcher list [--type T] [--mod M] [--limit N] [--offset N]
```

| Parameter | Default | Description |
|---|---|---|
| `--type` | null | Filter by def_type |
| `--mod` | null | Filter by mod_name |
| `--limit` | 20 | Page size |
| `--offset` | 0 | Skip first N rows |

**Output**: Array of `{def_name, def_type, label, mod_name, package_id}`, sorted by `def_type, def_name`.

**Examples**:
```bash
rimsearcher list --type ThingDef --offset 40
rimsearcher list --mod Core --limit 10
```

---

## get

Retrieve a single Def by exact def_name match.

```
rimsearcher get <defName> [--type T] [--brief]
```

| Parameter | Default | Description |
|---|---|---|
| `defName` | required | Exact def_name match |
| `--type` | null | Required when defName matches multiple def_types |
| `--brief` | false | Return only `thing_class` + `comp_classes` instead of full JSON |

**Output** (default): Full `full_data` JSON object — the complete Def serialization.

**Output** (--brief): `{def_name, def_type, label, mod_name, package_id, thing_class, comp_classes[]}`

**Multi-type behavior**: If `defName` matches multiple types and `--type` is not specified, the command
exits with code 2 and prints candidate types to stderr:

```
Error: 'Human' matches multiple Def types. Specify --type:
  BodyDef
  HediffGiverSetDef
  ThingDef
```

This is informative, not a crash. Add `--type` to resolve.

**Examples**:
```bash
rimsearcher get Apparel_ShieldBelt --type ThingDef           # full Def JSON
rimsearcher get Apparel_ShieldBelt --type ThingDef --brief   # C# types only
rimsearcher get Human                                        # multi-type → error with candidates
```

---

## find

Exact field-value match. Value matching uses `=` equality, not substring.

```
rimsearcher find <fieldPath> <value> [--type T] [--mod M] [--limit N]
```

| Parameter | Default | Description |
|---|---|---|
| `fieldPath` | required | Suffix-matched: `LIKE '%fieldPath'` |
| `value` | required | Exact match: `field_value = value` |
| `--type` | null | Filter by def_type |
| `--mod` | null | Filter by mod_name |
| `--limit` | 50 | Max results |

**Output**: Array of `{def_name, def_type, label, mod_name, package_id, field_path, field_value}`.

**0 results**: A hint is written to stderr suggesting `rimsearcher search "value"`.

**Key distinction**:
- `find` = **exact** field value match. Requires full name: `RimWorld.CompShield`
- `search` = **fuzzy** FTS5 match. Handles partial names, CJK, etc.

**Examples**:
```bash
rimsearcher find compClass RimWorld.CompShield
rimsearcher find thingClass RimWorld.Apparel --type ThingDef
rimsearcher find compClass Shield                          # returns []; value is exact match
```

---

## fields

List all field paths and values for a single Def.

```
rimsearcher fields <defName> --type <T> [--limit N]
```

| Parameter | Default | Description |
|---|---|---|
| `defName` | required | Exact def_name |
| `--type` | required | def_type |
| `--limit` | 1000 | Max results (fetches 2x internally to compensate for noise filter) |

**Output**: Array of `{field_path, field_value}`.

**Noise filtering**: The following are excluded:
- Fields matching: `debugRandomId`, `defNameHash`, `generated`, `ignoreConfigErrors`, `ignoreIllegalLabelCharacterConfigError`, `index`, `shortHash`
- Fields with path prefix `modContentPack.`

**Examples**:
```bash
rimsearcher fields Apparel_ShieldBelt --type ThingDef --limit 20
```

---

## values

Enumerate distinct values for a given field path suffix.

```
rimsearcher values <fieldPath> [--limit N]
```

| Parameter | Default | Description |
|---|---|---|
| `fieldPath` | required | Suffix-matched: `LIKE '%fieldPath'` |
| `--limit` | 200 | Max distinct values |

**Output**: String array of distinct field values.

**Examples**:
```bash
rimsearcher values compClass --limit 10
rimsearcher values thingClass
```

---

## types

List all Def types with counts.

```
rimsearcher types
```

No parameters.

**Output**: Array of `{def_type, count}`, sorted by count descending.

**Example output**:
```json
[{"def_type":"ThingDef","count":3415},{"def_type":"SoundDef","count":1231},...]
```

Actual counts depend on the loaded mod set.
---

## mods

List all mods with Def counts.

```
rimsearcher mods
```

No parameters.

**Output**: Array of `{mod_name, package_id?, def_count}`, sorted by def_count descending.

Dynamic/abstract Defs appear as `mod_name: "Unknown"` with `package_id: null`.

---

## install

Add the directory containing `rimsearcher.exe` to the user PATH (Windows).

```
rimsearcher install
```

No parameters. Idempotent — running it again when already in PATH does nothing.

---

## update

Download the latest release from GitHub and replace the current executable.

```
rimsearcher update
```

No parameters. Uses HTTP 302 redirects to resolve the latest version tag (no GitHub API rate limit).
Self-replacing: downloads `rimsearcher.new`, verifies, replaces the running exe, cleans up.


## See Also

- [DecompilerServer MCP Integration](decompiler-mcp.md) — loading assemblies, searching symbols, call graph, version comparison
