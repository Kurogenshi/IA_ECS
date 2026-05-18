using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;

namespace Crowd
{
    public enum AnimClipId : byte
    {
        Idle = 0,
        Walk = 1,
    }

    /// <summary>
    /// CPU-side animation state. The companion <see cref="AnimClipProperty"/> /
    /// <see cref="AnimTimeProperty"/> are what actually reaches the GPU.
    /// </summary>
    public struct AgentAnimationState : IComponentData
    {
        public AnimClipId CurrentClip;
        public float ClipTime;
        public float PhaseOffset; // randomized at spawn to desync agent cycles
    }

    /// <summary>
    /// Per-VAT static metadata copied into every agent so we can convert
    /// (clipId, time) -> a global frame index in the position texture.
    /// 4 supported clips is plenty for our crowd needs.
    /// </summary>
    public struct VATClipTable : IComponentData
    {
        public float4 ClipStartFrame;
        public float4 ClipFrameCount;
        public float4 ClipFps;
        public float TotalFrames;
        public float VertexCount;
    }

    // ---- Material properties (uploaded per-instance to the shader by Entities Graphics) ----

    [MaterialProperty("_AnimClip")]
    public struct AnimClipProperty : IComponentData
    {
        public float Value;
    }

    [MaterialProperty("_AnimTime")]
    public struct AnimTimeProperty : IComponentData
    {
        public float Value;
    }

    /// <summary>1.0 = render this agent normally, 0.0 = early-out in vertex shader.</summary>
    [MaterialProperty("_AgentVisible")]
    public struct AgentVisibleProperty : IComponentData
    {
        public float Value;
    }

    /// <summary>1.0 = cast shadows, 0.0 = skip shadow pass for this agent.</summary>
    [MaterialProperty("_AgentShadowVisible")]
    public struct AgentShadowVisibleProperty : IComponentData
    {
        public float Value;
    }
}
