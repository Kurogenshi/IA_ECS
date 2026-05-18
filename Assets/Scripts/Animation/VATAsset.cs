using System;
using UnityEngine;

namespace Crowd.Animation
{
    /// <summary>
    /// Baked vertex-animation data for one mesh + a set of animation clips.
    /// The Mesh is the static reference mesh (with vertex IDs stored in UV2.x).
    /// PositionMap rows = frames, columns = vertices. Each pixel = vertex worldPos at that frame.
    /// </summary>
    [CreateAssetMenu(menuName = "Crowd/VAT Asset", fileName = "VATAsset")]
    public class VATAsset : ScriptableObject
    {
        [Serializable]
        public struct ClipInfo
        {
            public string Name;
            public int StartFrame;
            public int FrameCount;
            public float Fps;
            public bool Loop;

            public float Duration => FrameCount / Mathf.Max(1f, Fps);
        }

        [Header("Baked Mesh")]
        public Mesh Mesh;

        [Header("Baked Textures")]
        [Tooltip("RGBAFloat. X axis = vertex id, Y axis = global frame index.")]
        public Texture2D PositionMap;
        [Tooltip("Optional RGBAHalf. Same layout as PositionMap.")]
        public Texture2D NormalMap;

        [Header("Mesh / Texture Dimensions")]
        public int VertexCount;
        public int TotalFrames;

        [Header("Texture Layout (multi-row per frame for large meshes)")]
        public int VATWidth;
        public int VATHeight;
        public int RowsPerFrame;

        [Header("Clips")]
        public ClipInfo[] Clips = Array.Empty<ClipInfo>();

        public int FindClipIndex(string clipName)
        {
            if (Clips == null) return -1;
            for (int i = 0; i < Clips.Length; i++)
            {
                if (Clips[i].Name == clipName) return i;
            }
            return -1;
        }
    }
}
