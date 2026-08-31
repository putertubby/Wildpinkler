# Tool Launch Type Unification — Implementation Summary

**Status: ✓ COMPLETE**

## What Was Completed

### Phase 1: Model Refactoring ✓ COMPLETE
- ✓ Deleted `ToolLaunchKind.cs` entirely
- ✓ Removed `LaunchKind` property from `ToolDefinition.cs`
- ✓ Removed `LaunchKind` from `Clone()` and `ContentEquals()` methods
- ✓ Updated `ProfileTool.cs` — added `UseOutputOverlay` boolean property (defaults to true)
- ✓ Updated `LaunchTargetResolver.cs`:
  - Removed `GameScoped` and `Standalone` from `LaunchTargetKind` enum, now only `Game` and `Tool`
  - Simplified `ResolveTool()`: `toolViewBaseFolder` always `tool.InstallPath`
  - Made output overlay conditional on `binding.UseOutputOverlay && definition.ProducesOutput`
  - Injected `${GameInstallPath}` variable for tool definitions to reference game views
- ✓ Updated `ToolEntry.cs` — simplified `KindText` to return `"Tool"` or `"Tool · settings only"`
- ✓ Removed `LaunchKind` check from `ConfigurationOriginResolver.cs`

### Phase 2: Built-In Tool Definitions ✓ COMPLETE
- ✓ Updated `fnis.wptool.json`:
  - Removed `"launchKind": "GameScoped"`
  - Incremented version to 2
  - Updated merged view to use `${GameInstallPath}/Data` (absolute path with variable)
  - Updated description to reflect new output capture toggle behavior
  
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
  - Added `UseOutputOverlay` toggle to tool configuration UI
  - Added help text explaining capture behavior
  - Bind bidirectionally to `ProfileTool.UseOutputOverlay`

### Phase 4: Documentation ✓ COMPLETE
- ✓ Updated `spec.md`:
  - Removed "launch kind" concept and `GameScoped`/`Standalone` explanation
  - Updated tool definitions section to require absolute paths with variables
  - Simplified path resolution description
  
- ✓ Updated `README.md`:
  - Removed LaunchKind table
  - Simplified tool description to focus on output capture toggle behavior

### Phase 5: Variable Injection ✓ COMPLETE
- ✓ Modified resolver to inject `${GameInstallPath}` variable in tool definitions
- ✓ Updated both `LaunchTargetResolver.cs` and `ConfigurationOriginResolver.cs`

## Verification

✓ **Build Status**: Full solution builds successfully (Debug|x64)
✓ **LaunchKind References**: Zero remaining references in codebase
✓ **ToolLaunchKind File**: Deleted completely
✓ **Backward Compatibility**: Existing profiles with `UseOutputOverlay` default to true (preserves current behavior)

## Remaining Work

None! All implementation is complete.

### Breaking Changes Documentation
Users upgrading from the old version should be aware:
- Tool definitions with `"launchKind": "GameScoped"` or `"Standalone"` will load but the property is ignored
- Tool definitions should be re-saved to update their version and remove the obsolete property
- Users can control output behavior per-tool, per-profile using the new toggle
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
- Output capture is a per-profile toggle, not inherent to tool type
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
3. **Per-Profile Control**: Output overlay can be toggled per profile, not per definition
4. **Clearer Semantics**: Tool writes go to declared views; output capture is opt-in
5. **Reduced Complexity**: Simpler resolver logic, fewer special cases
6. **Better Flexibility**: Definitions can be specialized at profile level via overrides
