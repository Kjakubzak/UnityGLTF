# KHR Character / Avatar Extensions (UnityGLTF)

Runtime, import, and export support for the Khronos Character/Avatar extension set (glTF **PR #2512**):
`KHR_character`, `KHR_character_expression` (+ `_morphtarget` / `_joint` / `_texture_transform` / `_mapping` /
`_suppression`), `KHR_character_reference_pose`, `KHR_character_skeleton_mapping`, `KHR_node_camera_hint`, and
`KHR_node_lookat_target`.

> **Status: non-ratified.** The extension set is still a PR, so this layer is **disabled by default** and
> marked `[NonRatifiedPlugin]`. Data shapes and behavior may change as the spec evolves.

## What it does

On import of a glTF whose top-level object carries the `KHR_character` marker, the plugin attaches a small set of
components under a single `KhrCharacter` hub component on the imported scene root. The independent camera-hint and
look-at-target node extensions also import on ordinary glTF assets without the marker.

| Component | Responsibility |
|---|---|
| `KhrCharacter` | Hub: its presence means "this asset contains a character". Capability list, readiness, references to the components below. |
| `ExpressionResponseSet` | Passive, stateless wire evaluator returning absolute response records by authoritative expression index. It never writes scene targets. |
| `ExpressionController` | Optional Unity host adapter for the legacy additive/override morph, joint, and material application policy. |
| `SkeletonMap` | Every skeleton mapping set (role → node index and transform) and every tagged reference pose with its resolved values; can (re)build a Unity humanoid Avatar. |
| `LookAtTargetSet` | Passive live `Transform` markers for `KHR_node_lookat_target`; never selects, ranks, or drives a consumer. |
| `CameraHintSet` | Passive advisory camera descriptors: initial camera pose, subject point, projection, default-view and presentation helpers (never creates, owns, or activates a camera). |
| `GazeSolver` | Optional Unity host adapter with an application-configured target, expression vocabulary, and response curve. It is never auto-created by import. |
| `ViewModeController` | First/third-person view utility (no extension required). |

Capabilities distinguish passive response data from optional scene-writing adapters. Schema recognition alone does
not satisfy an extension's required-use support contract.

## Character designation

`KHR_character` is a marker: `{}`. It designates the asset as containing at most one character and names no root,
member, or owned nodes, so the hub never resolves or stores a node for it. Unknown members, including a legacy
`rootNode`, are kept verbatim and carry no meaning; any such asset still opens the character gate. The marker is
valid only on the top-level glTF object: a node-level `KHR_character` logs a placement warning and adds no
capability. With the import plugin enabled, `KHR_character` in `extensionsRequired` is supported, because the hub
exposes the designation to host logic; without the plugin it is rejected.

The exporter always writes `"KHR_character": {}` and lists it in `extensionsUsed`. When an export set contains
several character roots, the first one supplies the exported character components; the others are exported as
ordinary content, because additional characters are out of scope for one asset.

## Names and identifiers

- **Extension names are exact.** Only the current names are recognized. Former or alternate spellings
  (`KHR_character_expression_mask`, `KHR_character_expression_texture`, the plural `_masks` / `_morphtargets` /
  `_joints`, `KHR_avatar_expression_*`, `KHR_character_bindpose`, `KHR_character_skeleton_bindpose`) import as
  unknown extensions: they are preserved where the containing object round-trips, and their channels are not animated.
- **Expression entries use `name`.** The label is required and nonempty, but not unique; the array index identifies
  an entry. An entry is a glTF property, so unknown members (including a legacy `expression` label) are preserved
  verbatim and never populate `name`. An entry without `name` is invalid expression data.
- **Mapping-set identifiers are opaque.** `KHR_character_expression_mapping` and `KHR_character_skeleton_mapping`
  accept any nonempty identifier and compare it by code point; nothing parses or dereferences it. Exporters should
  still prefer a version-pinned identifier, such as the commit-pinned VRM 1.0 URLs. Only an empty identifier is
  rejected.
- **Diagnostic names** on mapping contributions are compared with the referenced entry's `name` by code point
  (`Smile` and `smile` differ). On import a mismatch only warns, because the index stays authoritative; export
  rejects a mismatch.

## Suppressions

`KHR_character_expression_suppression` objects carry `target`, `type`, `amount` and `threshold`. Import keeps the
authored `amount` and `threshold` as JSON doubles together with whether each was present
(`SuppressionEntry.HasAmount` / `HasThreshold`); an omitted value reads as `1` or `0`. Export writes back what was
authored: `amount` when its flag is set or it differs from `1`, and `threshold` when its flag is set or it differs from
`0`. A value omitted on import keeps its default and stays omitted, while a non-default value whose flag is clear
(pre-rename serialized masks, inspector or code edits) is still written, because the runtime applies it. A threshold is
never written on a `blend` or omitted type. A custom type keeps its token and its uninterpreted `amount` and
`threshold`. A legacy suppression `name` is an unknown member: preserved, never compared with the target.

## Enabling the import plugin

`KhrCharacterImportPlugin` is a `GLTFImportPlugin` with `EnabledByDefault => false`. Enable it before
importing character assets:

- **Project-wide:** Project Settings → UnityGLTF → Import → enable **"KHR Character / Avatar Extensions"**.
- **Per-import (code):** enable the plugin on the `GLTFSettings`/import context you pass to the importer.

When disabled, used-only extension data can still import as ordinary glTF metadata. An asset that lists a
behavioral extension in `extensionsRequired` is rejected unless an enabled plugin claims its complete minimum
support; schema recognition alone is not a support claim.

`CreateExpressionController` explicitly opts into the Unity scene-writing adapter. It is off by default. When that
adapter is created successfully, `SuppressNonInteractiveAnimationAutoPlay` may additionally alter Unity's default
clip selection; it is also off by default. Invalid or passive-only expression data never triggers suppression.
Imported animation clips, wrap modes, and Animator state are otherwise left untouched.

The passive response representation is not yet an export source. Export aborts whenever a character has passive
expression data, even if a lossy legacy controller projection is also present, rather than silently discarding the
authoritative response. Preserve the original glTF for lossless pass-through until passive response export exists.

## Export

`KhrCharacterExportPlugin` is a `GLTFExportPlugin` with `EnabledByDefault => false`. Enable it before
exporting character assets:

- **Project-wide:** Project Settings → UnityGLTF → Export → enable **"KHR Character / Avatar Extensions"**.
- **Per-export (code):** enable the plugin on the `GLTFSettings`/export context you pass to the exporter.

When enabled, the plugin writes the following extensions from a Unity character with a `KhrCharacter` hub:

- **`KHR_character`**: Always the empty marker `{}`.
- **`KHR_character_expression`** (+ entry extensions): Expression entries (`name`, `animation`) and their listings.
  - `KHR_character_expression_morphtarget`: Morph target (blendshape) channels.
  - `KHR_character_expression_joint`: Joint (TRS) channels.
  - `KHR_character_expression_texture_transform`: `KHR_texture_transform` channels.
  - `KHR_character_expression_suppression`: Suppressions that attenuate other expressions' drivers.
  - `KHR_character_expression_mapping`: Vocabulary mapping sets.
- **`KHR_character_skeleton_mapping`**: Mapping-set identifier → role → glTF node association dictionary
  (`{ role: { node, name? } }`).
- **`KHR_character_reference_pose`**: Reference pose animation with bone TRS channels. `poseType` is always written
  explicitly; an unset label resolves to `t_pose`. The standard labels are `t_pose`, `a_pose` and `i_pose`; any other
  label, including a legacy `TPose`, is a custom label written verbatim. The animation is named
  `ReferencePose_<label>`.
- **`KHR_node_camera_hint` / `KHR_node_lookat_target`**: Independent passive node annotations. They export
  from all matching sets under the selected export roots and do not synthesize `KHR_character`.

### Response-animation scope

The exporter writes whatever response entries are in the `CharacterExpressionSet`. The wire extension is not
limited to a particular facial vocabulary, but each entry is a stateless response sampled from a finite host-supplied
scalar. Ordinary looping locomotion and idle playback belong on the standard UnityGLTF animation path unless an
author deliberately intends that response contract.

### Pending schema fields

The following fields are in the runtime contract but **not yet in the glTF schema** (pending PR #2512 update):

- `ExpressionTrack.BlendMode` (`Additive` | `Override`): **Not exported** — no ratified schema field exists yet.
- `ExpressionTrack.Priority`: **Not exported** — no ratified schema field exists yet.

The exporter writes **no** vendor `extras` for these, so the expression wire is fully Khronos-neutral. The import
baker reconstructs `Additive` + priority `0` regardless, so they round-trip via the authoring asset
(`CharacterExpressionSetAsset`) but **not** through a glTF import/export cycle. They may return later via a
ratified representation.

## Round-trip caveats

Honest fidelity notes for an export → import (or import → export) cycle. None of these break neutral-glTF
loading in a third-party viewer; they are documented so consumers know what is and isn't preserved.

- **The passive response evaluator preserves CUBICSPLINE.** It decodes in-tangent/value/out-tangent records and
  evaluates them with the glTF time-scaled Hermite rule. The optional legacy `ExpressionController` baker still
  reduces CUBICSPLINE to its older host-adapter representation. Exporting that legacy representation writes LINEAR,
  because it no longer contains the tangent triplets required for a valid CUBICSPLINE accessor; do not use that
  adapter as a wire-conformance oracle.
- **Legacy expression samplers are normalized to response progress.** Export maps every multi-key input range to
  `[0, 1]`, and expands a legacy single target into an initial-to-target LINEAR response. This guarantees that the
  response reaches its target at progress `1`, independent of the source clip's duration.
- **UV-transform export is anchored to the authored material rest.** `BaseSt` is the static material transform and
  every multi-key output is reconstructed as `BaseSt + frameRelativeDelta`. The first exported value must therefore
  match the static glTF `KHR_texture_transform`. `Frame0St` remains legacy import metadata but is not an export
  baseline: a foreign animation whose first key differs from its static material is rebased to the static value.
- **A material shared by multiple renderers is one entry on the wire.** UV-transform channels are addressed per
  material via `KHR_animation_pointer` (`/materials/{m}/...`), not per renderer. If two
  drivers in one expression resolve to the same concrete material property, identical curves are emitted once and
  conflicting curves reject the export. Give renderers distinct materials when they need independent texture
  animation.
- **`blendMode` / per-driver `priority` are not exported.**
  import baker reconstructs `Additive` + priority `0` regardless, so the exporter writes **no** vendor `extras`
  at all — the expression wire is fully Khronos-neutral. They may return later via a ratified representation. Each
  derived expression extension lists its animation channels under the `channels` key.
- **Camera hints / look-at targets preserve passive descriptor payloads.** `CameraHintSet` and
  `LookAtTargetSet` preserve their known fields, `extensions`, `extras`, unknown same-object properties, and
  required companion-extension provenance. Imported required look-at use is preserved; newly authored markers
  are used-only, and camera hints are always used-only. The optional camera projection index round-trips when
  the referenced camera definition is also instantiated by an ordinary exported core-camera node. A projection
  definition with no instantiated Unity `Camera` cannot currently be force-exported because
  `GLTFSceneExporter.ExportCamera` is private, so that optional reference is omitted.
- **One character per asset.** `KHR_character` is an asset-level marker; it does not assert scene membership, skin
  ownership, skeleton membership, or descendant coverage. If an export set contains multiple character components,
  the exporter deterministically exports the first one's character data and the others as ordinary glTF content.
- **Reference-pose labels are exact.** Import keeps every `poseType` verbatim, so a legacy `TPose` stays a custom
  label rather than being mapped to `t_pose`.

## Optional Unity compositing policy (additive vs. override)

`KHR_character_expression` returns absolute Asset Object Model response records and does not define their final
composition. `ExpressionController` is one optional Unity policy for applying a supported subset. It is created only
when `CreateExpressionController` is enabled.

`ExpressionControllerOwnershipMode.Standalone` (the default) restores targets it owns to their baked bases when no
contributor is active. `Integrated` leaves an inactive target untouched so an Animator or another earlier system can
refresh the live underlay before `LateUpdate`. Active joint and texture application still uses the adapter's baked
base policy described below; `Integrated` is an inactive-target handoff, not a portable mixing rule.

Expression evaluation is **target-major**: for each concrete target (a blendshape, a transform TRS channel,
or a material `_ST`/texture property), every active expression that touches it is combined, then written
once and re-based to the stored neutral each frame. The composition rule is selected per expression via
`ExpressionTrack.BlendMode`:

- **`Additive` (default):** contributors **sum** as deltas over the target's base. This is the
  VRM authoring convention and is correct for morph targets.
- **`Override` (winner-takes):** if **any** active (`d > 0`) Override contributor exists on a target, the
  **winner replaces** the additive result with its absolute pose (`base + winnerDelta`; for rotation
  `winnerDelta * base`). The winner is the highest **`Priority`**; ties are broken by **latest declaration**
  (highest expression index). Additive-only targets are byte-identical to pure-additive evaluation.

Composition lives in `ExpressionController`; sampling lives behind the `IExpressionSemantics` seam
(`Evaluation/AdditiveExpressionSemantics.cs`), so the policy is swappable without touching the math.

Texture **index swaps** on the same material slot resolve deterministically by **`Priority` desc, then
driver weight, then declaration order** (above an activation threshold); with equal priorities — the baker
default — this is identical to the prior most-active-wins behavior. The default (nothing active) is the
slot's base texture.

Using Unity material instances, `MaterialPropertyBlock`, shader parameters, or a separate texture-transform layer
are all valid host implementations. The wire contract identifies distinct glTF target properties and returns their
absolute samples; it does not require Unity materials. An adapter must keep separately addressed texture-info
properties independent and must not mistake its application or mixing policy for normative extension behavior.

### Known limitations

These are honest, documented gaps (raised in the glTF PR #2512 discussion):

1. **The optional controller is not the passive wire evaluator.** Its baked tracks are an older delta-oriented
   Unity representation and typed classifiers select the subset it can apply. Required expression-family use is
   conservatively rejected until every required target, classifier, mapping, suppression, and provenance rule is
   supported.
2. **Joint / texture absolute-pose expressions assume additive-authored deltas.** Two *absolute-pose*
   expressions on one bone composed additively will **over-rotate**. Author conflicting absolute poses as
   `Override` (with priorities), or keep deltas additive. The spec carries no `blendMode` yet, so the baker
   always emits `Additive`; `Override` is currently a **runtime-selectable** policy.
3. **Joint expressions compose over the baked node neutral, not the live Animator pose.** A joint
   expression resolves to `delta * nodeNeutral` (the node's authored local TRS captured at bake), so on a
   humanoid muscle bone it does **not** layer on top of the current Animator pose — it replaces it. Safe
   "delta over current pose" needs humanoid muscle-bone detection (opt-in Avatars only) and risks per-frame
   drift, so it is intentionally **not** done this phase.

The joint additive rest is the **node neutral** local TRS — **not** `reference_pose` (a retarget pose) and
not the skin bind pose.

## Skeleton mapping

`KHR_character_skeleton_mapping` is `identifier -> { role -> { node, name? } }`: the identifier is any nonempty
mapping-set key, the role is a vocabulary role (`hips`, `head`, …), and `node` is a glTF node index (a `glTFid` into
the document's global `nodes[]`). The optional `name` is a diagnostic copy of the referenced node name. The baker
(`KhrCharacterSkeletonBaker`) resolves each role via the node index and reports mismatched names without changing
resolution.

Every mapping set is exposed on `SkeletonMap.Result.MappingSets`. Each set's `NodeIndices` maps every role to its node
index; `Associations` maps the roles whose node was instantiated to its `Transform` (`TryGetAssociation`,
`TryGetAssociationNodeIndex`). An association to a node outside the imported scene keeps its index and only warns; an
index outside the `nodes` array makes the set's report invalid. Choosing a set and applying it to a skin is host
logic: when more than one mapping set is present, the Unity humanoid adapter selects the one that resolves the most
bones. With the plugin enabled, `KHR_character_skeleton_mapping` in `extensionsRequired` is supported; without it the
asset is rejected.

Building a Unity **humanoid Avatar** is opt-in (`SkeletonMap.BuildHumanoidOnAwake`); it self-validates
required bones and falls back to the generic rig.

**Rig import mode (FBX-style).** `KhrCharacterImportPlugin.Rig` selects how the importer treats the rig,
mirroring Unity's FBX "Rig" setting:

- **`Humanoid` (default):** build + assign a Mecanim humanoid Avatar when the skeleton mapping resolves the
  required bones (today's behavior).
- **`Generic`:** keep the generic rig and **never** build a humanoid Avatar.

The selection is **vendor-neutral** — it operates over whatever rig vocabularies the model declares in
`KHR_character_skeleton_mapping` (no rig name such as `vrmHumanoid` is privileged). The gating decision is
`KhrCharacterImportContext.ShouldBuildHumanoid`.

**Edit-time visibility.** Editor-imported prefabs persist the built humanoid Avatar as a sub-asset of the
`.glb` (named `KhrCharacterAvatar`) and assign it to the `Animator` at import — the Avatar slot is populated
at pure edit time (no need to enter Play). At runtime, `SkeletonMap` treats an already-assigned
`Animator.avatar` as authoritative: the runtime build path only runs when the slot is empty, so a manually-
or importer-assigned Avatar wins. Runtime `GLTFSceneImporter` loads (no editor import context) fall through
to the existing runtime build path unchanged.

## Reference poses

`SkeletonMap.ReferencePoses` lists every animation tagged with `KHR_character_reference_pose`, in animation order,
with its exact `poseType` (`t_pose` when omitted). Each node of the asset has a resolved value per tagged animation:
the properties the animation targets come from its single key, and every other node and property comes from the
node's authored `matrix` (decomposed as the importer does) or its TRS with the core defaults. Live transforms are
never read, so a node moved at runtime still resolves to its authored value. The values are on `ReferencePose`
(`ResolvedNodeIndices`, `ResolvedNodes` (null for a node outside the imported scene), `ResolvedLocalPositions`,
`ResolvedLocalRotations`, `ResolvedLocalScales`, `ParentIndices`). `Bones` and the `Local*` arrays keep the
instantiated targeted nodes, which is what export writes. Translations are in the imported scene's units: like node
positions, they include the scale factor of the editor importer.

Import checks each tagged animation: at least one channel, ordinary node TRS targets only (no matrix nodes), one
key per input, `LINEAR` or `STEP`, the core output type, count, and component type, decoded outputs (sparse
included) that are finite with unit rotations, and `extensionsUsed` listing both `KHR_character_reference_pose`
and `KHR_character`. An invalid animation stays in the list with `IsValid == false` and its `Errors`, and carries no
values.

`ApplyReferencePose(animationIndex)` writes the resolved values to every instantiated node, ancestors included;
the parameterless overload applies the first valid pose, which `BuildHumanoidAvatar` also uses.
`TryGetGlobalReferencePose(animationIndex, nodeIndex, out Matrix4x4)` composes the resolved locals along the glTF
parent chain, relative to the imported scene root in Unity coordinates. UnityGLTF turns camera and light GameObjects
180° about Y; resolved rotations exclude that turn and `ApplyReferencePose` adds it back. Playing the
`ReferencePose_<label>` clip through core animation is only a preview: it leaves untargeted properties at their live
values. With the plugin enabled, `KHR_character_reference_pose` in `extensionsRequired` is supported; without it the
asset is rejected.

## Camera hints

`CameraHintSet` exposes every imported `KHR_node_camera_hint` with its glTF node index (`CameraHint.NodeIndex`),
role, label, projection, and target.

- **Initial camera pose.** For every role, the pose is the hint node's position and the rotation `R` that
  `ComputeAuthoredRotation(hint, hierarchyRoot)` builds along the node path: each TRS node contributes its rotation
  times the signs of its scale (with the zero-scale rules), each matrix node its normalized authored columns (with
  the zero-column rules), and a reflection in the product is removed by negating its first column. Matrix nodes are
  recorded at import, because Unity's decomposition can lose their signs and zero columns. `Apply` composes `R` with
  the imported scene root's rotation (host placement) and uses this host's camera axes: the camera looks along the
  hint's -Z with +Y up, and the forward-axis turn UnityGLTF gives a node with a core camera or light is removed first.
- **Subject point.** `targetNode` never aims the camera. Import keeps it only when it names a different node that
  resolves, and `TryGetSubjectPoint` returns its position for framing, orbit, or look-at logic.
- **Projection.** It comes only from the hint's `camera`; a `node.camera` on the hint node never supplies it.
- **Roles.** `CameraHintRoles` lists the eight standard roles (`default_view`, `detail`, `first_person`,
  `framing_view`, `full_body`, `orbit_view`, `portrait`, `third_person`), compared exactly. Any other string,
  including `Default_View` or the former `framing_target` and `orbit_center`, is custom. A host recognizes a custom
  role by adding it to `RegisteredCustomRoles`. `GetPresentableHints()` returns only hints with recognized roles,
  for menus and other default affordances; every hint is still imported, exported, and shown in the inspector.
- **Default view.** `TrySelectDefaultView` picks, among hints whose role is exactly `default_view`, the one on the node
  with the lowest index. Otherwise list order, node order, and role imply no priority; `TryGetByRole` returns the
  first match without ranking.
- **Names.** `GetDisplayName(hint)` uses the label, else a nonempty node name, else the role, and accepts an optional
  provider of display names (for example localized ones) for recognized roles. A hint imported from an unnamed node
  has `NodeUnnamed` set, so the `GLTFNode<index>` name UnityGLTF gives its GameObject is never shown as a node name.

Assets must not require `KHR_node_camera_hint`, so the plugin never claims it for required use.

## Runtime rig switching

`SkeletonMap.SwitchRigMode(RigImportMode)` allows switching a character between **Generic** and **Humanoid**
rig modes at runtime, on a character prefab that was already imported.

```csharp
var skeleton = character.GetComponent<SkeletonMap>();

// Switch to Humanoid: builds and assigns a Mecanim humanoid Avatar when the skeleton
// mapping resolves the required bones. Returns true on success, false if bones are missing.
bool success = skeleton.SwitchRigMode(RigImportMode.Humanoid);

// Switch to Generic: removes the humanoid Avatar, destroys the built avatar to avoid leaks,
// sets HumanoidAvailable = false, and keeps the generic rig. Always returns true.
skeleton.SwitchRigMode(RigImportMode.Generic);
```

The API is useful for:
- Toggling a Generic-imported character to Humanoid at runtime (e.g., when the user enables IK).
- Reverting a Humanoid character to Generic (e.g., to save memory or avoid Animator overhead).
- Testing both rig modes on the same imported prefab without re-importing.

The `HumanoidAvailable` property reflects the current state. When switching to Humanoid fails (missing bones),
the character stays in Generic mode and `HumanoidAvailable` remains false.

## Gaze / look-at

`KHR_node_lookat_target` is implemented by `LookAtTargetSet`, a passive collection that exposes each evaluated
marker instance and reads its current global position. Importing a marker never creates `GazeSolver`, assigns a
consumer, or writes expression weights. Parent transforms and host-selected animation state naturally affect the
reported point through the live Unity `Transform`. A marker node that was not instantiated has no entry. Hints are
exact, case-sensitive labels (`ACME_eye_target` and `acme_eye_target` differ); `GetByHint` returns every marker
with a hint, and list or node order implies no priority among them.

`GazeSolver` is a separate, explicitly configured Unity host adapter. It maps the angle between a host-selected
origin and target onto four configured expression names, using this adapter's 90° saturation response. The marker
extension defines none of that vocabulary, linking, selection, response, scheduling, or animation behavior.

- **Reference frame (gaze origin).** `GazeSolver.ReferenceFrame` is an inspector-settable `Transform` resolved
  **explicit → mapped `head` bone → root transform**. Because it does not require a humanoid skeleton, a
  **non-humanoid** character drives look expressions by setting `ReferenceFrame` alone (no `SkeletonMap`). The
  field serializes with the prefab and is the explicit origin a future exporter reads/writes.
- **Expression names are host configuration.** The adapter's four public names are inspector-editable. Import
  does not infer a vocabulary or connect marker hints to expression labels.

### `EyeAimConstraint` — non-spec eye aiming (opt-in)

Geometric eye-bone aiming (rotate the eye bones toward the target, clamped to `MaxYaw/PitchDegrees`, re-based
to rest when inactive) is an **engine-only convenience that is NOT part of KHR_character**. It lives in a
separate `EyeAimConstraint` component that the importer **never attaches** — add it manually when you want it.

> **Boundary.** The passive KHR marker takes no dependency on `VRMC_character_expression_lookat`. The optional
> Unity adapter's four-name vocabulary and 90° response are host policy, not portable KHR semantics.

## Expression authoring

`CharacterExpressionSetAsset` (`Authoring/CharacterExpressionSetAsset.cs`) is a `ScriptableObject` that stores an
expression set as **scene-independent bindings** (`Authoring/CharacterExpressionBindings.cs`). From the
`ExpressionController` inspector, **Extract to ScriptableObject** calls `CharacterExpressionSetAsset.Extract(baked,
root)` to convert the baked `CharacterExpressionSet` into an `ExpressionBindingSet`: each driver's live scene
reference (SkinnedMeshRenderer / Transform / Renderer) becomes a stable hierarchy path, each morph's blendshape
index becomes its name, and the sampler/curve/base/priority data plus per-expression metadata (name, `BlendMode`,
domains, suppressions with their presence flags, mapping sets) are carried verbatim. Because the asset holds no live scene refs and no `Quaternion[]`
rotation curves on live drivers, it serializes cleanly into a project asset (the eye joint-rotation drivers that
previously broke extraction now round-trip). `CharacterExpressionSetAsset.Resolve(bindings, root)` is the inverse:
it re-resolves each path/name under a character root back into live runtime drivers (unresolved paths/names are
dropped with a warning).

> **Scope.** These bindings are the scene-independent authoring layer. After resolution, the exporter validates the
> concrete glTF target and emits the expression response; the runtime remains free to drive the resolved data from
> clips, controllers, user input, or another animation system.

## Character Health

`CharacterHealthReport` (`Components/CharacterHealth.cs`) reports, per capability/expression, whether it is
**Active / Degraded / Inert**, plus the expression count. This surfaces the "loads but is silently wrong"
states (inert sliders when `KHR_animation_pointer` is unavailable, unresolved skeleton joints,
humanoid-available-but-not-built, over-driven targets). `KhrCharacterDebugHUD` renders it.

## Serialize → rehydrate

The baked data is import-time only, but the components survive being saved as a prefab:

- `ExpressionController` persists the baked `CharacterExpressionSet` in a hidden serialized field and
  rehydrates it in `Awake` (a live import calls `Initialize` directly).
- `SkeletonMap` persists a `SerializableSkeletonMapping` (Unity cannot serialize the
  `Dictionary<string,Transform>`) and converts back to the runtime `SkeletonMappingResult` on load.
- `CameraHintSet` and `LookAtTargetSet` persist passive descriptors and intra-hierarchy `Transform` references;
  `CameraHintSet` also persists the authored matrices on its hints' paths. `KhrCharacter` rehydrates both hub links
  without creating a gaze consumer.

## Key files

- Contracts (import↔runtime boundary): `Contracts/KhrCharacter.Contracts.cs`
- Import/baking: `Import/KhrCharacterImportPlugin.cs`, `Import/KhrCharacterBaker.cs`,
  `Import/KhrCharacterSkeletonBaker.cs`
- Export: `Export/KhrCharacterExportPlugin.cs`, `Export/KhrCharacterExportContext.cs`
- Passive expression wire path: `Components/ExpressionResponseSet.cs`,
  `Evaluation/ExpressionResponseEvaluator.cs`, `Evaluation/ExpressionInitialValueValidation.cs`, and
  `Import/KhrCharacterResponseBaker.cs`
- Runtime host adapters: `Components/ExpressionController.cs`, `Components/SkeletonMap.cs`, `Components/CameraHintSet.cs`,
  `Components/LookAtTargetSet.cs`, `Components/GazeSolver.cs` (optional host adapter), and
  `Components/EyeAimConstraint.cs` (non-spec, opt-in eye aiming)
- Authoring: `Authoring/CharacterExpressionSetAsset.cs`
- Evaluation policy: `Evaluation/AdditiveExpressionSemantics.cs`
- Schema: `Schema/` (`KhrCharacterExtensionNames.cs` holds the exact extension names)
- Tests: `Tests/Runtime/KhrCharacter/`
