# View-Context Visibility Hints (UnityGLTF)

Runtime, import, and export support for two view-context visibility extensions:

- **`KHR_node_visibility_hint`** (node): a `role` (`both` | `first_person_only` | `third_person_only`, plus
  custom vocabulary) + optional `label`, applying to a node **and its subtree**.
- **`KHR_mesh_primitive_visibility_hint`** (mesh primitive): the same `role`/`label`, self-only (per primitive).

> **Status: non-ratified.** Both plugins are **disabled by default** and marked `[NonRatifiedPlugin]`. Data
> shapes and behavior may change as the extensions evolve.

This is a **standalone, generalized** plugin: it works on any asset that carries the hints and is **not** gated
on character detection. It has **no dependency** on the KhrCharacter plugin and makes **no changes** to core
UnityGLTF (it is auto-discovered by reflection like every other plugin).

## Composition with `KHR_node_visibility`

These hints **build on top of** core `KHR_node_visibility` (already fully supported in core UnityGLTF — this
plugin never reimplements it). Core `KHR_node_visibility` maps to `GameObject.SetActive(false)`; these hints
operate on `Renderer.enabled` / material slots of otherwise-active objects. The effective visibility is the
logical **AND**: an inactive node never renders regardless of any hint.

## What it does

On import (when the plugin is enabled) of a glTF carrying either hint, the plugin attaches components to the
imported scene root:

| Component | Responsibility |
|---|---|
| `ViewContextController` | First/third-person switch. Node hints toggle `Renderer.enabled`; primitive hints swap a sub-mesh material. Set `Mode` to switch context; subscribe to `OnViewContextChanged`. |
| `NodeVisibilityHintSet` | Authored node-hint entries + subtree-inheritance resolution (a descendant hint overrides an ancestor for its subtree). |
| `PrimitiveVisibilityHintSet` | Authored primitive-hint entries (per shared mesh + sub-mesh) resolved onto every renderer that uses the hinted mesh. |
| `InvisibleMaterialCache` | Shared, fully-transparent material used for the primitive material-swap. |

### The per-primitive material swap is a *representation*

A single `Renderer` cannot hide one sub-mesh via `Renderer.enabled`, so a hidden primitive is realized by
swapping `renderer.sharedMaterials[subMesh]` to a cached invisible material (the same trick UnityGLTF's
`MaterialVariants` uses). This is **Unity's representation of the hint data, not a normative runtime behavior**,
and its exact appearance is render-pipeline dependent (the extension carries only a visibility `role`, not a
material). Export **never** infers visibility from live material state — it emits the authored `role` from the
serialized hint entries, so a slot currently swapped to the invisible material still round-trips correctly.

## Enabling the plugins

Both are `EnabledByDefault => false`. Enable them before importing/exporting:

- **Project-wide:** Project Settings → UnityGLTF → Import / Export → enable **"KHR Visibility Hints (View Context)"**.
- **Per-import/export (code):** enable the plugin on the `GLTFSettings` you pass to the importer/exporter.

When disabled, assets still import/export as plain glTF; the unknown extensions round-trip as `DefaultExtension`
and are never rejected.

## Runtime usage

```csharp
// After importing (or on a rehydrated prefab) the scene root carries a ViewContextController.
var view = importedRoot.GetComponent<ViewContextController>();

view.Mode = ViewContextController.ViewContext.FirstPerson; // hide third_person_only, show first_person_only
view.OnViewContextChanged += ctx => Debug.Log($"View context is now {ctx}");
```

## Editor tooling

An **opt-in editor layer** (`Editor/Scripts/VisibilityHints/`, assembly `UnityGLTF.VisibilityHints.Editor`,
Editor platform only) makes the hints viewable, authorable, and testable by hand. It adds **no** runtime
components and makes **no** changes to core UnityGLTF.

- **Authorable inspectors** on the hint-set components — the same inspector shows imported entries and lets you
  add / edit / remove them:
  - `NodeVisibilityHintSet` — a list of `(Node, role, label)` rows. The **role** is a popup of
    `both` / `first_person_only` / `third_person_only` with a **Custom…** option that reveals a text field for
    the open role vocabulary.
  - `PrimitiveVisibilityHintSet` — a list of `(Mesh, sub-mesh, role, label)` rows, plus a **"Collect child
    renderers"** button that scans the subtree and appends any missing `(mesh, sub-mesh)` slots as role `both`
    for you to set (in the style of `MaterialVariants`).
  - Edits are written through the serialized backing list (undoable) and never toggle a live renderer/material,
    so editing an imported hint changes the serialized `Entries` (hence export and the next Play-mode resolve).
- **Play-mode preview:** the `ViewContextController` inspector shows a **Mode** popup (ThirdPerson / FirstPerson)
  in Play mode, so you can flip the view context live and watch third-person-only renderers disable and hinted
  sub-meshes swap to the invisible material (and restore).
- **Sample generator:** **GameObject → UnityGLTF → Generate Visibility Hints Sample** builds a small
  Head + Body hierarchy (Head → `third_person_only`; a Body sub-mesh → `first_person_only`), leaves it in the
  scene for inspection, and exports a `VisibilityHintsSample.glb` (with the export plugin enabled on a fresh,
  isolated default-settings instance) to a folder you choose. Re-import it with the import plugin enabled to see
  the components restored.

## Round-trip notes

- **Roles are open vocabulary.** `both` / `first_person_only` / `third_person_only` map to the runtime view
  roles; any unrecognized role is treated as `both` (never hidden) with a warning
  (`ViewContextController.ParseRole`). Unknown fields on the extension survive via lossless `RawData` passthrough.
- **`role` is required, `label` is optional (minLength:1).** Export skips a hint with an empty/missing role
  (with a warning) and omits an empty label from the wire.
- **Primitive hints are per-mesh.** The extension lives on the shared `meshes[m].primitives[i]`, so it applies
  to every node that references that mesh. Runtime/export key by the Unity `Mesh` + sub-mesh index.
- **Declared used, never required.** Both extensions are emitted into `extensionsUsed` (not
  `extensionsRequired`), so a plain viewer still loads the asset.
- **Serialize → rehydrate.** Authored entries persist in hidden serialized fields; `NodeVisibilityHintSet` /
  `PrimitiveVisibilityHintSet` re-resolve on `Awake` so an editor-imported prefab is live without a fresh import.

## Key files

- Schema (wire classes + factories, `namespace GLTF.Schema`): `Schema/KHR_node_visibility_hint.cs`,
  `Schema/KHR_mesh_primitive_visibility_hint.cs`; names/roles: `Schema/VisibilityHintExtensionNames.cs`;
  factory registration: `Schema/VisibilityHintSchemaRegistration.cs`
- Runtime: `Components/ViewContextController.cs`, `Components/NodeVisibilityHintSet.cs`,
  `Components/PrimitiveVisibilityHintSet.cs`, `Components/InvisibleMaterialCache.cs`
- Import: `Import/VisibilityHintImportPlugin.cs`, `Import/VisibilityHintImportContext.cs`
- Export: `Export/VisibilityHintExportPlugin.cs`, `Export/VisibilityHintExportContext.cs`
- Editor tooling (opt-in, Editor platform): `Editor/Scripts/VisibilityHints/` — inspectors
  (`NodeVisibilityHintSetEditor.cs`, `PrimitiveVisibilityHintSetEditor.cs`, `ViewContextControllerEditor.cs`) +
  sample generator (`VisibilityHintSampleGenerator.cs`)
- Tests: `Tests/Runtime/VisibilityHints/`

## Future consolidation

`ViewContextController` intentionally duplicates the first/third-person logic in the KhrCharacter
`ViewModeController` (no cross-dependency by design). A future refactor could let KhrCharacter delegate to this
generalized controller.
