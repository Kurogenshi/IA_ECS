using System;
using System.Collections.Generic;
using System.IO;
using Crowd;
using Crowd.Animation;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace Crowd.EditorTools
{
    /// <summary>
    /// Editor window that bakes a SkinnedMeshRenderer + AnimationClips into a VATAsset
    /// (Mesh + Position Texture + Material + ScriptableObject metadata).
    ///
    /// Open via: Crowd > VAT Baker.
    /// </summary>
    public class VATBakerWindow : EditorWindow
    {
        [Serializable]
        public class ClipEntry
        {
            public AnimationClip Clip;
            public AnimClipId Slot = AnimClipId.Idle;
            public bool Loop = true;
        }

        [Serializable]
        public class LODEntry
        {
            [Tooltip("3D-grid resolution for cluster decimation. 0 = full detail (LOD0). " +
                     "Lower numbers = fewer vertices. Typical: 24 / 14 / 8.")]
            [Range(0, 64)] public int CellsPerAxis = 0;
        }

        [SerializeField] private GameObject _sourcePrefab;
        [SerializeField] private DefaultAsset _outputFolder;
        [SerializeField] private int _fps = 30;
        [SerializeField] private List<ClipEntry> _clips = new List<ClipEntry>();
        [SerializeField] private List<LODEntry> _lods  = new List<LODEntry>
        {
            new LODEntry { CellsPerAxis = 0  }, // LOD0 always full detail
            new LODEntry { CellsPerAxis = 24 }, // LOD1
            new LODEntry { CellsPerAxis = 14 }, // LOD2
        };

        private SerializedObject _so;
        private ReorderableList _clipList;
        private ReorderableList _lodList;
        private Vector2 _scroll;

        [MenuItem("Crowd/VAT Baker")]
        public static void Open()
        {
            var w = GetWindow<VATBakerWindow>("VAT Baker");
            w.minSize = new Vector2(420, 360);
        }

        private void OnEnable()
        {
            _so = new SerializedObject(this);
            _clipList = new ReorderableList(_so, _so.FindProperty(nameof(_clips)), true, true, true, true);
            _clipList.drawHeaderCallback = r => EditorGUI.LabelField(r, "Animation Clips");
            _clipList.elementHeight = EditorGUIUtility.singleLineHeight * 3 + 8;
            _clipList.drawElementCallback = (rect, index, active, focused) =>
            {
                var el = _clipList.serializedProperty.GetArrayElementAtIndex(index);
                var lineH = EditorGUIUtility.singleLineHeight;
                var r1 = new Rect(rect.x, rect.y + 2, rect.width, lineH);
                var r2 = new Rect(rect.x, rect.y + 4 + lineH, rect.width, lineH);
                var r3 = new Rect(rect.x, rect.y + 6 + lineH * 2, rect.width, lineH);
                EditorGUI.PropertyField(r1, el.FindPropertyRelative(nameof(ClipEntry.Clip)));
                EditorGUI.PropertyField(r2, el.FindPropertyRelative(nameof(ClipEntry.Slot)));
                EditorGUI.PropertyField(r3, el.FindPropertyRelative(nameof(ClipEntry.Loop)));
            };

            _lodList = new ReorderableList(_so, _so.FindProperty(nameof(_lods)), true, true, true, true);
            _lodList.drawHeaderCallback = r => EditorGUI.LabelField(r, "LOD Levels (0 = full detail)");
            _lodList.elementHeight = EditorGUIUtility.singleLineHeight + 6;
            _lodList.drawElementCallback = (rect, index, active, focused) =>
            {
                var el = _lodList.serializedProperty.GetArrayElementAtIndex(index);
                var lineH = EditorGUIUtility.singleLineHeight;
                var r = new Rect(rect.x, rect.y + 2, rect.width, lineH);
                EditorGUI.PropertyField(r, el.FindPropertyRelative(nameof(LODEntry.CellsPerAxis)),
                    new GUIContent($"LOD{index} cells/axis"));
            };
        }

        private void OnGUI()
        {
            _so.Update();
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.LabelField("Source", EditorStyles.boldLabel);
            _sourcePrefab = (GameObject)EditorGUILayout.ObjectField(
                new GUIContent("Source Prefab", "Prefab containing a SkinnedMeshRenderer + Animator."),
                _sourcePrefab, typeof(GameObject), false);
            _outputFolder = (DefaultAsset)EditorGUILayout.ObjectField(
                new GUIContent("Output Folder", "Project folder where the baked assets will be written."),
                _outputFolder, typeof(DefaultAsset), false);
            _fps = EditorGUILayout.IntSlider(new GUIContent("Sample FPS"), _fps, 10, 60);

            EditorGUILayout.Space(8);
            _clipList.DoLayoutList();

            EditorGUILayout.Space(8);
            _lodList.DoLayoutList();

            EditorGUILayout.Space(8);
            using (new EditorGUI.DisabledScope(!ReadyToBake(out var why)))
            {
                if (GUILayout.Button("Bake VAT", GUILayout.Height(32)))
                {
                    try { Bake(); }
                    catch (Exception ex)
                    {
                        Debug.LogException(ex);
                        EditorUtility.DisplayDialog("VAT Bake Failed", ex.Message, "OK");
                    }
                }
            }

            if (!ReadyToBake(out var msg))
            {
                EditorGUILayout.HelpBox(msg, MessageType.Info);
            }

            EditorGUILayout.EndScrollView();
            _so.ApplyModifiedProperties();
        }

        private static string GetHierarchyPath(Transform t)
        {
            var sb = new System.Text.StringBuilder(t.name);
            while (t.parent != null)
            {
                t = t.parent;
                sb.Insert(0, t.name + "/");
            }
            return sb.ToString();
        }

        private bool ReadyToBake(out string reason)
        {
            if (_sourcePrefab == null)        { reason = "Assign a Source Prefab."; return false; }
            if (_outputFolder == null)        { reason = "Assign an Output Folder.";  return false; }
            if (_clips == null || _clips.Count == 0) { reason = "Add at least one animation clip."; return false; }
            foreach (var c in _clips)
            {
                if (c == null || c.Clip == null) { reason = "One of the clip entries has no AnimationClip assigned."; return false; }
            }
            reason = string.Empty;
            return true;
        }

        private void Bake()
        {
            string folderPath = AssetDatabase.GetAssetPath(_outputFolder);
            if (string.IsNullOrEmpty(folderPath) || !AssetDatabase.IsValidFolder(folderPath))
                throw new Exception("Output Folder must be a valid project folder.");

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(_sourcePrefab);
            if (instance == null) instance = UnityEngine.Object.Instantiate(_sourcePrefab);
            instance.hideFlags = HideFlags.HideAndDontSave;
            instance.transform.position = Vector3.zero;
            instance.transform.rotation = Quaternion.identity;
            instance.transform.localScale = Vector3.one;

            // includeInactive: true so we don't miss LOD0 / hidden body meshes
            // (Mixamo X-Bot sometimes ships with inactive children).
            var smrs = instance.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (smrs == null || smrs.Length == 0)
            {
                UnityEngine.Object.DestroyImmediate(instance);
                throw new Exception("Source Prefab has no SkinnedMeshRenderer in its hierarchy.");
            }

            // List every renderer we discovered so the user can verify the bake covers them all.
            var summary = new System.Text.StringBuilder();
            summary.AppendLine($"[VATBaker] Found {smrs.Length} SkinnedMeshRenderer(s) in '{_sourcePrefab.name}':");
            for (int i = 0; i < smrs.Length; i++)
            {
                var sm = smrs[i].sharedMesh;
                summary.AppendLine(
                    $"  [{i}] {smrs[i].name}  " +
                    $"verts={(sm != null ? sm.vertexCount : 0)}  " +
                    $"submeshes={(sm != null ? sm.subMeshCount : 0)}  " +
                    $"active={smrs[i].gameObject.activeInHierarchy}  " +
                    $"path={GetHierarchyPath(smrs[i].transform)}");
            }
            // Also flag any plain MeshRenderer found (those won't be baked — we want to know).
            var meshRenderers = instance.GetComponentsInChildren<MeshRenderer>(true);
            if (meshRenderers.Length > 0)
            {
                summary.AppendLine($"[VATBaker] WARNING: also found {meshRenderers.Length} non-skinned MeshRenderer(s) (NOT baked):");
                foreach (var mr in meshRenderers)
                    summary.AppendLine($"  - {mr.name}  path={GetHierarchyPath(mr.transform)}");
            }
            Debug.Log(summary.ToString());

            // Sort clips by Slot
            var sorted = new List<ClipEntry>(_clips);
            sorted.Sort((a, b) => ((int)a.Slot).CompareTo((int)b.Slot));

            // Per-SMR offsets / counts
            int totalVertexCount = 0;
            var smrOffsets = new int[smrs.Length];
            var smrCounts  = new int[smrs.Length];
            for (int i = 0; i < smrs.Length; i++)
            {
                smrOffsets[i] = totalVertexCount;
                smrCounts[i]  = smrs[i].sharedMesh != null ? smrs[i].sharedMesh.vertexCount : 0;
                totalVertexCount += smrCounts[i];
            }

            // Clip metadata
            int totalFrames = 0;
            var clipInfos = new List<VATAsset.ClipInfo>();
            foreach (var c in sorted)
            {
                int frames = Mathf.Max(1, Mathf.RoundToInt(c.Clip.length * _fps));
                clipInfos.Add(new VATAsset.ClipInfo
                {
                    Name = c.Slot.ToString(),
                    StartFrame = totalFrames,
                    FrameCount = frames,
                    Fps = _fps,
                    Loop = c.Loop,
                });
                totalFrames += frames;
            }

            if (totalVertexCount == 0 || totalFrames == 0)
            {
                UnityEngine.Object.DestroyImmediate(instance);
                throw new Exception("No vertices or no frames to bake.");
            }

            const int MAX_TEX_WIDTH = 4096;

            // Build the list of LODs to bake. LOD0 is always full detail; subsequent LODs
            // use cluster decimation with CellsPerAxis as decimation strength.
            var lods = new List<LODBake>();
            if (_lods != null)
            {
                for (int i = 0; i < _lods.Count; i++)
                    lods.Add(new LODBake { Level = i, CellsPerAxis = _lods[i].CellsPerAxis });
            }
            if (lods.Count == 0)
                lods.Add(new LODBake { Level = 0, CellsPerAxis = 0 }); // safety

            Vector3 boundsMin = new Vector3( float.PositiveInfinity,  float.PositiveInfinity,  float.PositiveInfinity);
            Vector3 boundsMax = new Vector3( float.NegativeInfinity,  float.NegativeInfinity,  float.NegativeInfinity);

            // Per-SMR reference data, captured from BakeMesh at frame 0 of clip 0 so the
            // static mesh's vertex ordering matches the VAT exactly.
            var smrRefPositions = new Vector3[smrs.Length][];
            var smrRefNormals   = new Vector3[smrs.Length][];
            var smrRefTangents  = new Vector4[smrs.Length][];
            var smrRefUV0       = new Vector2[smrs.Length][];
            var smrRefIndices   = new int[smrs.Length][];
            var smrRefTopo      = new MeshTopology[smrs.Length];

            // Snapshot the source materials NOW — once we DestroyImmediate the instance
            // below, the SkinnedMeshRenderer components are gone and any later access
            // to smrs[i].sharedMaterial throws MissingReferenceException.
            var srcMaterials = new Material[smrs.Length];
            for (int i = 0; i < smrs.Length; i++)
                srcMaterials[i] = smrs[i].sharedMaterial;

            var bakedMesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };

            AnimationMode.StartAnimationMode();
            try
            {
                // 1) Reference snapshot of every SMR at frame 0 of first clip.
                AnimationMode.SampleAnimationClip(instance, sorted[0].Clip, 0f);
                for (int i = 0; i < smrs.Length; i++)
                {
                    if (smrs[i].sharedMesh == null) continue;
                    smrs[i].BakeMesh(bakedMesh, true);
                    smrRefPositions[i] = bakedMesh.vertices;
                    smrRefNormals[i]   = bakedMesh.normals;
                    smrRefTangents[i]  = bakedMesh.tangents;
                    smrRefUV0[i]       = bakedMesh.uv;
                    if (bakedMesh.subMeshCount > 0)
                    {
                        smrRefIndices[i] = bakedMesh.GetIndices(0);
                        smrRefTopo[i]    = bakedMesh.GetTopology(0);
                    }

                    if (smrRefPositions[i] == null || smrRefPositions[i].Length != smrCounts[i])
                        throw new Exception($"BakeMesh returned wrong vertex count for SMR '{smrs[i].name}'.");
                }

                Debug.Log($"[VATBaker] {smrs.Length} SkinnedMeshRenderer(s) captured, total {totalVertexCount} vertices.");

                // --- Build per-LOD decimation + VAT textures ---
                foreach (var L in lods)
                {
                    L.SmrResults = new ClusterDecimator.Result[smrs.Length];
                    L.SmrOffsets = new int[smrs.Length];
                    L.SmrCounts  = new int[smrs.Length];

                    int off = 0;
                    for (int i = 0; i < smrs.Length; i++)
                    {
                        L.SmrOffsets[i] = off;

                        if (L.CellsPerAxis < 2 || smrCounts[i] == 0)
                        {
                            // LOD0 / passthrough: keep all source vertices
                            L.SmrResults[i] = null;
                            L.SmrCounts[i]  = smrCounts[i];
                        }
                        else
                        {
                            var r = ClusterDecimator.Decimate(
                                smrRefPositions[i],
                                smrRefNormals[i],
                                smrRefTangents[i],
                                smrRefUV0[i],
                                null,
                                smrRefIndices[i],
                                L.CellsPerAxis);

                            if (r == null || r.Positions == null || r.Positions.Length == 0)
                            {
                                L.SmrResults[i] = null;
                                L.SmrCounts[i]  = smrCounts[i];
                            }
                            else
                            {
                                L.SmrResults[i] = r;
                                L.SmrCounts[i]  = r.Positions.Length;
                            }
                        }
                        off += L.SmrCounts[i];
                    }
                    L.TotalVertexCount = off;

                    if (L.TotalVertexCount == 0) continue;

                    L.VATWidth     = Mathf.Min(L.TotalVertexCount, MAX_TEX_WIDTH);
                    L.RowsPerFrame = Mathf.CeilToInt((float)L.TotalVertexCount / L.VATWidth);
                    L.VATHeight    = totalFrames * L.RowsPerFrame;

                    L.Texture = new Texture2D(L.VATWidth, L.VATHeight, TextureFormat.RGBAHalf, false, true)
                    {
                        name = _sourcePrefab.name + "_VAT_Position" + (L.Level == 0 ? "" : "_LOD" + L.Level),
                        filterMode = FilterMode.Point,
                        wrapMode   = TextureWrapMode.Clamp,
                        anisoLevel = 0,
                    };
                    L.Pixels = new Color[L.VATWidth * L.VATHeight];
                }

                Debug.Log("[VATBaker] LOD plan:\n" +
                    string.Join("\n", lods.ConvertAll(L =>
                        $"  LOD{L.Level}: verts={L.TotalVertexCount} cells/axis={L.CellsPerAxis} tex={L.VATWidth}x{L.VATHeight}")));

                // 2) For each frame of each clip, bake every SMR. Each LOD's VAT is filled:
                //    - LOD0 (no decimation): write source vertex positions directly.
                //    - Higher LODs: write the AVERAGE position of each cluster's source verts.
                int frameIndex = 0;
                for (int ci = 0; ci < sorted.Count; ci++)
                {
                    var entry = sorted[ci];
                    int frames = clipInfos[ci].FrameCount;
                    for (int f = 0; f < frames; f++)
                    {
                        float t = entry.Clip.length * (frames > 1 ? (float)f / frames : 0f);
                        AnimationMode.SampleAnimationClip(instance, entry.Clip, t);

                        for (int i = 0; i < smrs.Length; i++)
                        {
                            if (smrCounts[i] == 0) continue;
                            smrs[i].BakeMesh(bakedMesh, true);
                            var verts = bakedMesh.vertices;

                            // Update bounds from raw source positions (most precise extent).
                            for (int v = 0; v < smrCounts[i]; v++)
                            {
                                var p = verts[v];
                                if (p.x < boundsMin.x) boundsMin.x = p.x;
                                if (p.y < boundsMin.y) boundsMin.y = p.y;
                                if (p.z < boundsMin.z) boundsMin.z = p.z;
                                if (p.x > boundsMax.x) boundsMax.x = p.x;
                                if (p.y > boundsMax.y) boundsMax.y = p.y;
                                if (p.z > boundsMax.z) boundsMax.z = p.z;
                            }

                            // Write to each LOD's VAT.
                            foreach (var L in lods)
                            {
                                if (L.TotalVertexCount == 0 || L.Pixels == null) continue;

                                int frameRowBase = frameIndex * L.RowsPerFrame;
                                int writeOff     = L.SmrOffsets[i];

                                if (L.SmrResults[i] == null)
                                {
                                    // LOD0 / passthrough: 1 pixel per source vertex
                                    for (int v = 0; v < smrCounts[i]; v++)
                                    {
                                        var p = verts[v];
                                        int globalV  = writeOff + v;
                                        int col      = globalV % L.VATWidth;
                                        int localRow = globalV / L.VATWidth;
                                        int pixelIdx = (frameRowBase + localRow) * L.VATWidth + col;
                                        L.Pixels[pixelIdx] = new Color(p.x, p.y, p.z, 1f);
                                    }
                                }
                                else
                                {
                                    // Decimated LOD: average source positions per cluster.
                                    var res = L.SmrResults[i];
                                    int clusterCount = res.Positions.Length;
                                    for (int c = 0; c < clusterCount; c++)
                                    {
                                        var members = res.ClusterToSources[c];
                                        int cnt = members.Count;
                                        Vector3 sum = Vector3.zero;
                                        for (int k = 0; k < cnt; k++)
                                            sum += verts[members[k]];
                                        Vector3 avg = sum / cnt;

                                        int clusterGlobal = writeOff + c;
                                        int col           = clusterGlobal % L.VATWidth;
                                        int localRow      = clusterGlobal / L.VATWidth;
                                        int pixelIdx      = (frameRowBase + localRow) * L.VATWidth + col;
                                        L.Pixels[pixelIdx] = new Color(avg.x, avg.y, avg.z, 1f);
                                    }
                                }
                            }
                        }
                        frameIndex++;

                        if ((frameIndex & 31) == 0)
                            EditorUtility.DisplayProgressBar("VAT Baker", $"Sampling {entry.Clip.name}", (float)frameIndex / totalFrames);
                    }
                }

                foreach (var L in lods)
                {
                    if (L.Texture == null) continue;
                    L.Texture.SetPixels(L.Pixels);
                    L.Texture.Apply(false, false);
                }
            }
            finally
            {
                AnimationMode.StopAnimationMode();
                EditorUtility.ClearProgressBar();
                UnityEngine.Object.DestroyImmediate(bakedMesh);
                UnityEngine.Object.DestroyImmediate(instance);
            }

            // 3) Per-LOD: build combined mesh (concatenated SMRs, per-vertex tint from source
            //    material) and save Mesh + Texture + Material + VATAsset.
            var shader = Shader.Find("Crowd/AgentVAT");
            if (shader == null)
                throw new Exception("Shader 'Crowd/AgentVAT' not found. Make sure AgentVAT.shader is in the project.");

            string baseName = _sourcePrefab.name + "_VAT";

            // Clean up obsolete material files from earlier baker iterations.
            for (int oldIdx = 0; oldIdx < 8; oldIdx++)
            {
                string stalePath = Path.Combine(folderPath, baseName + "_Material_" + oldIdx + ".mat").Replace("\\", "/");
                if (AssetDatabase.LoadAssetAtPath<Material>(stalePath) != null)
                {
                    AssetDatabase.DeleteAsset(stalePath);
                    Debug.Log($"[VATBaker] Removed obsolete material asset: {stalePath}");
                }
            }

            // Per-clip metadata vectors (shared across LODs).
            var starts = Vector4.zero;
            var counts = Vector4.zero;
            var fpss   = new Vector4(_fps, _fps, _fps, _fps);
            for (int i = 0; i < clipInfos.Count && i < 4; i++)
            {
                starts[i] = clipInfos[i].StartFrame;
                counts[i] = clipInfos[i].FrameCount;
                fpss[i]   = clipInfos[i].Fps;
            }

            // Representative source material to borrow texture / PBR params.
            Material srcRef = null;
            for (int i = 0; i < srcMaterials.Length; i++)
                if (srcMaterials[i] != null) { srcRef = srcMaterials[i]; break; }

            // Tight bounds for every LOD mesh (LOD0 source positions are the most precise).
            var bbSize = boundsMax - boundsMin;
            if (bbSize.x < 0.1f) bbSize.x = 0.1f;
            if (bbSize.y < 0.1f) bbSize.y = 0.1f;
            if (bbSize.z < 0.1f) bbSize.z = 0.1f;
            var bbCenter = (boundsMax + boundsMin) * 0.5f;
            var sharedBounds = new Bounds(bbCenter, bbSize * 1.05f);

            VATAsset primaryVAT = null;
            var bakeSummary = new System.Text.StringBuilder();
            bakeSummary.AppendLine($"Wrote assets to:\n{folderPath}\n");

            foreach (var L in lods)
            {
                if (L.TotalVertexCount == 0 || L.Texture == null) continue;

                string suffix = L.Level == 0 ? "" : "_LOD" + L.Level;

                // Build combined positions / normals / tangents / UV0 / colors / indices.
                var posArr = new Vector3[L.TotalVertexCount];
                var nrmArr = new Vector3[L.TotalVertexCount];
                var tanArr = new Vector4[L.TotalVertexCount];
                var uv0Arr = new Vector2[L.TotalVertexCount];
                var colArr = new Color[L.TotalVertexCount];
                bool hasN = false, hasT = false, hasUV = false;
                int totalIdx = 0;

                for (int i = 0; i < smrs.Length; i++)
                {
                    int off = L.SmrOffsets[i];
                    int n   = L.SmrCounts[i];
                    if (n == 0) continue;

                    Vector3[] sP; Vector3[] sN; Vector4[] sT; Vector2[] sU; int[] sIdx;
                    if (L.SmrResults[i] == null)
                    {
                        sP   = smrRefPositions[i];
                        sN   = smrRefNormals[i];
                        sT   = smrRefTangents[i];
                        sU   = smrRefUV0[i];
                        sIdx = smrRefIndices[i];
                    }
                    else
                    {
                        var r = L.SmrResults[i];
                        sP = r.Positions; sN = r.Normals; sT = r.Tangents; sU = r.UVs; sIdx = r.Indices;
                    }

                    System.Array.Copy(sP, 0, posArr, off, n);
                    if (sN != null && sN.Length == n) { System.Array.Copy(sN, 0, nrmArr, off, n); hasN = true; }
                    if (sT != null && sT.Length == n) { System.Array.Copy(sT, 0, tanArr, off, n); hasT = true; }
                    if (sU != null && sU.Length == n) { System.Array.Copy(sU, 0, uv0Arr, off, n); hasUV = true; }

                    // Per-vertex tint from source SMR material.
                    Color tint = Color.white;
                    var srcMat = srcMaterials[i];
                    if (srcMat != null)
                    {
                        if (srcMat.HasProperty("_BaseColor")) tint = srcMat.GetColor("_BaseColor");
                        else if (srcMat.HasProperty("_Color")) tint = srcMat.GetColor("_Color");
                    }
                    for (int v = 0; v < n; v++) colArr[off + v] = tint;

                    if (sIdx != null) totalIdx += sIdx.Length;
                }

                var idxCombined = new int[totalIdx];
                int idxWrite = 0;
                for (int i = 0; i < smrs.Length; i++)
                {
                    int[] sIdx = L.SmrResults[i] == null ? smrRefIndices[i] : L.SmrResults[i].Indices;
                    if (sIdx == null) continue;
                    int off = L.SmrOffsets[i];
                    for (int k = 0; k < sIdx.Length; k++)
                        idxCombined[idxWrite + k] = sIdx[k] + off;
                    idxWrite += sIdx.Length;
                }

                var lodMesh = new Mesh
                {
                    name = baseName + "_Mesh" + suffix,
                    indexFormat = L.TotalVertexCount > 65000
                        ? UnityEngine.Rendering.IndexFormat.UInt32
                        : UnityEngine.Rendering.IndexFormat.UInt16,
                };
                lodMesh.SetVertices(posArr);
                if (hasN)  lodMesh.SetNormals(nrmArr);
                if (hasT)  lodMesh.SetTangents(tanArr);
                if (hasUV) lodMesh.SetUVs(0, uv0Arr);
                lodMesh.SetColors(colArr);
                lodMesh.subMeshCount = 1;
                lodMesh.SetIndices(idxCombined, MeshTopology.Triangles, 0);
                lodMesh.bounds = sharedBounds;
                lodMesh.UploadMeshData(false);

                string meshPath = Path.Combine(folderPath, baseName + "_Mesh" + suffix + ".asset").Replace("\\", "/");
                string texPath  = Path.Combine(folderPath, baseName + "_Position" + suffix + ".asset").Replace("\\", "/");
                string matPath  = Path.Combine(folderPath, baseName + "_Material" + suffix + ".mat").Replace("\\", "/");
                string vatPath  = Path.Combine(folderPath, baseName + suffix + ".asset").Replace("\\", "/");

                AssetDatabase.CreateAsset(lodMesh,  meshPath);
                AssetDatabase.CreateAsset(L.Texture, texPath);

                var lodMat = new Material(shader)
                {
                    name = baseName + "_Material" + suffix,
                    enableInstancing = true,
                };
                if (srcRef != null)
                {
                    Texture srcTex = null;
                    if (srcRef.HasProperty("_BaseMap"))  srcTex = srcRef.GetTexture("_BaseMap");
                    if (srcTex == null && srcRef.HasProperty("_MainTex"))
                        srcTex = srcRef.GetTexture("_MainTex");
                    if (srcTex != null) lodMat.SetTexture("_BaseMap", srcTex);
                    if (srcRef.HasProperty("_Smoothness")) lodMat.SetFloat("_Smoothness", srcRef.GetFloat("_Smoothness"));
                    if (srcRef.HasProperty("_Metallic"))   lodMat.SetFloat("_Metallic",   srcRef.GetFloat("_Metallic"));
                }

                lodMat.SetTexture("_PositionMap", L.Texture);
                lodMat.SetFloat("_VertexCount",  L.TotalVertexCount);
                lodMat.SetFloat("_TotalFrames",  totalFrames);
                lodMat.SetFloat("_VATWidth",     L.VATWidth);
                lodMat.SetFloat("_VATHeight",    L.VATHeight);
                lodMat.SetFloat("_RowsPerFrame", L.RowsPerFrame);
                lodMat.SetVector("_ClipStartFrame", starts);
                lodMat.SetVector("_ClipFrameCount", counts);
                lodMat.SetVector("_ClipFps",        fpss);

                AssetDatabase.CreateAsset(lodMat, matPath);

                var lodVat = ScriptableObject.CreateInstance<VATAsset>();
                lodVat.Mesh         = lodMesh;
                lodVat.PositionMap  = L.Texture;
                lodVat.VertexCount  = L.TotalVertexCount;
                lodVat.TotalFrames  = totalFrames;
                lodVat.VATWidth     = L.VATWidth;
                lodVat.VATHeight    = L.VATHeight;
                lodVat.RowsPerFrame = L.RowsPerFrame;
                lodVat.Clips        = clipInfos.ToArray();
                AssetDatabase.CreateAsset(lodVat, vatPath);

                if (L.Level == 0) primaryVAT = lodVat;

                bakeSummary.AppendLine($"LOD{L.Level}: verts={L.TotalVertexCount} cells/axis={L.CellsPerAxis} tex={L.VATWidth}x{L.VATHeight}");
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            bakeSummary.AppendLine($"\nFrames: {totalFrames}, Clips: {clipInfos.Count}");
            bakeSummary.AppendLine("\nRuntime setup:");
            bakeSummary.AppendLine("Create an agent prefab with a LODGroup component +");
            bakeSummary.AppendLine($"{lods.Count} child GameObjects (one per LOD), each with MeshFilter +");
            bakeSummary.AppendLine("MeshRenderer pointing to the corresponding LOD's Mesh + Material.");
            bakeSummary.AppendLine("AgentAuthoring goes on the root and references the LOD0 VATAsset.");

            EditorUtility.DisplayDialog("VAT Bake Complete", bakeSummary.ToString(), "OK");

            if (primaryVAT != null)
            {
                EditorGUIUtility.PingObject(primaryVAT);
                Selection.activeObject = primaryVAT;
            }
        }

        /// <summary>Per-LOD bake state: decimation result + VAT texture + pixel buffer.</summary>
        private class LODBake
        {
            public int Level;
            public int CellsPerAxis;
            public int TotalVertexCount;
            public int VATWidth, VATHeight, RowsPerFrame;
            public Texture2D Texture;
            public Color[] Pixels;
            public ClusterDecimator.Result[] SmrResults;
            public int[] SmrOffsets;
            public int[] SmrCounts;
        }
    }
}
