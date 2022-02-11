using System;
using System.Collections.Generic;
using System.Linq;
using UniRx;
using UniRx.Triggers;
using UnityEngine;


public class MaterialTransition : MonoBehaviour, IPauseable
{
    [Header("UseByOwnerがfalseの場合、以下の演出はStartの段階で再生")]
    public List<MaterialTransitionColorSetting> materialTransitionColorSettings = new List<MaterialTransitionColorSetting>();
    public List<MaterialTransitionParameterSetting> materialTransitionParameterSettings = new List<MaterialTransitionParameterSetting>();
    public List<MaterialTransitionTextureSetting> materialTransitionTextureSettings = new List<MaterialTransitionTextureSetting>();
    [Header("オーナーが使うフラグ、Awakeタイミングで設定する必要があります", order = 0), Space(-10, order = 1)]
    [Header("falseの場合は自動的にレンダラーを確認して発動する", order = 2)]
    public bool useByOwner = true;
    [Header("全てのレンダラー。オーナーが使う場合、設定する必要がない")]
    [SerializeField] private List<Renderer> ownerRenderers = new List<Renderer>();
    public Dictionary<Renderer, Material[]> originMaterials { get; private set; } = new Dictionary<Renderer, Material[]>();     //元マテリアル記録用
    public Dictionary<Renderer, Material[]> instanceMaterials { get; private set; } = new Dictionary<Renderer, Material[]>();   //インスタンスマテリアル記録用
    private Dictionary<Renderer, Dictionary<int, Dictionary<string, Dictionary<MaterialTransitionSetting, float>>>> MaterialLateUpdatePlayDataDict = new Dictionary<Renderer, Dictionary<int, Dictionary<string, Dictionary<MaterialTransitionSetting, float>>>>();     //演出待ちリスト
    private Dictionary<MaterialTransitionSetting, string> materialTransitionSettingPlayKeyDict = new Dictionary<MaterialTransitionSetting, string>();   //重複再生しないように為のキー

    private bool isPausing = false;
    private float speed = 1;

    private void OnValidate()
    {
        // キャラのレンダラーは自身のものの確認
        if (ownerRenderers.Count > 0)
        {
            var selfRenderers = GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = ownerRenderers.Count - 1; i >= 0; i--)
            {
                bool isExist = false;
                foreach (var skinnedMeshRenderer in selfRenderers)
                {
                    if (ownerRenderers[i] == skinnedMeshRenderer)
                    {
                        isExist = true;
                        break;
                    }
                }
                // 自身のものじゃない、削除する
                if (isExist == false)
                {
                    ownerRenderers.RemoveAt(i);
                }
            }
        }
        // キャラのレンダラー自動設定
        if (ownerRenderers.Count == 0 && Application.isPlaying == false)
        {
            ownerRenderers.AddRange(GetComponentsInChildren<SkinnedMeshRenderer>(true));
        }
        // キャッシュしたプロパティ名を削除
        if (isActiveAndEnabled == true)
        {
            foreach (var materialTransitionSetting in materialTransitionColorSettings)
            {
                materialTransitionSetting.ClearPropertyNameCache();
            }
            foreach (var materialTransitionSetting in materialTransitionParameterSettings)
            {
                materialTransitionSetting.ClearPropertyNameCache();
            }
            foreach (var materialTransitionSetting in materialTransitionTextureSettings)
            {
                materialTransitionSetting.ClearPropertyNameCache();
            }
        }
    }

    private void Start()
    {
        if (useByOwner == false)
        {
            SetOwnerRenderers();
            foreach (var materialTransitionColorSetting in materialTransitionColorSettings)
                PlayMaterialTransition(materialTransitionColorSetting);
            foreach (var materialTransitionParameterSetting in materialTransitionParameterSettings)
                PlayMaterialTransition(materialTransitionParameterSetting);
        }
    }
    private void LateUpdate()
    {
        UpdateMaterial();
    }

    /// <summary>
    /// 演出待ちリストに溜まった演出を再生する
    /// </summary>
    public void UpdateMaterial()
    {
        foreach (var materialIndexDict in MaterialLateUpdatePlayDataDict)
        {
            Renderer renderer = materialIndexDict.Key;
            foreach (var propertyNameDict in materialIndexDict.Value)
            {
                int materialIndex = propertyNameDict.Key;
                // マテリアルの記録が無効や元々ない場合、再生をスキップする
                if (!originMaterials.TryGetValue(renderer, out Material[] materials) || materialIndex >= materials.Length || materials[materialIndex] == null)
                    continue;
                Material originMaterial = materials[materialIndex];                                     //元々のマテリアル
                Material targetMaterial = materialIndexDict.Key.sharedMaterials[propertyNameDict.Key];  //目標マテリアル
                foreach (var playDataList in propertyNameDict.Value)
                {
                    string propertyNames = playDataList.Key;
                    MaterialTransitionSetting firstSetting = null;
                    if (playDataList.Value.Count > 0)
                        firstSetting = playDataList.Value.Keys.First();
                    switch (firstSetting)
                    {
                        case MaterialTransitionColorSetting type:       //色演出
                            // 演出設定を統合
                            Color destColor = originMaterial.GetColor(propertyNames);                   //元々の色
                            Color colorAlpha = new Color(1, 1, 1, 1);
                            Color? colorSet = null;
                            Color colorAdditive = new Color(0, 0, 0, 0);
                            Color colorMultiply = new Color(1, 1, 1, 1);
                            foreach (var playData in playDataList.Value)
                            {
                                var setting = playData.Key as MaterialTransitionColorSetting;
                                if (setting == null)
                                    continue;
                                Color srcColor = setting.colorByTime.Evaluate(playData.Value);
                                switch (setting.blendingMode)
                                {
                                    case MaterialTransitionColorSetting.BlendingMode.ColorAlphaSet:
                                        colorAlpha.a *= srcColor.a;
                                        break;
                                    case MaterialTransitionColorSetting.BlendingMode.ColorSet:
                                        colorSet = (colorSet ?? new Color(1, 1, 1, 1)) * srcColor;
                                        break;
                                    case MaterialTransitionColorSetting.BlendingMode.ColorAdditive:
                                        colorAdditive += srcColor;
                                        break;
                                    case MaterialTransitionColorSetting.BlendingMode.ColorMultiply:
                                        colorMultiply *= srcColor;
                                        break;
                                }
                            }
                            // マテリアル設定
                            Color newColor = ((colorSet ?? destColor) + colorAdditive) * colorMultiply * colorAlpha;
                            targetMaterial.SetColor(propertyNames, newColor);
                            break;
                        case MaterialTransitionParameterSetting type:   //パラメター演出
                            // 演出設定を統合
                            float destParameter = originMaterial.GetFloat(propertyNames);                 //元々パラメター値
                            float? parameterSet = null;
                            float parameterAdditive = 0;
                            float parameterMultiply = 1;
                            foreach (var playData in playDataList.Value)
                            {
                                var setting = playData.Key as MaterialTransitionParameterSetting;
                                if (setting == null)
                                    continue;
                                float srcParameter = setting.parameterByTime.Evaluate(playData.Value);
                                switch (setting.blendingMode)
                                {
                                    case MaterialTransitionParameterSetting.BlendingMode.IntSet:
                                        parameterSet = Mathf.RoundToInt(srcParameter);
                                        break;
                                    case MaterialTransitionParameterSetting.BlendingMode.IntAdditive:
                                        parameterAdditive += Mathf.RoundToInt(srcParameter);
                                        break;
                                    case MaterialTransitionParameterSetting.BlendingMode.IntMultiply:
                                        parameterMultiply *= Mathf.RoundToInt(srcParameter);
                                        break;
                                    case MaterialTransitionParameterSetting.BlendingMode.FloatSet:
                                        parameterSet = srcParameter;
                                        break;
                                    case MaterialTransitionParameterSetting.BlendingMode.FloatAdditive:
                                        parameterAdditive += srcParameter;
                                        break;
                                    case MaterialTransitionParameterSetting.BlendingMode.FloatMultiply:
                                        parameterMultiply *= srcParameter;
                                        break;
                                }
                            }
                            // マテリアル設定
                            switch (type.blendingMode)
                            {
                                case MaterialTransitionParameterSetting.BlendingMode.IntSet:
                                case MaterialTransitionParameterSetting.BlendingMode.IntAdditive:
                                case MaterialTransitionParameterSetting.BlendingMode.IntMultiply:
                                    int newIntParameter = (int)(((parameterSet ?? destParameter) + parameterAdditive) * parameterMultiply);
                                    targetMaterial.SetInt(propertyNames, newIntParameter);
                                    break;
                                case MaterialTransitionParameterSetting.BlendingMode.FloatSet:
                                case MaterialTransitionParameterSetting.BlendingMode.FloatAdditive:
                                case MaterialTransitionParameterSetting.BlendingMode.FloatMultiply:
                                    float newFloatParameter = ((parameterSet ?? destParameter) + parameterAdditive) * parameterMultiply;
                                    targetMaterial.SetFloat(propertyNames, newFloatParameter);
                                    break;
                            }
                            break;
                        case MaterialTransitionTextureSetting type:     //テキスチャー演出
                            // 演出設定を統合
                            Vector2 destVector2Offset = originMaterial.GetTextureOffset(propertyNames);   //元々オフセット値
                            Vector2 destVector2Scale = originMaterial.GetTextureScale(propertyNames);     //元々スケール値
                            Vector2? Vector2OffsetSet = null;
                            Vector2 vector2OffsetAdditive = Vector2.zero;
                            Vector2 vector2OffsetMultiply = Vector2.one;
                            Vector2? Vector2ScaletSet = null;
                            Vector2 vector2ScaleAdditive = Vector2.zero;
                            Vector2 vector2ScaleMultiply = Vector2.one;
                            foreach (var playData in playDataList.Value)
                            {
                                var setting = playData.Key as MaterialTransitionTextureSetting;
                                if (setting == null)
                                    continue;
                                Vector2 srcVector2 = Vector2.Lerp(setting.vector2Start, setting.vector2End, playData.Value);
                                switch (setting.blendingMode)
                                {
                                    case MaterialTransitionTextureSetting.BlendingMode.OffsetSet:
                                        Vector2OffsetSet = srcVector2;
                                        break;
                                    case MaterialTransitionTextureSetting.BlendingMode.OffsetAdditive:
                                        vector2OffsetAdditive += srcVector2;
                                        break;
                                    case MaterialTransitionTextureSetting.BlendingMode.OffsetMultiply:
                                        vector2OffsetMultiply *= srcVector2;
                                        break;
                                    case MaterialTransitionTextureSetting.BlendingMode.ScaleSet:
                                        Vector2ScaletSet = srcVector2;
                                        break;
                                    case MaterialTransitionTextureSetting.BlendingMode.ScaleAdditive:
                                        vector2ScaleAdditive += srcVector2;
                                        break;
                                    case MaterialTransitionTextureSetting.BlendingMode.ScaleMultiply:
                                        vector2ScaleMultiply *= srcVector2;
                                        break;
                                }
                            }
                            // マテリアル設定
                            Vector2 newVector2Offset = ((Vector2OffsetSet ?? destVector2Offset) + vector2OffsetAdditive) * vector2OffsetMultiply;
                            targetMaterial.SetTextureOffset(propertyNames, newVector2Offset);
                            Vector2 newVector2Scale = ((Vector2ScaletSet ?? destVector2Scale) + vector2ScaleAdditive) * vector2ScaleMultiply;
                            targetMaterial.SetTextureScale(propertyNames, newVector2Scale);
                            break;
                    }
                }
            }
        }
        MaterialLateUpdatePlayDataDict.Clear();
    }

    /// <summary>
    /// レンダラー設定
    /// </summary>
    /// <param name="ownerRenderers">デフォルトは自身の全ての子レンダラー</param>
    public void SetOwnerRenderers(List<Renderer> ownerRenderers = null)
    {
        // 設定したことがある場合、リセットする
        if (instanceMaterials.Count > 0)
        {
            ResetPlay();
            ResetToOriginMaterial();
        }
        // レンダラー設定
        if (ownerRenderers == null) //デフォルト設定
        {
            if (this.ownerRenderers.Count == 0)
                this.ownerRenderers.AddRange(GetComponentsInChildren<SkinnedMeshRenderer>(true));   //子レンダラー取得
        }
        else
        {
            this.ownerRenderers.Clear();
            this.ownerRenderers.AddRange(ownerRenderers);
        }
        // マテリアル記録クリア
        originMaterials.Clear();
        instanceMaterials.Clear();
    }

    /// <summary>
    /// 複数のマテリアル演出を再生
    /// </summary>
    /// <param name="materialTransitionSettings">マテリアル演出リスト</param>
    /// <param name="observer">再生終了時に、OnCompleted情報を受け取る用</param>
    public void PlayMaterialTransition<T>(IReadOnlyList<T> materialTransitionSettings, IObserver<Unit> observer = null) where T : MaterialTransitionSetting
    {
        List<Subject<Unit>> allSubjects = new List<Subject<Unit>>();
        if (materialTransitionSettings != null)
            foreach (var materialTransitionSetting in materialTransitionSettings)
            {
                Subject<Unit> subject = null;
                if (observer != null)
                    allSubjects.Add(subject = new Subject<Unit>());
                PlayMaterialTransition(materialTransitionSetting, subject);
            }
        if (allSubjects.Count > 0)
            Observable.WhenAll(allSubjects).Subscribe(_ => observer.OnCompleted()).AddTo(gameObject);   //完成のイベントを送信
    }

    /// <summary>
    /// マテリアル演出を再生
    /// </summary>
    /// <param name="materialTransitionSetting">マテリアル演出</param>
    /// <param name="observer">再生終了時に、OnCompleted情報を受け取る用</param>
    public void PlayMaterialTransition(MaterialTransitionSetting materialTransitionSetting, IObserver<Unit> observer = null)
    {
        if (materialTransitionSetting == null)
            return;
        if (gameObject.activeInHierarchy == false)
            return;
        // 重複再生しないように為のキー
        string thisPlayKey = EffectSpawner.GenerateRandomKey();
        if (!materialTransitionSettingPlayKeyDict.ContainsKey(materialTransitionSetting))
            materialTransitionSettingPlayKeyDict.Add(materialTransitionSetting, thisPlayKey);
        else
            materialTransitionSettingPlayKeyDict[materialTransitionSetting] = thisPlayKey;
        // 演出遅延再生
        float timer = 0;
        if (materialTransitionSetting.delayPlayTime > 0)
        {
            IDisposable disposable = null;
            disposable = this.UpdateAsObservable().TakeWhile(_ => timer < materialTransitionSetting.delayPlayTime).Subscribe(_ =>
            {
                if (isPausing == false)
                {
                    timer += Time.deltaTime * speed;
                    // 再生前演出中止の場合は再生しない
                    if (materialTransitionSettingPlayKeyDict.TryGetValue(materialTransitionSetting, out string playKey) && thisPlayKey != playKey)
                        disposable?.Dispose();
                }
            }, () =>
            {
                Play(materialTransitionSetting, thisPlayKey, observer);
            }).AddTo(gameObject);
        }
        else
        {   // 演出遅延再生なし
            Play(materialTransitionSetting, thisPlayKey, observer);
        }
    }

    private void Play(MaterialTransitionSetting materialTransitionSetting, string thisPlayKey, IObserver<Unit> observer = null)
    {
        float timer = 0;
        timer = 0;
        SetMaterial(materialTransitionSetting, 0);
        // 再生中演出中止の場合は最後の結果だけ再生
        this.UpdateAsObservable().TakeWhile(_ => timer <= materialTransitionSetting.timeLength && materialTransitionSettingPlayKeyDict.TryGetValue(materialTransitionSetting, out string playKey) && thisPlayKey == playKey).Subscribe(_ =>
        {
            if (isPausing == false)
                timer += Time.deltaTime * speed; ;
            SetMaterial(materialTransitionSetting, timer / materialTransitionSetting.timeLength);
        }, () =>
        {
            SetMaterial(materialTransitionSetting, 1);
            observer?.OnCompleted();    //完成のイベントを送信
        }).AddTo(gameObject);
    }

    public void SetMaterial(MaterialTransitionSetting materialTransitionSetting, float timeRatio, List<Renderer> renderers = null)
    {
        renderers = renderers ?? ownerRenderers;
        if (renderers == null)
            return;
        foreach (Renderer renderer in renderers)
        {
            if (renderer != null)
            {
                for (int i = 0; i < renderer.sharedMaterials.Length; i++)
                {
                    // 使えるプロパティ名を確認
                    string propertyNames = null;
                    if (renderer.sharedMaterials[i] != null)
                    {
                        propertyNames = materialTransitionSetting.GetCachedPropertyName(renderer.sharedMaterials[i].shader);
                        if (string.IsNullOrEmpty(propertyNames))
                            propertyNames = materialTransitionSetting.TestPropertyName(renderer.sharedMaterials[i]);
                    }
                    if (string.IsNullOrEmpty(propertyNames))
                        continue;   //プロパティ名が見つかりません
                    // 初回再生の時にマテリアルをインスタンス化
                    if (!instanceMaterials.ContainsKey(renderer))
                    {
                        // 元のマテリアルを記録
                        if (!originMaterials.ContainsKey(renderer))
                        {
                            originMaterials.Add(renderer, renderer.sharedMaterials);
                        }
#if UNITY_EDITOR
                        if (Application.isPlaying == false) //エディタープレイしない時
                        {
                            // マテリアルをクーロンして使う
                            Material[] cloneMaterials = renderer.sharedMaterials;
                            for (int j = 0; j < cloneMaterials.Length; j++)
                            {
                                cloneMaterials[i] = new Material(renderer.sharedMaterials[i]);
                            }
                            renderer.sharedMaterials = cloneMaterials;
                            instanceMaterials.Add(renderer, cloneMaterials);
                        }
                        else
#endif
                        {
                            // マテリアルをインスタンス化して記録    //マテリアル設定なし(=null)はLitマテリアルのインスタンスとなる、元のマテリアルがない為再生しない
                            instanceMaterials.Add(renderer, renderer.materials);
                        }
                    }
                    // 演出待ちリストに追加
                    AddToPlayAtLateUpdate(renderer, i, propertyNames, materialTransitionSetting, timeRatio);
                }
            }
        }
    }

    private void AddToPlayAtLateUpdate(Renderer renderer, int materialIndex, string propertyName, MaterialTransitionSetting materialTransitionSetting, float timeRatio)  //演出待ちリストに追加
    {
        if (!MaterialLateUpdatePlayDataDict.TryGetValue(renderer, out var materialIndexDict))
        {
            materialIndexDict = new Dictionary<int, Dictionary<string, Dictionary<MaterialTransitionSetting, float>>>();
            MaterialLateUpdatePlayDataDict.Add(renderer, materialIndexDict);
        }
        if (!materialIndexDict.TryGetValue(materialIndex, out var propertyNameDict))
        {
            propertyNameDict = new Dictionary<string, Dictionary<MaterialTransitionSetting, float>>();
            materialIndexDict.Add(materialIndex, propertyNameDict);
        }
        if (!propertyNameDict.TryGetValue(propertyName, out var playDataList))
        {
            playDataList = new Dictionary<MaterialTransitionSetting, float>();
            propertyNameDict.Add(propertyName, playDataList);
        }
        if (!playDataList.ContainsKey(materialTransitionSetting))
        {   // 演出に追加
            playDataList.Add(materialTransitionSetting, timeRatio);
        }
        else
        {   // 既にあった演出は上書きする
            playDataList[materialTransitionSetting] = timeRatio;
        }
    }

    /// <summary>
    /// マテリアルを元に戻す
    /// </summary>
    public void ResetToOriginMaterial()
    {
        // インスタンス化したことあるマテリアルを元に戻す
        foreach (var keyValuePair in instanceMaterials)
        {
            Renderer renderer = keyValuePair.Key;
            if (originMaterials.TryGetValue(renderer, out Material[] value))
                renderer.sharedMaterials = value;
        }
        DestroyInstanceMaterials();             //インスタンスマテリアルを削除
    }

    /// <summary>
    /// 今行われている演出再生を中止、演出終了時の状態に飛ばす、次のフレームで表示する
    /// </summary>
    public void ResetPlay()
    {
        materialTransitionSettingPlayKeyDict.Clear();   //再生キー削除
        MaterialLateUpdatePlayDataDict.Clear();         //今の再生情報をクリア
    }

    public void Pause(bool pause)
    {
        isPausing = pause;
    }
    public void SetSpeed(float speed)
    {
        this.speed = speed;
    }

    private void DestroyInstanceMaterials()     //インスタンスマテリアルを削除
    {
        if (instanceMaterials?.Count > 0)
        {
            foreach (var materialsPair in instanceMaterials)
            {
                foreach (var material in materialsPair.Value)
                {
#if UNITY_EDITOR
                    if (Application.isPlaying == false) //エディタープレイ外
                    {
                        DestroyImmediate(material);
                    }
                    else
#endif
                        Destroy(material);
                }
            }
            instanceMaterials.Clear();
        }
    }

    private void OnDestroy()
    {
        DestroyInstanceMaterials();             //インスタンスマテリアルを削除
    }
}

[Serializable]
public abstract class MaterialTransitionSetting
{
    [Tooltip("シェーダー用のプロパティ名。順番が先の方が優先"), UnityEngine.Serialization.FormerlySerializedAs("keywords")]
    public List<string> propertyNames = new List<string>();
    [Tooltip("この時間まで再生を遅延。0:遅延しない")]
    [Range(0f, 10)] public float delayPlayTime;
    [Tooltip("再生時間")]
    [Range(0f, 10)] public float timeLength;

    private Dictionary<Shader, string> shaderPropertyDict = new Dictionary<Shader, string>();
    /// <summary>
    /// 確認したプロパティ名を取得
    /// </summary>
    /// <param name="shader"></param>
    /// <returns>null：確認したことがない。 "":確認したが、使えるプロパティ名がない</returns>
    public string GetCachedPropertyName(Shader shader)
    {
        if (shader == null)
            return null;
        bool exist = shaderPropertyDict.TryGetValue(shader, out string propertyName);
        return exist == false ? null : propertyName;
    }
    public void ClearPropertyNameCache()
    {
        shaderPropertyDict.Clear();
    }
    /// <summary>
    /// 使えるプロパティ名を確認
    /// </summary>
    /// <param name="material">マテリアル</param>
    /// <returns>"":使えるプロパティ名がない</returns>
    public string TestPropertyName(Material material)
    {
        if (material == null)
            return "";
        string propertyName = null;
        foreach (var propertyNameToTest in propertyNames)
        {
            if (material.HasProperty(propertyNameToTest))
            {
                propertyName = propertyNameToTest;
                break;
            }
        }
        propertyName = propertyName ?? "";
        // プロパティ名記録
        if (!shaderPropertyDict.ContainsKey(material.shader))
        {
            shaderPropertyDict.Add(material.shader, propertyName);
        }
        return propertyName;
    }
}

[Serializable]
public class MaterialTransitionColorSetting : MaterialTransitionSetting
{
    [Tooltip("ブレンド設定")]
    public BlendingMode blendingMode = BlendingMode.ColorMultiply;
    [Tooltip("時間当たりの色変化"), GradientUsage(true)]
    public Gradient colorByTime = new Gradient()
    {
        colorKeys = new GradientColorKey[2]
        {
            new GradientColorKey(new Color(0, 0, 0), 0),
            new GradientColorKey(new Color(1, 1, 1), 1)
        },
        alphaKeys = new GradientAlphaKey[2]
        {
            new GradientAlphaKey(1, 0),
            new GradientAlphaKey(1, 1)
        }
    };

    public enum BlendingMode
    {
        ColorAlphaSet,
        ColorSet,
        ColorAdditive,
        ColorMultiply,
    }
}

[Serializable]
public class MaterialTransitionParameterSetting : MaterialTransitionSetting
{
    [Tooltip("ブレンド設定")]
    public BlendingMode blendingMode = BlendingMode.FloatMultiply;
    [Tooltip("時間当たりのパラメター変化。X軸は時間、0から1まで")]
    public AnimationCurve parameterByTime = AnimationCurve.Linear(0, 0, 1, 1);

    public enum BlendingMode
    {
        IntSet,
        IntAdditive,
        IntMultiply,
        FloatSet,
        FloatAdditive,
        FloatMultiply,
    }
}

[Serializable]
public class MaterialTransitionTextureSetting : MaterialTransitionSetting
{
    [Tooltip("ブレンド設定")]
    public BlendingMode blendingMode = BlendingMode.OffsetAdditive;
    [Tooltip("時間当たりのVectorの起点")]
    public Vector2 vector2Start = new Vector2(0, 0);
    [Tooltip("時間当たりのVectorの終点")]
    public Vector2 vector2End = new Vector2(1, 1);

    public enum BlendingMode
    {
        OffsetSet,
        OffsetAdditive,
        OffsetMultiply,
        ScaleSet,
        ScaleAdditive,
        ScaleMultiply,
    }
}