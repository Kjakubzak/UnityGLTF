# Visibility Hints (UnityGLTF)

This non-ratified, disabled-by-default plugin imports and exports:

- `KHR_node_visibility_hint`, with logical AND over the node and all ancestor hints.
- `KHR_mesh_primitive_visibility_hint`, attached to a shared mesh primitive but evaluated for each containing node instance.

The runtime's semantic layer exposes pure predicates. It does not mutate `Renderer.enabled`, materials, meshes, lights, node activation, cameras, or authored/animated `KHR_node_visibility` state. Render integrations may use material/shader substitution, draw filtering, primitive splitting, or another observably equivalent route.

## Roles and role logic

Each hint carries `roles`: one or more distinct, nonempty, case-sensitive strings that say when the author intends
the content to be visible. Roles are author labels; the application's logic decides which roles apply in each render
view and asset instance. The host passes that logic to every query as a `VisibilityRoleContext`:

- `appliedRoles`: the roles that apply in this view for this instance;
- `rolesWithLogic`: further roles the application has logic for but that do not apply here (an applied role counts
  as having logic).

Evaluation follows the specification (KhronosGroup/glTF#2512 @ `7ab5368a`):

| role in a hint | applies when |
|---|---|
| `always` | always |
| `first_person`, `third_person` with no application logic | always (an application with no logic hides nothing) |
| `first_person`, `third_person` with application logic | the role is in `appliedRoles` |
| a custom role the application has logic for | the role is in `appliedRoles` |
| a custom role the application does not recognize | ignored |

A hint allows a view when any of its recognized roles applies (roles in one hint are alternatives). A hint whose roles
include `always`, or that has no recognized role, imposes no restriction. A differently cased standard token such as
`First_Person` is a custom role. A render view is a rendered viewpoint whose image is presented (a camera view, one
eye of a stereo pair, a mirror view); a shadow map or a lighting or reflection probe is not.

Two ready-made sources of role logic:

- `VisibilityRoleContext.NoLogic`, the default everywhere: no role logic, so every hint allows every view. Suitable for
  inspectors, thumbnails, and hosts without first- or third-person logic.
- `FirstPersonRolePolicy`: `first_person` applies where the application's first-person logic says, and `third_person`
  wherever `first_person` does not, as the specification recommends. `FirstPersonView`, `OtherView`, and
  `ForView(isFirstPersonView, appliedCustomRoles, recognizedCustomRoles)` build the context of one view; for example a
  mirror view is `ForView(false, new[] { "ACME_mirror_only" })`. When a view is first-person is the application's
  decision; the `first_person` role itself is informative.

A context built by hand with logic for `first_person` only, such as `new VisibilityRoleContext(new[] { "first_person" },
null)`, leaves `third_person` without logic, so it applies in every view and `third_person` content (a head, say) is
drawn in first-person views too. List `third_person` among the roles with logic, or use `FirstPersonRolePolicy`.
`PairsThirdPersonWithFirstPerson` is false for such a context, and for one with logic for both roles that applies
neither, so a host can assert it.

## Runtime query

Import attaches the authored `NodeVisibilityHintSet` and/or `PrimitiveVisibilityHintSet` plus one `ViewContextController` to the scene root. The controller's default role logic is `VisibilityRoleContext.NoLogic`.

```csharp
var visibility = importedRoot.GetComponent<ViewContextController>();

// Independent queries for two views in the same frame; neither changes asset state.
bool renderInOwnEyes = visibility.ShouldRenderPrimitiveForView(
    renderer, subMeshIndex, FirstPersonRolePolicy.FirstPersonView, ancestorInclusiveCoreVisible);
bool renderInMirror = visibility.ShouldRenderPrimitiveForView(
    renderer, subMeshIndex, FirstPersonRolePolicy.ForView(false, new[] { "ACME_mirror_only" }),
    ancestorInclusiveCoreVisible);
```

`ancestorInclusiveCoreVisible` is required: it is the caller's logical AND of current `KHR_node_visibility.visible` values on the node and its ancestors. The hints plugin does not infer that value from general-purpose Unity activation state.

For a host with one main view, call `SetDefaultRoles(context)` and use `ShouldRenderNode` / `ShouldRenderPrimitive`; `OnDefaultRolesChanged` reports changes. Role logic that differs per view or per instance is passed to the explicit `*ForView` queries. `VisibilityHintEvaluator.HintAllows(roles, context)` evaluates one hint on its own.

## Rendering boundary

The plugin preserves metadata and supplies pure predicates. The host omits directly drawn hint-hidden content; shadow, reflection and lighting policies are host-defined. The optional scoped adapter substitutes a no-draw material for color/depth and also gates node shadows as its own policy; its light helper is optional. An `always` child never cancels a restrictive ancestor, including for hosts electing that light policy. Cameras, transforms, animation and interaction remain active. Supported `KHR_node_visibility` retains its separate requirements.

Core Boolean state lives on `UnityGLTF.CoreNodeVisibility.Visible`, with `IsVisibleInHierarchy(node, instanceRoot)` as the cumulative query. Import and pointer animation do not deactivate GameObjects. Imported scenes and `LoadNodeAsync` subtrees receive `CoreVisibilityController`, which filters renderers and lights while preserving cameras and hierarchy; export writes each own Boolean, rather than an inherited false result. Partial node loads evaluate their instantiated subtree, without inferring source ancestors outside it. A host may separately include manual Unity activation or renderer state in its rendering policy; those are not authored core Boolean values.

Both extensions must not be listed in `extensionsRequired`. An asset that lists them there anyway is imported with a warning, never rejected (the factories do not require runtime support, and the import context accepts the listing), and export always declares the hints in `extensionsUsed` only.

### Optional scoped material adapter

`ScopedMaterialVisibilityAdapter` is one Unity-specific renderer route. The host supplies a pipeline-compatible no-draw material and applies it in a short-lived scope around one view render:

```csharp
using (adapter.ApplyForView(renderers, roles, ResolveAncestorInclusiveCoreVisibility))
using (adapter.ApplyLightsForView(lights, roles, ResolveAncestorInclusiveCoreVisibility))
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

A descendant node hint adds a restriction to all ancestor predicates. It cannot restore content hidden by an ancestor hint, including with `always`. An ancestor `KHR_node_visibility.visible: false` remains part of `coreVisible` and cannot be overridden.

## Import, export, and authoring

- Enable `VisibilityHintImportPlugin` or `VisibilityHintExportPlugin` on the relevant `GLTFSettings`; both are disabled by default.
- Export reads authored entries, not runtime query results, and preserves `extensions`, `extras`, and additional JSON properties.
- Export writes `roles` (distinct, nonempty, in authored order). A hint with no role is skipped with a warning because `roles` is required.
- Import reads `roles`; invalid or duplicate items are dropped with a warning. A hint without a valid `roles` array, including one with only the retired single `role` string, imposes no restriction and is not imported (warning); the schema object it parses to serializes to nothing, so re-serializing a parsed `GLTFRoot` drops it rather than writing a hint without `roles`. A retired `role` next to `roles` is ignored with a warning; it is never kept as an additional property or written on export.
- Scenes and prefabs saved by earlier versions migrate on load: an entry's serialized single `Role` becomes `Roles = [Role]` (`third_person_only` and `first_person_only` become the standard roles), and a controller's serialized default view tags become its default role logic (the tags apply, and the application has logic for `first_person` and `third_person`). An empty tag list, the old default, becomes no role logic.
- Primitive entries are keyed by Unity `Mesh` and sub-mesh index; the same primitive roles are combined independently with each containing node's predicate.
- Primitive-hinted meshes opt out of UnityGLTF's geometry-only mesh deduplication so distinct glTF primitive metadata cannot collapse.
- The editor inspectors author the serialized hint entries and name each one by its label, else (for node hints) the node name, else its roles (`NodeVisibilityHintSet.GetDisplayName`, `PrimitiveVisibilityHintSet.GetDisplayName`). An entry imported from an unnamed node has `NodeUnnamed` set, so the `GLTFNode<index>` name UnityGLTF gives its GameObject is never shown as a node name. The controller inspector edits the default role logic (presets for no logic, a first-person view and other views, plus custom roles); it does not preview rendering by mutating the scene.
