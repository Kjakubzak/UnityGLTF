# KHR Character / Avatar Extensions (UnityGLTF)

Runtime, import, and export support for the Khronos Character/Avatar extension set (glTF **PR #2512**):
`KHR_character`, `KHR_character_expression` (+ `_morphtarget` / `_joint` / `_texture` / `_mapping` /
`_mask`), `KHR_character_reference_pose`, `KHR_character_skeleton_mapping`, `KHR_node_camera_hint`, and
`KHR_node_lookat_target`.

> **Status: non-ratified.** The extension set is still a PR, so this layer is **disabled by default** and
> marked `[NonRatifiedPlugin]`. Data shapes and behavior may change as the spec evolves.

## What it does

On import of a glTF whose root carries `KHR_character`, the plugin attaches a small set of components under
a single `KhrCharacter` hub component. The independent camera-hint and look-at-target node extensions also
import on ordinary glTF assets without a character root.

| Component | Responsibility |
|---|---|
| `KhrCharacter` | Hub: capability list, readiness, references to the components below. |
| `ExpressionResponseSet` | Passive, stateless wire evaluator returning absolute response records by authoritative expression index. It never writes scene targets. |
| `ExpressionController` | Optional Unity host adapter for the legacy additive/override morph, joint, and material application policy. |
| `SkeletonMap` | Holds the resolved vocabulary→bone mapping + reference pose; can (re)build a Unity humanoid Avatar. |
| `LookAtTargetSet` | Passive live `Transform` markers for `KHR_node_lookat_target`; never selects or drives a consumer. |
| `CameraHintSet` | Passive advisory camera descriptors (never creates, owns, selects, or activates a camera). |
| `GazeSolver` | Optional Unity host adapter with an application-configured target, expression vocabulary, and response curve. It is never auto-created by import. |
| `ViewModeController` | First/third-person view utility (no extension required). |

Capabilities distinguish passive response data from optional scene-writing adapters. Schema recognition alone does
not satisfy an extension's required-use support contract.

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

- **`KHR_character_expression`** (+ sub-extensions): Expression metadata (names, drivers, masks, mappings).
  - `KHR_character_expression_morphtarget`: Morph target (blendshape) drivers.
  - `KHR_character_expression_joint`: Joint (TRS) animation drivers.
  - `KHR_character_expression_texture`: Texture UV-transform drivers.
  - `KHR_character_expression_mask`: Mask entries for attenuating other expressions.
  - `KHR_character_expression_mapping`: Vocabulary mapping sets.
- **`KHR_character_skeleton_mapping`**: Rig vocabulary → glTF node association dictionary (`{ vocabularyJoint: { node, name? } }`).
- **`KHR_character_reference_pose`**: Reference pose animation (e.g., T-Pose) with bone TRS channels.
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
  sub-extension lists its animation channels under the `channels` (plural) key.
- **Camera hints / look-at targets preserve passive descriptor payloads.** `CameraHintSet` and
  `LookAtTargetSet` preserve their known fields, `extensions`, `extras`, unknown same-object properties, and
  required companion-extension provenance. Imported required look-at use is preserved; newly authored markers
  are used-only, and camera hints are always used-only. The optional camera projection index round-trips when
  the referenced camera definition is also instantiated by an ordinary exported core-camera node. A projection
  definition with no instantiated Unity `Camera` cannot currently be force-exported because
  `GLTFSceneExporter.ExportCamera` is private, so that optional reference is omitted.
- **One `KHR_character` root designation per glTF asset.** `rootNode` identifies the author-selected character
  root; it does not assert scene membership, skin ownership, skeleton membership, descendant coverage, or that
  the asset contains only one character-like object. If an export set contains multiple character components,
  the exporter deterministically designates the first and leaves the others as ordinary glTF content.

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
   conservatively rejected until every required target, classifier, mapping, mask, and provenance rule is supported.
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

`KHR_character_skeleton_mapping` is `rigName -> { vocabularyJoint -> { node, name? } }`: the key is a known
vocabulary joint (`hips`, `head`, …), and `node` is a glTF node index (a `glTFid` into the document's global
`nodes[]`), exactly like `KHR_character.rootNode`. The optional `name` must exactly match the referenced node
name. The baker (`KhrCharacterSkeletonBaker`) resolves each joint via the node index and reports mismatched
labels without changing resolution. When more than one rig is present, it keeps the rig that resolves the most bones.

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
reported point through the live Unity `Transform`.

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
domains, masks, mapping sets) are carried verbatim. Because the asset holds no live scene refs and no `Quaternion[]`
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
  `KhrCharacter` rehydrates both hub links without creating a gaze consumer.

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
- Schema + name aliasing: `Schema/` (`KhrCharacterExtensionNames.cs` holds the closed alias table)
- Tests: `Tests/Runtime/KhrCharacter/`
