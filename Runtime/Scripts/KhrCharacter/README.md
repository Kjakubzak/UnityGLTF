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
| `ExpressionResponseSet` | Passive implementation of the expression pipeline: driver snapshot with suppression, per-entry sampling of the channels a supported derived expression extension lists, order-independent composition, and both mapping operations, by authoritative expression index. It also exposes each entry's suppressions, every mapping set and the validation status, and never writes scene targets. |
| `ExpressionController` | Optional Unity host applicator: drives the response set, or an authored set through the same pipeline, and writes the composed records to node transforms, blendshape weights and material property blocks. |
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
  prefer a fixed vocabulary identifier, such as `vrm1_expressions` or `vrm1_humanoid`. Only an empty identifier is
  rejected.
- **Independent names.** Mapping contribution and skeleton association names are optional labels independent of
  expression/node names. They never affect resolution, produce mismatch warnings, or require equality. Empty labels
  remain valid where the schema permits them. Skeleton association labels and payloads survive prefab/export.

## Suppressions

`KHR_character_expression_suppression` objects carry `target`, `type`, `amount` and `threshold`. Import keeps the
authored `amount` and `threshold` as JSON doubles together with whether each was present
(`SuppressionEntry.HasAmount` / `HasThreshold`); an omitted value reads as `1` or `0`. Export writes back what was
authored: `amount` when its flag is set or it differs from `1`, and `threshold` when its flag is set or it differs from
`0`. A value omitted on import keeps its default and stays omitted, while a non-default value whose flag is clear
(pre-rename serialized masks, inspector or code edits) is still written, because the runtime applies it. A threshold is
never written on a `blend` or omitted type. A custom type keeps its token and its uninterpreted `amount` and
`threshold`. A legacy suppression `name` is an unknown member: preserved, never compared with the target.

## Expression import and validation

The import plugin bakes `KHR_character_expression` into `ExpressionResponseSet` whether or not the Unity controller
is requested.

- **Listing decides which channels animate.** A channel produces response records only when a derived expression
  extension on its entry lists it, this implementation supports that extension, and its target resolves.
  `ExpressionResponseChannel.ListedBy` names the listing extension and `EmitsRecords` says whether the channel
  produces records. An unlisted channel, or one listed only by an unsupported extension, produces none, but it still
  counts toward the duration `T`. A time-zero sample different from the authored state is valid.
- **Derived extensions.** The built-in joint, morphtarget and texture_transform extensions list channels and are
  supported; `KHR_character_expression_suppression` is recognized and lists none. A host registers vendor entry
  extensions in `ExpressionDerivedExtensionRegistry.Default` before import, with whether they list channels, their
  channel classification and whether the host supports them. Legacy single-writer declarations remain metadata
  for compatibility and cannot disable mandatory additive aggregation. Registered extensions are unsupported by default.
- **Targets.** Node `translation`, `rotation`, `scale` and morph `weights` resolve in core and pointer form, as one
  canonical property. `KHR_texture_transform` `offset`, `scale` and `rotation` resolve on the core texture slots and
  on material-extension textures that the material remapper handles; the textureInfo must author a
  `KHR_texture_transform`, whose omitted values read as `[0, 0]`, `[1, 1]` and `0`. Texture rotation is sampled as
  an ordinary scalar and never wrapped. Pointer channels, texture transforms included, accept FLOAT, normalized
  integer and non-normalized integer outputs as `KHR_animation_pointer` allows; core channels follow core glTF.
  Each resolved target keeps its kind and authored initial value `b` (`ExpressionResponseTarget.Kind`,
  `AuthoredInitial`, `ArrayLength`). Node TRS and texture offset and scale carry the float32 values UnityGLTF
  parses; morph weights and texture rotation carry the JSON doubles.
- **Matrix nodes.** A core channel must not target a node that defines `matrix`, and a pointer must not target its
  `translation`, `rotation` or `scale`. Pointer-form morph weights on such a node are allowed.
- **Within-animation uniqueness.** A concrete target cannot occur twice in the same animation; whole-array and
  element targets overlap. Cross-entry shared targets are valid, including rotations and scales.
- **Invalid data is rejected as a whole.** Missing expression names, out-of-range channel indices, duplicate channel
  listings, classification errors, invalid sampler/resource data and prohibited matrix-node TRS targets reject the
  response set. Differing time-zero samples and cross-entry overlaps do not.
- **Incomplete validation is not a rejection.** `ExpressionResponseSet.Validation` reports, and the import logs as a
  warning, data the importer cannot check: an unrecognized or unsupported entry extension, a target it cannot
  resolve, or compressed buffer data. A bufferView with `KHR_meshopt_compression`, an unknown extension, or
  `EXT_meshopt_compression` without the decompression package and its import plugin is never read from fallback
  data: such an output leaves its channel without records, and such an input makes the whole entry unevaluable
  (`ExpressionResponse.IsEvaluable` is false).
- **Suppressions and mapping sets** are bound to the response set (`ExpressionResponseSetEntry.Suppressions`,
  `MappingSets`, with `InputMappingSets` as input-oriented adapters) with expression-array indices. An empty mapping-set identifier drops only
  that set, with an error.

See [October 2026 migration notes](NORMATIVE_SYNC_2026_10.md) for the current wire contract and host policies.

## Expression pipeline and composition

`ExpressionResponseSet` implements the normative pipeline as pure code over the imported data. It returns glTF-space
values keyed by canonical property (`/nodes/3/translation`, `/nodes/2/weights`, or the texture-transform pointer) and
never writes scene objects; applying the results and mixing them with other writers is host policy.

1. **Driver snapshot.** `BuildSnapshot(finalNativeDrivers)` takes the host's final native drivers by expression index,
   after any input mapping and arbitration. Each supplied driver must be finite and is clamped to `[0, 1]`; an absent
   driver is `0`. That vector `u` is turned into the effective vector `v` by every entry's suppressions: a `blend`
   factor is `1 - amount * u[source]`, a `block` factor is `1 - amount` when `u[source] > threshold` and `1`
   otherwise, and a custom type has factor `1`. Every factor reads `u`, so cycles and self-suppression are well
   defined. `ExpressionDriverSnapshot.U` and `V` expose both vectors.
2. **Sampling and composition.** `Compose(snapshot)` evaluates every entry with `v[i]` at `t = v[i] * T` and composes
   all responses in one step (`ExpressionComposer`). Each response record carries its sample `s` and the same channel
   sampled at time zero, `n` (`ExpressionResponseRecord.NeutralValue`). An additive component is `b + sum(s - n)`,
   added exactly in binary64 and rounded once (`ExactBinary64Sum`), even for a single record, so the result bits do
   not depend on entry order. `n` is never replaced by `b`, and the result is never clamped or wrapped. Node
   translation, texture offset and texture rotation are additive; rotation angles add as raw scalars, so whole turns
   survive. Morph weights fold whole-array and element records into one complete vector per node, whose untouched
   elements keep their authored value (`ExpressionAggregateRecord.Contributed` marks the touched ones). Node rotation
   and scale, texture scale, quaternions, Boolean and integer properties use this host's override policy: choose
   the absolute sample from the highest active expression-array index. Normalize quaternion results, preserve their
   chosen sign, and validate finite/discrete values. The driver selects sample time and is not applied again as a
   blend weight. An inactive entry emits no record and cannot override an active entry. Exact binary64 addition is
   a package guarantee; the current specification does not mandate that precision policy.
3. **Failures.** A sample that is not finite, a rotation sample of zero length (such as a CUBICSPLINE midpoint) or an
   aggregate that is not finite fails only its property: the property gets no record, the failure is logged as an error
   and listed in `ExpressionResponse.Failures` and `ExpressionComposition.Failures`, and the other properties still
   compose. A failed element withholds its node's whole morph vector.
4. **Mappings.** `EvaluateExpressionSetMapping(identifier, inputs)` maps the sole `expressionSetMappings` table
   from external input keys to an immutable full-count native vector. Wire `source` is the driven native index.
   Clamp each input, sum all terms including repeats, then clamp each native sum. Unknown keys contribute nothing;
   comparison is exact even when a host passes a case-insensitive dictionary. Host arbitration precedes `u` and
   suppression. `EvaluateInputMapping` is a compatibility input API. `EvaluateForwardMapping` and
   `EvaluateVocabularyOutputs` remain optional host projections of the same coefficients; they are not a standard
   output mapping, an inverse, or a round-trip guarantee.

`ExpressionDriverPipeline`, `ExpressionComposer` and `ExactBinary64Sum` expose the same stages for hosts that hold
their own response data.

- **Required use.** With the import plugin enabled, `KHR_character_expression` and its joint, morphtarget,
  texture_transform, suppression and mapping extensions are supported in `extensionsRequired`; without the plugin they
  are rejected. The hub reports these capabilities as Active whenever the response set exists.
- **Precision of `b` (deviation).** UnityGLTF parses node `translation`, `rotation` and `scale` and
  `KHR_texture_transform` `offset` and `scale` as float32, so the authored initial value of those targets is the
  float32 value nearest the JSON number rather than the JSON double. Only the additive translation and offset results
  depend on it, by at most that rounding. Morph weights and texture rotation keep the JSON doubles. Keeping the JSON
  doubles for every target is a follow-up.

## Enabling the import plugin

`KhrCharacterImportPlugin` is a `GLTFImportPlugin` with `EnabledByDefault => false`. Enable it before
importing character assets:

- **Project-wide:** Project Settings → UnityGLTF → Import → enable **"KHR Character / Avatar Extensions"**.
- **Per-import (code):** enable the plugin on the `GLTFSettings`/import context you pass to the importer.

When disabled, used-only extension data can still import as ordinary glTF metadata. An asset that lists a
behavioral extension in `extensionsRequired` is rejected unless an enabled plugin claims its complete minimum
support; schema recognition alone is not a support claim.

`CreateExpressionController` explicitly opts into the Unity scene-writing applicator (see
[Unity expression applicator](#unity-expression-applicator)). It is off by default.
`SuppressNonInteractiveAnimationAutoPlay` keeps the animations that expression entries reference, and the
reference-pose animations, from becoming Unity's default clip; they stay explicitly playable. It is also off by
default and does not need the controller. Imported animation clips, wrap modes, and Animator state are otherwise left
untouched.

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
  explicitly; missing/empty imported labels are invalid. Newly authored objects default to an explicit `t_pose`. The standard labels are `t_pose`, `a_pose` and `i_pose`; any other
  label, including a legacy `TPose`, is a custom label written verbatim. The animation is named
  `ReferencePose_<label>`.
- **`KHR_node_camera_hint` / `KHR_node_lookat_target`**: Independent passive node annotations. They export
  from all matching sets under the selected export roots and do not synthesize `KHR_character`.

### Listing and shared-target rules

- **Constant channels are not listed.** A channel whose exported keys all equal the authored initial value would
  still produce records, so the exporter omits it as an authoring choice. Every exported channel ends at normalized
  time `1`, so omitting one never shortens the duration. When every channel of an expression is constant, the
  exporter keeps one unlisted channel so the entry still has a duration, and warns that the expression produces no
  records. A missing `KHR_texture_transform` is authored only on textureInfos an exported channel points at.
- **Shared targets are valid.** Every supported channel remains available. Export does not require helper nodes
  or reject shared rotation/scale targets. Mandatory additive targets accumulate every current contribution;
  non-additive targets use the documented host composition policy.

### Response-animation scope

The exporter writes whatever response entries are in the `CharacterExpressionSet`. The wire extension is not
limited to a particular facial vocabulary, but each entry is a stateless response sampled from a finite host-supplied
scalar. Ordinary looping locomotion and idle playback belong on the standard UnityGLTF animation path unless an
author deliberately intends that response contract.

## Round-trip caveats

Honest fidelity notes for an export → import (or import → export) cycle. None of these break neutral-glTF
loading in a third-party viewer; they are documented so consumers know what is and isn't preserved.

- **The passive response evaluator preserves CUBICSPLINE.** It decodes in-tangent/value/out-tangent records and
  evaluates them with the glTF time-scaled Hermite rule. The authoring model (`CharacterExpressionSet`), which the
  legacy baker builds for an imported character, reduces CUBICSPLINE to its value keys. Exporting that model writes
  LINEAR, because it no longer contains the tangent triplets required for a valid CUBICSPLINE accessor.
- **Legacy expression samplers are normalized to response progress.** Export maps every multi-key input range to
  `[0, 1]`, and expands a legacy single target into an initial-to-target LINEAR response. This guarantees that the
  response reaches its target at progress `1`, independent of the source clip's duration.
- **UV-transform export is anchored to the authored material rest.** `BaseSt` is the static material transform and
  every multi-key output is reconstructed as `BaseSt + frameRelativeDelta`. A differing first value is valid and
  remains the channel neutral sample used for additive composition. `Frame0St` remains legacy import metadata but is not an export
  baseline: a foreign animation whose first key differs from its static material is rebased to the static value.
- **A material shared by multiple renderers is one entry on the wire.** UV-transform channels are addressed per
  material via `KHR_animation_pointer` (`/materials/{m}/...`), not per renderer. If two
  drivers in one expression resolve to the same concrete material property, identical curves are emitted once and
  conflicting curves reject the export. Give renderers distinct materials when they need independent texture
  animation.
- **The expression wire is Khronos-neutral.** The exporter writes no vendor `extras` on expression entries. Each
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

## Unity expression applicator

`KHR_character_expression` defines how simultaneous expressions compose (see
[Expression pipeline and composition](#expression-pipeline-and-composition)); only their mix with other writers is
host-defined. `ExpressionController` is the optional Unity host that applies the composed output. The import plugin
adds it only when `CreateExpressionController` is enabled.

- **Pipeline.** Each `LateUpdate`, after the Animator and after `GazeSolver`, the controller forms the final native
  drivers: the direct weights (`SetWeight`), or the output of the selected input mapping set
  (`SelectVocabularyInputSet`, `SetWeightByVocabulary`). It then builds the driver snapshot, composes every entry at
  `t = v[i] * T` and writes the aggregate records. Drivers and endpoint commands must be finite and are clamped to
  `[0, 1]`. `EvaluateVocabularyOutputs` computes an optional host projection from `u`, so a suppressed source keeps its
  projected output. This API is outside the current wire contract. `Snapshot` and `Composition` expose the last step's `u`, `v` and failures, which the inspector
  shows.
- **Imported characters** evaluate their `ExpressionResponseSet`. Import binds every property a channel can write
  to the imported Unity objects (`ExpressionTargetBinding`), with its authored initial value `b`; the legacy bake is
  kept only as `Set`/`BakedSet`, the authoring model for extraction and export. The controller persists the response
  set and bindings, so an editor-imported prefab or an instantiated copy rehydrates in `Awake`.
- **Authored sets** (`Initialize(CharacterExpressionSet)`) run through the same pipeline: each track becomes the
  response animation its export would contain (`AuthoredExpressionCurves`), with legacy samplers normalized to
  response progress, values in glTF space and constant channels unlisted, so previewing an authored set matches
  evaluating the exported asset. Their properties are keyed `authored:<instanceId>/<property>`. A track that the
  exporter would refuse produces no output and one warning, for example a curve with invalid samples. Shared targets and differing time-zero values remain valid.
- **Application.** Translation is written as `(-x, y, z)` to `localPosition` (times the editor import scale factor),
  rotation as `(x, -y, -z, w)` to `localRotation`, keeping UnityGLTF's 180° turn on camera and light nodes, and scale
  to `localScale`. Morph weights are the node's complete vector, written as `SetBlendShapeWeight(j, w * frameWeight)`
  for every element and never clamped. Unity clamps blendshape weights to `[0, 100]` unless
  `PlayerSettings.legacyClampBlendShapeWeights` is off, so turn it off to see weights outside `[0, 1]`. A
  `KHR_texture_transform`'s offset, scale and rotation go to a `MaterialPropertyBlock` per renderer and material
  slot: `<texture>_ST` with the importer's conversion (V flip, and the mirrored form for flipped textures) and
  `<texture>Rotation` when the material has one.
- **Ownership.** `ExpressionControllerOwnershipMode.Standalone` (the default) owns the bound targets: a property
  without a record returns to its authored initial value. `Integrated` leaves a property without a record, and a
  morph weight no record covers, to an earlier writer such as an Animator. Because a texture's offset and scale share
  one `_ST` vector, when only one of them has a record the other keeps the earlier writer's value, read back from the
  slot's property block or else from the material. In both modes, a property with a record replaces the earlier
  writer's value with the composed result; it is not layered on top of it.
- **Export.** The exporter writes authored sets. Export of imported expressions still fails closed
  (`RejectPassiveExpressionData`): a character with an `ExpressionResponseSet` aborts the export rather than
  re-exporting the legacy projection.

Using Unity material instances, `MaterialPropertyBlock`, shader parameters, or a separate texture-transform layer
are all valid host implementations. The wire contract identifies distinct glTF target properties and their composed
values; it does not require Unity materials.

## Skeleton mapping

`KHR_character_skeleton_mapping` is `identifier -> { role -> { node, name? } }`: the identifier is any nonempty
mapping-set key, the role is a vocabulary role (`hips`, `head`, …), and `node` is a glTF node index (a `glTFid` into
the document's global `nodes[]`). The optional `name` is an independent label, including an empty string. The baker
(`KhrCharacterSkeletonBaker`) resolves each role only via the node index and preserves labels and association
payloads; label differences produce no warning.

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
with its exact required, nonempty `poseType`. Each node of the asset has a resolved value per tagged animation:
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

- **Initial camera pose.** `TryGetInitialCameraFrame(hint, hierarchyRoot, out frame)` uses the full global node
  transform before removing global scaling under core camera rules. It retains reflected frames and returns false
  for singular/zero-scale transforms; such a hint remains valid but defines no initial pose. `Apply` leaves the camera
  untouched in that case. Otherwise it applies the asset frame plus host placement through `worldToCameraMatrix`,
  looking along local -Z with +Y up. `TryGetInitialPose` / `ComputeAuthoredRotation` are rigid Unity host adaptations;
  a quaternion cannot retain reflection or shear. The compatibility wrapper throws for an undefined frame. The old
  per-local scale-sign/zero-column rotation recipe is no longer the normative camera interpretation.
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
index becomes its name, and the sampler/curve/base data plus per-expression metadata (name, domains, suppressions
with their presence flags, mapping sets) are carried verbatim. Because the asset holds no live scene refs and no `Quaternion[]`
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
humanoid-available-but-not-built, over-driven targets). The `KhrCharacter` inspector shows it.

## Serialize → rehydrate

The baked data is import-time only, but the components survive being saved as a prefab:

- `ExpressionResponseSet` persists its decoded entries, suppressions, mapping sets and validation status, so a
  rehydrated prefab builds snapshots and composes like a live import.
- `ExpressionController` persists its authoring set and, for an imported character, its response set and target
  bindings in hidden serialized fields, and rehydrates them in `Awake` (a live import calls `Initialize` directly).
- `SkeletonMap` persists a `SerializableSkeletonMapping` (Unity cannot serialize the
  `Dictionary<string,Transform>`) and converts back to the runtime `SkeletonMappingResult` on load.
- `CameraHintSet` and `LookAtTargetSet` persist passive descriptors and intra-hierarchy `Transform` references;
  `CameraHintSet` also persists the authored matrices on its hints' paths. `KhrCharacter` rehydrates both hub links
  without creating a gaze consumer.

## Key files

- Contracts (import↔runtime boundary): `Contracts/KhrCharacter.Contracts.cs`, `Contracts/ExpressionTargetBinding.cs`
- Import/baking: `Import/KhrCharacterImportPlugin.cs`, `Import/KhrCharacterBaker.cs`,
  `Import/ExpressionTargetBindingBaker.cs`, `Import/KhrCharacterSkeletonBaker.cs`
- Export: `Export/KhrCharacterExportPlugin.cs`, `Export/KhrCharacterExportContext.cs`
- Passive expression wire path: `Components/ExpressionResponseSet.cs`,
  `Evaluation/ExpressionResponseEvaluator.cs`, `Evaluation/ExpressionDriverPipeline.cs`,
  `Evaluation/ExpressionComposer.cs`, `Evaluation/ExactBinary64Sum.cs`,
  `Evaluation/ExpressionInitialValueValidation.cs`, `Evaluation/ExpressionDerivedExtensionRegistry.cs`, and
  `Import/KhrCharacterResponseBaker.cs`
- Runtime host adapters: `Components/ExpressionController.cs`, `Components/SkeletonMap.cs`, `Components/CameraHintSet.cs`,
  `Components/LookAtTargetSet.cs`, `Components/GazeSolver.cs` (optional host adapter), and
  `Components/EyeAimConstraint.cs` (non-spec, opt-in eye aiming)
- Authoring: `Authoring/CharacterExpressionSetAsset.cs`, `Authoring/AuthoredExpressionCurves.cs`
- Schema: `Schema/` (`KhrCharacterExtensionNames.cs` holds the exact extension names)
- Tests: `Tests/Runtime/KhrCharacter/`
