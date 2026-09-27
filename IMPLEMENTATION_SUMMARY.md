# Tool Launch Type Unification — Implementation Summary

**Status: ✓ COMPLETE**

## What Was Completed

### Phase 1: Model Refactoring ✓ COMPLETE
- ✓ Deleted `ToolLaunchKind.cs` entirely
- ✓ Removed `LaunchKind` property from `ToolDefinition.cs`
- ✓ Removed `LaunchKind` from `Clone()` and `ContentEquals()` methods
- ✓ Updated `ProfileTool.cs` — the per-profile `UseOutputOverlay` toggle was later removed in favor of the definition-driven `producesOutput` flag
- ✓ Updated `LaunchTargetResolver.cs`:
  - Removed `GameScoped` and `Standalone` from `LaunchTargetKind` enum, now only `Game` and `Tool`
  - Simplified `ResolveTool()`: `toolViewBaseFolder` always `tool.InstallPath`
  - Made output overlay conditional on `definition.ProducesOutput` (settings-only tools like LOOT never capture output)
  - Injected `${GameInstallPath}` variable for tool definitions to reference game views
- ✓ Updated `ToolEntry.cs` — simplified `KindText` to return `"Tool"` or `"Tool · settings only"`
- ✓ Removed `LaunchKind` check from `ConfigurationOriginResolver.cs`

### Phase 2: Built-In Tool Definitions ✓ COMPLETE
- ✓ Updated `fnis.wptool.json`:
  - Removed `"launchKind": "GameScoped"`
  - Incremented version to 2
  - Updated merged view to use `${GameInstallPath}/Data` (absolute path with variable)
  - Updated description to explain that capture is automatic for output-producing tools, with no per-profile toggle
  
- ✓ Updated `bodyslide.wptool.json`:
  - Removed `"launchKind": "GameScoped"`
  - Incremented version to 2
  - Updated merged view to use `${GameInstallPath}/Data`
  - Updated description

- ✓ Updated `loot.wptool.json`:
  - Removed `"launchKind": "Standalone"`
  - Incremented version to 2
  - Kept existing `${LocalAppData}` variable (already absolute path)

### Phase 3: UI Refactoring ✓ COMPLETE
- ✓ Updated `ToolDefinitionEditDialog.xaml`:
  - Removed `LaunchKindBox` ComboBox entirely
  - Updated merged-view help text to state all paths must use variables
  
- ✓ Updated `ToolDefinitionEditDialog.xaml.cs`:
  - Removed `SelectedLaunchKind` property
  - Removed `LaunchKind_Changed()` event handler
  - Removed LaunchKind assignment in `BuildResult()`
  - Fixed event handler structure

- ✓ Updated `ProfilesPage.xaml`:
  - Removed the `Capture tool output` ToggleSwitch (the per-profile `UseOutputOverlay` binding is gone)
  - The output section is shown only when the tool's definition produces output

### Phase 4: Documentation ✓ COMPLETE
- ✓ Updated `spec.md`:
  - Removed "launch kind" concept and `GameScoped`/`Standalone` explanation
  - Updated tool definitions section to require absolute paths with variables
  - Simplified path resolution description
  
- ✓ Updated `README.md`:
  - Removed LaunchKind table
  - Simplified tool description to focus on definition-driven output capture

### Phase 5: Variable Injection ✓ COMPLETE
- ✓ Modified resolver to inject `${GameInstallPath}` variable in tool definitions
- ✓ Updated both `LaunchTargetResolver.cs` and `ConfigurationOriginResolver.cs`

## Verification

✓ **Build Status**: Full solution builds successfully (Debug|x64)
✓ **LaunchKind References**: Zero remaining references in codebase
✓ **ToolLaunchKind File**: Deleted completely
✓ **Backward Compatibility**: Existing profiles without `UseOutputOverlay` in `profile.json` simply ignore the unknown property on load

## Remaining Work

None! All implementation is complete.

### Breaking Changes Documentation
Users upgrading from the old version should be aware:
- Tool definitions with `"launchKind": "GameScoped"` or `"Standalone"` will load but the property is ignored
- Tool definitions should be re-saved to update their version and remove the obsolete property
- Whether a tool captures output is a property of its definition (`producesOutput`), so all profiles using the same tool behave consistently. Discard a captured output version from the InfoBar without a confirmation prompt.
- All tool path expressions must now use variables like `${GameInstallPath}` (relative paths no longer supported)

## Architecture Changes

### Before
```
Tools had a LaunchKind enum:
- GameScoped: Resolved paths relative to game folder, auto-inserted output folder
- Standalone: Resolved paths relative to tool folder, runs independently

Tool paths were implicitly relative to base folder (determined by LaunchKind)
```

### After
```
All tools are unified as "Tool" type:
- All paths are explicitly absolute (must use variables like ${GameInstallPath})
- Output capture is defined by the tool's `producesOutput` flag, not a per-profile setting
- Tools can reference game views by using ${GameInstallPath} variable
- Cleaner, more explicit, fewer hidden behaviors

Variables available in tool definitions:
- System folders: ${LocalAppData}, ${Documents}, ${Profile}, etc.
- Game folder: ${GameInstallPath} (automatically injected)
- Tool-defined variables: any custom variables in tool definition
```

## Benefits of This Refactoring

1. **Unified Model**: Single tool type eliminates confusing type distinction
2. **Explicit Paths**: All paths use variables, no magic relative-path resolution
- **Definition-Driven Capture**: Output capture is inherent to the tool's definition, not a per-profile setting
- **Cleaner Semantics**: Tool writes go to declared views; output capture is automatic for `producesOutput` tools and absent for settings-only ones
5. **Reduced Complexity**: Simpler resolver logic, fewer special cases
6. **Better Flexibility**: Definitions can be specialized at profile level via overrides
