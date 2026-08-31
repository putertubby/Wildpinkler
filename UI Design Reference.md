# Fluent 2 and WinUI 3 UI Design Reference

Status: researched against Microsoft Fluent 2 and Windows/WinUI documentation on 2026-08-20 (Q3 2026). Prefer the linked first-party sources when a control or API changes.

## Decision Rule

For a Windows 11 desktop app, Fluent 2 is the visual and interaction language; WinUI 3 and the Windows App SDK are the canonical implementation. Start with standard WinUI controls, default templates, and named theme resources. Customize only when the product need is clear, and preserve the behavior the standard control already supplies: states, keyboard operation, UI Automation, text scaling, contrast themes, RTL, and dynamic layout.

Choose the visual density and page structure from the work users need to complete. Productivity and data-management tools need calm hierarchy, direct manipulation, concise copy, dense but scannable lists, and predictable command placement. Creative, media, consumer, and immersive applications may use richer imagery and greater spatial expression, but must retain legibility, accessibility, and task clarity. Avoid decorative cards, gratuitous animation, arbitrary colors, and custom controls that merely duplicate a built-in control.

## Pattern Selection

Choose the smallest familiar pattern that supports the user task. Keep top-level destinations peer-like and shallow; make related work visible together instead of forcing users to bounce between pages.

| User need | Prefer | Avoid |
| --- | --- | --- |
| 5-10 top-level destinations | Left `NavigationView` | A custom side menu |
| <=5 equally important destinations | Top `NavigationView` or `SelectorBar` | Overflowing a broad horizontal command strip |
| Several open/reorderable documents | `TabView` | Treating document tabs as application navigation |
| Parent/child location path | `BreadcrumbBar` | A deep hidden navigation tree |
| Find/filter a known collection | `AutoSuggestBox` or `TextBox` plus explicit filter controls | A custom pointer-only search surface |
| Browse a selectable collection and inspect it | Responsive list/details | A modal dialog for ordinary item inspection |
| Edit one focused object | Page, side-by-side detail, or `ContentDialog` based on scope | A long, nested modal workflow |
| Choose a value from a small fixed set | `RadioButtons` or `ComboBox` | A free-text field when choices are known |
| Enable an immediate binary setting | `ToggleSwitch` | A toggle for a deferred or destructive action |
| Execute a frequent page action | `CommandBar` | A row of ad hoc buttons with inconsistent layout |
| Reveal secondary/contextual actions | `MenuFlyout`, `CommandBar` overflow, or context menu | Always-visible tiny action buttons on every row |
| Explain a transient feature or control | `TeachingTip` | A blocking dialog for nonessential instruction |
| Notify about ongoing page/app state | `InfoBar` | A modal dialog or fleeting toast for persistent state |
| Confirm a consequential action | `ContentDialog` | A confirmation for every routine, reversible action |

## UI-Only Refactor Playbook

Use this process when the requirement is to redesign a screen without changing domain models, persistence, services, validation rules, side effects, or navigation contracts. A visual refactor is still a behavior-preservation exercise: the new surface must expose the existing capabilities more clearly without silently changing what any operation does.

1. Inventory the existing screen before changing XAML: source data, load/refresh path, commands and their enablement prerequisites, selection mode, keyboard shortcuts, context actions, dialogs and their results, validation messages, background-operation status, error states, and navigation/back behavior.
2. Write a state matrix for the new surface: loading, load failure, empty, filtered-empty, one selection, multiple selection, unavailable action, editing-valid, editing-invalid, saving, save failure, and background work. Every existing state needs one intentional place in the redesigned UI.
3. Keep domain objects, stores, service calls, operation queues, and persistence boundaries intact. Introduce a page-level presentation model only when it makes UI state explicit, such as `SearchText`, `SelectedItem`, `SelectedItems`, `IsBusy`, `StatusMessage`, or derived command availability. It may format and coordinate existing data but must not duplicate business validation or persistence rules.
4. Rebind the new XAML to the same input/output contracts. Keep command handlers or `ICommand` implementations as the authority for actions; do not put storage, process, or domain decisions in converters, visual-state setters, or pointer events.
5. Preserve identity through filtering, sorting, and refresh. Retain the selected item's stable ID, restore selection only when that item remains available, and clear details/disable commands when it does not. Never infer selection from a visual row index after a collection changes. See "Controlled State and Non-Destructive Data Updates" for the full set of rules this depends on.
6. Keep loading and mutation asynchronous. Disable or show progress only for the affected action, retain recoverable user input, marshal UI updates to the UI thread, and show a specific error/status result. Do not block the UI thread to make the new layout appear synchronous.
7. Test behavioral parity before visual polish: each existing command, shortcut, row context action, validation rule, cancellation path, picker result, and failure state must still work. Then run the visual, accessibility, and resize checks in this guide.

### Presentation state and binding

- Prefer `x:Bind` with `x:DataType` for page-owned and typed `DataTemplate` bindings in a refactor; it provides compile-time binding validation and efficient generated code. Use `{Binding}` when the source is late-bound, dynamic, or intentionally inherited through `DataContext`.
- State binding direction explicitly. `{x:Bind}` defaults to `OneTime`; use `Mode=OneWay` for changing status, selection, and command state, and `Mode=TwoWay` for editable fields and for controlled interaction state such as expanded, checked, or open, which must round-trip back to the model.
- Do not leave expansion, selection, checked state, or open/closed state to a control's own default. Bind it to the presentation model so it survives filtering, refresh, and container recycling. See "Controlled State and Non-Destructive Data Updates".
- `TextBox.Text` two-way binding normally updates on focus loss. Use `UpdateSourceTrigger=PropertyChanged` only when immediate search, preview, or validation is required; debounce expensive work. Use an explicit draft object for an edit dialog when changes must not reach the underlying item until Save.
- Use `ObservableCollection<T>` for items added or removed while visible and `INotifyPropertyChanged` for properties that change on existing items. After asynchronous data arrives, call `Bindings.Update()` only when one-time `x:Bind` values genuinely need to refresh; do not compensate for missing change notification with broad, repeated reloads.
- Update a visible collection by reconciling it against stable keys. Clearing and refilling it, or reassigning `ItemsSource`, resets the control and destroys selection, scroll, expansion, and focus.
- Keep converters small, pure, and presentation-only. Prefer a named view-state property or `x:Bind` function for a simple formatting rule; never perform I/O, mutate state, or make a business decision in a converter.

### Responsive list/details implementation

Use `VisualStateManager` for broad structural changes such as side-by-side to stacked detail, and measured page logic only for content-dependent details such as which optional data columns fit. Do not use a visual state to alter backend behavior.

```xaml
<Grid x:Name="PageLayout">
    <Grid.ColumnDefinitions>
        <ColumnDefinition x:Name="ListColumn" Width="*" />
        <ColumnDefinition x:Name="DetailsColumn" Width="360" />
    </Grid.ColumnDefinitions>

    <ListView x:Name="ItemsList" SelectionMode="Single" />
    <ContentPresenter x:Name="DetailsPresenter" Grid.Column="1" />

    <VisualStateManager.VisualStateGroups>
        <VisualStateGroup>
            <VisualState x:Name="Narrow">
                <VisualState.Setters>
                    <Setter Target="DetailsColumn.Width" Value="0" />
                </VisualState.Setters>
            </VisualState>
            <VisualState x:Name="Wide">
                <VisualState.StateTriggers>
                    <AdaptiveTrigger MinWindowWidth="641" />
                </VisualState.StateTriggers>
                <VisualState.Setters>
                    <Setter Target="DetailsColumn.Width" Value="360" />
                </VisualState.Setters>
            </VisualState>
        </VisualStateGroup>
    </VisualStateManager.VisualStateGroups>
</Grid>
```

- At 320-640 epx, a selected item should drill into a dedicated stacked details page or replace the list region; provide Back to return to the list with its query, scroll position, and selection context preserved. At >=641 epx, display the list and details side by side.
- When a detail panel opens at wide width, reserve its actual width before deciding which columns can remain in the list. Keep the primary identifier visible; progressively hide or move secondary metadata into details rather than allowing a horizontal row scroller.
- Do not add nested vertical scroll regions. Let the collection own its scroll area; make a details pane independently scrollable only if it is an independently visible pane. Leave a right-side gutter where an overlaid scrollbar could cover interactive content.
- A fixed-width details pane at the wide breakpoint should still be user-resizable, not just a hardcoded number. Put a thin, draggable splitter (`GridSplitter`, e.g. from `CommunityToolkit.WinUI.Controls.Sizers`) in its own narrow column between the list and details columns, with `MinWidth` set on both sides (so neither can be squeezed unreadable) and a `MaxWidth` on the details column (so it cannot swallow the whole window). Only show the splitter in the side-by-side layout state; there is nothing to drag once the layout has collapsed to a single stacked column. Persist the width the user drags to the same place other layout/shell state is already persisted in the app, and restore it on the next load instead of resetting to the default every time.

### Management-collection control choice

- Use `ListView` for a text-led, read-and-select management collection with a custom row template, standard selection behavior, contextual actions, and a details pane. Keep its default virtualizing panel; replacing it with a nonvirtualizing panel can make larger collections slow.
- Use `DataGrid` from the Windows Community Toolkit only when users truly need a spreadsheet-like table: sortable columns, resizable columns, column headers with table semantics, or inline cell editing across many rows. Treat it as a product dependency and validate keyboard, screen-reader, high-contrast, column-overflow, and edit-commit behavior before adopting it.
- Use `ItemsView` when a collection must switch dynamically between list, grid, or custom layout while preserving selection. It is not a reason to replace a working `ListView` whose interaction model is already appropriate.
- Use `GridView` or `ItemsView` grid layouts for image- or visual-card-led browsing, not dense textual records that users read top-to-bottom. Use `ItemsRepeater` only when the required layout/interaction cannot be built from the feature-complete collection controls and the team is prepared to implement missing selection, focus, automation, and interaction behavior.
- Use one selection model consistently. `Single` is the default for list/details; `Extended` is appropriate only when users need range/additive desktop selection and the UI exposes batch actions and selected count. Do not combine row activation with selection unless the activation order and its keyboard equivalent are intentional and tested.
- Keeping the default virtualizing panel means containers are recycled and reused for different items as the user scrolls. Any state that matters must therefore live on the item, not on the container. See "Controlled State and Non-Destructive Data Updates".

## Controlled State and Non-Destructive Data Updates

**A control must never collapse, deselect, scroll back to the top, lose keyboard focus, discard typed input, or close an open flyout because the data behind it changed.** When that happens it is not a quirk of the control: it means the state was left uncontrolled, or the refresh was destructive, or both. Treat either as a defect of the same severity as a wrong value on screen — users lose their place, lose context, and lose trust in every subsequent update.

Two rules prevent almost all of it, and both are mandatory:

1. **Controlled state.** Every piece of interaction state that a user would notice disappearing — expanded, selected, checked, scrolled, focused, open, being edited — is owned by a presentation model keyed by a stable identity, and bound two-way. It is never left to a control's internal default or to a realized container.
2. **Non-destructive updates.** A visible collection is updated in place, item by item, against stable keys. It is never cleared and refilled, and its `ItemsSource` is never reassigned, merely to show newer data.

### Controlled and uncontrolled state

State is *controlled* when the model is the authority and the control follows it. State is *uncontrolled* when the control is the only place it exists, in which case it lives exactly as long as the container does — which is until the next recycle, filter, or refresh.

| State | Controlled home | Uncontrolled anti-pattern |
| --- | --- | --- |
| Expanded/collapsed row or node | `IsExpanded` on the item model, `Mode=TwoWay` | `IsExpanded` left at the template default, or set imperatively on a container |
| Selection | Selected item key on the page model, restored after every refresh | Reading or restoring `SelectedIndex` |
| Scroll position | Anchor item key, re-shown after the update | Assuming the offset survives a rebuild |
| Open flyout, menu, dialog, or teaching tip | Page-level flag plus the key of the item it targets | A flyout attached to, and owned by, a recycled row container |
| Checked/toggled row state | Property on the item model | `IsChecked` read back out of the container at commit time |
| In-progress edit | An explicit draft object on the model | Live control values treated as the source of truth |
| Per-item busy/progress/error | Properties on the item model | A `ProgressRing` shown by mutating a container found by index |

Rules that follow from this:

- Bind interaction state with `Mode=TwoWay` inside `DataTemplate`s. `{x:Bind}` defaults to `OneTime`, and one-time expansion state is indistinguishable from no state at all after the first recycle.
- Never write UI state into the visual tree and read it back later. `ContainerFromItem`, `ContainerFromIndex`, and visual-tree walks are for measurement and focus, not for storage.
- Give every item a stable key that survives a reload from its source: an identifier, not a display name, not a position, and not object reference identity, which a reload does not preserve.
- If a control genuinely has no bindable property for the state you need, keep the state in the model anyway and apply it explicitly after each structural change, so there is exactly one authority.

### Never rebuild a bound collection to refresh it

Clearing an observable collection and adding items back raises a single `NotifyCollectionChangedAction.Reset`. Items controls answer a reset by throwing away every realized container and starting over. Everything the containers were holding goes with them:

- selection, and the selection-derived command enablement that follows it
- scroll position, including the user's carefully found place in a long list
- expanded rows and expanded tree nodes
- keyboard focus, and therefore the caret and any partially typed text
- open context menus, row flyouts, and drag operations in flight
- item-level animations and transitions, which restart or are skipped entirely

Reassigning `ItemsSource` is the same destruction in a different costume, and so is swapping in a freshly constructed collection instance. Neither is an acceptable refresh mechanism for a collection the user is currently looking at.

Refresh by reconciling instead. Route every collection refresh through one shared, generic diff so the behavior is identical everywhere:

```csharp
// Reconciles in place so realized containers, selection, and expansion survive.
public static void Diff<T, TKey>(
    ObservableCollection<T> target,
    IReadOnlyList<T> source,
    Func<T, TKey> keySelector)
    where TKey : notnull
{
    var incoming = source.ToDictionary(keySelector);

    for (int i = target.Count - 1; i >= 0; i--)
    {
        if (!incoming.ContainsKey(keySelector(target[i])))
        {
            target.RemoveAt(i);
        }
    }

    for (int i = 0; i < source.Count; i++)
    {
        var key = keySelector(source[i]);
        int existing = -1;

        for (int j = i; j < target.Count; j++)
        {
            if (EqualityComparer<TKey>.Default.Equals(keySelector(target[j]), key))
            {
                existing = j;
                break;
            }
        }

        if (existing < 0)
        {
            target.Insert(i, source[i]);
        }
        else if (existing != i)
        {
            target.Move(existing, i);
        }
    }
}
```

- Prefer updating the surviving instance's properties over replacing it. A replaced item is a new item to the control: it loses selection and expansion even though the diff was granular.
- Emit `Move` for reordering rather than remove-then-insert, so the control can animate and keep the moved item selected.
- Apply the same discipline to hierarchical data. Diff each level of children against its keys instead of clearing and re-adding a node's `Children`.
- Batch a large structural change only when the control supports it; otherwise many granular notifications are still preferable to one reset.

### Collection types for changing data

The collection type chosen at design time decides whether a non-destructive update is even possible later.

- Use `ObservableCollection<T>` — or a type implementing non-generic `IList` plus `INotifyCollectionChanged` — for anything that can change while it is on screen. This is the only way granular add, remove, move, and replace reach the control.
- Use `List<T>` or a plain enumerable only for genuinely static data bound once. Note that binding to generic `IList<T>` and `IEnumerable<T>` is not supported; a custom collection must implement `IList`, `IEnumerable`, `IList<object>`, or `IEnumerable<object>`.
- Implement `INotifyPropertyChanged` on the items themselves. A changed value should be a property notification that updates one row in place, never a collection operation and never a reload.
- Expose the collection as a get-only property populated once, rather than a settable property reassigned on each load. A settable collection property is an invitation to reset.
- Keep the bound collection separate from the full source set. Filtering and sorting produce the visible collection by reconciling into it; they do not rebuild it.
- Use `CollectionViewSource` with `IsSourceGrouped` for grouped data, and keep the group collections observable too, so a group gaining or losing an item does not reset the whole view.
- For very large or remote data, implement `ISupportIncrementalLoading` on an observable collection rather than periodically replacing pages of items.

### Identity across refresh, filter, and sort

- Capture the selected item's key, the anchor item's key, and the set of expanded keys *before* a structural change; reapply them *after* it, in that order.
- Restore selection by key only when that item is still present. When it is gone, clear the details pane and disable the commands that depended on it rather than silently selecting a neighbour.
- Guard restoration against feedback loops. Suppress the `SelectionChanged` handler's side effects while programmatically reselecting, and re-enable them afterwards.
- Restore scroll by bringing the anchor item back into view, not by reapplying a pixel offset that no longer refers to the same content.
- Do not reorder or remove the item a user is currently editing or has open in a flyout. Defer that part of the update, or visibly mark the item as stale, but never move it out from under an interaction.
- Never derive identity from a position. An index captured before a refresh describes a different item after it.

### Expansion state: Expander, TreeView, groups, and panes

Expansion is the state users notice losing first, because collapsing throws away work they did to find something.

- An `Expander` inside a `DataTemplate` must bind `IsExpanded` two-way to the item model. Without it, container recycling will hand a reused row the previous item's expansion, which is worse than losing the state: it is wrong state, silently.
- `TreeView` is not virtualized, and expansion lives on `TreeViewNode.IsExpanded`. Rebuilding `RootNodes` therefore discards the entire expansion structure. Keep an app-owned set of expanded keys and reapply it after any rebuild.
- The lazy-realization pattern — `HasUnrealizedChildren` plus clearing a node's `Children` when it collapses — is a deliberate trade of state for memory. Adopt it only alongside an explicit restore path, and treat re-expansion as an ordered walk: realize a level, expand the keys that were expanded, then descend.
- Prefer diffing a node's `Children` on refresh over clearing them. A node whose children reconcile stays expanded with no restore logic at all.
- Group headers, pane open/closed state, and `NavigationView` expanded menu items follow the same rule: model-owned, reapplied deliberately, never dependent on a container surviving.
- When restoring is genuinely impossible, restore the user's *place* instead: keep the previously focused item visible and selected rather than silently collapsing to the root.

### Refreshing while the user is busy

An update that is correct but badly timed is still a lost interaction.

- Queue a refresh, rather than applying it, while a row flyout or context menu is open, a field is being edited, a drag is in flight, or focus is inside the collection. Apply it when the interaction completes.
- Apply item-level property updates immediately even while structural updates are deferred; they are safe and they keep the data honest.
- Debounce and coalesce. A background source that notifies frequently must not translate into frequent structural churn; collapse pending changes into one reconciliation.
- Never issue a diff from inside the collection's own `CollectionChanged` handler, and never restructure a collection while a reorder-enabled items control is mid-drag. Re-entering a control during its own bookkeeping desynchronizes it, and the failure is a crash or corrupted ordering, not a visual glitch. Marshal the work to the dispatcher so the control settles first.
- Marshal every collection and item mutation to the UI thread. A background-thread mutation of a bound collection is undefined behavior, not a performance optimization.
- Show that a refresh happened without moving anything: a status line, a subtle new-items affordance, or a manual "show new results" action is better than reordering under the user's pointer.
- Scope a refresh to what the triggering change could actually have affected. A handler reacting to a collection's own change notification should inspect the change kind (add/remove versus a pure reorder) and skip recomputing sibling state that kind of change cannot affect, rather than funneling every kind of change through one "refresh everything" entry point. Diffing each affected collection correctly does not help if an oversized handler still touches unrelated selections, sections, or triggers unrelated work on every change.

### Anti-patterns

- `Clear()` followed by re-adding, as a refresh.
- Reassigning `ItemsSource`, or assigning a newly constructed collection instance, to show updated data.
- Binding live data to `List<T>` and then wondering why only a reload updates the UI.
- Calling `Bindings.Update()`, reloading a page, or renavigating as a substitute for change notification.
- Storing expanded, checked, or selected state only in the visual tree, then reading it back out of containers.
- Restoring selection or expansion by index after a collection changed.
- Collapsing everything on refresh and "helpfully" re-expanding the first node or the first result.
- Rebuilding a tree or list on a timer while the user is working in it.
- Setting expansion or selection imperatively on a container found by walking the visual tree.
- One "refresh everything" handler that recomputes and repaints every section regardless of which change kind triggered it, instead of scoping the work to what that change kind could affect.
- Loading another feature's data from a cached page's constructor, so the page keeps showing a snapshot frozen at the session's first visit.

## Fluent 2 Principles Applied to Windows

1. **Clear hierarchy over decoration.** A user should immediately see their location, primary task, relevant collection or content, selection, and status. Use position, type ramp, surface layering, and spacing before color or shadow.
2. **Content first.** The central user task and data are the product. Chrome should orient and command, not compete for attention.
3. **Purposeful depth.** Windows 11 has a base layer and a content layer. Apply a single Mica backdrop at the window level, then use a contiguous content layer or a small number of meaningful cards. Shadows communicate a real elevation change such as a flyout or dialog; they are not decoration.
4. **Calm, personal color.** Respect the system light/dark mode and accent. Accent identifies selection, focus, and the primary action; it is not a fill color for every component.
5. **Inclusive by default.** Keyboard, Narrator, contrast themes, display scaling, text scaling, pointer, and touch are first-class interaction paths. Built-in controls cover much of this automatically.

## App Shell and Navigation

### NavigationView

- Use left navigation for roughly 5-10 important top-level categories; use top navigation only for five or fewer equally important categories when labels need to stay visible.
- Keep labels concrete and stable. Use familiar `SymbolIcon` values or explicit `FontIcon` children where an icon is genuinely clearer; never depend on an icon alone in a compact navigation pane without a tooltip or accessible name.
- Use `PaneDisplayMode="Auto"` unless a task-specific design proves a different mode is better. Its Windows defaults are `Left` at >=1008 epx, `LeftCompact` at 641-1007 epx, and `LeftMinimal` at <=640 epx.
- Avoid navigation beyond two levels. If deeper hierarchy becomes necessary, add a `BreadcrumbBar` for the location path and return affordances.
- Handle either `ItemInvoked` or `SelectionChanged` as the navigation authority, not both. Never add a duplicate back-stack entry when the already-current destination is invoked.
- Bind `IsBackEnabled` to `Frame.CanGoBack`; Back dismisses transient UI before traversing app history. Selection inside a list/details page is not page navigation and should not pollute the back stack.
- Use `NavigationView.Header` for the current page title when it improves orientation. Standard content margins are 24 epx outside minimal mode and 12 epx in minimal mode.

### Page lifetime and cross-page data

A cached page (`NavigationCacheMode` of `Required` or `Enabled`) is constructed once and reused for the rest of the session. Anything loaded only from its constructor is a snapshot taken at the first visit, and every later change made elsewhere in the app stays invisible to it until the app restarts.

- Load the data a page *owns* once; re-read the data another page owns every time the page is shown. A page that lists tools, games, or accounts maintained on a different page must refresh that catalog from its navigated-to entry point, not from its constructor.
- Reload only those foreign catalogs there, and reconcile rather than rebuild, so selection, scroll position, and expansion survive returning to the page. See "Controlled State and Non-Destructive Data Updates".
- Guard the reload so it cannot race the initial load still in flight from construction.
- Getting this wrong presents as a *filtering* bug: an item that exists and qualifies is simply absent. Before investigating the predicate, confirm that the collection it filters was read after the item was created.

### Modern title bar and Mica

The current canonical WinUI 3 shell is a `TitleBar` above `NavigationView`, with one window-level Mica backdrop. The `TitleBar` owns the drag region, system caption integration, back button, and pane toggle. Hide NavigationView's built-in back/toggle controls when using it.

```xaml
<Window ...>
    <Window.SystemBackdrop>
        <MicaBackdrop />
    </Window.SystemBackdrop>

    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto" />
            <RowDefinition Height="*" />
        </Grid.RowDefinitions>

        <TitleBar x:Name="AppTitleBar"
                  Title="App name"
                  IsBackButtonVisible="{x:Bind ContentFrame.CanGoBack, Mode=OneWay}"
                  BackRequested="AppTitleBar_BackRequested"
                  IsPaneToggleButtonVisible="True"
                  PaneToggleRequested="AppTitleBar_PaneToggleRequested" />

        <NavigationView x:Name="NavView"
                        Grid.Row="1"
                        IsBackButtonVisible="Collapsed"
                        IsPaneToggleButtonVisible="False">
            <Frame x:Name="ContentFrame" />
        </NavigationView>
    </Grid>
</Window>
```

```csharp
ExtendsContentIntoTitleBar = true;
SetTitleBar(AppTitleBar);

private void AppTitleBar_BackRequested(TitleBar sender, object args)
{
    if (ContentFrame.CanGoBack)
        ContentFrame.GoBack();
}

private void AppTitleBar_PaneToggleRequested(TitleBar sender, object args)
{
    NavView.IsPaneOpen = !NavView.IsPaneOpen;
}
```

- Adopt this as a deliberate shell upgrade, not page-by-page styling. It requires checking current Windows App SDK APIs and validating drag, caption-button, navigation, and compact-pane behavior.
- `MicaBackdrop` is the default foundation for long-lived Windows 11 app windows. Use `MicaBackdrop Kind="BaseAlt"` only when a stronger command/navigation distinction is genuinely required, such as a tabbed title-bar experience.
- Apply the backdrop once to `Window.SystemBackdrop`; keep intervening containers transparent where Mica should be visible. Do not apply a backdrop to every panel.
- Use `LayerFillColorDefaultBrush` for a content layer over Mica. For Mica Alt commanding regions, use `LayerOnMicaBaseAltFillColorDefaultBrush`, then `LayerFillColorDefaultBrush` for content above it.
- Use acrylic primarily for transient, light-dismiss surfaces. Standard flyouts, menus, and combo-box popups already handle this. Do not place adjacent acrylic panes or use it as the main page background.
- Mica and acrylic automatically fall back for contrast themes, disabled transparency, Battery Saver, inactive windows, unsupported OS versions, or lower-end hardware. Do not build workflows that rely on translucency to remain understandable.

## Layout, Density, and Responsive Behavior

- Design in effective pixels, not physical pixels. Align dimensions, gaps, margins, and corner radii to multiples of 4 epx for sharp rendering at Windows scale factors.
- Treat small <=640 epx, medium 641-1007 epx, and large >=1008 epx as starting breakpoints. Measure the actual available container width, not the monitor size or raw window width.
- Prefer one fluid layout. Use responsive techniques in increasing order of disruption: resize margins/controls, reposition, reflow, reveal/hide secondary metadata, then replace the architecture only if necessary.
- Preserve the primary identifier and task at every width. In a collection row, hide less-important metadata before the name, selection, state, or essential command.
- For list/details, use side-by-side at >=641 epx and a stacked drill-in detail page at 320-640 epx. Do not leave a narrow list plus an unusably thin details card.
- Put long data in a column that can grow and wrap; use `TextTrimming` only where the full value is available elsewhere. Do not force horizontal scrolling for standard lists or forms.
- Use `Grid` for aligned rows and label/value layouts, `StackPanel` for simple sequential content, and `ScrollViewer` only around the axis that needs it. Always declare `Grid.RowDefinitions` and `ColumnDefinitions` for every referenced row/column.
- Leave enough room for text scaling, localization, scrollbars, and compact NavigationView overlays. Do not use fixed inner widths to force a dialog or form wider than its parent; size the `ContentDialog` root when a fixed dialog width is justified.
- Use an 8 epx or smaller card corner radius unless the standard control template supplies the shape. Avoid card-inside-card composition; a card should separate a meaningful, independently actionable or scannable region.

## Surfaces, Color, and Tokens

### Use semantic resources

- Use `{ThemeResource ...}` whenever a resource must update when Light, Dark, HighContrast, or user settings change. Use `{StaticResource ...}` for ordinary static style references and inside theme dictionary resource definitions as required by WinUI's theme-resource rules.
- Favor built-in semantic brushes such as `TextFillColorPrimaryBrush`, `TextFillColorSecondaryBrush`, `CardStrokeColorDefaultBrush`, and `LayerFillColorDefaultBrush`; do not hard-code white/black/gray values for shared UI.
- When creating product resources, name them by role, not appearance: `PageContentBackgroundBrush`, `MetadataTextBrush`, `DangerStatusBrush`, not `BlueBrush` or `Gray70Brush`.
- Define custom `Light`, `Dark`, and `HighContrast` dictionaries. In the HighContrast dictionary, map semantic brushes to appropriate `SystemColor...` resources, rather than fixed hexadecimal colors.

```xaml
<ResourceDictionary.ThemeDictionaries>
    <ResourceDictionary x:Key="Light">
        <SolidColorBrush x:Key="MetadataTextBrush"
                         Color="{StaticResource TextFillColorSecondary}" />
    </ResourceDictionary>
    <ResourceDictionary x:Key="Dark">
        <SolidColorBrush x:Key="MetadataTextBrush"
                         Color="{StaticResource TextFillColorSecondary}" />
    </ResourceDictionary>
    <ResourceDictionary x:Key="HighContrast">
        <SolidColorBrush x:Key="MetadataTextBrush"
                         Color="{ThemeResource SystemColorWindowTextColor}" />
    </ResourceDictionary>
</ResourceDictionary.ThemeDictionaries>
```

### Color and depth rules

- Let system light/dark theme and accent remain recognizable. Accent is sparse: selected navigation/list item, keyboard focus, active state, and a single primary action.
- Never make color the sole signal for success, warning, error, selection, or required input; pair it with text, icon, state, or position.
- Keep normal visible text at a minimum contrast ratio of 4.5:1 against its actual background. Test final composited combinations, especially custom material tint, disabled-like metadata, and status text.
- Use the two-layer system: Mica base, then a content layer. Use a limited number of 1px outlined cards at elevation 8 for truly separated content. Default control elevation is 2; flyouts 32; dialogs 128. Prefer standard controls because they implement the matching contour and shadow states.
- Do not use background acrylic for large persistent app areas. Do not place accent-colored text or hyperlinks on acrylic without verifying contrast.

## Typography, Icons, and Content

- Use the default Segoe UI Variable/Segoe Fluent Variable system font through standard controls. Do not introduce a display font for operational UI.
- Use sentence case for page titles, commands, labels, and helper text. Use ALL CAPS only for a short, established section-label convention; do not use it for normal field labels.
- Use regular text for body copy and semibold for hierarchy/emphasis. Avoid bold and italic as routine emphasis. Keep normal UI text at least 12 epx regular or 14 epx semibold.
- Use the type-ramp styles instead of ad hoc sizes: `CaptionTextBlockStyle` (12), `BodyTextBlockStyle` and `BodyStrongTextBlockStyle` (14), `BodyLarge...` (18), `SubtitleTextBlockStyle` (20), `TitleTextBlockStyle` (28). Page titles should usually be `TitleTextBlockStyle`; form section titles `SubtitleTextBlockStyle` or the local section-label style.
- Default to left alignment; center only when the content is intrinsically centered, such as a small empty state. Keep prose near 50-60 characters per line when possible.
- Use `TextBlock` for static text and `TextBox`/`RichEditBox` only for editable content. Static text should not become a tab stop merely to make Narrator announce it.
- Use built-in `SymbolIcon`, `FontIcon`, `BitmapIcon`, or `ImageIcon` according to the asset. For simple commands, `AppBarButton Icon="Add"` is preferred. For an explicit glyph, use a `FontIcon` child, not a raw glyph string in `Button.Content`.
- Pair unfamiliar icon-only controls with `ToolTipService.ToolTip` and a meaningful `AutomationProperties.Name`. Avoid icon-only text buttons where a familiar symbol control exists.

## Commands, Lists, and Status

### CommandBar

- Use `CommandBar` with `AppBarButton`, `AppBarToggleButton`, and `AppBarSeparator` for page-level frequent actions. Keep labels short, preferably one word; `DefaultLabelPosition="Right"` is a good desktop choice.
- Order `PrimaryCommands` by importance; dynamic overflow moves lower-priority commands first. Put rare/destructive/contextual actions in `SecondaryCommands`, a row context menu, or a details action rather than the first toolbar position.
- Use a `SplitButton` only when the primary action has a common default plus closely related alternatives. Use a `MenuFlyout` for a compact, contextual command group.
- Keep a command in the same location across pages where it means the same thing. Enable it only when its prerequisites are met, and retain a tooltip that explains an unavailable state where useful.
- Use familiar accelerators only for frequent operations, such as `Ctrl+N`, `Ctrl+S`, and `Ctrl+F`. Define `KeyboardAccelerator` behavior and expose the shortcut in tooltip and `AutomationProperties.AcceleratorKey`; the latter announces metadata but does not implement the shortcut.

### Collections and list/details

- Use `ListView` for dense selectable collections and preserve standard selection, keyboard, touch, focus, and UI Automation behavior. Do not retemplate it unless required.
- Make a row's entire meaningful region selectable. Put secondary row actions in a context flyout or command bar rather than many always-visible tiny buttons.
- Keep columns aligned between header and rows. Give the primary name a flexible `*` column and a deliberate minimum. When the details pane appears, recompute responsive columns against usable list width, including row padding and scrollbar allowance.
- Do not set an explicit `Foreground` on `TextBlock`s inside selected `ListViewItem` templates unless a HighContrast-specific style omits that setter. List item states must be allowed to invert foreground correctly.
- Use empty states that say what is absent and provide one next action. Use `ProgressRing`/`ProgressBar` for work in progress rather than repeatedly changing labels.
- Refresh a list without disturbing the user: reconcile items by key, keep row state on the item model, and restore selection, scroll anchor, and expansion afterwards.
- Keep list headers visually and behaviorally connected to their values. If the screen needs sortable columns, expose sort controls as real buttons/menu items with an accessible name, current direction, and a keyboard path; static decorative labels do not create table semantics.

### Search, filter, sort, and selection

- Place search close to the collection it searches. Use `AutoSuggestBox` when suggestions, previous searches, or known values improve discovery; otherwise use a `TextBox` with a clear label and an explicit submit behavior where live filtering is too expensive or disruptive.
- Put the query, the filter controls, the result count and the selected count inside the collection's own container, above its scroll region, rather than in page chrome beside it. They describe that one collection, so they belong to it: they stay adjacent to the results they explain, they can be labelled once by the container instead of each carrying its own header, and in a drill-in layout they disappear together with the collection instead of lingering over a details pane they no longer act on. Hide the whole block in the loading, load-failure and genuinely-empty states, where the empty state owns the surface and there is nothing to query.
- Debounce expensive searches, preserve the query when users navigate away and back within the same task, show result count or no-results feedback, and never silently reinterpret a search as a destructive filter.
- Keep active filters visible, removable, and countable. Use `ComboBox`, `RadioButtons`, `ToggleButton`, `CheckBox`, or a `MenuFlyout` according to the number and relationship of choices. Provide a single clear-all action when several filters can be active.
- Make the current sort field and direction explicit. Do not use a visual arrow without a label or accessible name. Keep stable sort behavior where users expect it, and do not reorder a selected row unexpectedly during an edit.
- Produce filtered and sorted results by reconciling the visible collection, not by rebuilding it. Rebuilding on each keystroke resets the list and drops selection, scroll position, and expansion.
- Distinguish selection from activation: one action selects a row; double-click, Enter, or a clearly labeled command opens it when opening is meaningful. For multi-select, expose the selected count and commands that operate on it.
- Support `Ctrl+A` only when selecting the entire current collection is safe and expected. Use `Shift` range selection and `Ctrl` additive selection only where desktop collection conventions are appropriate.

### Tabs, documents, trees, and settings

- Use `TabView` for concurrently open documents, views, or work contexts that users may switch, reorder, close, or move between windows. Tabs are not a substitute for primary navigation. Give each tab a concise title, a close action when supported, and unsaved-state protection.
- Use `SelectorBar` or `Pivot` for a small set of alternative views of the same content; do not use them for a process that must be completed in order.
- Use `TreeView` for genuine hierarchical data that users browse or reorganize. Keep hierarchy shallow, expose expand/collapse with keyboard, and do not use a tree merely to simulate navigation.
- Use a settings page for persistent preferences. Group by user goal, apply simple settings immediately, make impactful changes clear, and provide defaults/reset only when they are understandable and reversible.
- Use `Expander` for optional detail that is useful in context but not necessary to scan. Do not hide primary content, validation, or the only way to continue behind an expander.

### Menus, flyouts, and context actions

- Use `MenuBar` for traditional application-wide menus when the product has many established desktop commands, complex keyboard access, or users expect menu discovery. Use `MenuFlyout` for a small contextual command list.
- Context menus supplement, never replace, discoverable primary commands. Mirror the most useful row/object actions, order them by frequency, separate destructive commands, and ensure every command is keyboard reachable.
- Use a `Flyout` for lightweight contextual details or a small related interaction. Use a `ContentDialog` when the decision blocks the app or requires focused completion. Use a `TeachingTip` for a temporary, non-blocking explanation tied to a target.
- Keep menus concise, use sentence-case verbs, group related commands with separators, and disable unavailable commands only when users benefit from seeing that capability exists; otherwise omit them.

### Feedback and errors

- Use `InfoBar` for significant, persistent, non-modal app/page state: unavailable connectivity, background completion, action required, or a page-level load failure. Use the built-in `Severity` values; they already handle icons, color, contrast, and assistive technology.
- Do not flash or rapidly replace InfoBars. Because an InfoBar shifts inline layout, reserve a stable location and ensure page content reflows coherently when it opens.
- Display validation close to the invalid field, describe the remediation, and prevent invalid submission where practical. A dialog is the wrong response to routine field validation.
- Use a `ContentDialog` only for a consequential decision, critical blocking condition, or focused add/edit task that is too small to merit a page. Set `XamlRoot` before `ShowAsync`; only one dialog may be open per window.
- Dialog copy: title gives the decision or task, body gives needed detail without repeating it, action labels name outcomes (`Delete`, `Keep`, `Cancel`) rather than generic `OK`. Always supply a safe, non-destructive close action. Primary/secondary action buttons appear left of the safe close action.

### Loading, empty, first-use, and recovery states

- Design every data surface for loading, empty, populated, partial-failure, offline, and permission-denied states before finalizing its layout. Keep the page structure stable as state changes so content does not jump unnecessarily.
- Use an indeterminate `ProgressRing` when duration is unknown; use a `ProgressBar` with a numeric/meaningful status when progress can be measured. Pair long operations with status text and a cancel action when cancellation is safe.
- Empty states should identify what is absent, explain the next useful action, and offer at most one prominent primary action. Do not use an empty state as a marketing panel or make it visually heavier than actual content.
- Use onboarding progressively: let users begin useful work quickly, teach a feature at the point it is needed, and allow tips to be dismissed or rediscovered. Do not block a capable user behind a feature tour.
- For destructive or potentially lossy actions, communicate scope and consequence before commit, offer Undo for reversible actions, and make recovery paths visible. Avoid confirmation dialogs for routine reversible actions because they train users to dismiss warnings.
- Preserve user input and selection on recoverable failures. Explain the problem in user terms, state whether work was saved, and offer a specific recovery action such as Retry, Sign in, Choose another file, or View details.

## Forms and Editing

- Prefer the most constrained correct control: `CalendarDatePicker` rather than a free-text date, `NumberBox` for numbers, `ToggleSwitch` only for immediate binary settings, `CheckBox` for independent choices, `RadioButtons` for a small mutually exclusive group, and a list control for five or more options.
- Use the input control's `Header` for an above-field label. For groups, use a `TextBlock` with a type-ramp style. Label every individual and grouped input for visual users and screen readers.
- Choose between an instantly updating settings form and a submit form. For submit forms, disable commit until required values are valid; use `PlaceholderText` only to demonstrate format, not as a replacement for the label; set `InputScope` where it improves touch keyboard input.
- Mark required input consistently and validate on change and on commit. Preserve entered values after a recoverable failure.
- Use approximately 24 epx between individual input controls and 48 epx between logical groups as a starting rhythm. Make forms responsive: one column first, then a limited number of columns only where scanning remains clear.
- In a long editing dialog, use a scrollable body with visible action buttons, preserve focus, and do not set a fixed inner width that can clip on smaller windows or enlarged text.
- Use a draft for submit-style editing. Populate it from the selected item, validate the draft as fields change, commit it only after Save succeeds, and leave the underlying display item untouched when the user cancels or a save fails.
- Present a field error immediately below or in the field's validation area, identify the invalid field and remedy in text, and set `AutomationProperties.HelpText` when a custom validation presentation would otherwise be missed by assistive technology. Move focus to the first invalid field only after an attempted submit, not while a user is still typing.
- When a dialog's primary action performs asynchronous work, prevent duplicate invocation and keep the dialog open until the operation either succeeds or reports a recoverable error. Use the primary-button click event and its deferral where supported; do not close first and make users reconstruct input after an avoidable failure.

## Motion and Interaction

- Built-in WinUI state transitions are the baseline. Add custom motion only when it explains a relationship: a pane opening, an item moving after a user action, progress, or a deliberate transition between distinct content.
- Keep motion short, consistent, interruptible, and secondary to the task. Do not animate static decoration, loop nonessential motion, or make an operation's completion ambiguous.
- Respect Windows animation settings and avoid relying on animation to communicate the result. Provide a persistent final state or textual status.
- Use standard pointer behaviors and hit targets. Any pointer-only action must also be invokable by keyboard; wrap an image or decorative surface in a real `Button` rather than attaching pointer handlers to non-focusable elements.

### Pointer, touch, pen, drag/drop, and multi-window

- Design for keyboard and pointer first, then verify touch and pen. Keep interactive controls comfortably targetable, avoid hover-only meaning, and ensure touch does not conceal required commands behind inaccessible hover affordances.
- Use `SwipeControl` for a small set of frequent touch actions on collection items, but retain equivalent visible/context-menu/keyboard actions. Do not place destructive behavior behind a swipe without an undo or confirmation appropriate to the risk.
- Support drag and drop only where it maps to a clear user intention such as reordering, moving, attaching, or importing. Give valid targets visible feedback, name the result before drop, support keyboard/menu alternatives, and never make drag/drop the only import path.
- Use the system file/folder pickers for file selection. Describe accepted types before opening the picker and show clear per-item outcomes after an import or drop.
- Use multiple windows when users benefit from side-by-side independent tasks or documents. Each window needs its own navigation, focus, title, state restoration strategy, and accessible name; do not create a second window merely to avoid designing a detail view.
- Restore a sensible window size, position, and session state where appropriate, but never restore windows off-screen or force users back into an unsafe transient state such as an unfinished destructive confirmation.

## Accessibility Non-Negotiables

### Semantics and Narrator

- Built-in controls are preferred because they expose UI Automation roles/patterns. Give icon-only, custom, image-based, or otherwise unlabeled controls an accurate `AutomationProperties.Name`.
- Use `AutomationProperties.HelpText` for useful extra guidance, not to repeat a visible label. Expose state changes through the correct built-in control/state wherever possible.
- A new custom control needs keyboard behavior, visible focus states, meaningful accessible names, and normally a dedicated `AutomationPeer`. Test it with UI Automation tools and Narrator before accepting it.
- Use `AutoSuggestBox` rather than hand-rolling a `TextBox` and suggestion list. If custom auto-suggest is unavoidable, connect controlled peers and raise the required UIA selection/layout events.

### Keyboard and focus

- Verify a logical, visual-order-aligned `Tab` path. Interactive elements are tab stops; labels and static text are not. Set `TabIndex` only to correct a real layout-order mismatch.
- Keep the standard WinUI focus visual. If a control is retemplated, retain equivalent focus states in Light, Dark, and HighContrast.
- Enter/Space invokes focused commands; Escape dismisses transient UI; arrows navigate within lists, trees, menus, and related control groups; Home/End work in lists and scrolling regions.
- Do not set initial focus to a destructive command. On a task page, target the primary meaningful region/action; on a dialog, let a focusable form field receive focus when appropriate.
- Add accelerators for high-frequency commands and access keys for important in-window controls where their localization and discoverability can be supported.
- Implement `F6`/`Shift+F6` cycling for major app regions when the shell becomes complex (for example: navigation, page commands or search, main content, and details). It is not automatic; each target should have an accessible name.

### Contrast, scaling, and localization

- Test Light, Dark, and all built-in contrast themes. Avoid hard-coded colors. In HighContrast dictionaries, use compatible `SystemColor` foreground/background pairs; reserve `SystemColorGrayTextColor` for disabled content and `SystemColorHotlightColor` for hyperlinks.
- Do not broadly disable `IsTextScaleFactorEnabled`; design layout that wraps and expands at Windows text scales. Test Windows display scale, text-size scale, Magnifier, and minimum window widths.
- Ensure values do not rely only on red/green, icons have text or names, and images containing meaningful text have equivalent automation names.
- Allow localization growth and RTL layout. Avoid text-sized fixed widths, hard-coded left/right semantics, and copy that relies on English word length.

## Implementation Checklist

Before starting a page or control:

- Identify the user task, primary action, success state, empty/loading/error state, and destructive or irreversible operations.
- Choose the existing WinUI control/pattern first: `NavigationView`, `CommandBar`, `ListView`, `InfoBar`, `ContentDialog`, form controls, `TeachingTip`, `AutoSuggestBox`, `TabView`, or `BreadcrumbBar`.
- Decide which metadata is essential at narrow width and which progressively hides or moves to details.
- Decide the stable key for every collection, and decide where each piece of interaction state lives — expansion, selection, scroll anchor, checked state, edit drafts, and open transient UI all belong to the model, not to a container.
- Define semantic local styles/resources; add a new one only if it describes a real recurring role. Include Light, Dark, and HighContrast behavior where the resource affects visible UI.

Before considering a UI feature done:

- Resize from small through large width, including relevant side panes, scrollbars, and compact/minimal NavigationView states.
- Run keyboard-only: Tab/Shift+Tab, arrows inside lists, Enter/Space actions, Escape dismissal, common accelerators, and Back navigation.
- Run Narrator (`Win+Ctrl+Enter`) through the key flow. Confirm names, control types, selected/disabled states, validation, messages, and icon-only actions are announced sensibly.
- Test Light, Dark, each contrast theme, display scale, text-size scale, and a long/localized label. Confirm there is no clipping, unreadable selected row, invisible focus, or reliance on color alone.
- Trigger a data refresh while a node is expanded, a row is selected part-way down a scrolled list, a row flyout is open, and a field is mid-edit. Nothing may collapse, jump, deselect, close, or lose typed input.
- Run Accessibility Insights for Windows FastPass and use Live Inspect for custom controls or unusual templates. Treat critical findings as build-blocking.
- Compare the final surface with the WinUI 3 Gallery before introducing custom styles or templates.

## Practical Implementation Gotchas (Verified Against Real Behavior)

These are concrete, verified WinUI 3 implementation pitfalls discovered while applying the guidance
above, kept here because they are easy to reintroduce and not obvious from the API surface or
documentation alone.

### ContentDialog: subclassing, styling, and sizing

- A `ContentDialog` defined as its own type (an `x:Class`-based subclass with its own `.xaml` and
  code-behind, as opposed to a plain `new ContentDialog { ... }` instance built inline) does not
  reliably receive the implicit default style at runtime, even though the XAML designer preview
  renders it correctly. This is a known, long-standing WinUI/UWP issue. The visible symptom is
  incorrect chrome: square corners instead of the theme's rounded corners, and a different/lighter
  smoke-layer overlay than a plain `ContentDialog` shows. Fix: explicitly set
  `Style="{StaticResource DefaultContentDialogStyle}"` on every subclassed `ContentDialog`'s root
  element. Plain, non-subclassed `ContentDialog` instances are not affected and do not need this.
- Setting `HorizontalAlignment`, `VerticalAlignment`, `HorizontalContentAlignment`, or
  `VerticalContentAlignment` on a `ContentDialog` has no effect on where the dialog is positioned.
  The control already fills the entire smoke-layer popup; its own alignment properties are
  meaningless in that context, and the visible card's centering is handled entirely inside the
  default control template, independent of any property on the `ContentDialog` instance.
- Do not set `Width`, `MinWidth`, or `MaxWidth` directly on a `ContentDialog`'s root element to
  control its size. An explicit, narrow fixed-width constraint on the `ContentDialog` itself can
  break the default template's internal centering logic, causing the dialog to render flush
  against one edge of the window instead of centered - even though its corners/chrome still look
  correct. Let the dialog size itself from the theme's default `ContentDialogMinWidth`/
  `ContentDialogMaxWidth`, and if a specific screen genuinely needs more horizontal room, widen the
  individual inner controls (for example, an explicit `Width` on a `TextBox` or `ComboBox`) rather
  than constraining the dialog root.

### x:Bind scope and change notification

- `x:Bind` cannot resolve a named root-level element's compiled code-behind type from inside a
  nested `DataTemplate` that declares its own `x:DataType`. A binding such as
  `{x:Bind RootElementName.SomeProperty}` written inside that template resolves the named element
  only as its base declared XAML type (for example `Page`), not the actual compiled subclass, and
  fails to compile ("property not found on type"). Use a classic `{Binding ElementName=...,
  Path=...}` for that specific outward-reaching reference; `x:Bind` continues to work normally for
  everything else in the same template, including the templated item's own properties.
- `x:Bind Mode=OneWay` can observe a plain `DependencyProperty` change on a named element, not only
  properties from an `INotifyPropertyChanged` source. Binding directly to another element's
  property (for example, another control's text, or a `Frame`'s `CanGoBack`) updates automatically
  without any extra plumbing, as long as the source property is a real `DependencyProperty`.
- Do not assume an `x:Bind Mode=TwoWay` push-back has already updated the model by the time a
  same-element event handler runs (for example, `IsOn="{x:Bind IsEnabled, Mode=TwoWay}"
  Toggled="ToolEnabled_Toggled"`). Both the compiler-generated push-back and the user's own handler
  subscribe to the same event, and their relative firing order is an implementation detail of
  generated binding/connection code, not a guaranteed contract - a handler that reads the bound
  model property can see the *previous* value. The symptom is silent and easy to misdiagnose: the
  control visibly changes state, no exception is thrown, but the action the handler performs (save,
  commit, recompute) behaves as if the toggle never happened. Read the control's own current value
  directly from `sender` inside the handler (for example, `((ToggleSwitch)sender).IsOn`) and assign
  it to the model explicitly before doing anything that depends on it, instead of trusting the
  two-way binding to have already landed.

### CommunityToolkit.Mvvm on a Page or UserControl

- A `Page` or `UserControl` cannot also derive from `CommunityToolkit.Mvvm`'s `ObservableObject`
  base class, since it already derives from the WinUI base type and C# does not support multiple
  inheritance. To use `[ObservableProperty]`/`[RelayCommand]` source generators on a `Page` or
  `UserControl`, apply the class-level `[INotifyPropertyChanged]` generator attribute instead of
  inheriting the base class.
- The `[RelayCommand]` generator derives its generated command property's name from the method
  name, stripping a trailing `Async` (for example, `SaveAsync()` produces `SaveCommand`). If a XAML
  element in the same class already has that exact `x:Name`, this is a duplicate-member compile
  error. Give interactive elements a distinct name (for example, `SaveButton`) before wiring
  `Command="{x:Bind SaveCommand}"` to them.

### Shell and Windows App SDK version

- The `TitleBar` XAML control (used for the modern `TitleBar` + `NavigationView` + single `Mica`
  backdrop shell) requires Windows App SDK 1.7 or later. Earlier versions do not expose the type at
  all (`Unknown type 'TitleBar'` at compile time) - verify or bump the `Microsoft.WindowsAppSDK`
  package reference before adopting this shell pattern.
- Unpackaged WinUI 3 desktop apps do not get a `Mica` backdrop for free. `Window.SystemBackdrop`
  must be set explicitly (for example, `<MicaBackdrop />`); do not assume it is on by default.

### List/details row shape

- A `ListView` row built from several fixed-width columns with a header-label row above it (a
  spreadsheet layout) is the signal to use `DataGrid` instead, not a reason to keep hand-rolling
  column logic in a `ListView`. If there is no genuine need for column sorting or resizing, avoid
  discrete per-breakpoint column hide/show logic entirely - it reads as an abrupt "pop" as the
  window resizes. Prefer a single flexible title-plus-subtitle row (the primary name as a bold
  title, secondary metadata as one ellipsis-trimmed subtitle line below it, `TextTrimming=
  "CharacterEllipsis"`) so the row reflows continuously as the container narrows, instead of
  columns appearing and disappearing at fixed width thresholds.

### Collection reset destroys more than the list

- `ObservableCollection<T>.Clear()` raises one `NotifyCollectionChangedAction.Reset` rather than a
  remove notification per item. Items controls handle a reset by discarding every realized container
  and rebuilding from scratch, so a "just refresh the list" call silently takes selection, scroll
  position, expanded rows, keyboard focus, in-progress text, and any open row flyout with it. The
  symptom is usually reported as the UI "collapsing" or "jumping to the top" on save or on a
  background update, and it is easy to misread as an animation or virtualization problem. Assigning a
  new collection instance to `ItemsSource` has exactly the same effect. Reconcile the existing
  collection against stable keys instead, and prefer updating a surviving item's properties over
  replacing the item, because a replacement is a different item to the control even if it compares
  equal by value.

### Model-level state and serialization

- A collection or object property with an `init` accessor is not safe from instance replacement just
  because nothing in application code reassigns it. Reflection-based `System.Text.Json` treats `init`
  as a real setter, so deserializing the owning type builds a brand new instance for that property and
  assigns it - discarding whatever instance existed from the property's own field initializer. If a
  constructor wired an event subscription (for example, `CollectionChanged`) to that original
  instance, the subscription is now attached to a discarded object and never fires again for anything
  deserialized from disk; hand-constructed instances built entirely in memory are unaffected, because
  their property is never reassigned. The symptom is not a crash or a visible reset: it is silently
  wrong derived state (a stale computed count, order, or index) that only appears after the type has
  been round-tripped through serialization at least once, which makes it easy to mistake for a logic
  bug in whatever computed the derived value.
- The obvious-looking fix - dropping `init`/`set` entirely to get a plain get-only property - trades
  that bug for a worse, silent one: by default `System.Text.Json` skips a property with no setter at
  all during deserialization, so the collection is never populated and ends up permanently empty after
  a load, with no exception. A get-only collection property is only populated in place (`Add`ed into
  the existing instance, preserving any subscription) when it is explicitly annotated
  `[JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]` (.NET 8+); without that
  attribute, get-only is not a safe default either. Verify the actual round-trip behavior with a real
  serialize/deserialize test before trusting either assumption - both plausible-looking fixes for this
  class of bug fail silently in opposite directions (stale data vs. missing data).

### Expansion state does not survive a rebuild, and recycles onto the wrong item

- `TreeViewNode.IsExpanded` lives on the node, so clearing and rebuilding `RootNodes` discards the
  entire expansion structure even when the new nodes represent identical data. The lazy-realization
  pattern (`HasUnrealizedChildren` plus clearing `Children` on collapse) compounds this: expanded
  descendants no longer exist to restore. If a tree is rebuilt at all, capture the expanded keys
  first, then re-realize and re-expand level by level after the rebuild.
- An `Expander` inside a `DataTemplate` whose `IsExpanded` is not bound two-way is worse than
  stateless. Containers are recycled for different items as the list scrolls or filters, so a reused
  row can appear expanded because a *previous* item was expanded. This reads as random expansion
  rather than lost expansion, and it will not reproduce in a short list where no recycling occurs.

## Canonical Resources and Examples

### Primary design and implementation references

- [Fluent 2 design system](https://fluent2.microsoft.design/): language, Figma kits, design foundations, and platform component references. Use its Windows material alongside Windows-specific guidance.
- [Windows app design overview](https://learn.microsoft.com/windows/apps/design/): current entry point for Windows design principles and guidelines.
- [WinUI 3 overview](https://learn.microsoft.com/windows/apps/winui/winui3/): WinUI 3 is the recommended native Windows desktop framework.
- [Windows controls and patterns](https://learn.microsoft.com/windows/apps/develop/ui/controls/): canonical control selection index.
- [WinUI 3 Gallery](https://apps.microsoft.com/detail/9P3JFPWWDZRC) and [source](https://github.com/microsoft/WinUI-Gallery): current working examples and templates; inspect these before creating a bespoke control.

### Direct guidance by topic

- [NavigationView](https://learn.microsoft.com/windows/apps/develop/ui/controls/navigationview) and [navigation basics](https://learn.microsoft.com/windows/apps/design/basics/navigation-basics)
- [TitleBar](https://learn.microsoft.com/windows/apps/develop/ui/controls/title-bar) and [system backdrops](https://learn.microsoft.com/windows/apps/develop/ui/system-backdrops)
- [Mica](https://learn.microsoft.com/windows/apps/design/style/mica), [Acrylic](https://learn.microsoft.com/windows/apps/design/style/acrylic), and [layering/elevation](https://learn.microsoft.com/windows/apps/design/signature-experiences/layering)
- [XAML theme resources](https://learn.microsoft.com/windows/apps/develop/platform/xaml/xaml-theme-resources), [color](https://learn.microsoft.com/windows/apps/design/signature-experiences/color), and [typography](https://learn.microsoft.com/windows/apps/design/signature-experiences/typography)
- [Responsive design](https://learn.microsoft.com/windows/apps/design/layout/responsive-design), [breakpoints](https://learn.microsoft.com/windows/apps/design/layout/screen-sizes-and-breakpoints-for-responsive-design), and [list/details](https://learn.microsoft.com/windows/apps/develop/ui/controls/list-details)
- [ListView and GridView](https://learn.microsoft.com/windows/apps/develop/ui/controls/listview-and-gridview), [ItemsView](https://learn.microsoft.com/windows/apps/develop/ui/controls/itemsview), and [scroll controls](https://learn.microsoft.com/windows/apps/develop/ui/controls/scroll-controls)
- [Data binding in depth](https://learn.microsoft.com/windows/apps/develop/data-binding/data-binding-in-depth), [CommandBar](https://learn.microsoft.com/windows/apps/develop/ui/controls/command-bar), [forms](https://learn.microsoft.com/windows/apps/develop/ui/controls/forms), [InfoBar](https://learn.microsoft.com/windows/apps/develop/ui/controls/infobar), and [ContentDialog](https://learn.microsoft.com/windows/apps/develop/ui/controls/dialogs-and-flyouts/dialogs)
- [TreeView](https://learn.microsoft.com/windows/apps/develop/ui/controls/tree-view), [Expander](https://learn.microsoft.com/windows/apps/develop/ui/controls/expander), and [ObservableCollection&lt;T&gt;](https://learn.microsoft.com/dotnet/api/system.collections.objectmodel.observablecollection-1)
- [Accessibility overview](https://learn.microsoft.com/windows/apps/design/accessibility/accessibility-overview), [keyboard accessibility](https://learn.microsoft.com/windows/apps/design/accessibility/keyboard-accessibility), [accessible text](https://learn.microsoft.com/windows/apps/design/accessibility/accessible-text-requirements), [contrast themes](https://learn.microsoft.com/windows/apps/design/accessibility/high-contrast-themes), and [accessibility testing](https://learn.microsoft.com/windows/apps/design/accessibility/accessibility-testing)
- [Accessibility Insights for Windows](https://accessibilityinsights.io/docs/windows/overview)