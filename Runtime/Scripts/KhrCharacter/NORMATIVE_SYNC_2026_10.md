# October 2026 normative synchronization

Specification: `Kjakubzak/glTF-private@9dc0781cebd1a24a0869aa98d2a3b902754ecd92`.
Package baseline: `Kjakubzak/UnityGLTF avatar_ext_normative@a7fb6367963ed553540a96c19730df8475a0dfff`.

## Current expression mapping

The sole wire table is `expressionSetMappings[identifier][externalInputKey]`. Each contribution's wire `source`
is the native destination expression index. `weight` is a finite coefficient in `[0,1]`; optional `name` is an
independent label, including an empty string. Identifiers are opaque, nonempty, exact strings. The preferred VRM
identifier is `vrm1_expressions`; an older URL remains a valid custom identifier.

Use `ExpressionResponseSet.EvaluateExpressionSetMapping(identifier, IReadOnlyDictionary<string,double>)` to obtain
an immutable, full-count native driver vector. Every supplied input must be finite; this API rejects nonfinite
unknown inputs too. Recognized inputs are clamped independently, repeated terms and inputs add, and each native
sum is clamped. Missing and unknown keys contribute zero. Dictionary comparison is ordinal even if the caller
uses a case-insensitive dictionary. The result feeds host arbitration, then `BuildSnapshot`, suppression, and
response sampling. Native expression indices retain their identity even when an entry's channels are unsupported.

For authoring, use `CharacterExpressionSet.MappingSets`. The C# compatibility names `MappingTarget.TargetName` and
`MappingContribution.SourceIndex` now mean external input key and driven native index. The controller derives its
`VocabularyInputSets` from this table; `SelectVocabularyInputSet` selects the mapped vector as a whole, and direct
weights remain the separate host-selected alternative. No implicit addition of direct and mapped weights occurs.

The obsolete `expressionSetInputMappings` wire operation is parsed for migration but never emitted. Legacy authored
`InputMappingSets` must be explicitly converted with `ExpressionDriverPipeline.FromLegacyInputMappingSets`, assigned
to `MappingSets`, and cleared before export. If an asset's former `expressionSetMappings` intended native-to-endpoint
output, decide and regenerate its input table: transposing coefficients does not produce a general inverse.
`EvaluateForwardMapping` and `EvaluateVocabularyOutputs` remain optional host projections for compatibility; the
specification promises neither output portability nor inversion.

## Shared targets and neutral state

Shared targets across expression entries are valid. A shared rotation or scale never causes a mixed track's
translation or morph channels to be discarded. Duplicate or overlapping targets within one animation remain invalid.
The response evaluator returns absolute samples at `v[i] * T`; drivers are not applied again as blend weights.
Authored initial `b` and channel time-zero sample `n` may differ. Translation, morph weights, texture offsets,
texture rotation angles and other additive numeric properties use `b + sum(s - n)` without clamping or wrapping.
Morph whole-array and element contributions yield one full vector, preserving untouched authored components.

This Unity host chooses the absolute sample from the highest active serialized expression index for quaternions,
node scale, texture scale, Boolean and integer properties. It normalizes chosen quaternions without changing their
chosen sign and validates finite/discrete samples. An inactive expression has no record and cannot override another.
No slerp, extra driver weighting, reference-pose subtraction, or scale multiplication is added during composition.
`IsStandardSingleWriter`, `SingleWriterPredicate` and `IsSingleWriter` are compatibility API names for these host
property types; they no longer impose structural ownership, and custom declarations cannot disable mandatory
additive aggregation. A decoder for a custom target is responsible for resolving its supported AOM property and
validating that target's sampler/type rules.

Exact binary64 additive accumulation remains a stronger package guarantee. Current normative text permits other
precision policies. Evaluation errors and nonfinite results produce property-level diagnostics under this host's
policy; no fabricated response is emitted. Unsupported target semantics remain incomplete rather than being
certified valid. Pointers into `extras` receive the same supported-AOM treatment; there is no blanket rejection.

The standalone controller restores an inactive property's authored initial value. Integrated ownership leaves it
to earlier writers. The final mix with ordinary animations and other systems is a host choice.

## Ancestor-inclusive hint visibility

`NodeVisibilityHintSet.IsHintVisible` evaluates the queried node and every ancestor within the current instance and
ANDs their predicates independently for each view. A child's `always` cannot restore a false ancestor. A node below
`first_person` and `third_person` requires both tags. Unknown roles fail closed unless the host explicitly supports
them or selects its unsupported-role policy. Custom-role resolver signatures remain unchanged and run for each hint.

`ResolveRole` remains nearest authored metadata for presentation. It cannot represent the composite Boolean and
must not drive rendering. Existing node/primitive view-controller signatures now consume cumulative visibility;
primitive predicates further restrict that node result. Authored tags, exports, GameObject activation, transforms,
ordinary animation and cameras are preserved. Hint omission of direct color/depth content is scoped per view.
Shadows, reflections, indirect effects and lighting use separate documented host policies.

## Core Boolean visibility integration

Imported `KHR_node_visibility` attaches `UnityGLTF.CoreNodeVisibility`, whose public `Visible` field holds its own
current Boolean. `IsVisibleInHierarchy(node, instanceRoot)` combines own and ancestor values. Pointer animation binds
to this component, not `GameObject.activeSelf`. Export writes the own Boolean, preserving a true child beneath a
false ancestor and retaining extension payloads. Boolean pointer export uses unsigned-byte outputs and STEP.

Both scene imports and `LoadNodeAsync` attach one default `CoreVisibilityController` at the imported instance root
when core visibility is present. A partial node-tree load evaluates only its instantiated hierarchy; source ancestors
outside that subtree are not imported or inferred. The controller disables descendant Renderers and Lights while core visibility is false,
restoring their captured enabled state when visible again or when the controller is disabled. It never deactivates
GameObjects or Cameras: ordinary animation and interaction remain active. This is a global Boolean adapter; per-view
hints still use the scoped material adapter. Hosts with another renderer-state owner should use the core query in
their own adapter instead of simultaneously controlling `Renderer.enabled` while this controller suppresses it.
Manually inactive GameObjects remain an independent host state, not imported core visibility metadata.

## Camera and skeleton metadata

`CameraHintSet.TryGetInitialCameraFrame` follows the core camera frame: accumulate full local matrices, then remove
global basis-column scale while retaining reflection and nonorthogonal axes. Any exact-zero local scale makes the
frame undefined; no epsilon rejects valid small scales. Undefined frames leave `Apply`'s camera unchanged. `Apply`
sets the view matrix from the full frame. `TryGetInitialPose` and `ComputeAuthoredRotation` provide a rigid Unity
Transform adaptation; that quaternion alone cannot represent a reflected or sheared core frame. The subject point
does not aim or otherwise alter camera orientation. Host placement and projection remain host integration choices.

Every imported reference pose requires its own explicit nonempty `poseType`. Missing labels are retained as invalid,
never silently defaulted. Newly authored objects default to an explicit `t_pose`; custom labels remain exact.
Skeleton association names are independent optional labels, including empty strings. Node indices determine
associations; root/entry metadata survives serialization and export. The preferred identifier is `vrm1_humanoid`.

## Existing limits

Passive imported expression data remains authoritative and export still fails closed until lossless passive export
exists; preserve the original asset for pass-through. Legacy authoring projection does not retain CUBICSPLINE tangent
triplets. Unity's numeric object model stores several authored transforms as float32. Core visibility's default
renderer adapter is global, and hosts own indirect rendering and competing renderer-state policies. No new custom
expression-target decoders are implied by exposing Boolean/integer host composition types.
