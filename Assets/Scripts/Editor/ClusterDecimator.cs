using System.Collections.Generic;
using UnityEngine;

namespace Crowd.EditorTools
{
    /// <summary>
    /// Cluster-based mesh decimation.
    /// Vertices are bucketed into a 3D grid; each cell becomes a single "cluster vertex"
    /// whose attributes are the average of the source vertices that fell into it.
    /// Triangles are remapped to cluster indices; degenerate triangles (two or three
    /// vertices in the same cluster) are dropped.
    ///
    /// Quality is rough but stable, runs fast, and is well-suited for crowd LOD where
    /// agents are seen from a distance.
    /// </summary>
    public static class ClusterDecimator
    {
        public class Result
        {
            public Vector3[] Positions;
            public Vector3[] Normals;
            public Vector4[] Tangents;
            public Vector2[] UVs;
            public Color[] Colors;
            public int[] Indices;

            /// <summary>For each source vertex (index in the input arrays), the index
            /// of the cluster vertex it belongs to. Used by callers (e.g. the VAT baker)
            /// to know which cluster a source vertex contributes to.</summary>
            public int[] SourceToCluster;

            /// <summary>For each cluster vertex, the list of source vertex indices that
            /// merged into it. Same information as SourceToCluster, but pre-grouped.</summary>
            public List<int>[] ClusterToSources;
        }

        /// <param name="cellsPerAxis">Grid resolution per axis. Higher = more detail. Typical: 24/16/8.</param>
        public static Result Decimate(
            Vector3[] positions,
            Vector3[] normals,
            Vector4[] tangents,
            Vector2[] uvs,
            Color[] colors,
            int[] indices,
            int cellsPerAxis)
        {
            int n = positions.Length;
            if (n == 0 || cellsPerAxis < 2) return null;

            // 1) Bounding box.
            Vector3 boundsMin = positions[0];
            Vector3 boundsMax = positions[0];
            for (int i = 1; i < n; i++)
            {
                var p = positions[i];
                if (p.x < boundsMin.x) boundsMin.x = p.x;
                if (p.y < boundsMin.y) boundsMin.y = p.y;
                if (p.z < boundsMin.z) boundsMin.z = p.z;
                if (p.x > boundsMax.x) boundsMax.x = p.x;
                if (p.y > boundsMax.y) boundsMax.y = p.y;
                if (p.z > boundsMax.z) boundsMax.z = p.z;
            }
            Vector3 size = boundsMax - boundsMin;
            if (size.x < 1e-4f) size.x = 1e-4f;
            if (size.y < 1e-4f) size.y = 1e-4f;
            if (size.z < 1e-4f) size.z = 1e-4f;
            Vector3 invSize = new Vector3(1f / size.x, 1f / size.y, 1f / size.z);

            // 2) Bucket every vertex.
            int[] vertexCellId = new int[n];
            var cellToCluster = new Dictionary<int, int>(n / 2);
            var clusterToSources = new List<List<int>>();

            for (int i = 0; i < n; i++)
            {
                Vector3 p = positions[i];
                int cx = Mathf.Clamp((int)((p.x - boundsMin.x) * invSize.x * cellsPerAxis), 0, cellsPerAxis - 1);
                int cy = Mathf.Clamp((int)((p.y - boundsMin.y) * invSize.y * cellsPerAxis), 0, cellsPerAxis - 1);
                int cz = Mathf.Clamp((int)((p.z - boundsMin.z) * invSize.z * cellsPerAxis), 0, cellsPerAxis - 1);
                int cellId = (cz * cellsPerAxis + cy) * cellsPerAxis + cx;

                if (!cellToCluster.TryGetValue(cellId, out int clusterIdx))
                {
                    clusterIdx = clusterToSources.Count;
                    cellToCluster[cellId] = clusterIdx;
                    clusterToSources.Add(new List<int>());
                }
                vertexCellId[i] = clusterIdx;
                clusterToSources[clusterIdx].Add(i);
            }

            int clusterCount = clusterToSources.Count;

            // 3) Build per-cluster averaged attributes.
            var outPos     = new Vector3[clusterCount];
            var outNrm     = normals  != null ? new Vector3[clusterCount] : null;
            var outTan     = tangents != null && tangents.Length == n ? new Vector4[clusterCount] : null;
            var outUV      = uvs      != null && uvs.Length == n ? new Vector2[clusterCount] : null;
            var outColor   = colors   != null && colors.Length == n ? new Color[clusterCount] : null;

            for (int c = 0; c < clusterCount; c++)
            {
                var members = clusterToSources[c];
                int count = members.Count;
                float invCount = 1f / count;

                Vector3 sumPos = Vector3.zero;
                Vector3 sumNrm = Vector3.zero;
                Vector4 sumTan = Vector4.zero;
                Vector2 sumUV  = Vector2.zero;
                Color   sumCol = new Color(0, 0, 0, 0);

                for (int k = 0; k < count; k++)
                {
                    int srcIdx = members[k];
                    sumPos += positions[srcIdx];
                    if (outNrm   != null) sumNrm += normals[srcIdx];
                    if (outTan   != null) sumTan += tangents[srcIdx];
                    if (outUV    != null) sumUV  += uvs[srcIdx];
                    if (outColor != null) sumCol += colors[srcIdx];
                }

                outPos[c] = sumPos * invCount;
                if (outNrm   != null) outNrm[c]   = (sumNrm.sqrMagnitude > 1e-6f) ? sumNrm.normalized : Vector3.up;
                if (outTan   != null) outTan[c]   = sumTan * invCount;
                if (outUV    != null) outUV[c]    = sumUV * invCount;
                if (outColor != null) outColor[c] = sumCol * invCount;
            }

            // 4) Remap triangle indices, drop degenerates.
            var outIdx = new List<int>(indices.Length);
            for (int t = 0; t < indices.Length; t += 3)
            {
                int a = vertexCellId[indices[t]];
                int b = vertexCellId[indices[t + 1]];
                int c = vertexCellId[indices[t + 2]];
                if (a == b || b == c || a == c) continue;
                outIdx.Add(a); outIdx.Add(b); outIdx.Add(c);
            }

            return new Result
            {
                Positions        = outPos,
                Normals          = outNrm,
                Tangents         = outTan,
                UVs              = outUV,
                Colors           = outColor,
                Indices          = outIdx.ToArray(),
                SourceToCluster  = vertexCellId,
                ClusterToSources = clusterToSources.ToArray(),
            };
        }
    }
}
