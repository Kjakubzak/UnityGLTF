# View-Context Visibility Hints (UnityGLTF)

This non-ratified, disabled-by-default plugin imports and exports:

- `KHR_node_visibility_hint`, with nearest-ancestor inheritance and descendant replacement.
- `KHR_mesh_primitive_visibility_hint`, attached to a shared mesh primitive but evaluated for each containing node instance.

The runtime's semantic layer exposes pure predicates. It does not mutate `Renderer.enabled`, materials, meshes, node activation, cameras, or authored/animated `KHR_node_visibility` state. Render integrations may use material/shader substitution, draw filtering, primitive splitting, or another observably equivalent route.

## Runtime query

Import attaches the authored `NodeVisibilityHintSet` and/or `PrimitiveVisibilityHintSet` plus one `ViewContextController` to the scene root. No active context is supplied by default, so hints do not suppress content.

```csharp
var visibility = importedRoot.GetComponent<ViewContextController>();

// Independent queries for two views in the same frame; neither changes asset state.
bool renderInFirstPerson = visibility.ShouldRenderPrimitiveForContext(
    renderer, subMeshIndex, "first_person", ancestorInclusiveCoreVisible);
bool renderInMirror = visibility.ShouldRenderPrimitiveForContext(
    renderer, subMeshIndex, "mirror", ancestorInclusiveCoreVisible);
```

`ancestorInclusiveCoreVisible` is required: it is the caller's logical AND of current `KHR_node_visibility.visible` values on the node and its ancestors. The hints plugin does not infer that value from general-purpose Unity activation state.

For a host with one convenience context, call `SetActiveContext(context)` and use `ShouldRenderNode` / `ShouldRenderPrimitive`. Call `ClearActiveContext()` to restore the no-context behavior. Explicit `*ForContext` queries are preferred for multi-view rendering.

## Standard roles

Role and context strings are exact and case-sensitive:

| role | no context | `first_person` context | any other supplied context |
|---|---|---|---|
| `always` | visible | visible | visible |
| `first_person` | visible | visible | hidden |
| `third_person` | visible | hidden | visible |
| unrecognized | visible | visible | visible |

An unrecognized custom role remains valid metadata and uses the visible fallback unless a separately supported specification defines its semantics.

## Rendering boundary

The plugin provides metadata preservation and the standard predicates, not a complete render-pipeline adapter. A consumer claiming support when either extension is listed in `extensionsRequired` must integrate these predicates into each visual render view and omit hidden node or primitive content from all relevant visual passes. The stock metadata plugin does not make that claim and rejects required use unless the host explicitly enables the corresponding `HostSupportsRequiredNodeUse` or `HostSupportsRequiredPrimitiveUse` capability after providing a complete integration.

Newly authored entries use a conservative `extensionsUsed`-only export policy. An imported required declaration is preserved on round-trip.

### Optional scoped material adapter

`ScopedMaterialVisibilityAdapter` is one Unity-specific renderer route. The host supplies a pipeline-compatible no-draw material and applies it in a short-lived scope around one view render:

```csharp
using (adapter.ApplyForView(renderers, context, ResolveAncestorInclusiveCoreVisibility))
{
    RenderOneView(camera); // host/render-pipeline-specific
}
```

The adapter replaces only that renderer instance's hidden material slots and restores exact references on disposal. It never modifies a `Material` asset. The supplied shader must contribute to no relevant color, depth, shadow, depth-normal, motion-vector, or pipeline-specific pass; alpha zero alone is insufficient. The adapter covers renderer output only, so it is not by itself a complete required-use implementation for every possible node visual feature. Hosts are responsible for camera ordering, reentrancy, XR, and applying the scope separately to every renderer instance.

The helper requires exactly one material slot per mesh sub-mesh and rejects overlapping scopes for the same renderer. Built-in render-pipeline hosts may scope a direct camera render; SRP/URP hosts generally integrate through pipeline camera callbacks or render requests. Material assignments must remain stable for the duration of a scope.

## Composition

For node visual content:

```text
renderNodeVisualContent = hintVisible AND coreVisible
```

For a primitive instance:

```text
renderPrimitiveInstance = primitiveHintVisible AND nodeHintVisible AND coreVisible
```

A descendant node hint replaces its inherited node hint. It can therefore make its own content hint-visible even when an ancestor's hint is false. An ancestor `KHR_node_visibility.visible: false` remains part of `coreVisible` and cannot be overridden.

## Import, export, and authoring

- Enable `VisibilityHintImportPlugin` or `VisibilityHintExportPlugin` on the relevant `GLTFSettings`; both are disabled by default.
- Export reads authored entries, not runtime query results, and preserves `extensions`, `extras`, and additional JSON properties.
- Empty roles are skipped on export because `role` is required and nonempty.
- Primitive entries are keyed by Unity `Mesh` and sub-mesh index; the same primitive role is combined independently with each containing node's predicate.
- Primitive-hinted meshes opt out of UnityGLTF's geometry-only mesh deduplication so distinct glTF primitive metadata cannot collapse.
- The editor inspectors author the serialized hint entries. The controller inspector only selects a convenience query context; it does not preview rendering by mutating the scene.
