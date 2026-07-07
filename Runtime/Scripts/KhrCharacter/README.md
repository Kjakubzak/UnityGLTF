# KHR Character / Avatar Extensions (UnityGLTF)

Runtime, import, and export support for the Khronos Character/Avatar extension set (glTF **PR #2512**):
`KHR_character`, `KHR_character_expression` (+ `_morphtarget` / `_joint` / `_texture` / `_mapping` /
`_mask`), `KHR_character_reference_pose`, `KHR_character_skeleton_mapping`, `KHR_node_camera_hint`, and
`KHR_node_lookat_target`.

> **Status: non-ratified.** The extension set is still a PR, so this layer is **disabled by default** and
> marked `[NonRatifiedPlugin]`. Data shapes and behavior may change as the spec evolves.

## What it does

On import of a glTF whose root carries `KHR_character`, the plugin attaches a small set of components under
a single `KhrCharacter` hub component:

| Component | Responsibility |
|---|---|
| `KhrCharacter` | Hub: capability list, readiness, references to the components below. |
| `ExpressionController` | Drives morph / joint / texture expressions each frame (`Components/ExpressionController.cs`). |
| `SkeletonMap` | Holds the resolved vocabulary→bone mapping + reference pose; can (re)build a Unity humanoid Avatar. |
| `GazeSolver` | Spec-aligned, **expression-driven** gaze: drives look-* expression weights toward a target, measured against a `ReferenceFrame` (see [Gaze / look-at](#gaze--look-at)). |
| `CameraHintSet` | Advisory camera framing hints (never creates or owns a camera). |
| `ViewModeController` | First/third-person view utility (no extension required). |

Capabilities are derived from the extensions actually present and the data actually baked
(`KhrCharacterImportContext.DeriveCapabilities`).

## Enabling the import plugin

`KhrCharacterImportPlugin` is a `GLTFImportPlugin` with `EnabledByDefault => false`. Enable it before
importing character assets:

- **Project-wide:** Project Settings → UnityGLTF → Import → enable **"KHR Character / Avatar Extensions"**.
- **Per-import (code):** enable the plugin on the `GLTFSettings`/import context you pass to the importer.

When disabled, character assets still import as plain glTF; unknown extensions round-trip as
`DefaultExtension` and are never rejected.

## Export

`KhrCharacterExportPlugin` is a `GLTFExportPlugin` with `EnabledByDefault => false`. Enable it before
exporting character assets:

- **Project-wide:** Project Settings → UnityGLTF → Export → enable **"KHR Character / Avatar Extensions"**.
- **Per-export (code):** enable the plugin on the `GLTFSettings`/export context you pass to the exporter.

When enabled, the plugin writes the following extensions from a Unity character with a `KhrCharacter` hub:

- **`KHR_character_expression`** (+ sub-extensions): Expression metadata (names, drivers, masks, mappings).
  - `KHR_character_expression_morphtarget`: Morph target (blendshape) drivers.
  - `KHR_character_expression_joint`: Joint (TRS) animation drivers.
  - `KHR_character_expression_texture`: Texture (UV transform or index swap) drivers.
  - `KHR_character_expression_mask`: Mask entries for attenuating other expressions.
  - `KHR_character_expression_mapping`: Vocabulary mapping sets.
- **`KHR_character_skeleton_mapping`**: Rig vocabulary → glTF node-index mapping dictionary (`{ vocabularyJoint: nodeIndex }`).
- **`KHR_character_reference_pose`**: Reference pose animation (e.g., T-Pose) with bone TRS channels.

### Scope rule (facial expressions only)

The exporter writes **whatever expressions are in the `CharacterExpressionSet`**. The **caller is responsible**
for putting only **facial expressions** (0→1 driven, no loop expectation) into the set. **Body/locomotion
animations** (walk cycles, idle poses, etc.) must be exported via the **standard UnityGLTF animation export
path**, not through this plugin. The KHR_character extension set is reserved for facial expressions and
other scalar-driven (0→1) animations without loop expectations.

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

- **CUBICSPLINE animations are imported as LINEAR.** The baker has no cubic-spline evaluator: it samples each
  keyframe's value block (`[inTangent, value, outTangent]`) and records the track as `LINEAR`, discarding the
  tangents (`KhrCharacterBaker.MapInterp`). `STEP` and `LINEAR` round-trip exactly.
- **Multi-key UV-transform animations round-trip exactly on the first cycle.** Import captures the animation's
  frame-0 absolute `_ST` (`Frame0St`) alongside the frame-0-relative deltas and the material's static `_ST`
  (`BaseSt`). On export the absolute `_ST` per key is reconstructed as `Frame0St + (frame_k − frame0)`, so both the
  inter-key *shape* (deltas) and the authored absolute baseline are preserved — a foreign asset whose animated
  frame 0 differs from its material's static `_ST` round-trips exactly on the first cycle. (Drivers that carry no
  captured frame 0 — e.g. hand-authored sets — fall back to anchoring on `BaseSt`, the prior behavior.) `BaseSt`
  stays the runtime rest the compositor applies deltas over.
- **A material shared by multiple renderers is one entry on the wire.** Texture (UV-transform / index-swap)
  channels are addressed per material via `KHR_animation_pointer` (`/materials/{m}/...`), not per renderer. If two
  renderers share a material but carry different texture animations, both resolve to the same material index and
  collapse to a single animated material on export. Give renderers distinct materials when they need independent
  texture animation.
- **`blendMode` / per-driver `priority` are not exported.**
  import baker reconstructs `Additive` + priority `0` regardless, so the exporter writes **no** vendor `extras`
  at all — the expression wire is fully Khronos-neutral. They may return later via a ratified representation. Each
  sub-extension lists its animation channels under the `channels` (plural) key.
- **Camera hints / look-at targets export, but the camera projection index does not round-trip.** `CameraHintSet`
  hints export as `KHR_node_camera_hint` (`role`, `label`, `targetNode`) and `GazeSolver` authored targets export
  as `KHR_node_lookat_target` (`hint`); all of these round-trip. The optional `camera` index is **omitted** unless
  the referenced camera was already exported onto its own node — `GLTFSceneExporter.ExportCamera` is private and
  there is no public way to force-export a camera here, and import does not populate the projection link today.
  `camera` is optional in the spec, so the omission is conformant.
- **One character per glTF document (PR #2512).** `KHR_character` is a root singleton with a single `rootNode`, so
  a document models exactly one character. If an export set contains multiple character roots, the exporter
  deterministically emits the **first** (in `RootTransforms` order) and logs a warning naming the skipped roots —
  nothing is silently dropped. To export multiple characters, export each character root to its **own** glTF
  document (one document per character); each round-trips independently.

## Compositing policy (additive vs. override)

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

### Known limitations

These are honest, documented gaps (raised in the glTF PR #2512 discussion):

1. **Joint / texture absolute-pose expressions assume additive-authored deltas.** Two *absolute-pose*
   expressions on one bone composed additively will **over-rotate**. Author conflicting absolute poses as
   `Override` (with priorities), or keep deltas additive. The spec carries no `blendMode` yet, so the baker
   always emits `Additive`; `Override` is currently a **runtime-selectable** policy.
2. **Joint expressions compose over the baked node neutral, not the live Animator pose.** A joint
   expression resolves to `delta * nodeNeutral` (the node's authored local TRS captured at bake), so on a
   humanoid muscle bone it does **not** layer on top of the current Animator pose — it replaces it. Safe
   "delta over current pose" needs humanoid muscle-bone detection (opt-in Avatars only) and risks per-frame
   drift, so it is intentionally **not** done this phase.

The joint additive rest is the **node neutral** local TRS — **not** `reference_pose` (a retarget pose) and
not the skin bind pose.

## Skeleton mapping

`KHR_character_skeleton_mapping` is `rigName -> { vocabularyJoint -> nodeIndex }`: the key is a known
vocabulary joint (`hips`, `head`, …) and the value is a glTF node index (a `glTFid` into the document's
global `nodes[]`), exactly like `KHR_character.rootNode`. The baker (`KhrCharacterSkeletonBaker`) resolves
each joint via a direct node-index → GameObject lookup, so there is no name coupling and no direction to
detect. When more than one rig is present, the baker keeps the one that resolves the most bones.

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

`GazeSolver` is the **spec-aligned**, vendor-neutral gaze component. It is **expression-driven only**: it maps
the angle between a gaze origin and the target onto the four look-direction expression weights
(`lookLeft/Right/Up/Down`), each **saturating to 1 at 90°** (`saturate(angle / (π/2))`). It carries **no**
eye-bone rotation and **no** non-spec clamps.

- **Reference frame (gaze origin).** `GazeSolver.ReferenceFrame` is an inspector-settable `Transform` resolved
  **explicit → mapped `head` bone → root transform**. Because it does not require a humanoid skeleton, a
  **non-humanoid** character drives look expressions by setting `ReferenceFrame` alone (no `SkeletonMap`). The
  field serializes with the prefab and is the explicit origin a future exporter reads/writes.
- **Auto-detected look names.** At import the look directions bind to whichever expressions actually exist in
  the baked set, using a vendor-neutral ordered candidate list per direction (camelCase / snake_case / eye-/
  eyes-/gaze- prefixes — not VRM only); unmatched directions keep the default and stay inert (`SetWeight`
  no-ops on unknown names). The names remain public/inspector-editable, so detection is overridable
  (`KhrCharacterImportContext.BindLookExpressionNames`).

### `EyeAimConstraint` — non-spec eye aiming (opt-in)

Geometric eye-bone aiming (rotate the eye bones toward the target, clamped to `MaxYaw/PitchDegrees`, re-based
to rest when inactive) is an **engine-only convenience that is NOT part of KHR_character**. It lives in a
separate `EyeAimConstraint` component that the importer **never attaches** — add it manually when you want it.
This keeps the KHR import path purely expression-driven, matching the spec (the non-spec `MaxYaw/MaxPitchDegrees`
clamps are not on `GazeSolver`).

> **Vendor-neutral stance.** The KHR gaze path takes **no dependency on `VRMC_character_expression_lookat`**
> (it is recognized but kept strictly separate — `KhrCharacterExtensionNames.VrmcExpressionLookat`). The 90°
> saturation matches the VRM convention but is implemented as a neutral convention, not a VRM import. The spec
> gaps here — no KHR gaze origin / `referenceNode`, no KHR look vocabulary, the "1 at 90°" rule living only in
> VRMC — are routed to the glTF PR #2512 discussion.

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

> **Scope.** This is groundwork for the future exporter, **not** the exporter. The binding keeps each texture
> driver's stable `PropertyId` (enough to re-resolve a runtime driver); capturing the human-readable shader
> property name and the glTF export itself are deferred to the exporter work.

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

## Key files

- Contracts (import↔runtime boundary): `Contracts/KhrCharacter.Contracts.cs`
- Import/baking: `Import/KhrCharacterImportPlugin.cs`, `Import/KhrCharacterBaker.cs`,
  `Import/KhrCharacterSkeletonBaker.cs`
- Export: `Export/KhrCharacterExportPlugin.cs`, `Export/KhrCharacterExportContext.cs`
- Runtime: `Components/ExpressionController.cs`, `Components/SkeletonMap.cs`, `Components/GazeSolver.cs`,
  `Components/EyeAimConstraint.cs` (non-spec, opt-in eye aiming)
- Authoring: `Authoring/CharacterExpressionSetAsset.cs`
- Evaluation policy: `Evaluation/AdditiveExpressionSemantics.cs`
- Schema + name aliasing: `Schema/` (`KhrCharacterExtensionNames.cs` holds the closed alias table)
- Tests: `Tests/Runtime/KhrCharacter/`
