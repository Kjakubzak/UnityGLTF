# Visibility Hints (UnityGLTF)

This non-ratified, disabled-by-default plugin imports and exports:

- `KHR_node_visibility_hint`, with nearest-ancestor inheritance and descendant replacement.
- `KHR_mesh_primitive_visibility_hint`, attached to a shared mesh primitive but evaluated for each containing node instance.

The runtime's semantic layer exposes pure predicates. It does not mutate `Renderer.enabled`, materials, meshes, lights, node activation, cameras, or authored/animated `KHR_node_visibility` state. Render integrations may use material/shader substitution, draw filtering, primitive splitting, or another observably equivalent route.

## View tags

The host gives each render view zero, one, or several view tags. The standard tags are `first_person` (a view from the character's own viewpoint) and `third_person` (a third-person view of the character, such as a spectator or mirror view). A host may add custom tags defined by a separate specification or application profile; they should be vendor-qualified, for example `ACME_mirror`. Tags are exact, case-sensitive strings and are not stored in the asset. How a host tags its views, including giving different instances of the asset different tags within one view, is host-defined.

A render view is a rendered viewpoint whose image is presented, directly or inside another view: a camera view, one eye of a stereo pair, or a mirror view. A shadow map or a lighting or reflection probe is not a render view.

## Runtime query

Import attaches the authored `NodeVisibilityHintSet` and/or `PrimitiveVisibilityHintSet` plus one `ViewContextController` to the scene root. The controller starts with no default view tags, so its convenience queries describe an untagged view.

```csharp
var visibility = importedRoot.GetComponent<ViewContextController>();

// Independent queries for two views in the same frame; neither changes asset state.
bool renderInFirstPerson = visibility.ShouldRenderPrimitiveForView(
    renderer, subMeshIndex, new[] { "first_person" }, ancestorInclusiveCoreVisible);
bool renderInMirror = visibility.ShouldRenderPrimitiveForView(
    renderer, subMeshIndex, new[] { "third_person", "ACME_mirror" }, ancestorInclusiveCoreVisible);
```

`ancestorInclusiveCoreVisible` is required: it is the caller's logical AND of current `KHR_node_visibility.visible` values on the node and its ancestors. The hints plugin does not infer that value from general-purpose Unity activation state.

For a host with one main view, call `SetDefaultViewTags(tags)` and use `ShouldRenderNode` / `ShouldRenderPrimitive`; `OnDefaultViewTagsChanged` reports changes. Tags that differ per view or per instance are passed to the explicit `*ForView` queries.

## Standard roles

Roles are exact and case-sensitive. For a render view with view tags `T`:

| role | visible when |
|---|---|
| `always`, or no hint | always, including when `T` is empty |
| `first_person` | `T` contains `first_person` |
| `third_person` | `T` contains `third_person` |
| any other role | the custom-role resolver says so; otherwise only when `RenderUnsupportedRoles` is set |

Custom tags in `T` never affect the standard roles. An untagged view therefore shows only `always` and un-hinted content: `third_person` content disappears from views that do not carry the `third_person` tag, such as an untagged Scene view or thumbnail camera.

## Custom roles

Any other role string, including a differently cased standard token such as `First_Person`, is a custom role. A consumer supports a custom role only through a separate specification or application profile. Register that support as an `IVisibilityRoleResolver` on `ViewContextController.CustomRoleResolver` (not serialized): `TryResolve(role, viewTags, out visible)` returns true for a role it supports. Content whose role no resolver supports is hidden by default, as the role-support rule recommends; `RenderUnsupportedRoles = true` is the application's override to render it anyway. The evaluator takes the same resolver and override as optional arguments.

## Rendering boundary

The plugin provides metadata preservation and the predicates, not a complete render-pipeline adapter. Hint-hidden content must not contribute to any visual pass of that view: color, depth, or shadow. Replacing its surface color with a transparent material is not enough. How that is achieved for passes shared between views, such as a shadow map used by several cameras, is host-defined, and so is whether mirrors, planar reflections, and reflection probes inside a view include content that is hint-hidden in that view. Content hidden by `KHR_node_visibility` stays out of them. A light attached to a node whose visual-content predicate is false must not light that view; give a light that must reach every view `role: always` on its own node. Cameras, camera hints, look-at markers, selectability, hoverability, picking, physics, and animation are outside the visibility contract.

A consumer claiming support when either extension is listed in `extensionsRequired` must integrate these predicates into each visual render view and omit hidden node or primitive content from all relevant visual passes. The stock metadata plugin does not make that claim and rejects required use unless the host explicitly enables the corresponding `HostSupportsRequiredNodeUse` or `HostSupportsRequiredPrimitiveUse` capability after providing a complete integration.

Newly authored entries use a conservative `extensionsUsed`-only export policy. An imported required declaration is preserved on round-trip.

### Optional scoped material adapter

`ScopedMaterialVisibilityAdapter` is one Unity-specific renderer route. The host supplies a pipeline-compatible no-draw material and applies it in a short-lived scope around one view render:

```csharp
using (adapter.ApplyForView(renderers, viewTags, ResolveAncestorInclusiveCoreVisibility))
using (adapter.ApplyLightsForView(lights, viewTags, ResolveAncestorInclusiveCoreVisibility))
{
    RenderOneView(camera); // host/render-pipeline-specific
}
```

The adapter replaces only that renderer instance's hidden material slots and restores exact references on disposal. It never modifies a `Material` asset. When the renderer's node is hidden, it also sets `shadowCastingMode` to `Off` for the scope. `ApplyLightsForView` turns off `Light.enabled` for lights on hidden nodes and turns them back on afterwards; it leaves cameras alone. The supplied shader must contribute to no relevant color, depth, shadow, depth-normal, motion-vector, or pipeline-specific pass; alpha zero alone is insufficient. The adapter covers renderers and lights only, so it is not by itself a complete required-use implementation for every possible node visual feature. Hosts are responsible for camera ordering, reentrancy, XR, shared shadow maps, and applying the scope separately to every renderer instance.

The helper requires exactly one material slot per mesh sub-mesh and rejects overlapping scopes for the same renderer or light. A value that another system changes during a scope is left as that system set it. Built-in render-pipeline hosts may scope a direct camera render; SRP/URP hosts generally integrate through pipeline camera callbacks or render requests. Material assignments must remain stable for the duration of a scope.

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
- The editor inspectors author the serialized hint entries and name each one by its label, else (for node hints) the node name, else its role (`NodeVisibilityHintSet.GetDisplayName`, `PrimitiveVisibilityHintSet.GetDisplayName`). The controller inspector edits the default view tags and the unsupported-role override; it does not preview rendering by mutating the scene.
