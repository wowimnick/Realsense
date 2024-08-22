using UnityEngine;
using Intel.RealSense;
using System;
using System.Collections;
using System.Collections.Generic;

public class SandboxTerrainUpdater : MonoBehaviour
{
    public RsDevice rsDevice;
    public Terrain sandboxTerrain;
    public float updateInterval = 0.05f;
    public float minDistance = 0.5f;
    public float maxDistance = 1.5f;
    public float smoothingFactor = 0.5f;
    public float blendRange = 0.05f;

    public List<TerrainLayerInfo> terrainLayers = new List<TerrainLayerInfo>();

    private struct DepthFrameData
    {
        public short[] depthData;
        public int width;
        public int height;
    }

    private Queue<DepthFrameData> depthFrameQueue = new Queue<DepthFrameData>();
    private float[,] heights;
    private float[,] previousHeights;
    private float lastUpdateTime;
    private int frameCount = 0;
    private const int LOG_INTERVAL = 30;

    private void Start()
    {
        if (rsDevice == null)
        {
            rsDevice = FindObjectOfType<RsDevice>();
        }

        if (rsDevice != null)
        {
            rsDevice.OnNewSample += QueueDepthFrameData;
        }
        else
        {
            Debug.LogError("RsDevice not found. Please assign it in the inspector or ensure it exists in the scene.");
        }

        InitializeTerrainData();
        SyncTerrainLayers();
        SortTerrainLayers();
        StartCoroutine(ProcessDepthDataCoroutine());
    }

    private void InitializeTerrainData()
    {
        int resolution = sandboxTerrain.terrainData.heightmapResolution;
        heights = new float[resolution, resolution];
        previousHeights = new float[resolution, resolution];

        if (sandboxTerrain.terrainData.alphamapLayers == 0)
        {
            Debug.LogWarning("Terrain has no splatmap layers. Please add at least one terrain layer to your terrain.");
        }

        if (terrainLayers.Count == 0)
        {
            Debug.LogError("No terrain layers defined. Please add at least one layer in the inspector.");
            enabled = false;
        }
    }

    private void SyncTerrainLayers()
    {
        TerrainLayer[] unityLayers = sandboxTerrain.terrainData.terrainLayers;
        if (unityLayers.Length != terrainLayers.Count)
        {
            Debug.LogWarning("Number of terrain layers doesn't match the script's layers. Adjusting...");
            terrainLayers.Clear();
            for (int i = 0; i < unityLayers.Length; i++)
            {
                terrainLayers.Add(new TerrainLayerInfo
                {
                    name = unityLayers[i].name,
                    height = (float)i / unityLayers.Length
                });
            }
        }
    }

    private void SortTerrainLayers()
    {
        terrainLayers.Sort((a, b) => a.height.CompareTo(b.height));
    }

    private void QueueDepthFrameData(Frame frame)
    {
        try
        {
            using (FrameSet frameset = frame.As<FrameSet>())
            using (DepthFrame depthFrame = frameset?.DepthFrame)
            {
                if (depthFrame != null)
                {
                    DepthFrameData depthFrameData = new DepthFrameData
                    {
                        depthData = new short[depthFrame.Width * depthFrame.Height],
                        width = depthFrame.Width,
                        height = depthFrame.Height
                    };
                    depthFrame.CopyTo(depthFrameData.depthData);

                    lock (depthFrameQueue)
                    {
                        if (depthFrameQueue.Count > 5)
                        {
                            depthFrameQueue.Dequeue();
                        }
                        depthFrameQueue.Enqueue(depthFrameData);
                    }
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Error in QueueDepthFrameData: {e.Message}\n{e.StackTrace}");
        }
    }

    private IEnumerator ProcessDepthDataCoroutine()
    {
        while (true)
        {
            if (Time.time - lastUpdateTime >= updateInterval)
            {
                DepthFrameData? depthFrameData = null;

                lock (depthFrameQueue)
                {
                    if (depthFrameQueue.Count > 0)
                    {
                        depthFrameData = depthFrameQueue.Dequeue();
                    }
                }

                if (depthFrameData.HasValue)
                {
                    ProcessDepthData(depthFrameData.Value);
                    lastUpdateTime = Time.time;
                }
            }

            yield return null;
        }
    }

    private void ProcessDepthData(DepthFrameData depthFrameData)
    {
        frameCount++;
        bool shouldLog = frameCount % LOG_INTERVAL == 0;

        TerrainData terrainData = sandboxTerrain.terrainData;
        int heightResolution = terrainData.heightmapResolution;
        int alphaResolution = terrainData.alphamapResolution;
        int alphaLayers = terrainData.alphamapLayers;

        float minDepth = minDistance * 1000;
        float maxDepth = maxDistance * 1000;
        float xScale = (float)depthFrameData.width / heightResolution;
        float yScale = (float)depthFrameData.height / heightResolution;
        float resetSpeed = 1f;

        float[,,] alphamaps = alphaLayers > 0 ? new float[alphaResolution, alphaResolution, alphaLayers] : null;

        float minDetectedDepth = float.MaxValue;
        float maxDetectedDepth = float.MinValue;
        int validDepthCount = 0;

        for (int y = 0; y < heightResolution; y++)
        {
            int frameY = Mathf.FloorToInt(y * yScale);
            int frameYOffset = frameY * depthFrameData.width;

            for (int x = 0; x < heightResolution; x++)
            {
                int frameX = Mathf.FloorToInt(x * xScale);
                int index = frameYOffset + frameX;

                if (index < depthFrameData.depthData.Length)
                {
                    float depth = depthFrameData.depthData[index];
                    if (depth >= minDepth && depth <= maxDepth)
                    {
                        float normalizedHeight = 1f - (depth - minDepth) / (maxDepth - minDepth);
                        heights[y, x] = Mathf.Lerp(previousHeights[y, x], Mathf.Clamp01(normalizedHeight), 1 - smoothingFactor);

                        if (alphamaps != null)
                        {
                            SetLayerWeightsForDepth(alphamaps, x, y, depth, alphaResolution);
                        }

                        if (shouldLog)
                        {
                            minDetectedDepth = Mathf.Min(minDetectedDepth, depth);
                            maxDetectedDepth = Mathf.Max(maxDetectedDepth, depth);
                            validDepthCount++;
                        }
                    }
                    else
                    {
                        heights[y, x] = Mathf.Lerp(previousHeights[y, x], 0f, resetSpeed);
                        if (alphamaps != null)
                        {
                            SetLayerWeightsForDepth(alphamaps, x, y, maxDepth + 1, alphaResolution);
                        }
                    }
                }
                else
                {
                    heights[y, x] = Mathf.Lerp(previousHeights[y, x], 0f, resetSpeed);
                    if (alphamaps != null)
                    {
                        SetLayerWeightsForDepth(alphamaps, x, y, maxDepth + 1, alphaResolution);
                    }
                }
            }
        }

        terrainData.SetHeights(0, 0, heights);
        if (alphamaps != null)
        {
            terrainData.SetAlphamaps(0, 0, alphamaps);
        }

        (heights, previousHeights) = (previousHeights, heights);

        if (shouldLog)
        {
            Debug.Log($"Frame {frameCount}: Depth range: {minDetectedDepth:F2}mm to {maxDetectedDepth:F2}mm, Valid depths: {validDepthCount}/{heightResolution*heightResolution}");
        }
    }

private void SetLayerWeightsForDepth(float[,,] alphamaps, int x, int y, float depth, int alphaResolution)
{
    int layerCount = sandboxTerrain.terrainData.alphamapLayers;
    if (layerCount == 0) return;

    int alphaX = Mathf.FloorToInt((float)x / heights.GetLength(1) * alphaResolution);
    int alphaY = Mathf.FloorToInt((float)y / heights.GetLength(0) * alphaResolution);

    depth = Mathf.Clamp(depth, minDistance * 1000, maxDistance * 1000);
    float normalizedHeight = Mathf.InverseLerp(minDistance * 1000, maxDistance * 1000, depth);

    int activeLayer = terrainLayers.Count - 1;
    float blendFactor = 0f;

    for (int i = 0; i < terrainLayers.Count - 1; i++)
    {
        if (normalizedHeight <= terrainLayers[i + 1].height)
        {
            activeLayer = i;
            float nextLayerHeight = terrainLayers[i + 1].height;
            float blendStart = Mathf.Max(terrainLayers[i].height, nextLayerHeight - blendRange);
            blendFactor = Mathf.InverseLerp(blendStart, nextLayerHeight, normalizedHeight);
            break;
        }
    }

    for (int i = 0; i < layerCount; i++)
    {
        if (i == activeLayer)
            alphamaps[alphaY, alphaX, i] = 1 - blendFactor;
        else if (i == activeLayer + 1 && activeLayer < layerCount - 1)
            alphamaps[alphaY, alphaX, i] = blendFactor;
        else
            alphamaps[alphaY, alphaX, i] = 0f;
    }

    if (frameCount % LOG_INTERVAL == 0 && x == 0 && y == 0)
    {
        Debug.Log($"Raw depth value: {depth}");
        Debug.Log($"Normalized height: {normalizedHeight:F2}, Active Layer: {activeLayer} ({terrainLayers[activeLayer].name}), Blend: {blendFactor:F2}");
    }
}

    private void OnDisable()
    {
        if (rsDevice != null)
        {
            rsDevice.OnNewSample -= QueueDepthFrameData;
        }
    }
}