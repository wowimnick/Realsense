using UnityEngine;
using System.Collections.Generic;

public class SlopedTerrainGenerator : MonoBehaviour
{
    public Terrain sandboxTerrain;
    public float minHeight = 0f;
    public float maxHeight = 1f;
    public List<TerrainLayerInfo> terrainLayers = new List<TerrainLayerInfo>();

    [System.Serializable]
    public class TerrainLayerInfo
    {
        public string name;
        public float height;
    }

    private void Start()
    {
        if (sandboxTerrain == null)
        {
            Debug.LogError("Terrain is not assigned. Please assign a terrain in the inspector.");
            return;
        }

        if (terrainLayers.Count == 0)
        {
            Debug.LogError("No terrain layers defined. Please add at least one layer in the inspector.");
            return;
        }

        GenerateSlopedTerrain();
        ApplyTerrainLayers();
    }

    private void GenerateSlopedTerrain()
    {
        TerrainData terrainData = sandboxTerrain.terrainData;
        int resolution = terrainData.heightmapResolution;
        float[,] heights = new float[resolution, resolution];

        for (int y = 0; y < resolution; y++)
        {
            for (int x = 0; x < resolution; x++)
            {
                float normalizedX = (float)x / (resolution - 1);
                heights[y, x] = Mathf.Lerp(minHeight, maxHeight, normalizedX);
            }
        }

        terrainData.SetHeights(0, 0, heights);
    }

    private void ApplyTerrainLayers()
    {
        TerrainData terrainData = sandboxTerrain.terrainData;
        int alphaMapResolution = terrainData.alphamapResolution;
        float[,,] alphaMap = new float[alphaMapResolution, alphaMapResolution, terrainLayers.Count];

        // Sort layers by height in descending order
        terrainLayers.Sort((a, b) => b.height.CompareTo(a.height));

        for (int y = 0; y < alphaMapResolution; y++)
        {
            for (int x = 0; x < alphaMapResolution; x++)
            {
                float terrainHeight = terrainData.GetHeight(
                    Mathf.RoundToInt((float)x / alphaMapResolution * terrainData.heightmapResolution),
                    Mathf.RoundToInt((float)y / alphaMapResolution * terrainData.heightmapResolution)
                ) / terrainData.size.y;

                int activeLayer = terrainLayers.Count - 1; // Default to the lowest layer
                for (int i = 0; i < terrainLayers.Count; i++)
                {
                    if (terrainHeight >= terrainLayers[i].height)
                    {
                        activeLayer = i;
                        break;
                    }
                }

                for (int i = 0; i < terrainLayers.Count; i++)
                {
                    alphaMap[y, x, i] = (i == activeLayer) ? 1f : 0f;
                }
            }
        }

        terrainData.SetAlphamaps(0, 0, alphaMap);
    }

    private void OnValidate()
    {
        if (terrainLayers.Count > 0)
        {
            terrainLayers.Sort((a, b) => b.height.CompareTo(a.height));
        }
    }
}