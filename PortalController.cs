using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

public class PortalController : MonoBehaviour
{
    [Header("门")]
    public Renderer frontDoorRenderer;

    public Renderer backDoorRenderer;

    [Header("当前 Ref")]
    [Range(1, 10)] public int activeRef = 1;

    [Header("溶解外观（全局，所有场景共用）")]
    public float dissolveScale = 3f;
    [Range(0f, 1f)] public float dissolveDetail = 0.35f;

    public Color dissolveEdgeColor = new Color(1f, 0.85f, 0.55f, 1f);
    public Color dissolveEdgeColor2 = new Color(0.9f, 0.2f, 0.03f, 1f);
    [Range(0f, 30f)] public float dissolveEdgeIntensity = 8f;
    [Range(0.5f, 8f)] public float dissolveEdgePower = 2.5f;
    [Range(0.001f, 0.35f)] public float dissolveEdgeWidth = 0.12f;

    [Header("穿门过渡")]
    public float crossDuration = 1f;
    public PortalScreenTransition.ScreenFxMode crossFxMode = PortalScreenTransition.ScreenFxMode.屏幕闪光;
    public Color crossCoverTint = Color.black;
    public Color crossRimTint = new Color(1f, 0.85f, 0.55f, 1f);
    public Color crossTargetTint = Color.black;
    [Range(0f, 1f)] public float crossTargetAmount = 1f;
    public Texture crossTargetTexture;

    [Header("闪光参数")]
    public Color flashTint = Color.white;
    [Range(0.2f, 6f)] public float flashRadius = 1.6f;
    [Range(0.5f, 6f)] public float flashFalloffPower = 1.6f;

    [Header("漩涡模式参数")]
    [Range(0f, 40f)] public float swirlStrength = 26f;
    [Range(0.2f, 4f)] public float swirlFalloff = 1.2f;
    [Range(0f, 0.8f)] public float swirlPinch = 0.4f;
    [Range(0f, 1f)] public float swirlCover = 0f;
    [Range(0f, 4f)] public float swirlWobble = 1.8f;
    [Range(0f, 0.4f)] public float swirlRipple = 0.18f;

    [Header("切换过渡（按数字键换场景）")]
    public float sceneSwitchDuration = 1.4f;
    public bool animateSceneSwitch = true;

    [Header("回看总站")]
    public bool showHubThroughBackDoor = false;
    [Range(1, 10)] public int hubRef = 10;

    public bool hubPassiveView = false;

    [Header("碰撞")]
    public bool keepHubCollidersOutside = false;

    [Header("调试")]
    public bool hotkeySwitch = true;
    public bool logVerbose = false;

    Material[] _maskMaterials;
    readonly Dictionary<int, List<Renderer>> _sceneGroups = new Dictionary<int, List<Renderer>>();
    readonly List<Material> _contentMaterials = new List<Material>();
    readonly Dictionary<int, List<Collider>> _groupColliders = new Dictionary<int, List<Collider>>();
    readonly Dictionary<int, List<Light>> _groupLights = new Dictionary<int, List<Light>>();
    readonly Dictionary<Transform, int> _rootGroupId = new Dictionary<Transform, int>();
    bool _inner;
    bool _switching;
    Coroutine _switchRoutine;

    const string SceneShaderPrefix = "Portal/Scene";
    static readonly int StencilRefId = Shader.PropertyToID("_StencilRef");
    static readonly int StencilCompId = Shader.PropertyToID("_StencilComp");
    static readonly int DissolveId = Shader.PropertyToID("_Dissolve");

    const float COMPARE_EQUAL = 3f;
    const float COMPARE_NOT_EQUAL = 6f;
    const float COMPARE_ALWAYS = 8f;

    // 启动流程：刷全局参数 → 生成遮罩材质 → 收集场景分组 → 实例化内容材质 → 应用初始 Ref
    void Awake()
    {
        if (frontDoorRenderer == null) frontDoorRenderer = GetComponent<Renderer>();

        PushGlobalFx();
        CreateMaskMaterials();
        CollectSceneGroups();
        InstanceContentMaterials();
        SwitchViewRef(activeRef);
    }

    void Start()
    {
        PrintState("PLAY START");
    }

    // 刷全局参数、更新门的屏幕矩形、处理数字键切场景
    void Update()
    {
        PushGlobalFx();
        UpdateDoorScreenRect();

        if (!hotkeySwitch) return;

        for (int i = 1; i <= 9; i++)
            if (Input.GetKeyDown(KeyCode.Alpha0 + i)) SwitchScene(i);

        if (Input.GetKeyDown(KeyCode.Alpha0)) SwitchScene(10);
    }

    void OnValidate()
    {
        if (Application.isPlaying) PushGlobalFx();
    }

    int CountOf(int refId)
    {
        return _sceneGroups.TryGetValue(refId, out var l) && l != null ? l.Count : 0;
    }

    // 溶解外观是全局参数，所有场景材质共用，改一次全生效
    void PushGlobalFx()
    {
        Shader.SetGlobalFloat("_PortalNoiseScale", dissolveScale);
        Shader.SetGlobalFloat("_PortalNoiseDetail", dissolveDetail);
        Shader.SetGlobalColor("_PortalEdgeColor", dissolveEdgeColor);
        Shader.SetGlobalColor("_PortalEdgeColor2", dissolveEdgeColor2);
        Shader.SetGlobalFloat("_PortalEdgeIntensity", dissolveEdgeIntensity);
        Shader.SetGlobalFloat("_PortalEdgePower", dissolveEdgePower);
        Shader.SetGlobalFloat("_PortalEdgeWidth", dissolveEdgeWidth);
    }

    // 切场景时直接换材质实例，避免运行时改共享材质
    void CreateMaskMaterials()
    {
        if (frontDoorRenderer == null)
        {
            Debug.LogError("[Portal] Mask Renderer 没设置");
            return;
        }

        Material baseMat = frontDoorRenderer.sharedMaterial;
        if (baseMat == null || !baseMat.HasProperty(StencilRefId))
        {
            Debug.LogError("[Portal] 门的材质不是 Portal/Mask（缺 _StencilRef）");
            return;
        }

        _maskMaterials = new Material[11];
        for (int r = 1; r <= 10; r++)
        {
            var m = new Material(baseMat) { name = baseMat.name + " R" + r };
            m.SetFloat(StencilRefId, r);
            _maskMaterials[r] = m;
        }

        Log("[Portal] 已生成 10 套遮罩材质");
    }

    // 换门显示的 Ref
    public void SwitchViewRef(int n)
    {
        activeRef = Mathf.Clamp(n, 1, 10);

        RefreshSceneState();
        Log("[Portal] 门 Ref → " + activeRef + "（该 Ref 的场景物体数：" + CountOf(activeRef) + "）");
    }

    public void CycleViewRef() { SwitchViewRef(activeRef >= 10 ? 1 : activeRef + 1); }

    // 扫全场景，按材质 shader 
    void CollectSceneGroups()
    {
        _sceneGroups.Clear();

        foreach (Renderer r in FindObjectsOfType<Renderer>())
        {
            if (r == null) continue;

            foreach (Material m in r.sharedMaterials)
            {
                int n = ReadSceneRef(m);
                if (n <= 0) continue;

                if (!_sceneGroups.TryGetValue(n, out var list))
                {
                    list = new List<Renderer>();
                    _sceneGroups[n] = list;
                }
                if (!list.Contains(r)) list.Add(r);
            }
        }

        _rootGroupId.Clear();
        foreach (var kv in _sceneGroups)
            foreach (var r in kv.Value)
                if (r != null) _rootGroupId[r.transform.root] = kv.Key;

        _groupColliders.Clear();
        foreach (Collider c in FindObjectsOfType<Collider>(true))
        {
            if (c == null) continue;
            if (!_rootGroupId.TryGetValue(c.transform.root, out int refId)) continue;

            if (!_groupColliders.TryGetValue(refId, out var list))
            {
                list = new List<Collider>();
                _groupColliders[refId] = list;
            }
            list.Add(c);
        }

        _groupLights.Clear();
        foreach (Light l in FindObjectsOfType<Light>(true))
        {
            if (l == null) continue;
            if (!_rootGroupId.TryGetValue(l.transform.root, out int lid)) continue;

            if (!_groupLights.TryGetValue(lid, out var llist))
            {
                llist = new List<Light>();
                _groupLights[lid] = llist;
            }
            llist.Add(l);
        }

        var sb = new System.Text.StringBuilder();
        foreach (var kv in _sceneGroups)
            sb.Append("Ref" + kv.Key + "=" + kv.Value.Count + "  ");

        var sb2 = new System.Text.StringBuilder();
        foreach (var kv in _groupColliders)
            sb2.Append("Ref" + kv.Key + "=" + kv.Value.Count + "  ");

        Log("[Portal] 自动扫描场景：" + (sb.Length > 0 ? sb.ToString() : "（没找到 Portal/SceneN 材质）"));
        var sb3 = new System.Text.StringBuilder();
        foreach (var kv in _groupLights)
            sb3.Append("Ref" + kv.Key + "=" + kv.Value.Count + "  ");

        Log("[Portal] 碰撞体分组：" + (sb2.Length > 0 ? sb2.ToString() : "（无）"));
        Log("[Portal] 灯光分组：" + (sb3.Length > 0 ? sb3.ToString() : "（无）"));
    }

    static int ReadSceneRef(Material m)
    {
        if (m == null || m.shader == null) return 0;

        string name = m.shader.name;
        if (!name.StartsWith(SceneShaderPrefix)) return 0;

        string s = name.Substring(SceneShaderPrefix.Length);

        int len = 0;
        while (len < s.Length && char.IsDigit(s[len])) len++;
        if (len == 0) return 0;

        int n;
        return int.TryParse(s.Substring(0, len), out n) ? n : 0;
    }

    // 把内容材质复制一份，后续改 StencilComp 不会污染资源
    void InstanceContentMaterials()
    {
        _contentMaterials.Clear();
        var cache = new Dictionary<Material, Material>();

        foreach (var kv in _sceneGroups)
        {
            foreach (var r in kv.Value)
            {
                if (r == null) continue;

                Material[] shared = r.sharedMaterials;
                var inst = new Material[shared.Length];

                for (int i = 0; i < shared.Length; i++)
                {
                    Material s = shared[i];
                    if (s == null) continue;

                    if (!cache.TryGetValue(s, out Material m))
                    {
                        m = new Material(s) { name = s.name + " (Portal)" };
                        cache[s] = m;
                        _contentMaterials.Add(m);
                    }
                    inst[i] = m;
                }

                r.sharedMaterials = inst;
            }
        }

        Log("[Portal] 内容材质实例化：" + _contentMaterials.Count + " 个");
    }

    public bool IsInner { get { return _inner; } }

    // 进入当前世界：把它的比较规则改成 Always，同时开启碰撞/灯光/阴影
    public void EnterScene()
    {
        _inner = true;
        RefreshSceneState();
        PrintState("ENTER（已进入）");
    }

    // 退出：还原成只在门里可见（Equal），并关掉碰撞/灯光/阴影
    public void ExitScene()
    {
        _inner = false;
        RefreshSceneState();
        PrintState("EXIT（已离开）");
    }

    // 决定门显示谁：门外看目的地，门内看背面门（回望总站）
    void UpdateDoorMask()
    {
        if (_maskMaterials == null) return;

        if (frontDoorRenderer != null && _maskMaterials[activeRef] != null)
            frontDoorRenderer.sharedMaterial = _maskMaterials[activeRef];

    }

    public void BindBackDoor(Renderer back)
    {
        backDoorRenderer = back;
        if (_maskMaterials == null) CreateMaskMaterials();
        UpdateDoorMask();
    }

    // 单一入口：门遮罩 + 各组比较规则 + 投影 + 碰撞 + 灯光，一次同步
    void RefreshSceneState()
    {
        UpdateDoorMask();

        int renderTouched = 0, shadowOn = 0, shadowOff = 0;

        foreach (var kv in _sceneGroups)
        {
            bool isHub = showHubThroughBackDoor && kv.Key == hubRef;

            bool active = isHub
                ? (!hubPassiveView && !_inner)
                : (_inner && kv.Key == activeRef);

            float comp;
            float refVal;

            if (isHub)
            {
                comp = active ? COMPARE_ALWAYS : COMPARE_EQUAL;
                refVal = hubRef;
            }
            else if (active)
            {
                bool hasBack = showHubThroughBackDoor && backDoorRenderer != null;
                comp = hasBack ? COMPARE_NOT_EQUAL : COMPARE_ALWAYS;
                refVal = hasBack ? hubRef : kv.Key;
            }
            else
            {
                comp = COMPARE_EQUAL;
                refVal = kv.Key;
            }

            foreach (Renderer r in kv.Value)
            {
                if (r == null) continue;

                Material[] mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    Material m = mats[i];
                    if (m == null) continue;
                    if (m.HasProperty(StencilCompId)) m.SetFloat(StencilCompId, comp);
                    if (m.HasProperty(StencilRefId)) m.SetFloat(StencilRefId, refVal);
                }

                bool noShadow = r.GetComponentInParent<PortalNoShadow>() != null;

                ShadowCastingMode mode = (active && !noShadow)
                    ? ShadowCastingMode.On
                    : ShadowCastingMode.Off;

                if (r.shadowCastingMode != mode)
                {
                    r.shadowCastingMode = mode;
                    if (mode == ShadowCastingMode.On) shadowOn++; else shadowOff++;
                }

                renderTouched++;
            }
        }

        int onCount = 0, offCount = 0;

        foreach (var kv in _groupColliders)
        {
            bool isHub = showHubThroughBackDoor && kv.Key == hubRef;

            bool on = keepHubCollidersOutside
                ? (kv.Key == activeRef)
                : (isHub ? (!hubPassiveView && !_inner) : (_inner && kv.Key == activeRef));

            foreach (var c in kv.Value)
            {
                if (c == null) continue;
                if (c.enabled == on) continue;

                c.enabled = on;
                if (on) onCount++; else offCount++;
            }
        }

        int lon = 0, loff = 0;
        foreach (var kv in _groupLights)
        {
            bool isHub = showHubThroughBackDoor && kv.Key == hubRef;

            bool on = keepHubCollidersOutside
                ? (kv.Key == activeRef)
                : (isHub ? (!hubPassiveView && !_inner) : (_inner && kv.Key == activeRef));

            foreach (var l in kv.Value)
            {
                if (l == null) continue;
                if (l.enabled == on) continue;

                l.enabled = on;
                if (on) lon++; else loff++;
            }
        }

        Log("[Portal] 场景刷新：渲染器 " + renderTouched +
                  "，投影 开" + shadowOn + "/关" + shadowOff +
                  "，碰撞体 开" + onCount + "/关" + offCount +
                  "，灯光 开" + lon + "/关" + loff +
                  "（已穿梭=" + _inner + "，Ref=" + activeRef + "）");
    }

    // 切场景：溶解旧世界 → 换 Ref → 溶入新世界
    public void SwitchScene(int n)
    {
        n = Mathf.Clamp(n, 1, 10);

        if (showHubThroughBackDoor && n == hubRef)
        {
            Log("[Portal] Ref " + n + " 是总站，跳过");
            return;
        }

        if (!animateSceneSwitch || sceneSwitchDuration <= 0.01f || !isActiveAndEnabled)
        {
            SwitchViewRef(n);
            ResetGroupDissolve(n);
            return;
        }

        if (_switchRoutine != null) StopCoroutine(_switchRoutine);
        _switchRoutine = StartCoroutine(TransitionRoutine(n));
    }

    System.Collections.IEnumerator TransitionRoutine(int target)
    {
        _switching = true;

        int from = activeRef;

        if (from != target && CountOf(from) > 0)
            yield return FadeRoutine(from, 0f, 1f, sceneSwitchDuration * 0.5f);

        SwitchViewRef(target);

        if (CountOf(target) > 0)
            yield return FadeRoutine(target, 1f, 0f, sceneSwitchDuration * 0.5f);

        ResetGroupDissolve(target);

        _switching = false;
        _switchRoutine = null;
    }

    System.Collections.IEnumerator FadeRoutine(int refId, float from, float to, float duration)
    {
        if (!_sceneGroups.TryGetValue(refId, out var list) || list == null || list.Count == 0)
            yield break;

        float t = 0f;
        while (t < duration)
        {
            t += Time.deltaTime;
            float v = Mathf.SmoothStep(from, to, Mathf.Clamp01(t / duration));
            SetGroupDissolve(list, v);
            yield return null;
        }

        SetGroupDissolve(list, to);
    }

    // 给一整组材质统一设置溶解量
    void SetGroupDissolve(List<Renderer> list, float v)
    {
        foreach (Renderer r in list)
        {
            if (r == null) continue;
            foreach (Material m in r.sharedMaterials)
                if (m != null && m.HasProperty(DissolveId)) m.SetFloat(DissolveId, v);
        }
    }

    void ResetGroupDissolve(int refId)
    {
        if (_sceneGroups.TryGetValue(refId, out var list) && list != null) SetGroupDissolve(list, 0f);
    }

    public bool IsSwitching { get { return _switching; } }

    // 相机上没有过渡组件就补一个
    public PortalScreenTransition EnsureScreenFx()
    {
        Camera cam = Camera.main;
        if (cam == null)
        {
            LogWarn("[Portal] 找不到 Main Camera，穿门过渡跳过");
            return null;
        }

        var fx = cam.GetComponent<PortalScreenTransition>();
        if (fx == null) fx = cam.gameObject.AddComponent<PortalScreenTransition>();
        return fx;
    }

    // 把控制器上的过渡参数同步给相机组件
    public void PushScreenFxParams(PortalScreenTransition fx)
    {
        if (fx == null) return;

        fx.coverColor = crossCoverTint;
        fx.dissolveEdgeColor = crossRimTint;
        fx.tintColor = crossTargetTint;
        fx.tintAmount = crossTargetAmount;
        fx.coverTexture = crossTargetTexture;
        fx.mode = crossFxMode;
        fx.flashColor = flashTint;
        fx.flashRadius = flashRadius;
        fx.flashPower = flashFalloffPower;
        fx.swirlStrength = swirlStrength;
        fx.swirlFalloff = swirlFalloff;
        fx.swirlPinch = swirlPinch;
        fx.swirlCover = swirlCover;
        fx.swirlWobble = swirlWobble;
        fx.swirlRipple = swirlRipple;
    }

    // 把门的屏幕矩形喂给过渡组件，保证特效只作用在门里
    void UpdateDoorScreenRect()
    {
        Camera cam = Camera.main;
        if (cam == null || frontDoorRenderer == null) return;

        var fx = EnsureScreenFx();
        if (fx == null) return;

        Bounds b = frontDoorRenderer.bounds;
        float w = Mathf.Max(Screen.width, 1);
        float h = Mathf.Max(Screen.height, 1);

        Vector2 min = new Vector2(float.MaxValue, float.MaxValue);
        Vector2 max = new Vector2(float.MinValue, float.MinValue);
        int valid = 0;

        for (int i = 0; i < 8; i++)
        {
            Vector3 corner = b.center + Vector3.Scale(b.extents,
                new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));

            Vector3 sp = cam.WorldToScreenPoint(corner);
            if (sp.z <= 0f) continue;

            Vector2 uv = new Vector2(sp.x / w, sp.y / h);
            min = Vector2.Min(min, uv);
            max = Vector2.Max(max, uv);
            valid++;
        }

        if (valid < 4) return;

        Vector2 center = (min + max) * 0.5f;
        Vector2 extent = (max - min) * 0.5f * 1.08f;
        fx.SetScreenRect(center, extent);
    }

    [ContextMenu("套用推荐过渡参数")]
    // 一键填一套能用的默认过渡参数
    public void ApplyDefaultPreset()
    {
        crossDuration = 1.0f;
        sceneSwitchDuration = 1.4f;

        crossFxMode = PortalScreenTransition.ScreenFxMode.屏幕闪光;
        flashTint = Color.white;
        flashRadius = 1.6f;
        flashFalloffPower = 1.6f;

        crossTargetTint = Color.black;
        crossTargetAmount = 1f;

        swirlStrength = 26f;
        swirlFalloff = 1.2f;
        swirlPinch = 0.4f;
        swirlWobble = 1.8f;
        swirlRipple = 0.18f;
        swirlCover = 0f;

        Log("[Portal] 已套用推荐过渡参数");
    }

    public void PrintState(string tag)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("========== [Portal] " + tag + " ==========");
        sb.AppendLine("activeRef=" + activeRef + "   hubRef=" + hubRef +
                      "   inside=" + _inner +
                      "   returnView=" + showHubThroughBackDoor +
                      "   hubViewOnly=" + hubPassiveView);
        sb.AppendLine("frontDoor: " + DescribeMaterial(frontDoorRenderer));
        sb.AppendLine("backDoor : " + DescribeMaterial(backDoorRenderer));
        sb.AppendLine("--- 场景组 ---");

        for (int r = 1; r <= 10; r++)
        {
            if (!_sceneGroups.TryGetValue(r, out var list) || list == null || list.Count == 0) continue;

            string comp = "?";
            string shadow = "?";
            foreach (var rr in list)
            {
                if (rr == null) continue;
                shadow = rr.shadowCastingMode.ToString();
                foreach (var m in rr.sharedMaterials)
                {
                    if (m == null) continue;
                    if (m.HasProperty(StencilCompId)) comp = m.GetFloat(StencilCompId).ToString("F0");
                    break;
                }
                break;
            }

            int cOn = 0, cTot = 0;
            if (_groupColliders.TryGetValue(r, out var cl))
                foreach (var c in cl) if (c != null) { cTot++; if (c.enabled) cOn++; }

            int lOn = 0, lTot = 0;
            if (_groupLights.TryGetValue(r, out var ll))
                foreach (var l in ll) if (l != null) { lTot++; if (l.enabled) lOn++; }

            sb.AppendLine("  Ref" + r + "  n=" + list.Count +
                          "  comp=" + comp +
                          "  shadow=" + shadow +
                          "  colliders=" + cOn + "/" + cTot +
                          "  lights=" + lOn + "/" + lTot);
        }

        sb.AppendLine("======================================");
        Debug.Log(sb.ToString());
    }

    static string DescribeMaterial(Renderer r)
    {
        if (r == null) return "（未设置）";
        var m = r.sharedMaterial;
        if (m == null) return r.name + "（无材质）";

        string refv = m.HasProperty("_StencilRef") ? m.GetFloat("_StencilRef").ToString("F0") : "?";
        string sh = m.shader != null ? m.shader.name : "?";
        return r.name + " | " + m.name + " | shader=" + sh + " | Ref=" + refv;
    }

    void Log(string msg) { if (logVerbose) Debug.Log(msg); }

    void LogWarn(string msg) { if (logVerbose) Debug.LogWarning(msg); }

}
