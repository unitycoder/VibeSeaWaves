using UnityEngine;

/// <summary>
/// Builds a flat XZ grid mesh for the ocean surface with expanded bounds
/// so it isn't frustum-culled when the vertex shader displaces it.
/// </summary>
[ExecuteAlways]
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class OceanMeshBuilder : MonoBehaviour
{
    public float size = 1000f;
    [Range(16, 1024)] public int resolution = 400; // quads per side
    public float maxWaveHeight = 20f;

    void OnEnable() => Build();
    void OnValidate() { if (isActiveAndEnabled) Build(); }

    void Build()
    {
        int vertsPerSide = resolution + 1;
        var vertices = new Vector3[vertsPerSide * vertsPerSide];
        var triangles = new int[resolution * resolution * 6];

        float half = size * 0.5f;
        float step = size / resolution;

        for (int z = 0, i = 0; z < vertsPerSide; z++)
            for (int x = 0; x < vertsPerSide; x++, i++)
                vertices[i] = new Vector3(x * step - half, 0f, z * step - half);

        for (int z = 0, t = 0; z < resolution; z++)
        {
            for (int x = 0; x < resolution; x++, t += 6)
            {
                int i = z * vertsPerSide + x;
                triangles[t]     = i;
                triangles[t + 1] = i + vertsPerSide;
                triangles[t + 2] = i + 1;
                triangles[t + 3] = i + 1;
                triangles[t + 4] = i + vertsPerSide;
                triangles[t + 5] = i + vertsPerSide + 1;
            }
        }

        var mesh = new Mesh
        {
            name = "OceanGrid",
            indexFormat = UnityEngine.Rendering.IndexFormat.UInt32,
            vertices = vertices,
            triangles = triangles,
            bounds = new Bounds(Vector3.zero,
                new Vector3(size + 20f, maxWaveHeight * 2f, size + 20f))
        };

        GetComponent<MeshFilter>().sharedMesh = mesh;
    }
}
