# KHR Character / Avatar Extensions (UnityGLTF)

Runtime + import support for the Khronos Character/Avatar extension set (glTF **PR #2512**):
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

These are honest, documented gaps (raised back to PR #2512 — see `pr2512_feedback.md`):

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

## Skeleton direction auto-detect

`KHR_character_skeleton_mapping` is `rigName -> { jointA -> jointB }` with an ambiguous direction. The baker
(`KhrCharacterSkeletonBaker`) resolves both interpretations and picks the one that resolves **more real
transforms** — the reliable signal — using the known-vocabulary token count only as a **tiebreaker**. This
makes both the spec layout and the inverted layout resolve even when bones are literally named
with vocabulary tokens (`Hips`, `Head`, …). The resolved `MappingDirection` is surfaced in Character Health.

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
> VRMC — are routed to PR #2512 (see `pr2512_feedback.md`, items 10–12).

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
**Active / Degraded / Inert**, plus the resolved skeleton direction and expression count. This surfaces the
"loads but is silently wrong" states (inert sliders when `KHR_animation_pointer` is unavailable, dropped
name-couplings, humanoid-available-but-not-built, over-driven targets). `KhrCharacterDebugHUD` renders it.

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
- Runtime: `Components/ExpressionController.cs`, `Components/SkeletonMap.cs`, `Components/GazeSolver.cs`,
  `Components/EyeAimConstraint.cs` (non-spec, opt-in eye aiming)
- Authoring: `Authoring/CharacterExpressionSetAsset.cs`
- Evaluation policy: `Evaluation/AdditiveExpressionSemantics.cs`
- Schema + name aliasing: `Schema/` (`KhrCharacterExtensionNames.cs` holds the closed alias table)
- Tests: `Tests/Runtime/KhrCharacter/`
