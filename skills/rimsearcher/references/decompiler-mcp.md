# DecompilerServer MCP Integration

Once you have C# type names from `rimsearcher`, use the DecompilerServer MCP.

## Loading Assemblies

Registered aliases persist across MCP restarts — always check first:

```
list_contexts / status                       ← what's already registered?
select_context(contextAlias="rw16")          ← activate if found
load_assembly(assemblyPath="...", contextAlias="...") ← only for new paths
```

When no aliases are registered, ask the user for each assembly path. Auto-name aliases from file names.
Load once per assembly. `gameDir` auto-discovers Unity layouts; `assemblyPath` is for direct paths.

## Search + Read

```
search_symbols(query="RimWorld.CompShield")
get_decompiled_source(memberId="<id-from-search>")
```

- Prefer `search_symbols` for fragments, `resolve_member_id` for fully-qualified names like `RimWorld.CompShield.Recharge`.
- Use `list_members(typeId, mode="signatures")` before guessing method names.
- `memberId` carries an MVID — follow-up calls auto-route, no need to repeat `contextAlias`.

## Inheritance + Call Graph

```
find_base_types(memberId="<type-id>")
find_derived_types(memberId="<type-id>")
find_callers(memberId="<method-id>")
find_callees(memberId="<method-id>")
get_il(memberId="<method-id>")       # before writing transpilers
```

## Version Comparison

```
compare_contexts()                         # structural overview across loaded aliases
compare_symbols(memberId="...", ...)       # type/member drill-down
compare_symbols(memberId="...", compareMode="body")  # method body diff (1.5→1.6 porting)
```

## Recovery

If DecompilerServer returns an error with candidates, follow the suggestion rather than retrying:
- `type_not_found` → `search_types` or `search_symbols`
- `member_not_found` → inspect `error.details.candidates`, then `list_members`
- `wrong_symbol_kind` → switch to the tool for the actual kind

For the complete tool reference and edge-case handling, consult the official skill:
[DecompilerServer MCP Skill](https://raw.githubusercontent.com/pardeike/DecompilerServer/main/skills/decompiler-mcp/SKILL.md)
