using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Drives the FFT ocean simulation (OceanFFT.compute) and publishes the
/// resulting displacement / derivative / turbulence texture arrays as
/// global shader properties for the Kelobyte/Ocean shader.
/// </summary>
[ExecuteInEditMode]
public class OceanSimulation : MonoBehaviour
{
    const int Cascades = 3;

    [Header("Setup")]
    public ComputeShader oceanCompute;
    [Range(6, 10)] public int sizePower = 8; // 256 default

    [Header("Cascade tile sizes (m) — keep ratios non-integer to hide tiling")]
    public float lengthScale0 = 250f;
    public float lengthScale1 = 47f;
    public float lengthScale2 = 9f;

    [Header("Wind / spectrum (JONSWAP)")]
    public float windSpeed = 8f;              // m/s
    [Range(0f, 360f)] public float windDirectionDeg = 30f;
    public float fetch = 100000f;             // m
    [Range(0f, 1f)] public float spread = 0.8f;
    public float peakEnhancement = 3.3f;      // gamma
    public float depth = 500f;
    public float shortWaveFade = 0.01f;
    public float amplitudeMultiplier = 1f;

    [Header("Waves")]
    [Range(0f, 2f)] public float choppiness = 1f;
    public float foamDecay = 0.06f;
    public float timeScale = 1f;

    RenderTexture h0, wavesData, twiddle, pingBuffer;
    RenderTexture dxdz, dydxz, dyxdyz, dxxdzz;
    RenderTexture displacement, derivatives, turbulence;
    Texture2D noise;

    int size, logSize;
    int kTwiddle, kInit, kConj, kTime, kHFFT, kVFFT, kAssemble;
    float simTime;
    bool spectrumDirty = true;

    // ----------------------------------------------------------------- init

    void OnEnable()
    {
        if (oceanCompute == null) return;

        size = 1 << sizePower;
        logSize = sizePower;

        kTwiddle  = oceanCompute.FindKernel("PrecomputeTwiddle");
        kInit     = oceanCompute.FindKernel("InitialSpectrum");
        kConj     = oceanCompute.FindKernel("ConjugateSpectrum");
        kTime     = oceanCompute.FindKernel("TimeSpectrum");
        kHFFT     = oceanCompute.FindKernel("HorizontalIFFT");
        kVFFT     = oceanCompute.FindKernel("VerticalIFFT");
        kAssemble = oceanCompute.FindKernel("AssembleMaps");

        CreateResources();
        spectrumDirty = true;
    }

    void OnDisable() => ReleaseResources();
    void OnValidate() => spectrumDirty = true;

    void CreateResources()
    {
        ReleaseResources();

        h0           = CreateArray(RenderTextureFormat.ARGBFloat);
        wavesData    = CreateArray(RenderTextureFormat.ARGBFloat);
        pingBuffer   = CreateArray(RenderTextureFormat.RGFloat);
        dxdz         = CreateArray(RenderTextureFormat.RGFloat);
        dydxz        = CreateArray(RenderTextureFormat.RGFloat);
        dyxdyz       = CreateArray(RenderTextureFormat.RGFloat);
        dxxdzz       = CreateArray(RenderTextureFormat.RGFloat);
        displacement = CreateArray(RenderTextureFormat.ARGBHalf, mips: true);
        derivatives  = CreateArray(RenderTextureFormat.ARGBHalf, mips: true);
        turbulence   = CreateArray(RenderTextureFormat.RHalf,    mips: true);

        twiddle = new RenderTexture(logSize, size, 0, RenderTextureFormat.ARGBFloat,
                                    RenderTextureReadWrite.Linear)
        { enableRandomWrite = true, filterMode = FilterMode.Point };
        twiddle.Create();

        noise = GenerateGaussianNoise(size);

        // start with foam fully dissipated (Jacobian ~ 1)
        for (int i = 0; i < Cascades; i++)
        {
            Graphics.SetRenderTarget(turbulence, 0, CubemapFace.Unknown, i);
            GL.Clear(false, true, Color.white);
        }
        Graphics.SetRenderTarget(null);

        oceanCompute.SetInt("Size", size);
        oceanCompute.SetTexture(kTwiddle, "TwiddleData", twiddle);
        oceanCompute.Dispatch(kTwiddle, logSize, size / 16, 1);
    }

    RenderTexture CreateArray(RenderTextureFormat format, bool mips = false)
    {
        var rt = new RenderTexture(size, size, 0, format, RenderTextureReadWrite.Linear)
        {
            dimension = TextureDimension.Tex2DArray,
            volumeDepth = Cascades,
            enableRandomWrite = true,
            useMipMap = mips,
            autoGenerateMips = false,
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Trilinear,
            anisoLevel = mips ? 6 : 0
        };
        rt.Create();
        return rt;
    }

    static Texture2D GenerateGaussianNoise(int size)
    {
        var rng = new System.Random(12345);
        var tex = new Texture2D(size, size, TextureFormat.RGFloat, false, true)
        { filterMode = FilterMode.Point };

        var pixels = new Color[size * size];
        for (int i = 0; i < pixels.Length; i++)
        {
            float u1 = Mathf.Max((float)rng.NextDouble(), 1e-6f);
            float u2 = (float)rng.NextDouble();
            float r  = Mathf.Sqrt(-2f * Mathf.Log(u1));
            pixels[i] = new Color(r * Mathf.Cos(2f * Mathf.PI * u2),
                                  r * Mathf.Sin(2f * Mathf.PI * u2), 0f);
        }
        tex.SetPixels(pixels);
        tex.Apply(false, true);
        return tex;
    }

    // ------------------------------------------------------------- spectrum

    void InitializeSpectrum()
    {
        float b1 = 2f * Mathf.PI / lengthScale1 * 6f;
        float b2 = 2f * Mathf.PI / lengthScale2 * 6f;

        oceanCompute.SetInt("Size", size);
        oceanCompute.SetVector("LengthScales", new Vector4(lengthScale0, lengthScale1, lengthScale2, 1f));
        oceanCompute.SetVector("CutoffLow",  new Vector4(0.0001f, b1, b2, 0f));
        oceanCompute.SetVector("CutoffHigh", new Vector4(b1, b2, 9999f, 0f));

        float rad = windDirectionDeg * Mathf.Deg2Rad;
        oceanCompute.SetFloat("WindSpeed", Mathf.Max(windSpeed, 0.1f));
        oceanCompute.SetVector("WindDirection", new Vector2(Mathf.Cos(rad), Mathf.Sin(rad)));
        oceanCompute.SetFloat("Fetch", Mathf.Max(fetch, 1000f));
        oceanCompute.SetFloat("Spread", spread);
        oceanCompute.SetFloat("Gamma", peakEnhancement);
        oceanCompute.SetFloat("Depth", Mathf.Max(depth, 1f));
        oceanCompute.SetFloat("ShortWaveFade", shortWaveFade);
        oceanCompute.SetFloat("AmplitudeMultiplier", amplitudeMultiplier);

        oceanCompute.SetTexture(kInit, "Noise", noise);
        oceanCompute.SetTexture(kInit, "H0", h0);
        oceanCompute.SetTexture(kInit, "WavesData", wavesData);
        Dispatch(kInit);

        oceanCompute.SetTexture(kConj, "H0", h0);
        Dispatch(kConj);

        spectrumDirty = false;
    }

    // ------------------------------------------------------------ per frame

    void Update()
    {
        if (oceanCompute == null || h0 == null) return;
        if (spectrumDirty) InitializeSpectrum();

        simTime += Time.deltaTime * timeScale;

        oceanCompute.SetFloat("Time", simTime);
        oceanCompute.SetFloat("DeltaTime", Time.deltaTime * timeScale);
        oceanCompute.SetFloat("Lambda", choppiness);
        oceanCompute.SetFloat("FoamDecay", foamDecay);

        oceanCompute.SetTexture(kTime, "H0", h0);
        oceanCompute.SetTexture(kTime, "WavesData", wavesData);
        oceanCompute.SetTexture(kTime, "DxDz", dxdz);
        oceanCompute.SetTexture(kTime, "DyDxz", dydxz);
        oceanCompute.SetTexture(kTime, "DyxDyz", dyxdyz);
        oceanCompute.SetTexture(kTime, "DxxDzz", dxxdzz);
        Dispatch(kTime);

        InverseFFT(dxdz);
        InverseFFT(dydxz);
        InverseFFT(dyxdyz);
        InverseFFT(dxxdzz);

        oceanCompute.SetTexture(kAssemble, "DxDz", dxdz);
        oceanCompute.SetTexture(kAssemble, "DyDxz", dydxz);
        oceanCompute.SetTexture(kAssemble, "DyxDyz", dyxdyz);
        oceanCompute.SetTexture(kAssemble, "DxxDzz", dxxdzz);
        oceanCompute.SetTexture(kAssemble, "Displacement", displacement);
        oceanCompute.SetTexture(kAssemble, "Derivatives", derivatives);
        oceanCompute.SetTexture(kAssemble, "Turbulence", turbulence);
        Dispatch(kAssemble);

        displacement.GenerateMips();
        derivatives.GenerateMips();
        turbulence.GenerateMips();

        Shader.SetGlobalTexture("_Ocean_Displacement", displacement);
        Shader.SetGlobalTexture("_Ocean_Derivatives", derivatives);
        Shader.SetGlobalTexture("_Ocean_Turbulence", turbulence);
        Shader.SetGlobalVector("_Ocean_LengthScales",
            new Vector4(lengthScale0, lengthScale1, lengthScale2, 1f));
    }

    void InverseFFT(RenderTexture target)
    {
        // 2*logSize steps total (even) => result always lands back in Buffer0 (target)
        oceanCompute.SetTexture(kHFFT, "TwiddleData", twiddle);
        oceanCompute.SetTexture(kHFFT, "Buffer0", target);
        oceanCompute.SetTexture(kHFFT, "Buffer1", pingBuffer);
        oceanCompute.SetTexture(kVFFT, "TwiddleData", twiddle);
        oceanCompute.SetTexture(kVFFT, "Buffer0", target);
        oceanCompute.SetTexture(kVFFT, "Buffer1", pingBuffer);

        bool readZero = true;
        for (int step = 0; step < logSize; step++)
        {
            oceanCompute.SetInt("Step", step);
            oceanCompute.SetBool("PingPong", readZero);
            Dispatch(kHFFT);
            readZero = !readZero;
        }
        for (int step = 0; step < logSize; step++)
        {
            oceanCompute.SetInt("Step", step);
            oceanCompute.SetBool("PingPong", readZero);
            Dispatch(kVFFT);
            readZero = !readZero;
        }
    }

    void Dispatch(int kernel) => oceanCompute.Dispatch(kernel, size / 8, size / 8, Cascades);

    void ReleaseResources()
    {
        Release(ref h0); Release(ref wavesData); Release(ref twiddle); Release(ref pingBuffer);
        Release(ref dxdz); Release(ref dydxz); Release(ref dyxdyz); Release(ref dxxdzz);
        Release(ref displacement); Release(ref derivatives); Release(ref turbulence);
        if (noise != null) { DestroyImmediate(noise); noise = null; }
    }

    static void Release(ref RenderTexture rt)
    {
        if (rt != null) { rt.Release(); DestroyImmediate(rt); rt = null; }
    }
}
