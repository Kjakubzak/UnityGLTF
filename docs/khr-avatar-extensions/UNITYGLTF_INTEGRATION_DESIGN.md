# KHR_character Export — UnityGLTF Integration Design (Phases 2–4)

Author: `unitygltf-architect`
Scope: How the `KhrCharacterExportContext` plugin produces `KHR_character_expression`
(+ `_morphtarget` / `_joint` / `_texture` / `_mask`), `KHR_character_expression_mapping`,
`KHR_character_skeleton_mapping`, `KHR_character_reference_pose`, and `KHR_character`,
integrating cleanly with the existing UnityGLTF export pipeline.

This document is the **UnityGLTF-mechanics** companion to the spec-architecture design.
It assumes the schema classes already exist (they do — see `Runtime/Scripts/KhrCharacter/Schema/`)
and the importer/baker (`KhrCharacterBaker`, `KhrCharacterSkeletonBaker`) define the round-trip contract.

---

## 0. Three decisions that shape everything

1. **Hook is `AfterSceneExport`, not `OnExporting`.**
   `GLTFExportPluginContext` (`Runtime/Scripts/Plugins/Core/GltfExportPlugin.cs:20-34`) has **no**
   `OnExporting` method. The current `KhrCharacterExportContext.OnExporting` override
   (`Runtime/Scripts/KhrCharacter/Export/KhrCharacterExportContext.cs:25`) **does not compile against the
   base class and is never invoked**. All work moves into
   `public override void AfterSceneExport(GLTFSceneExporter exporter, GLTFRoot gltfRoot)`.

2. **The plugin is cross-assembly → public API only.**
   `UnityGLTF.KhrCharacter.Runtime` references `UnityGLTFScripts` but is a **separate** assembly
   (`Runtime/Scripts/KhrCharacter/UnityGLTF.KhrCharacter.Runtime.asmdef`). A `partial class
   GLTFSceneExporter` is therefore impossible. We **cannot** call the private typed `ExportAccessor(float[])`,
   `ExportAccessor(Vector3[])`, etc. We must build animation data through **public** exporter APIs.

3. **Channels must be built with per-channel native/pointer control.**
   `AddAnimationData(...)` converts **every** channel to a `KHR_animation_pointer` channel when
   `UseAnimationPointer` is on (`ExporterAnimationPointer.cs:536`), and `UseAnimationPointer` is **global**
   for the export. But the importer requires:
   - **joint** channels to be **native** (`Target.Node` + `path` ∈ {translation,rotation,scale}) — `KhrCharacterBaker.BakeJointChannels:265`,
   - **texture** channels to be **pointer** (`KhrCharacterBaker.BakeTextureChannels:407` requires `GetPointer`),
   - **morph** channels either native full-width *or* per-blendshape pointer (`KhrCharacterBaker.BakeMorphChannels:106`).

   No single value of the global flag satisfies all three. → We build channels **manually** so each channel
   is exactly native or pointer as required. (Joints may alternatively reuse `AddAnimationData` when the
   pointer plugin is off — see §3.5b.)

4. **Export runs in EDIT mode → read BAKED state, never runtime state.**
   `Awake`/`Start`/`Bind` never run during editor export (no `[ExecuteAlways]`), so `ExpressionController.Set`,
   `SkeletonMap.Result`, and the `KhrCharacter` hub's wired refs are **null/empty**. Use `ExpressionController.BakedSet`
   and a new `SkeletonMap.EditorBakedResult`; discover components via `GetComponentInChildren`, not the hub
   (confirmed by unity-best-practices C1/C2). See §2.1–2.2. **This adds one required additive accessor to `SkeletonMap`.**

---

## 1. Lifecycle, ordering, and why `AfterSceneExport` is correct

Export order inside `SaveGLBToStream` / `SaveGLTFandBin` (`GLTFSceneExporter.cs:770-855 / 862-908`):

```
BeforeSceneExport(plugins)               // nodes/meshes/materials NOT yet created
ExportScene → ExportNode (recursive)     // nodes, meshes, materials, textures created here
if (ExportAnimations) ExportAnimation()  // standard AnimationClip export
ExportSkinFromNode(...)
AfterSceneExport(plugins)   ◄────────────  WE RUN HERE. all indices stable.
animationPointerResolver.Resolve(this)   // built-in pointer late-resolution pass
_root.Serialize(...)                     // JSON written
```

Running in `AfterSceneExport` gives us:
- **Stable node indices** via `exporter.GetTransformIndex(Transform)` (`SceneExporter/ExporterAnimation.cs:1604`).
- **Stable material indices** via `exporter.GetMaterialId(gltfRoot, Material)` (`GLTFSceneExporter.cs:1384`).
- A fully-built `GLTFRoot` we can append animations and root extensions to.
- It runs **after** standard `ExportAnimation()`, so our new `GLTFAnimation`s are appended after any
  body/locomotion clips — exactly the plugin's scope rule (facial expressions only; body anims go through
  the standard path). Our animation indices are therefore stable and known at creation time.

Because all indices are known **at `AfterSceneExport` time**, we **resolve all JSON-pointer path strings
inline** and do **not** need the built-in late `animationPointerResolver` pass (which exists only because the
standard exporter builds channels during node traversal, before indices are stable). This also means we do
**not** depend on the `KHR_animation_pointer` *plugin* being enabled — we declare the extension usage ourselves.

> Template to copy: `MaterialVariantsPlugin.AfterSceneExport` (`Runtime/Scripts/Plugins/Experimental/MaterialVariantsPlugin.cs:23`)
> and `InteractivityExportContext.ApplyInteractivityExtension` (`Runtime/Scripts/Interactivity/Export/InteractivitiyExportContext.cs:58`)
> both show "read root → build `IExtension` → `gltfRoot.AddExtension(...)` → `exporter.DeclareExtensionUsage(...)`".

### Required code fix

`KhrCharacterExportContext` (edit-mode-safe discovery + per-character try/catch — see §2.1):
```csharp
// DELETE: public override void OnExporting(GLTFSceneExporter exporter, GLTFRoot gltfRoot)
public override void AfterSceneExport(GLTFSceneExporter exporter, GLTFRoot gltfRoot)
{
    var roots = exporter.RootTransforms;          // public; see MaterialVariantsPlugin usage
    if (roots == null) return;

    foreach (var root in roots)
    {
        // Export runs with Awake/Start UN-RUN (§2.1): the KhrCharacter hub's wired refs
        // (.Skeleton/.Expressions/.Capabilities) are null/empty. Discover concrete components directly;
        // use the hub only for the root-node transform.
        var hub  = root.GetComponentInChildren<KhrCharacter>(true);
        var ctrl = root.GetComponentInChildren<ExpressionController>(true);
        var skel = root.GetComponentInChildren<SkeletonMap>(true);
        if (!hub && !ctrl && !skel) continue;     // nothing to export; no-op keeps other plugins unaffected

        try
        {
            if (hub)  ExportCharacterRoot(exporter, gltfRoot, hub.transform);  // Phase 2 (KHR_character)
            if (ctrl) ExportExpressions(exporter, gltfRoot, ctrl);             // Phase 2 + 3 (interleaved, §3)
            if (skel) ExportSkeletonMapping(exporter, gltfRoot, skel);         // Phase 4
            if (skel) ExportReferencePose(exporter, gltfRoot, skel);           // Phase 4
        }
        catch (System.Exception e)
        {
            // A malformed character must not abort the whole glTF export (unity-best-practices C3).
            UnityEngine.Debug.LogError($"[KHR_character] Export failed for '{root.name}': {e}");
        }
    }
}
```
Test note: `Tests/Runtime/KhrCharacter/KhrCharacterExportTests.cs:89` has a TODO referencing
`OnExporting`; update it to `AfterSceneExport`.

---

## 2. Data sources (what we read on the Unity side)

### 2.1 ⚠️ Export runs in EDIT mode — read BAKED state, not runtime state

Confirmed by unity-best-practices (blocker C1): glTF export executes in the editor with **`Awake`/`Start`/`Bind`
never called** (no KHR component has `[ExecuteAlways]`). The runtime working state is therefore **null/empty** at
export time and must NOT be used:

- ❌ `ExpressionController.Set` (runtime `_set`) is null → ✅ use `ExpressionController.BakedSet`
  (`=> _serializedSet`, public, explicitly edit-time-safe, `ExpressionController.cs:99`). Robust form:
  `ctrl.Set ?? ctrl.BakedSet`.
- ❌ `SkeletonMap.Result` (runtime `_result`) is null at edit time; its baked data lives in the **private**
  `_serializedMapping` with **no edit-time accessor today** → **required additive fix, §2.2**.
- ❌ The `KhrCharacter` hub's `.Skeleton` / `.Expressions` / `.Capabilities` are wired in `Start()` → null/empty
  at edit time. **Do not branch on `hub.Has(...)` / `hub.Skeleton` / `hub.Expressions`.** Discover concrete
  components via `GetComponentInChildren<ExpressionController>()` / `<SkeletonMap>()`; the hub object itself
  exists at edit time, so `hub.transform` (for `rootNode`) is valid — only its wired refs are empty.

### 2.2 Prerequisite accessor — ✅ LANDED (`SkeletonMap.EditorBakedResult`)

`SkeletonMap` exposes no baked data at edit time. Add the mirror of `ExpressionController.BakedSet`:
```csharp
// SkeletonMap.cs — additive, no behavior change
public SkeletonMappingResult EditorBakedResult => _result ?? _serializedMapping?.ToResult();
```
`SerializableSkeletonMapping.ToResult()` already exists (`Contracts:218`). **This has now LANDED** at
`SkeletonMap.cs:49` exactly as specified, so Phase 4 can read the baked mapping + reference pose at edit time.

### 2.3 Source map (edit-time-safe)

| glTF output | Edit-time source | Type |
|---|---|---|
| `KHR_character.rootNode` | `GetComponentInChildren<KhrCharacter>().transform` | `GetTransformIndex(hub.transform)` |
| `KHR_character_expression[*]` + sub-exts | `ctrl.Set ?? ctrl.BakedSet` | `CharacterExpressionSet` (`ExpressionController.cs:99`) |
| `KHR_character_expression_mapping` | `(ctrl.Set ?? ctrl.BakedSet).MappingSets` | `ExpressionMappingSet[]` |
| `KHR_character_skeleton_mapping` | `(skel.Result ?? skel.EditorBakedResult)` → `.Bones`/`.SelectedRig`/`.Direction` | `SkeletonMappingResult` |
| `KHR_character_reference_pose` anim | `(skel.Result ?? skel.EditorBakedResult).ReferencePose` | `ReferencePose` (`Contracts`) |

`CharacterExpressionSet` (`Contracts/KhrCharacter.Contracts.cs:130`) holds `ExpressionTrack[] Expressions`,
each with `MorphDriver[] / JointDriver[] / TextureDriver[] / MaskEntry[]`. These are the **delta model**
(see §3.4 — must be reconstructed to absolute on export). Detect which domains exist from non-empty driver
arrays, **not** from `hub.Capabilities` (unwired at edit time).

---

## 3. Phase 2 + Phase 3 — expression metadata **and** animation data are interleaved

### 3.1 Why they cannot be separated in code

The sub-extensions reference **channel indices within the expression's own animation**:

- `KHR_character_expression_morphtarget.Channels : int[]` → indices into `animation.Channels`
  (`Schema/KHR_character_expression_morphtarget.cs:14`; consumed at `KhrCharacterBaker.cs:114`).
- `_joint.Channels`, `_texture.Channels` — same contract.

So we cannot "write metadata in Phase 2, then data in Phase 3". For each expression we must:
create the animation → add channels (recording each channel's index) → build the sub-extensions from those
indices → add the `ExpressionItem`. Phase 2 and Phase 3 are one pass.

### 3.2 One animation per expression

```csharp
var exprExt = new KHR_character_expression();           // root extension accumulator
foreach (var track in set.Expressions) {
    var anim = new GLTFAnimation { Name = track.Name, Channels = new List<AnimationChannel>(), Samplers = new List<AnimationSampler>() };
    int animationIndex = gltfRoot.Animations?.Count ?? 0; // index BEFORE add (see §3.3)

    var morphChannels   = new List<int>();
    var jointChannels   = new List<int>();
    var textureChannels = new List<int>();

    foreach (var d in track.MorphDrivers   ?? Empty)  AddMorphChannel(exporter, gltfRoot, anim, d, morphChannels);
    foreach (var d in track.JointDrivers   ?? Empty)  AddJointChannel(exporter, gltfRoot, anim, d, jointChannels);
    foreach (var d in track.TextureDrivers ?? Empty)  AddTextureChannels(exporter, gltfRoot, anim, d, textureChannels);

    if (anim.Channels.Count == 0) continue;             // mirrors core gate (ExporterAnimation.cs:249)
    gltfRoot.Animations ??= new List<GLTFAnimation>();
    gltfRoot.Animations.Add(anim);

    var item = new KHR_character_expression.ExpressionItem { Expression = track.Name, Animation = animationIndex };
    if (morphChannels.Count   > 0) item.Morphtarget = new KHR_character_expression_morphtarget { Channels = morphChannels.ToArray() };
    if (jointChannels.Count   > 0) item.Joint       = new KHR_character_expression_joint       { Channels = jointChannels.ToArray() };
    if (textureChannels.Count > 0) item.Texture     = new KHR_character_expression_texture     { Channels = textureChannels.ToArray() };
    // item.Mask: see §3.6
    exprExt.Expressions.Add(item);
}

if (exprExt.Expressions.Count > 0) {
    gltfRoot.AddExtension(KHR_character_expression.EXTENSION_NAME, exprExt);   // GLTFProperty.AddExtension throws on dup → guard
    exporter.DeclareExtensionUsage(KHR_character_expression.EXTENSION_NAME, false);
}
```

### 3.3 Tracking channel indices (the linchpin)

Channel index = position in `animation.Channels`. The `Add*Channel` helpers append channels and record indices:

```csharp
int before = anim.Channels.Count;
// ... append 1 (morph/joint/index-swap) or 2 (UV scale+offset) channels ...
for (int i = before; i < anim.Channels.Count; i++) outChannelList.Add(i);
```

This `Count`-delta technique also works if we ever choose to route a channel through `AddAnimationData`
(which is `void`): capture `Count` before/after to learn which channel indices it produced.

Animation index = `gltfRoot.Animations.Count` **before** `Add` (a newly added animation lands at `Count-1`).
We compute it before adding channels because the `ExpressionItem.Animation` field must point at this animation.

### 3.4 ⚠️ Delta → absolute reconstruction (correctness-critical)

The drivers store **frame-0-relative deltas + a base value** (`Contracts` header; `KhrCharacterBaker`
`BuildMorphDrivers:163`, `BuildJointVectorDriver:324`, `BuildJointRotationDriver:360`,
`BuildUvTransformDriver:504`). glTF samplers need **absolute** values. We invert the baker:

| Domain | Multi-key reconstruction | Single-key (`Sampler.SingleKey == true`) |
|---|---|---|
| Morph weight | `w[k] = BaseValue + DeltaValues[k]` (raw [0..1]; `BaseValue==0` for facial) | `w = DeltaValues[0]` (delta already holds absolute target) |
| Joint translation/scale | `v[k] = BaseVec + DeltaVec[k]` | `v = DeltaVec[0]` |
| Joint rotation | `q[k] = DeltaQuat[k] * BaseQuat` | `q = DeltaQuat[0]` |
| Texture UV `_ST` | `st[k] = BaseSt + StValues[k]` | `st = StValues[0]` |

The single-key rule mirrors the importer ("Single-key samplers store the absolute target",
`KhrCharacterBaker.cs:186-188` etc.). Rotation uses quaternion composition because the baker computed
`delta = q * inverse(frame0)` (`KhrCharacterBaker.cs:377`).

> Scope caveat to surface to the spec/integration teammates: the delta model with `BaseValue==0` for morphs
> **loses the original glTF frame-0 weight** if it was non-zero. Within the plugin's stated scope (facial
> expressions, 0→1 driven, neutral = 0) this is lossless. Document it; do not try to "fix" it here.

### 3.5 The three channel builders (manual, public-API only)

All three use the **accessor + channel primitives** in §4. Times come from `Sampler.Times`; interpolation from
`MapInterp(Sampler.Interp)` (inverse of `KhrCharacterBaker.MapInterp:723` → `Step/Linear/CubicSpline` →
`STEP/LINEAR/CUBICSPLINE`).

**(a) Morph — per-blendshape pointer** `/nodes/{nodeIndex}/weights/{blendShapeIndex}`
(matches the task spec and `KhrCharacterBaker.TryParseNodeWeightsPointer:581` / `BuildMorphPointerDriver:225`).

```csharp
int node = exporter.GetTransformIndex(driver.Smr.transform);
if (node < 0) return;                                   // SMR not exported → skip
float[] times  = driver.Sampler.Times;
float[] values = ReconstructMorph(driver);              // §3.4, SCALAR, raw [0..1]
var input  = ExportTimes(exporter, times);
var output = ExportScalar(exporter, values);
string pointer = $"/nodes/{node}/weights/{driver.BlendShapeIndex}";
AddPointerChannel(exporter, gltfRoot, anim, driver.Smr, pointer, input, output, MapInterp(driver.Sampler.Interp), outChannels);
```
*Note:* a single blendshape → SCALAR output (1 float/keyframe). No `BlendShapeFrameWeight` multiplier is
needed because the driver already stores raw [0..1] (unlike the native full-width path in `AddAnimationData`'s
`SkinnedMeshRenderer` case at `ExporterAnimationPointer.cs:275-301`).

> ⚠️ **U3 (blendshape index drift, unity-best-practices):** the `{blendShapeIndex}` in the pointer must be the
> **glTF morph-target index**, which equals the Unity `driver.BlendShapeIndex` only if export preserves blendshape
> order 1:1 with no targets dropped/merged. UnityGLTF exports morph targets in Unity blendshape order, so the
> common case is identity — but verify against the exported mesh's morph-target count and remap if the exporter
> ever culls empty/zero targets. A drifted index silently drives the wrong shape on re-import.

**(b) Joint — native TRS** (`Target.Node` set, `Path` = "translation"/"rotation"/"scale"):

```csharp
int node = exporter.GetTransformIndex(driver.Target);
if (node < 0) return;
var input = ExportTimes(exporter, driver.Sampler.Times);
AccessorId output; string path;
switch (driver.Channel) {
  case TrsChannel.Translation: output = ExportVec3(exporter, Reconstruct(driver), convert:true);  path="translation"; break;
  case TrsChannel.Scale:       output = ExportVec3(exporter, Reconstruct(driver), convert:false); path="scale";       break;
  case TrsChannel.Rotation:    output = ExportVec4(exporter, ReconstructQuat(driver));            path="rotation";    break;
}
AddNativeChannel(gltfRoot, anim, node, path, input, output, MapInterp(driver.Sampler.Interp), outChannels);
```
Handedness: translation via `ToGltfVector3Convert` (negates X to glTF), scale via `ToGltfVector3Raw`,
rotation via `ToGltfQuaternionConvert` — the exact inverses of the baker's `ToUnity*Convert/Raw`
(`KhrCharacterBaker.cs:296-308`). **Joints must stay native** (`KhrCharacterBaker.BakeJointChannels` only reads
native TRS); they must never become pointer channels.

> **Reconciliation with spec-architect (their constraint #3):** they recommend `AddAnimationData(transform,
> path, anim, times, object[]{Quaternion/Vector3...})` for joints so core handles handedness. That is correct
> **only while `UseAnimationPointer` is OFF** — if the `KHR_animation_pointer` plugin is enabled in the same
> export, `AddAnimationData` converts the joint channel to a pointer (`ExporterAnimationPointer.cs:536`) and
> joint import breaks. Two safe options, both acceptable:
> - **Deterministic (recommended):** build joints manually-native as above (handedness via the public
>   `ToGltf*` helpers — identical math to core, zero flag dependency, single consistent path with morph/texture).
> - **Reuse-core:** call `AddAnimationData` for joints **only** when
>   `!exporter.Plugins.Any(p => p is AnimationPointerExportContext)`; else fall back to manual-native. Capture
>   `anim.Channels.Count` before/after to recover indices (it returns `void`).
>
> Either way the *handedness result is the same*; the choice is determinism vs. less code. I lean manual-native
> because morph + texture already require manual construction, so it keeps one path.

**(c) Texture — pointer** (`KHR_animation_pointer` + `KHR_texture_transform`):

- **UV transform** → two channels (scale + offset), STEP/LINEAR per sampler:
  - `/materials/{m}/pbrMetallicRoughness/baseColorTexture/extensions/KHR_texture_transform/scale`
  - `/materials/{m}/pbrMetallicRoughness/baseColorTexture/extensions/KHR_texture_transform/offset`
- **Index swap** → one STEP channel: `/materials/{m}/pbrMetallicRoughness/baseColorTexture/index`

```csharp
var mat = driver.Renderer.sharedMaterials[driver.SubmeshSlot];
int m = exporter.GetMaterialId(gltfRoot, mat)?.Id ?? -1;
if (m < 0) return;
// G-B fix LANDED (Contracts:88-89): TextureDriver now carries `GltfTextureSlot` + `PropertyName` directly,
// so we NEVER reverse the PropertyId hash. Map the slot leaf to its glTF-spec parent path:
//   baseColorTexture / metallicRoughnessTexture -> pbrMetallicRoughness/<slot>;  normal/occlusion/emissive -> top-level.
string slotPath = ToGltfTextureInfoPath(driver.GltfTextureSlot);  // e.g. "baseColorTexture" -> "pbrMetallicRoughness/baseColorTexture"
```

For UV transform we reconstruct absolute Unity `_ST` (§3.4) then **unpack to glTF scale/offset** with the
inverse of `KhrCharacterBaker.PackSt:555`:
`scale = (st.x, st.y)`, `offset = (st.z, 1 - st.w - st.y)` (this is what core's `DecomposeScaleOffset` does at
`ExporterAnimationPointer.cs:448`). Emit `scale[]` (VEC2) and `offset[]` (VEC2) accessors.

For index swap, the output is the **glTF texture index per STEP key**. ⚠️ **U2 (unity-best-practices):** the
`driver.SwapTextures[]` are referenced *only by the driver* — they are NOT bound to any exported material slot,
so UnityGLTF never exports them on its own and `GetTextureId` returns **null** → the `/index` channel would write
a bad/missing index. **Proactively export each swap texture first** with the public
`exporter.ExportTexture(tex, slot)` (`ExporterTextures.cs:212`, returns `TextureId`); write its `.Id` as the
SCALAR value. Warn+skip any key whose texture is null/unexportable. STEP interpolation always.

**Texture slot / property name** — ✅ **RESOLVED by the G-B contract fix** (`TextureDriver.GltfTextureSlot` +
`TextureDriver.PropertyName`, `Contracts:88-89`). The earlier plan to reverse `driver.PropertyId` was wrong:
`Shader.PropertyToID` is a **one-way hash**, not reversible to a name. Now we read the slot/name straight from
the driver and build the pointer from `slotPath` above — no remapper needed. (Fallback only if `GltfTextureSlot`
is empty from an older bake: `DefaultMaterialPropertiesRemapper.GetMapFromUnityMaterial(mat, driver.PropertyName, out map)`
→ `map.GltfPropertyName`. Recommend baker always populates `GltfTextureSlot`.) Suggest `GltfTextureSlot` carry
the **full** path (`pbrMetallicRoughness/baseColorTexture`) to drop the `ToGltfTextureInfoPath` leaf→path map.

**Pointer channel construction** (no built-in resolver needed — path is final):
```csharp
void AddPointerChannel(GLTFSceneExporter exporter, GLTFRoot root, GLTFAnimation anim, Object animated,
                       string pointerPath, AccessorId input, AccessorId output, InterpolationType interp, List<int> outCh)
{
    var sampler = new AnimationSampler { Input = input, Output = output, Interpolation = interp };
    var target  = new AnimationChannelTarget { Node = null, Path = "pointer" };
    var ptr = new KHR_animation_pointer { propertyBinding = /* suffix */ null, animatedObject = animated, path = pointerPath };
    target.AddExtension(KHR_animation_pointer.EXTENSION_NAME, ptr);   // serializes {"pointer": pointerPath} (Extensions/KHR_animation_pointer.cs:20)
    int s = anim.Samplers.Count; anim.Samplers.Add(sampler);
    var channel = new AnimationChannel { Sampler = new AnimationSamplerId { Id = s, GLTFAnimation = anim, Root = root }, Target = target };
    int c = anim.Channels.Count; anim.Channels.Add(channel); outCh.Add(c);
    exporter.DeclareExtensionUsage(KHR_animation_pointer.EXTENSION_NAME, false);
    exporter.DeclareExtensionUsage("KHR_texture_transform", false);   // for UV transform channels
}
```
`KHR_animation_pointer.Serialize()` only writes `{"pointer": path}` from the `path` field
(`Extensions/KHR_animation_pointer.cs:20-24`), so setting `path` directly is sufficient — we bypass the
`animationPointerResolver` (private; cannot be reached cross-assembly anyway).

### 3.6 Mask sub-extension + mapping root extension

- **Mask** is a per-expression sub-extension (`KHR_character_expression_mask`, parsed at
  `KhrCharacterBaker.BuildMaskEntries:643`): build from `track.Masks` → `{ target: expressionName, type, amount, threshold }`.
  Attach as `item.Mask`. (`MaskEntry.TargetIndex` → resolve back to the expression name via the set's index.)
- **Mapping** is a **root** extension `KHR_character_expression_mapping` (built at
  `KhrCharacterBaker.BuildMappingSets:669`), not a per-expression one. Emit once from
  `set.MappingSets`: `{ setName: { targetName: [ { source: exprName, weight } ] } }`, then
  `gltfRoot.AddExtension(...)` + `DeclareExtensionUsage`.
- **Runtime hints (blendMode + priorities)** — **intentionally not exported.** There is no ratified KHR field for
  them, and the import baker reconstructs `Additive` + priority `0` regardless, so emitting a vendor `extras` token
  would be write-only. The exporter therefore leaves `ExpressionItem.Extras` null and the expression wire carries
  no vendor extras (fully Khronos-neutral). blendMode/priority may return later via a ratified representation.

---

## 4. Accessor creation without private APIs

We need SCALAR (times, morph weights, texture index), VEC2 (uv scale/offset), VEC3 (translation/scale),
VEC4 (rotation) accessors. The only **public** accessor entry points are the two `byte[]` overloads
(`SceneExporter/ExporterAccessors.cs:342, 384`):

```csharp
public AccessorId ExportAccessor(byte[] data, uint count, GLTFAccessorAttributeType type,
                                 GLTFComponentType componentType, List<double> min, List<double> max);
```

### Recommended: thin packing helpers in the KhrCharacter assembly

```csharp
static AccessorId ExportScalar(GLTFSceneExporter e, float[] v, bool minMax = false) {
    var bytes = new byte[v.Length * 4]; Buffer.BlockCopy(v, 0, bytes, 0, bytes.Length);
    List<double> mn = null, mx = null;
    if (minMax) { float a=float.MaxValue,b=float.MinValue; foreach (var x in v){ if(x<a)a=x; if(x>b)b=x; } mn=new(){a}; mx=new(){b}; }
    return e.ExportAccessor(bytes, (uint)v.Length, GLTFAccessorAttributeType.SCALAR, GLTFComponentType.Float, mn, mx);
}
// VEC2/VEC3/VEC4: flatten component floats (after ToGltf* handedness) → Buffer.BlockCopy → ExportAccessor(..., VECn, Float, null, null)
static AccessorId ExportTimes(GLTFSceneExporter e, float[] t) => ExportScalar(e, t, minMax: true); // glTF REQUIRES min/max on sampler.input
```

- **Times accessor requires `min`/`max`** (glTF spec for animation `sampler.input`). The public byte[] overload
  takes them explicitly — we compute them. (The private `ExportAccessor(float[])` does this automatically at
  `ExporterAccessors.cs:447`; we replicate just the min/max.)
- **Handedness** uses public `SchemaExtensions` helpers (`ToGltfVector3Convert:279`, `ToGltfVector3Raw:286`,
  `ToGltfQuaternionConvert:179`) — extract their `.X/.Y/.Z[/.W]` into the float buffer. (Verify the `GLTF.Math`
  component field type during implementation; flatten accordingly.)

### Option B (cleaner, recommended if the team will touch core): widen visibility

Either make the existing **typed** overloads `public` (visibility-only, purely additive, breaks nothing):
`ExportAccessor(float[])`, `ExportAccessor(Vector2[])`, `ExportAccessor(Vector3[])`, `ExportAccessor(Vector4[])`,
and `ExportAccessorSwitchHandedness(Quaternion[], bool)` (`ExporterAccessors.cs:447,635,708,954,166`); **or** add a
single `[assembly: InternalsVisibleTo("UnityGLTF.KhrCharacter.Runtime")]` to the core assembly and make those
overloads `internal` (unity-best-practices C4). Either deletes our packing helpers entirely and reuses the
battle-tested min/max + handedness logic — the lowest-duplication option. The only reason it's "optional" is that
it edits the core assembly. (`InternalsVisibleTo` is the lighter touch; widening to `public` is the more broadly
reusable one.)

> Do **not** reach for `AddAnimationData` as the general path: it's `void` (no channel index), forces pointer
> conversion under the global flag (breaks joints), and its `SkinnedMeshRenderer` path emits full-width weights
> (`/nodes/{i}/weights`) which the importer's per-blendshape parser rejects. It remains a *possible* shortcut
> only for the texture UV-transform case (it reuses the remapper + `DecomposeScaleOffset`), but only when
> `KHR_animation_pointer` is enabled — not worth the inconsistency given we already build channels manually.

---

## 5. Phase 4 — skeleton mapping + reference pose

### 5.1 `KHR_character_skeleton_mapping` (root extension)

Source: `skel.Result ?? skel.EditorBakedResult` (see §2.1–2.2 — `Result` is null at edit time;
`EditorBakedResult` is the required additive accessor). `SkeletonMappingResult`: `.Bones` =
`Dictionary<vocabJoint, Transform>`, `.SelectedRig`, `.Direction`. Schema:
`KHR_character_skeleton_mapping.SkeletalRigMappings` = `{ rigName → { key → value } }`
(`Schema/KHR_character_skeleton_mapping.cs:16`).

- Resolve each bone's **glTF node name** (authoritative), not the GameObject name: 
  `gltfRoot.Nodes[exporter.GetTransformIndex(t)].Name`. This matches the importer's
  `BuildNameToTransform`, which prefers `node.Name` (`KhrCharacterSkeletonBaker.cs:242-245`).
- Emit in the spec direction `TargetKeyToNodeValue` → `{ vocabJoint → nodeName }`. If
  `Result.Direction == NodeKeyToTargetValue`, the importer auto-detects either layout
  (`KhrCharacterSkeletonBaker.ResolveRig:161`), so emitting the spec layout is always safe.
- `rigName = Result.SelectedRig`.
- `gltfRoot.AddExtension(KHR_character_skeleton_mapping.EXTENSION_NAME, ext)` + `DeclareExtensionUsage`.

### 5.2 `KHR_character_reference_pose` (animation extension)

The importer (`KhrCharacterSkeletonBaker.BakeReferencePose:72`) looks for an **animation** whose
`Extensions` contains `KHR_character_reference_pose`, then samples **frame 0** of each **native** TRS channel.
So export creates a dedicated single-keyframe animation:

```csharp
var pose = (skel.Result ?? skel.EditorBakedResult)?.ReferencePose;   // edit-safe; Bones[], LocalPositions/Rotations/Scales[]
if (pose?.Bones == null || pose.Bones.Length == 0) return;
var anim = new GLTFAnimation { Name = "ReferencePose", Channels = new(), Samplers = new() };
var t0 = ExportTimes(exporter, new[] { 0f });            // single keyframe at t=0

for (int i = 0; i < pose.Bones.Length; i++) {
    int node = exporter.GetTransformIndex(pose.Bones[i]); if (node < 0) continue;
    AddNativeChannel(anim, node, "translation", t0, ExportVec3(exporter, new[]{ pose.LocalPositions[i] }, convert:true));
    AddNativeChannel(anim, node, "rotation",    t0, ExportVec4(exporter, new[]{ pose.LocalRotations[i] }));
    AddNativeChannel(anim, node, "scale",       t0, ExportVec3(exporter, new[]{ pose.LocalScales[i] }, convert:false));
}
anim.AddExtension(KHR_character_reference_pose.EXTENSION_NAME,
                  new KHR_character_reference_pose { PoseType = pose.PoseType ?? "TPose" });
gltfRoot.Animations ??= new(); gltfRoot.Animations.Add(anim);
exporter.DeclareExtensionUsage(KHR_character_reference_pose.EXTENSION_NAME, false);
```

Handedness identical to joint channels (Convert for translation, Raw for scale, Convert for rotation) so it
round-trips through `BakeReferencePose`'s `ToUnity*` calls (`KhrCharacterSkeletonBaker.cs:108-116`). The
extension goes on the **animation**, not the root.

> ⚠️ Reference-pose channels are **native-only** (`BakeReferencePose` reads `channel.Target.Node` + native TRS
> path, `KhrCharacterSkeletonBaker.cs:92-94`) — the **same hazard as joints (§3.5b)**. If the impl reuses
> `AddAnimationData` here for handedness and `KHR_animation_pointer` is enabled, the pose channels become
> pointers and `BakeReferencePose` silently returns null. Build them manually-native, or guard with
> `!exporter.Plugins.Any(p => p is AnimationPointerExportContext)`.

---

## 6. `KHR_character` root extension (Phase 2, trivial)

```csharp
int rootNode = exporter.GetTransformIndex(hub.transform);
if (rootNode >= 0) {
    gltfRoot.AddExtension(KHR_character.EXTENSION_NAME, new KHR_character { RootNode = rootNode });
    exporter.DeclareExtensionUsage(KHR_character.EXTENSION_NAME, false);
}
```

---

## 7. Compatibility with other export plugins

- **No-op when absent**: if no KHR character components are found on any root, `AfterSceneExport` returns
  immediately — zero impact on non-character exports.
- **Per-character `try/catch`**: a malformed character is logged and skipped, never aborting the whole glTF
  export (unity-best-practices C3).
- **`AddExtension` throws on duplicate keys** (`GLTFProperty.cs:148`). Guard every root/animation extension add
  with `Extensions == null || !Extensions.ContainsKey(name)` (same pattern the Interactivity context uses,
  `InteractivitiyExportContext.cs:177-187`).
- **Append-only to `_root.Animations` / `_root.Accessors`**: we never reorder or mutate existing entries, so
  standard `ExportAnimation()` output and other plugins' indices stay valid. Our animation/accessor/bufferview
  indices are all freshly allocated at the tail.
- **Independent of `KHR_animation_pointer` plugin**: we declare `KHR_animation_pointer` / `KHR_texture_transform`
  usage ourselves and resolve pointer strings inline, so enabling/disabling that plugin doesn't change our
  output and we don't perturb its global `UseAnimationPointer` path.
- **`NonRatifiedPlugin`, disabled by default** stays as-is (`KhrCharacterExportPlugin.cs:17,26`).

---

## 8. Open questions / verification items for implementation

1. `GLTF.Math.Vector3/Vector4/Quaternion` component field type (float vs double) — confirms the byte-packing
   stride in §4. (Affects only the manual-packing path; **moot** under Option B — public/`InternalsVisibleTo`.)
2. Discovery uses `root.GetComponentInChildren<…>(true)` (include-inactive) because imported character roots may
   be inactive; `exporter.RootTransforms` is the root surface (confirmed `MaterialVariantsPlugin.cs:25`).
3. Multi-renderer / multi-material slot resolution for textures: `driver.Renderer.sharedMaterials[SubmeshSlot]`
   must map to the same material index the mesh primitive used. The importer resolves slot→material via
   primitive material id (`KhrCharacterBaker.TryResolveRendererSlot:591`); confirm `GetMaterialId` returns that
   same index on export (it should, since materials are exported before `AfterSceneExport`).
4. ✅ **RESOLVED** (unity-best-practices C1): do **not** gate on `hub.Capabilities` (unwired at edit time). Gate
   on non-empty **baked** driver arrays; discover via `GetComponentInChildren` (§2.1).
5. ✅ **COMMUNICATED** to spec-architect: the delta-model frame-0 caveat (§3.4) is a scope statement, not a bug;
   the spec doc states it. No action remaining here.
6. ✅ **RESOLVED — JOINTS & REFERENCE-POSE ARE MANUAL-NATIVE** (independently confirmed by integration-validator).
   `AddAnimationData` is NOT viable for joints/pose: it converts to pointer when `UseAnimationPointer` is on
   (`ExporterAnimationPointer.cs:536`) and the importer reads only native TRS (`KhrCharacterBaker.cs:275-278`,
   `KhrCharacterSkeletonBaker.cs:92-94`); it also takes `object[]` (boxed), so `Vector3[]`/`Quaternion[]` won't
   bind. Build these channels manually-native (handedness via the public `ToGltf*` helpers, §3.5b/§4). The
   guarded-`AddAnimationData` variant is dropped.
7. ⛔ **PREREQUISITE** (unity-best-practices C2): `SkeletonMap.EditorBakedResult` accessor must land before Phase 4
   is implementable (§2.2).

---

## 9. File-touch summary

| File | Change |
|---|---|
| `Runtime/Scripts/KhrCharacter/Components/SkeletonMap.cs` | ⛔ **Required additive (prereq, §2.2)**: `public SkeletonMappingResult EditorBakedResult => _result ?? _serializedMapping?.ToResult();` |
| `Runtime/Scripts/KhrCharacter/Export/KhrCharacterExportContext.cs` | Replace `OnExporting` with `AfterSceneExport`; edit-safe component discovery + per-character try/catch; add the per-phase methods + manual channel/accessor helpers. |
| `Runtime/Scripts/KhrCharacter/Export/` (new file optional) | e.g. `KhrCharacterAnimationWriter.cs` for accessor/channel helpers if `KhrCharacterExportContext` grows large. |
| `Tests/Runtime/KhrCharacter/KhrCharacterExportTests.cs` | Update the `OnExporting` TODO at :89 to `AfterSceneExport`; add round-trip assertions. **T7** (integration-validator): export a joint expression with `AnimationPointerExport` ENABLED → assert the channel target is native (no `KHR_animation_pointer`) and the `JointDriver` survives re-import. **T9**: same assertion for the reference-pose animation (native TRS under pointer-on; pose survives re-import). |
| `Runtime/Scripts/SceneExporter/ExporterAccessors.cs` *(or `AssemblyInfo`)* | **Optional (Option B, §4)**: make typed `ExportAccessor` overloads `public`, **or** add `InternalsVisibleTo("UnityGLTF.KhrCharacter.Runtime")` + `internal`, to remove packing duplication. |

---

## 10. Key references (file:line)

- Plugin hooks: `Runtime/Scripts/Plugins/Core/GltfExportPlugin.cs:20-34`
- Hook invocation order: `GLTFSceneExporter.cs:785-808 / 886-908`
- `GetTransformIndex` / `GetIndex` / `GetObjectId`: `SceneExporter/ExporterAnimation.cs:1604,1580,1595`
- `GetMaterialId` / `GetTextureId`: `GLTFSceneExporter.cs:1384,1404`
- `GetRoot` / `RootTransforms`: `GLTFSceneExporter.cs:720` / used `MaterialVariantsPlugin.cs:25`
- `AddExtension` (throws on dup) / `DeclareExtensionUsage`: `GLTFProperty.cs:148` / `GLTFSceneExporter.cs:1001`
- `AddAnimationData` + `ConvertToAnimationPointer`: `SceneExporter/ExporterAnimationPointer.cs:65,536,578`
- Public `byte[]` accessor: `SceneExporter/ExporterAccessors.cs:342`; private typed: `:166,447,635,708,954`
- Handedness: `Schema/SchemaExtensions.cs:179,279,286`
- Animation schema: `Plugins/GLTFSerialization/Schema/{GLTFAnimation,AnimationSampler,AnimationChannel,AnimationChannelTarget}.cs`
- `KHR_animation_pointer` serialize: `Plugins/GLTFSerialization/Extensions/KHR_animation_pointer.cs:20`
- Material remapper: `Runtime/Scripts/Plugins/AnimationPointer/MaterialPropertiesRemapper.cs:320` (texXform `:173`)
- Import contract (round-trip truth): `KhrCharacter/Import/KhrCharacterBaker.cs`, `KhrCharacterSkeletonBaker.cs`
- Schema classes: `KhrCharacter/Schema/KHR_character*.cs`
- Templates: `Plugins/Experimental/MaterialVariantsPlugin.cs:23`, `Interactivity/Export/InteractivitiyExportContext.cs:33,58`

---

## 11. Pre-mortem — UnityGLTF-mechanics failure modes (orchestrator request)

Ranked by likelihood × impact. Items 1–4 are the ones most likely to ship as silent round-trip corruption.

| # | Failure mode | Likelihood | Impact | Mitigation (owned here) |
|---|---|---|---|---|
| P1 | **Mirrored joints/pose** — manual TRS forgets the asymmetric handedness (translation x-flip, rotation flip, scale NO flip). | High | High | Centralize in ONE `ExportVec3(convert)/ExportVec4(quat)` helper using `ToGltfVector3Convert`/`Raw` + `ToGltfQuaternionConvert` (§3.5b, §4). Round-trip test asserting bone TRS within ε. |
| P2 | **Joints/reference-pose silently become pointers** if `KHR_animation_pointer` plugin is enabled and impl routed them through `AddAnimationData`. Importer is native-only → channels dropped, NO error. | Med | High | Build joints + reference-pose manually-native, OR guard `!exporter.Plugins.Any(p => p is AnimationPointerExportContext)` (§3.5b, §5.2). |
| P3 | **F4b channel-index desync** — sub-extension `Channels[]` (and any parallel priority array) indexed by *driver ordinal* instead of *emitted channel index*. UV emits 2, a skipped/failed driver emits 0. | High | High | Partition strictly by recorded `anim.Channels.Count` before/after each driver (§3.3); a driver that emits 0 contributes 0 indices. If spec adds a priority array, build it in the SAME per-emitted-channel loop, not per-driver. (Coordinate w/ spec-architect.) |
| P4 | **F2 missing root `KHR_character`** → import gates all baking on it (`KhrCharacterImportPlugin.cs:70`); export is write-only. | Med | Critical | Phase 2 emits it unconditionally when a hub exists (§6). Owned here. Test asserts the root extension present. |
| P5 | **Dangling channel to an unexported node/material** — `GetTransformIndex`/`GetMaterialId` return −1 for disabled/EditorOnly/culled objects. | Med | Med | Guard every index; skip that driver. If an expression ends with 0 channels, **drop the `ExpressionItem`** (don't emit `animation: -1`). Mirrors core gate `ExporterAnimation.cs:249`. |
| P6 | **`AddExtension` throws on duplicate key** — multiple character roots, or a re-run reusing a root. | Low | Med | One aggregated root `KHR_character_expression`; guard `Extensions == null || !ContainsKey` before every root/anim add (§7). |
| P7 | **Viewer rejects animation** — `sampler.input` accessor missing min/max. | Med | Med | `ExportTimes` always computes scalar min/max (§4). |
| P8 | **Texture pointer path wrong** — `GltfTextureSlot` is a leaf ("baseColorTexture") but base/metallicRoughness live under `pbrMetallicRoughness/`. | Med | Med | `ToGltfTextureInfoPath` leaf→path map (§3.5c); recommend baker store the full slot path to remove the map. |
| P9 | **Morph 100× error** — routing morph through `AddAnimationData(SMR,"weights")` applies `1/maxBlendShapeFrameWeight`. | Low | High | Per-blendshape pointer with manual SCALAR accessor writes raw [0..1] directly (§3.5a); never use the SMR weights path. |
| P10 | **Edit-mode null state** (C1) — reading runtime `Set`/`Result`/hub refs. | — | — | ✅ Resolved: baked sources + `GetComponentInChildren` (§2.1). |
| U2 | **Index-swap textures never exported** — `SwapTextures[]` aren't on any material slot → `GetTextureId` null → bad `/index`. | Med | High | Proactively `exporter.ExportTexture(tex, slot)` (`ExporterTextures.cs:212`) before writing the channel; warn+skip if unexportable (§3.5c). |
| U3 | **Blendshape index drift** — pointer `{j}` must be the glTF morph-target index, not blindly the Unity index. | Low | High | Verify 1:1 order vs the exported mesh; remap if the exporter culls targets (§3.5a). |

**Consensus checkpoints I depend on from others:** spec-architect owns the priority-array/channel partition contract (P3) and the delta semantics (§3.4); integration-validator owns the end-to-end round-trip harness that would catch P1/P2/P3. My phases (2/3/4 export mechanics + F2) are internally consistent with the landed contract (`TextureDriver.GltfTextureSlot/PropertyName`, `SkeletonMap.EditorBakedResult`).

---

## 12. Round-trip caveats (verified)

The behaviors below are intentional and now pinned by the `Tests/Runtime/KhrCharacter` suite. They do not
break neutral-glTF loading in a third-party viewer (no `KHR_*` is marked required, and the exporter writes no
vendor `extras` at all — the wire carries no vendor token). The user-facing summary lives in
`Runtime/Scripts/KhrCharacter/README.md`
("Round-trip caveats"); this section records the mechanics.

- **CUBICSPLINE → LINEAR.** The baker samples only the per-key value block of a CUBICSPLINE accessor and records
  the track as `LINEAR` (`KhrCharacterBaker.MapInterp`); tangents are discarded. `STEP`/`LINEAR` are exact.
- **UV-transform re-anchor (FU2) — first-cycle-exact.** Import captures the animation's frame-0 absolute `_ST`
  (`Frame0St`, flagged by `HasFrame0St`) alongside the frame-0-relative `_ST` deltas and the material's static
  `_ST` (`BaseSt`). Export reconstructs `st_k = Frame0St + (frame_k − frame0)` when a frame 0 was captured, so both
  the inter-key shape (deltas) **and** the authored absolute baseline are preserved — a foreign asset whose animated
  frame 0 ≠ material rest now round-trips **exactly on the first cycle** (it is no longer merely self-stabilizing on
  a second cycle). Drivers with no captured frame 0 (hand-authored / synthesized, `HasFrame0St == false`) fall back
  to `st_k = BaseSt + (frame_k − frame0)` — the prior behavior — so existing sets are unaffected. `BaseSt` remains
  the runtime rest the compositor applies deltas over. (`KhrCharacterExportContext.WriteUvTransformChannels`,
  `KhrCharacterBaker.BuildUvTransformDriver`.)
- **Shared-material texture (P4).** Texture pointers are addressed per material (`/materials/{m}/...`), so renderers
  that share a material collapse to one animated material on the wire. Distinct per-renderer texture animation
  requires distinct materials.
- **Duplicate node names.** Skeleton-mapping values are exported as node names and UnityGLTF does not uniquify
  them; same-named bound bones are ambiguous on re-import (the humanoid build warns via `HasDuplicateBoundName`).
- **`channels` plural + `extras` neutrality.** Sub-extensions list animation channels under the `channels` key, and
  the exporter writes no expression `extras` at all, so the wire carries no vendor token (fully Khronos-neutral).
  `blendMode` / per-driver `priority` are intentionally not exported and may return via a ratified representation later.
- **Camera/look-at node extensions (#4); the camera projection index does not round-trip.** `CameraHintSet` hints
  export as `KHR_node_camera_hint` (`role`, `label`, `targetNode`) and `GazeSolver` authored targets as
  `KHR_node_lookat_target` (`hint`) — both node extensions, declared **used, never required** (neutral); role/label/
  targetNode/hint round-trip (`ExportNodeFeatures`, mirrors `KHR_node_visibility` export). The optional `camera`
  index is **omitted** unless the referenced camera was already exported onto its own node: `GLTFSceneExporter.
  ExportCamera` is **private** and there is no public `GetCameraId`, so the exporter only *reads* an already-exported
  `gltfRoot.Nodes[camNode].Camera` and never force-exports a camera. `camera` is optional in the spec, so the
  omission is conformant. A hint whose `role` is null/empty is **skipped entirely** (role is spec-required,
  `minLength:1`, and cannot be omitted), so an invalid camera hint never reaches the wire.
- **One character per glTF document (#6).** `KHR_character` is a root singleton with a single `rootNode`, so a
  document models exactly one character. `FindCharacterRoot` selects the **first** character root in
  `RootTransforms` order deterministically and, when more than one is present, logs a warning naming the skipped
  roots — nothing is silently dropped. The documented multi-character workflow is one glTF document per character;
  each round-trips independently.

### Coverage map (Phase-Z matrix gaps → tests)

| Item | Behavior | Test |
|---|---|---|
| T3 | mixed morph+joint+texture channel partition | `KhrCharacterExportMatrixTests.ExpressionMetadataExport_PartitionsMixedDomainChannels` |
| R1 | LINEAR multi-key morph export→re-bake | `KhrCharacterRoundTripTests.Morph_LinearMultiKey_ExportRebake_PreservesDeltasAndInterpolation` |
| R2 | joint rotation+translation export→re-bake (±ε, handedness) | `…Joint_RotationAndTranslation_ExportRebake_PreservesDeltasAndHandedness` |
| X1 | skeleton-only export keeps root `KHR_character`, no expression ext | `KhrCharacterExportMatrixTests.SkeletonOnlyExport_EmitsRootCharacter_NoExpressionExtension` |
| X3 | un-exported target driver skipped + warn, channels contiguous | `…JointDriver_TargetNotInExport_SkippedWithWarning_ContiguousChannels` |
| X4 | duplicate node names resolve to real exported nodes | `…SkeletonMappingExport_DuplicateNodeNames_ValuesResolveToRealNodes` |
| X5 | CUBICSPLINE→LINEAR downgrade (morph + joint) | `KhrCharacterBakerEdgeTests.Morph_CubicSpline_DowngradesToLinear`, `…JointRotation_CubicSpline_DowngradesToLinear` |
| X6 | empty `GltfTextureSlot` skipped + warn | `KhrCharacterExportMatrixTests.TextureDriver_EmptyGltfSlot_SkippedWithWarning` |
| N1 | import gate: no root `KHR_character` → nothing baked | `KhrCharacterBakerEdgeTests.Import_WithoutRootCharacter_AttachesNothing`, `…Import_WithRootCharacter_OpensGateAndAttachesHub` |
| N2 | edit-time export via `BakedSet` / `EditorBakedResult` | `KhrCharacterExportMatrixTests.Export_UsesBakedSetAndEditorBakedResult_WhenRuntimeStateNull` |
| N3 | `AnimationPointer` export disabled → joints/pose native, morph self-contained | `…Export_WithAnimationPointerDisabled_JointsAndPoseNative_MorphStillPointer` |
| P3 | sampler input min/max for morph/joint/pose | `…SamplerInputs_CarryMinMax_ForMorphJointAndReferencePose` |
| P4 | shared material resolves to one material index | `…TextureExport_SharedMaterial_ResolvesToSingleMaterialIndex` |
| P5 | idempotence (export→re-bake→export reaches a fixed point) | `KhrCharacterRoundTripTests.Idempotence_ExportRebakeCycles_ReachStructuralFixedPoint` (active/passing; proves structural stability across export→re-bake→export cycles via the real importer builders, no full in-process `GLTFSceneImporter.LoadScene` needed) |

Already covered before Phase Z (not duplicated): T1, T2, T4, T5, T6, T7, T9, R3, R4, X2, P1 (clip suppression),
and P2 (index-swap export, `ExpressionMetadataExport_IncludesIndexSwapTextureChannel`).

### Coverage map (post-Phase-Z: UV re-anchor #2, camera/look-at #4, one-doc #6)

These 12 tests ship in `KhrCharacterExportTests` and pin the #2 / #4 / #6 behaviors documented above (the 11 from
the feature work, plus the empty/null-`role` guard added in final hardening, #4e):

| Item | Behavior | Test (`KhrCharacterExportTests`) |
|---|---|---|
| #2a | multi-key UV-transform anchors at `Frame0St`, not the material rest | `UvTransform_MultiKey_AnchorsAtFrame0_NotMaterialRest` |
| #2b | multi-key UV-transform with no captured frame 0 falls back to `BaseSt` | `UvTransform_MultiKey_NoFrame0St_FallsBackToBaseSt` |
| #2c | foreign frame 0 round-trips first-cycle-exact (real baker + real export) | `UvTransform_RoundTrip_FirstCycleExact_ForeignFrame0` |
| #4a | camera hint emits `KHR_node_camera_hint` (role/label/targetNode), used-not-required | `CameraHintExport_EmitsNodeExtension` |
| #4b | look-at target emits `KHR_node_lookat_target` (hint), used-not-required | `LookatTargetExport_EmitsNodeExtension` |
| #4c | camera-hint + look-at survive serialize → factory deserialize | `CameraHint_Lookat_RoundTrip` |
| #4d | self-referencing `targetNode` omitted (role still present) | `CameraHint_SelfTargetOmitted` |
| #4e | null/empty `role` skipped (spec-required, `minLength:1`); a valid role still exports | `CameraHint_EmptyOrNullRole_NotExported_ValidRoleStillExported` |
| #4f | empty look-at hint still emits the marker extension | `LookatTarget_EmptyHint_StillEmitsExtension` |
| #4g | no authored targets → nothing emitted, extension not declared | `GazeSolver_NoAuthoredTargets_EmitsNothing` |
| #6a | two roots in one set → first wins deterministically + warning; second does not leak | `MultiCharacter_ExportsFirstDeterministically_AndWarns` |
| #6b | separate single-root exports each round-trip independently (no warning) | `MultiCharacter_SeparateExports_EachRoundTripsIndependently` |
