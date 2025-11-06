using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

[VolumeComponentMenu("Accumulation Motion Blur")]
[SupportedOnRenderPipeline(typeof(UniversalRenderPipelineAsset))]
public sealed class AccumulationBlurVolumeComponent : VolumeComponent
{
    public AccumulationBlurVolumeComponent()
    {
        displayName = "Accumulation Motion Blur";
    }

    public FloatParameter decayRate = new ClampedFloatParameter(1f,0f,1f);
    public Vector2Parameter linearTransformation = new Vector2Parameter(Vector2.zero);
    public FloatParameter radialTransformation = new FloatParameter(0f);
    
    public bool IsActive()
    {
        //return decayRate != 1f; //This will work to completely disable the effect when not in use, but it also makes smooth transitions in and out of the effect impossible.
        //Considering the negligible performance impact of the AMB, there's little reason not to just leave this alone unless you need to implement a failsafe for GPUs that... Can't handle Blitting for some reason...?

        return true;
    }
}