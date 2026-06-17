using System.Collections.Generic;
using UnityEngine;

namespace UnityGLTF.KhrCharacter
{
    /// <summary>
    /// Drives expressions each frame. Runs in LateUpdate (after the Animator) at a higher execution order than
    /// <see cref="GazeSolver"/> so gaze can feed the same-frame evaluation. Morph, joint, and texture targets
    /// are evaluated target-major (all active expressions combined per target, then written once) and re-based
    /// to the stored neutral each frame.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(100)]
    public class ExpressionController : MonoBehaviour
    {
        private const float IndexSwapThreshold = 0.5f;

        public struct ExpressionHandle
        {
            public string Name;
            public float Value;
            public ExpressionDomain Domains;
            public bool IsBinary;
        }

        // One accumulation slot per concrete (renderer, blendshape) target.
        private sealed class MorphTarget
        {
            public SkinnedMeshRenderer Smr;
            public int BlendShapeIndex;
            public float BaseValue;
            public float Multiplier;
            public readonly List<int> ExprIndices = new List<int>();
            public readonly List<MorphDriver> Drivers = new List<MorphDriver>();
        }

        // One accumulation slot per concrete (transform, TRS channel) target.
        private sealed class JointTarget
        {
            public Transform Target;
            public TrsChannel Channel;
            public Vector3 BaseVec;
            public Quaternion BaseQuat;
            public readonly List<int> ExprIndices = new List<int>();
            public readonly List<JointDriver> Drivers = new List<JointDriver>();
        }

        // Additive _ST property within a render target.
        private sealed class UvProp
        {
            public int PropId;
            public Vector4 BaseSt;
            public readonly List<int> ExprIndices = new List<int>();
            public readonly List<TextureDriver> Drivers = new List<TextureDriver>();
        }

        // Winner-takes texture-index property within a render target.
        private sealed class IndexProp
        {
            public int PropId;
            public Texture BaseTexture;
            public readonly List<int> ExprIndices = new List<int>();
            public readonly List<TextureDriver> Drivers = new List<TextureDriver>();
        }

        // One MaterialPropertyBlock per (renderer, submesh slot), aggregating its UV + index properties.
        private sealed class TextureRenderTarget
        {
            public Renderer Renderer;
            public int Slot;
            public MaterialPropertyBlock Mpb;
            public readonly List<UvProp> Uv = new List<UvProp>();
            public readonly List<IndexProp> Index = new List<IndexProp>();
        }

        // Persisted so an editor-imported prefab can rehydrate on Awake (the live import calls Initialize,
        // which also stores here). Not the runtime working copy — see _set below, which is rebuilt from this.
        // Hidden from the inspector: it's baked data, surfaced read-only by ExpressionControllerEditor.
        [SerializeField, HideInInspector] private CharacterExpressionSet _serializedSet;

        private CharacterExpressionSet _set;
        private IExpressionSemantics _semantics;
        private ExpressionHandle[] _handles = System.Array.Empty<ExpressionHandle>();

        private float[] _weights;   // user-set driver per expression
        private float[] _rawInputs; // scratch (post-map)
        private float[] _d;         // scratch (post-mask + clamp)
        private readonly List<MorphTarget> _morphTargets = new List<MorphTarget>();
        private readonly List<JointTarget> _jointTargets = new List<JointTarget>();
        private readonly List<TextureRenderTarget> _textureTargets = new List<TextureRenderTarget>();
        private readonly Dictionary<string, ExpressionMappingSet> _mappingSets = new Dictionary<string, ExpressionMappingSet>();
        private readonly Dictionary<(string, string), float> _vocabWeights = new Dictionary<(string, string), float>();

        public IReadOnlyList<ExpressionHandle> Expressions => _handles;
        public CharacterExpressionSet Set => _set;

        // The baked set as persisted with the component, available at edit time too (the runtime working copy
        // _set is only built in Play mode / after Initialize). Intended for editor authoring/extraction tooling.
        public CharacterExpressionSet BakedSet => _serializedSet;

        public int Count => _handles.Length;
        public IReadOnlyList<string> VocabularySets { get; private set; } = new List<string>();

        // Rehydrate an editor-imported prefab. A live import adds this component fresh (no serialized set) and
        // calls Initialize itself, so this is a no-op in that path; it only fires for a deserialized prefab.
        private void Awake()
        {
            // Rehydrate only when a baked set was actually persisted. Unity may rehydrate a never-assigned
            // [Serializable] field as a default (non-null) instance, so require a real payload, not just non-null.
            if (_set == null && _serializedSet?.Expressions != null && _serializedSet.Expressions.Length > 0)
                Initialize(_serializedSet);
        }

        /// <summary>
        /// Receive the baked expression set and (optionally) an evaluation policy. The default additive policy
        /// is used when none is supplied.
        /// </summary>
        public void Initialize(CharacterExpressionSet set, IExpressionSemantics semantics = null)
        {
            _set = set;
            // Persist for prefab rehydration. Guard the self-assign: Awake calls Initialize(_serializedSet), where
            // set and _serializedSet are already the same reference.
            if (!ReferenceEquals(_serializedSet, set)) _serializedSet = set;
            _semantics = semantics ?? AdditiveExpressionSemantics.Default;
            _set?.RebuildIndex();

            _handles = System.Array.Empty<ExpressionHandle>();
            _morphTargets.Clear();
            _jointTargets.Clear();
            _textureTargets.Clear();
            _mappingSets.Clear();
            _vocabWeights.Clear();

            var setNames = new List<string>();
            if (_set?.MappingSets != null)
                foreach (var ms in _set.MappingSets)
                    if (ms?.SetName != null) { _mappingSets[ms.SetName] = ms; setNames.Add(ms.SetName); }
            VocabularySets = setNames;

            int n = _set?.Expressions?.Length ?? 0;
            _weights = new float[n];
            _rawInputs = new float[n];
            _d = new float[n];

            if (_set?.Expressions == null) return;

            // Array (not List): SetWeight/ResetAll replace elements in place; an array's element-write doesn't bump
            // a version, so callers may safely enumerate Expressions while driving weights.
            _handles = new ExpressionHandle[_set.Expressions.Length];
            var morphLookup = new Dictionary<(SkinnedMeshRenderer, int), MorphTarget>();
            var jointLookup = new Dictionary<(Transform, TrsChannel), JointTarget>();
            var textureLookup = new Dictionary<(Renderer, int), TextureRenderTarget>();

            for (int e = 0; e < _set.Expressions.Length; e++)
            {
                var track = _set.Expressions[e];
                _handles[e] = new ExpressionHandle
                {
                    Name = track?.Name,
                    Value = 0f,
                    Domains = track?.Domains ?? ExpressionDomain.None,
                    IsBinary = track?.IsBinary ?? false,
                };

                if (track?.MorphDrivers != null)
                {
                    foreach (var driver in track.MorphDrivers)
                    {
                        if (driver?.Smr == null) continue;
                        var key = (driver.Smr, driver.BlendShapeIndex);
                        if (!morphLookup.TryGetValue(key, out var mt))
                        {
                            mt = new MorphTarget
                            {
                                Smr = driver.Smr,
                                BlendShapeIndex = driver.BlendShapeIndex,
                                BaseValue = driver.BaseValue,
                                Multiplier = GetMultiplier(driver.Smr, driver.BlendShapeIndex),
                            };
                            morphLookup[key] = mt;
                            _morphTargets.Add(mt);
                        }
                        mt.ExprIndices.Add(e);
                        mt.Drivers.Add(driver);
                    }
                }

                if (track?.JointDrivers != null)
                {
                    foreach (var driver in track.JointDrivers)
                    {
                        if (driver?.Target == null) continue;
                        var key = (driver.Target, driver.Channel);
                        if (!jointLookup.TryGetValue(key, out var jt))
                        {
                            jt = new JointTarget
                            {
                                Target = driver.Target,
                                Channel = driver.Channel,
                                BaseVec = driver.BaseVec,
                                BaseQuat = driver.BaseQuat,
                            };
                            jointLookup[key] = jt;
                            _jointTargets.Add(jt);
                        }
                        jt.ExprIndices.Add(e);
                        jt.Drivers.Add(driver);
                    }
                }

                if (track?.TextureDrivers != null)
                {
                    foreach (var driver in track.TextureDrivers)
                        AddTextureDriver(textureLookup, e, driver);
                }
            }
        }

        private void AddTextureDriver(Dictionary<(Renderer, int), TextureRenderTarget> lookup, int expr, TextureDriver driver)
        {
            if (driver?.Renderer == null) return;
            var key = (driver.Renderer, driver.SubmeshSlot);
            if (!lookup.TryGetValue(key, out var target))
            {
                target = new TextureRenderTarget { Renderer = driver.Renderer, Slot = driver.SubmeshSlot, Mpb = new MaterialPropertyBlock() };
                lookup[key] = target;
                _textureTargets.Add(target);
            }

            if (driver.Kind == TexKind.UvTransform)
            {
                var uv = target.Uv.Find(p => p.PropId == driver.PropertyId);
                if (uv == null)
                {
                    uv = new UvProp { PropId = driver.PropertyId, BaseSt = driver.BaseSt };
                    target.Uv.Add(uv);
                }
                uv.ExprIndices.Add(expr);
                uv.Drivers.Add(driver);
            }
            else // IndexSwap
            {
                var idx = target.Index.Find(p => p.PropId == driver.PropertyId);
                if (idx == null)
                {
                    idx = new IndexProp { PropId = driver.PropertyId, BaseTexture = GetBaseTexture(driver.Renderer, driver.SubmeshSlot, driver.PropertyId) };
                    target.Index.Add(idx);
                }
                idx.ExprIndices.Add(expr);
                idx.Drivers.Add(driver);
            }
        }

        public void SetWeight(string name, float driver)
        {
            if (!TryIndex(name, out int i)) return;
            _weights[i] = driver;
            var h = _handles[i]; h.Value = driver; _handles[i] = h;
        }

        public float GetWeight(string name) => TryIndex(name, out int i) ? _weights[i] : 0f;

        public void ResetAll()
        {
            if (_weights == null) return;
            for (int i = 0; i < _weights.Length; i++)
            {
                _weights[i] = 0f;
                var h = _handles[i]; h.Value = 0f; _handles[i] = h;
            }
        }

        public void SetWeightByVocabulary(string setName, string targetExpression, float driver)
        {
            if (setName == null || targetExpression == null) return;
            _vocabWeights[(setName, targetExpression)] = driver;
        }

        public IReadOnlyList<string> VocabularyExpressions(string setName)
        {
            var list = new List<string>();
            if (setName != null && _mappingSets.TryGetValue(setName, out var ms) && ms.Targets != null)
                foreach (var t in ms.Targets)
                    if (t?.TargetName != null) list.Add(t.TargetName);
            return list;
        }

        private void LateUpdate()
        {
            if (_set?.Expressions == null || _semantics == null) return;
            if (_morphTargets.Count == 0 && _jointTargets.Count == 0 && _textureTargets.Count == 0) return;

            int n = _set.Expressions.Length;

            // MAP: direct weights, then distribute any vocabulary-driven weights onto their source inputs.
            for (int i = 0; i < n; i++) _rawInputs[i] = _weights[i];
            if (_mappingSets.Count > 0 && _vocabWeights.Count > 0)
            {
                foreach (var kv in _vocabWeights)
                {
                    if (kv.Value <= 0f) continue;
                    if (_mappingSets.TryGetValue(kv.Key.Item1, out var ms))
                        _semantics.DistributeMapping(ms, kv.Key.Item2, kv.Value, _rawInputs, _set.NameToIndex);
                }
            }

            // MASK + CLAMP.
            for (int i = 0; i < n; i++)
                _d[i] = _semantics.Clamp01(_semantics.ResolveMaskedInput(i, _rawInputs, _set.Expressions));

            EvaluateMorphTargets();
            EvaluateJointTargets();
            EvaluateTextureTargets();
        }

        // Compositing policy (shared by morph / joint / texture-UV): contributors sum additively as the
        // default. If any active (d>0) Override contributor exists on a target, the winner (highest Priority,
        // tie -> latest declaration / highest expression index) replaces the additive result with its absolute
        // pose (base + winnerDelta). Additive-only targets are byte-identical to the pre-Override behavior
        // because the winner branch is skipped entirely when no Override contributor is active.
        private void EvaluateMorphTargets()
        {
            for (int t = 0; t < _morphTargets.Count; t++)
            {
                var mt = _morphTargets[t];
                if (mt.Smr == null) continue;

                float acc = mt.BaseValue;
                int winner = -1, winnerPrio = 0, winnerExpr = -1;
                for (int k = 0; k < mt.Drivers.Count; k++)
                {
                    int e = mt.ExprIndices[k];
                    float di = _d[e];
                    if (di <= 0f) continue;
                    var driver = mt.Drivers[k];
                    acc += _semantics.SampleScalarDelta(driver.Sampler, driver.DeltaValues, mt.BaseValue, di);
                    if (IsOverride(e) && IsBetterWinner(driver.Priority, e, winner, winnerPrio, winnerExpr))
                    { winner = k; winnerPrio = driver.Priority; winnerExpr = e; }
                }
                if (winner >= 0)
                {
                    var d = mt.Drivers[winner];
                    acc = mt.BaseValue + _semantics.SampleScalarDelta(d.Sampler, d.DeltaValues, mt.BaseValue, _d[mt.ExprIndices[winner]]);
                }
                mt.Smr.SetBlendShapeWeight(mt.BlendShapeIndex, _semantics.Clamp01(acc) * mt.Multiplier);
            }
        }

        private void EvaluateJointTargets()
        {
            for (int t = 0; t < _jointTargets.Count; t++)
            {
                var jt = _jointTargets[t];
                if (jt.Target == null) continue;

                if (jt.Channel == TrsChannel.Rotation)
                {
                    var accDelta = Quaternion.identity;
                    int winner = -1, winnerPrio = 0, winnerExpr = -1;
                    for (int k = 0; k < jt.Drivers.Count; k++)
                    {
                        int e = jt.ExprIndices[k];
                        float di = _d[e];
                        if (di <= 0f) continue;
                        var driver = jt.Drivers[k];
                        var delta = _semantics.SampleRotationDelta(driver.Sampler, driver.DeltaQuat, driver.BaseQuat, di);
                        accDelta = _semantics.AccumulateRotation(accDelta, delta, 1f);
                        if (IsOverride(e) && IsBetterWinner(driver.Priority, e, winner, winnerPrio, winnerExpr))
                        { winner = k; winnerPrio = driver.Priority; winnerExpr = e; }
                    }
                    if (winner >= 0)
                    {
                        var d = jt.Drivers[winner];
                        accDelta = _semantics.SampleRotationDelta(d.Sampler, d.DeltaQuat, d.BaseQuat, _d[jt.ExprIndices[winner]]);
                    }
                    jt.Target.localRotation = accDelta * jt.BaseQuat;
                }
                else
                {
                    var acc = jt.BaseVec;
                    int winner = -1, winnerPrio = 0, winnerExpr = -1;
                    for (int k = 0; k < jt.Drivers.Count; k++)
                    {
                        int e = jt.ExprIndices[k];
                        float di = _d[e];
                        if (di <= 0f) continue;
                        var driver = jt.Drivers[k];
                        acc += _semantics.SampleVectorDelta(driver.Sampler, driver.DeltaVec, driver.BaseVec, di);
                        if (IsOverride(e) && IsBetterWinner(driver.Priority, e, winner, winnerPrio, winnerExpr))
                        { winner = k; winnerPrio = driver.Priority; winnerExpr = e; }
                    }
                    if (winner >= 0)
                    {
                        var d = jt.Drivers[winner];
                        acc = jt.BaseVec + _semantics.SampleVectorDelta(d.Sampler, d.DeltaVec, d.BaseVec, _d[jt.ExprIndices[winner]]);
                    }
                    if (jt.Channel == TrsChannel.Translation) jt.Target.localPosition = acc;
                    else jt.Target.localScale = acc;
                }
            }
        }

        private void EvaluateTextureTargets()
        {
            for (int t = 0; t < _textureTargets.Count; t++)
            {
                var target = _textureTargets[t];
                if (target.Renderer == null) continue;

                target.Renderer.GetPropertyBlock(target.Mpb, target.Slot);

                // UV transforms: additive _ST over the base, with the same Override winner-takes rule as morph/joint.
                for (int u = 0; u < target.Uv.Count; u++)
                {
                    var uv = target.Uv[u];
                    var st = uv.BaseSt;
                    int winner = -1, winnerPrio = 0, winnerExpr = -1;
                    for (int k = 0; k < uv.Drivers.Count; k++)
                    {
                        int e = uv.ExprIndices[k];
                        float di = _d[e];
                        if (di <= 0f) continue;
                        var driver = uv.Drivers[k];
                        st += _semantics.SampleVector4Delta(driver.Sampler, driver.StValues, driver.BaseSt, di);
                        if (IsOverride(e) && IsBetterWinner(driver.Priority, e, winner, winnerPrio, winnerExpr))
                        { winner = k; winnerPrio = driver.Priority; winnerExpr = e; }
                    }
                    if (winner >= 0)
                    {
                        var d = uv.Drivers[winner];
                        st = uv.BaseSt + _semantics.SampleVector4Delta(d.Sampler, d.StValues, d.BaseSt, _d[uv.ExprIndices[winner]]);
                    }
                    target.Mpb.SetVector(uv.PropId, st);
                }

                // Index swaps: among drivers active above threshold, pick the winner by Priority desc, then
                // driver weight, then declaration order (earliest). With equal priorities (the baker default)
                // this is identical to the prior most-active-wins rule. Otherwise re-base to the slot's texture.
                for (int x = 0; x < target.Index.Count; x++)
                {
                    var idx = target.Index[x];
                    int best = -1, bestPrio = 0;
                    float bestWeight = 0f;
                    for (int k = 0; k < idx.Drivers.Count; k++)
                    {
                        float di = _d[idx.ExprIndices[k]];
                        if (di <= IndexSwapThreshold) continue;
                        int prio = idx.Drivers[k].Priority;
                        if (best < 0 || prio > bestPrio || (prio == bestPrio && di > bestWeight))
                        { best = k; bestPrio = prio; bestWeight = di; }
                    }

                    Texture tex = idx.BaseTexture;
                    if (best >= 0)
                    {
                        var driver = idx.Drivers[best];
                        int ki = _semantics.SampleStepIndex(driver.Sampler, _d[idx.ExprIndices[best]]);
                        if (driver.SwapTextures != null && ki >= 0 && ki < driver.SwapTextures.Length && driver.SwapTextures[ki] != null)
                            tex = driver.SwapTextures[ki];
                    }
                    if (tex != null) target.Mpb.SetTexture(idx.PropId, tex);
                }

                target.Renderer.SetPropertyBlock(target.Mpb, target.Slot);
            }
        }

        // True when the expression at exprIndex requests Override (winner-takes) compositing.
        private bool IsOverride(int exprIndex)
        {
            var exprs = _set?.Expressions;
            return exprs != null && exprIndex >= 0 && exprIndex < exprs.Length
                && exprs[exprIndex] != null && exprs[exprIndex].BlendMode == ExpressionBlendMode.Override;
        }

        // Override winner selection on a forward scan of a target's contributors: higher Priority wins; on a tie
        // the latest declaration wins. A target's expression indices are non-decreasing across k, so a >= test
        // keeps the latest (highest exprIndex / latest declaration order). bestK < 0 means "no winner yet".
        private static bool IsBetterWinner(int priority, int exprIndex, int bestK, int bestPriority, int bestExpr)
        {
            if (bestK < 0) return true;
            if (priority != bestPriority) return priority > bestPriority;
            return exprIndex >= bestExpr;
        }

        private bool TryIndex(string name, out int index)
        {
            index = -1;
            if (_set?.NameToIndex == null || name == null) return false;
            return _set.NameToIndex.TryGetValue(name, out index);
        }

        private static float GetMultiplier(SkinnedMeshRenderer smr, int blendShapeIndex)
        {
            var mesh = smr.sharedMesh;
            if (mesh == null || blendShapeIndex < 0 || blendShapeIndex >= mesh.blendShapeCount) return 1f;
            // The importer adds one blendshape frame per target with weight = BlendShapeFrameWeight.
            return mesh.GetBlendShapeFrameWeight(blendShapeIndex, 0);
        }

        private static Texture GetBaseTexture(Renderer renderer, int slot, int propId)
        {
            var mats = renderer.sharedMaterials;
            if (mats == null || slot < 0 || slot >= mats.Length || mats[slot] == null) return null;
            return mats[slot].HasProperty(propId) ? mats[slot].GetTexture(propId) : null;
        }
    }
}
