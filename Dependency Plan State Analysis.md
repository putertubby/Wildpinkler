# Dependency plan state and action analysis

## Purpose

This document records the plan-page lifecycle, state-sensitive row actions, the distinction
between identification and replacement decisions, and the contextual recommendation behavior.

The ordinary progression is **Unmatched → To download → Ready → Installed**. In this flow,
**Installed** is the end state: the mod is present in the target profile. **Conflict** is a
blocking condition that must be resolved or intentionally set aside. **Excluded** and **Satisfied
by another mod** are set-aside decisions, not additional installation stages. Replacement is
represented by updating the original row to the replacing mod; it remains in the active lifecycle.

An action is not determined by the displayed state alone. The current policy also checks the
underlying resolver status, download state, row decision, page URL, and (for the mod-manager
download route) whether the row is the root mod.

## States and actions

| Displayed state | Available actions (conditional on row data) | Suggested action name | Goal |
|---|---|---|---|
| **Unmatched — ambiguous / needs matching** | **Find this mod**; **Identify or satisfy with…**; **Open site page** when available; **Download from mod page** when eligible; **Set aside**; **Undo changes** if changed. A qualified recommendation may appear above the other menu actions. | **Identify as…** (library mod); **Satisfy with…** (another plan mod) | Identify which mod the requirement refers to, or confirm that another mod already in the plan covers it. Then progress to **To download** or **Ready**, or intentionally set it aside. |
| **Unmatched — fetch failed** | **Retry lookup** when not already retrying; open the site page / download from it when eligible; set aside; undo changes if changed. | **Retry lookup**; **Find this mod**; **Set aside** | Retry the lookup, handle it manually, or intentionally set it aside. |
| **Unmatched — gone from site** | Open the site page or download from it when eligible; set aside; undo changes if changed. There is no retry-lookup action for terminal not-found. | **Open site page**; **Set aside** | Find a suitable alternative or omit the requirement from the plan. |
| **Conflict** | **View conflict** when a conflicting row exists; identify/satisfy if the resolver status permits; open/download from the site page when eligible; **Set aside**; **Undo changes** if changed. | **View conflict**; **Identify as…** / **Satisfy with…**; **Set aside** | Resolve the incompatible choice or intentionally set aside one side so the conflict no longer blocks installation. |
| **To download** | Open/download from the site page when eligible; set aside when no download is underway; undo changes if changed; **Retry download** after a failed download. For a file mismatch, choose and apply a compatible file in Details. | **Choose file**; **Download from mod page**; **Retry download** | Choose the required file and acquire its archive. A local archive advances the row to **Ready**. |
| **Ready** | Identify/satisfy only when the resolver status permits; open/download from the site page when eligible; set aside when no download is underway; undo changes if changed. | **Install selected** (page action); **Satisfy with…** where applicable | The archive is available locally. Select it for installation when the plan has no blocking rows. |
| **Installed** | Open site page; update when available; identify/satisfy when the resolver status permits; replace; set aside; undo changes if changed. A qualified recommendation (e.g. **Replace with…**) may appear above the other menu actions. | **Update to latest**; **Identify as…** / **Satisfy with…**; **Replace with…** | The requirement is already satisfied in the target profile. Updates or replacement are optional decisions, not required lifecycle steps. After replacement, the same row shows the replacement identity and its active install state. |
| **Excluded** | **Restore to plan**; **Undo changes** if changed; open site page when available. No download, match, update, conflict, or set-aside action. | **Restore to plan**; **Undo changes** | Restore the row and dependent rows excluded with it, or leave it intentionally omitted. |
| **Satisfied by another mod** (internal `Folded`) | **Undo changes**; open site page when available. No download or other mutating action. | **Undo satisfaction**; **Open site page** | Keep the requirement covered by another plan mod, or undo the recorded decision. |
| **Replacement active state** | The row keeps the replacement's lifecycle actions, plus **Undo changes**. It shows **Replaces: [original mod]** and the Details pane describes the replacement decision. | **Undo changes** | Install or retain the replacement in the ordinary flow; reset restores the original mod and removes only newly added dependencies. |

### Where actions live

- The row checkbox selects a mod for this plan's download/install operation; it does not advance
  the row's lifecycle state.
- **Download selected** and **Install downloaded** are page-level actions rather than row-menu
  commands. A completed download moves a row to **Ready**.
- **Apply file** is in the Details pane for a file-mismatch row; choosing and applying a suitable
  file clears the mismatch and can make the row downloadable.
- **View conflict** navigates to the conflicting row; it does not itself resolve the conflict.
- Identify, satisfy, replace, exclude, include, and reset operations record or undo plan decisions.
- The identify/satisfy chooser uses one profile-picker-style list with a **Library / Plan** source
  selector. It shows the lifecycle status used by the plan row and adds version, archive/profile,
  and site identity details to distinguish similar names. Search uses Nexus' `gsearch` query
  parameter and falls back to the source mod key if a requirement has no display name.
- Nexus identity is the site plus its numeric mod ID; the ID is authoritative and the version is
  not part of identity. An installed match with the same Nexus ID remains installed even when the
  local version differs; the version comparison instead indicates whether an update is available.
  Plan reconciliation removes duplicate rows for the same remote identity, and the Details pane
  exposes the remote ID and the matched local library entry for diagnosis. Saved manual match-link
  application remains a separate, deferred change. The mod library repairs malformed Nexus keys
  only when a trusted HTTPS Nexus mod URL supplies one unambiguous numeric ID; conflicting or
  insufficient URL evidence is logged and left untouched. New invalid Nexus identities are
  rejected at provider, library-store, match-store, and mod-list validation boundaries.

## Install readiness

The current install gate blocks when a row is unmatched, conflicting, or is a required
**To download** row without a local archive. Excluded and satisfied rows do not block. A replaced
requirement is represented by an active row, so the replacement's lifecycle state determines
whether it blocks. The page can proceed when there are no blocking rows, but the Install button
also requires at least one selected row with a local archive. If every row is already installed or
set aside, the plan may be ready but have nothing to install.

The intended completion rule is therefore:

1. Each in-scope requirement reaches an accepted outcome: **Installed**, intentionally **Excluded**,
   **Satisfied by another mod**, or represented by its replacement in the normal active lifecycle.
2. No row blocks installation.
3. At least one selected, locally available archive remains to install; otherwise report that
   there is nothing to install instead of implying that an install can start.

## Naming recommendations

Prefer concise, goal-oriented verbs in the row menu:

- **Find this mod** rather than **Search on Nexus**
- **Retry lookup** rather than **Retry fetch**
- **Choose file** / **Apply file** for resolving a file mismatch
- **Download from mod page** rather than **Use Mod Manager Download**
- **Identify as…** when the requirement is a particular library mod
- **Satisfy with…** when a different mod already in the plan fulfills the requirement
- **View conflict** rather than **Review conflict**
- **Set aside** rather than **Exclude**
- **Restore to plan** rather than **Include**
- **Replace with…** rather than **Obsoleted by**
- **Undo changes** for a general reset; use a more specific label such as **Undo satisfaction** if the
  action is unambiguous in that row

Keep explanation and consequences in tooltips, Details, or a confirmation surface where needed;
do not make menu labels carry all the implementation detail.

## Action model and recommendations

The three actions represent different user assertions and must remain distinct:

1. **Identify as…** means “this ambiguous requirement is this particular library mod.” It stores
   an identity association (`match` in the persisted schema).
2. **Satisfy with…** means “this other mod already in the plan covers this requirement.” It stores
   a plan alias (`fold` internally), sets the requirement aside, and keeps the target mod.
3. **Replace with…** means “this newer/current mod replaces the old requirement.” The original
   row is updated to show the replacement's identity, version, file, and install state, while a
   **Replaces: [original mod]** line and the Details pane explain what was replaced. The row remains
   active and **Undo changes** restores its original identity and state. For library replacements,
   only newly added dependencies are removed on undo; a replacement already in the plan is merged
   into the original row and restored as a separate row when undone. A compatible `obsolete` link is
   retained for history where the replacement has a site identity.

**Verdict on “Folded in”:** it is an implementation/history term, not a good user-facing name for
identifying a mod. Identification does not combine mods. Use **Identify as…** for identity,
**Satisfy with…** for plan coverage, and **Replace with…** for replacement. Keep the existing
persisted `match`, `fold`, and `obsolete` values for backwards compatibility.

Recommendations are deliberately deterministic and reviewable:

- A single valid prior accepted association with the same source name may be recommended when its
  local or plan target still exists.
- Otherwise, a unique normalized exact-name library match may be recommended when its game
  association is compatible with the current plan. Partial matches are not recommendations;
  duplicate exact matches are treated as ambiguous.
- Set-aside, conflicting, unavailable, self, and ancestor-cycle plan targets are not recommended.
- Installed rows with a single accepted *obsolete* (replacement) relationship keyed to the row's
  current site+mod identity receive **Recommended: Replace with…**: a current plan row that still
  passes the replacement eligibility checks wins; otherwise a single library mod matching the
  remembered target key or name is offered. Stale targets, already-replaced rows, and ambiguous
  history (0 or >1 candidates) produce no recommendation.
- The row shows **Recommended: Identify as…**, **Recommended: Satisfy with…**, or
  **Recommended: Replace with…**. Clicking it **applies the previously chosen target directly**
  (no chooser dialog) — the recommendation is only shown when exactly one remembered target is
  valid, so there is nothing left to choose. The target is revalidated first: if it no longer
  exists or fails the same eligibility checks the action itself applies, nothing is applied and
  the user can act on the row directly. Applying the recommendation (like performing the manual
  action) **confirms the decision for the plan session**, so later rescans re-apply it
  automatically (see *Sticky decisions* below).
- To pick a *different* target, the user invokes the regular action (which opens the chooser).
  A recommendation never persists a decision by itself; it only re-applies a decision the user
  already made.

**Sticky decisions (a coherent, confirmation-gated lifecycle).** The lifecycle of every
persisted decision is the same, per plan:

1. **New plan** — every persisted decision (Satisfy with / Replace with / Identify as) is shown
   **only as a recommendation** on the affected row. Nothing is auto-applied; the user decides.
2. **Confirmation** — when the user applies the recommendation or performs the manual action
   (fold / replace / match dialog), the plan records the decision as *confirmed for this plan
   session* under the decision's link key.
3. **Rescans of the same plan** — the sticky pass re-applies **only confirmed** decisions
   automatically:
   - **Satisfy with…** (`fold`, id-keyed or contextual/name-only): the requirement row is set
     aside as **Folded** and the target row records the satisfied requirement.
   - **Replace with…** (`obsolete`): applied in place — the replacement (a plan row, or a row
     added from the library) becomes the referenced row and the target row is removed, exactly
     the state the manual action produces.
4. **Stale target** — a confirmed decision whose target is no longer a valid choice is not
   applied; the row stays active and the recommendation layer re-offers it (a "previously
   accepted" proposal) instead of silently dropping the decision.
5. **Undo / Reset** — removes the remembered link and the plan-session confirmation, so the
   decision is no longer sticky; the recommendation keeps offering it.
6. **New plan / full re-resolve** — the confirmation set is cleared: every persisted decision
   is a recommendation again until the user confirms it in the new plan session.

**Identify as…** and **Set aside (Excluded)** were already sticky in a different sense: the
resolver itself consumes match links during the walk (the local mod's site reference), and
exclusions carry across rescans through the resolver's skip keys. They therefore never needed
the sticky pass; the confirmation gate applies to the fold/obsolete decisions that pass runs on
the page's reconcile.

Matching is deterministic and idempotent: the same rows, links, and confirmations always
re-apply to the same state. (The sticky pass that implemented this was removed in the
edge-based rewrite; decisions now live in [PlanDecisionStore](./src/managed/Wildpinkler.App/Services/Mods/PlanDecisionStore.cs)
and are re-applied by [PlanDecisionApplier](./src/managed/Wildpinkler.App/Services/Mods/PlanDecisionApplier.cs)
after every resolve.)

This refines the earlier backlog decision "No automatic folding (matching) on initial scan —
only use user cues": on a new plan nothing is auto-applied, and user cues (confirmed decisions)
*are* applied automatically on later rescans of the same plan, while heuristic/AI proposals
remain review-only.

This is a suitable extension point for a future assistant: it can provide a proposal plus evidence
to the same review surface, but must not silently mutate a plan. Store provenance and confidence for
future AI-derived proposals and require explicit confirmation before saving them.

## Closed policy gaps

The row policy first distinguishes set-aside states from active rows. Excluded rows expose **Include**;
folded/satisfied rows expose **Reset**; both may retain informational **Open site page** and **Reset**
when changed. They do not expose conflict review, downloads, matching, updates, or exclusion. A
replacement is not set aside: it retains actions based on the replacement's active state and exposes
**Reset** to undo the replacement. Tests cover set-aside policy and replacement reset behavior.

## Code references

- Lifecycle states, labels, section assignment, and install-blocking rule:
  [DependencyPlan.cs](./src/managed/Wildpinkler.App/Models/DependencyPlan.cs)
- Contextual action availability:
  [PlanRowActionPolicy.cs](./src/managed/Wildpinkler.App/Models/PlanRowActionPolicy.cs)
- Recommendation generation and evidence:
  [PlanRowRecommendation.cs](./src/managed/Wildpinkler.App/Models/PlanRowRecommendation.cs)
- Persisted decisions and automatic re-application (replaced the sticky pass):
  [PlanDecisionStore.cs](./src/managed/Wildpinkler.App/Services/Mods/PlanDecisionStore.cs),
  [PlanDecisionApplier.cs](./src/managed/Wildpinkler.App/Services/Mods/PlanDecisionApplier.cs)
- Install-readiness reporting and page-level operations:
  [DependencyPlanPage.xaml.cs](./src/managed/Wildpinkler.App/Pages/DependencyPlanPage.xaml.cs)

## Name-only requirements and remote identity

A requirement with no provider-supplied key is still a real plan row, but it is not a remote mod
identity. The row now has a plan-local `EntryKey`; its `RemoteIdentity` is absent until an actual
provider key exists. Display names are never substituted into `RemoteRef.ModKey`, provider URLs,
download links, or ID-keyed match records. The row remains searchable and can be identified or
satisfied through the plan picker. Remote-only actions stay unavailable until a genuine identity
is available.

An explicit match for a uniquely contextualized name-only requirement can be remembered separately
from provider-keyed links. Its key is based on the parent provider/game/mod identity and the
normalized requirement name. Nexus parent and target IDs still have to be positive numeric IDs.
Repeated same-name requirements under the same parent have ambiguous context and therefore remain
plan-local; no decision is auto-applied based only on a display name. A remembered local match or
**Satisfy with…** choice is presented as an explainable recommendation only while its target still
exists and passes the current eligibility checks. The user reviews and confirms the recommendation
again; a stale or ambiguous target is not silently applied.

The match store's version 2 format adds a separate contextual-link table and preserves existing
site+mod-key decisions. Unknown identities do not use a magic numeric sentinel. Reset removes the
contextual decision associated with a user-applied name-only match or satisfaction.
