
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

public class FrameAccumulationRendererFeature : ScriptableRendererFeature
{
    //The initial two-stage pass (and education regarding how URP handles frame buffers) was done by Carrie (https://github.com/adc-ax) and Alice (https://github.com/bottinogames).
    //This effect has been updated by Feverdream Johnny (https://github.com/FeverDevJohnny) to support buffer transformation, a volume component for blending (as well as radial and linear motion blur), various tweaks and fixes, and some comments as well.

    public RenderPassEvent injectionPoint = RenderPassEvent.AfterRenderingPostProcessing;

    class AccumulationPass : ScriptableRenderPass
    {
        private Material blitMaterial;
        private Material transformerMaterial; 

        private RTHandle accumulationBuffer;
        private RTHandle transformerBuffer;

        public AccumulationPass(Material blitMaterial, Material transformerMaterial)
        {
            this.blitMaterial = blitMaterial;
            this.transformerMaterial = transformerMaterial;
        }
        
        void ReAllocateAccumulation(RenderTextureDescriptor desc)
        {
            desc.msaaSamples = 1;
            desc.depthStencilFormat = GraphicsFormat.None;
            RenderingUtils.ReAllocateHandleIfNeeded(ref accumulationBuffer, desc, name: "_AccumulationBuffer");
        }

        void ReAllocateTransformer(RenderTextureDescriptor desc)
        {
            desc.msaaSamples = 1;
            desc.depthStencilFormat = GraphicsFormat.None;
            RenderingUtils.ReAllocateHandleIfNeeded(ref transformerBuffer, desc, name: "_TransformerBuffer");
        }

        // This class stores the data needed by the pass, passed as parameter to the delegate function that executes the pass
        private class PassData
        {
            internal TextureHandle src;
            internal TextureHandle dst;
            internal Material blitMaterial;
        }
        
        //We store both the accumulation texture and the transformed buffer textures as ContextItems, which are essentially pieces of data you can store between frames inside the frameData.
        public class FrameBufferData : ContextItem 
        {
            public TextureHandle accumulationTextureHandle;
            public TextureHandle transformationTextureHandle;

            public override void Reset()
            {
                accumulationTextureHandle = TextureHandle.nullHandle;
                transformationTextureHandle = TextureHandle.nullHandle;
            }
        }

        //A basic blit pass, writes from one texture to another with a material. If you've used BIRP you'd probably recognize this as the SRP equivalent of Graphics.Blit!
        static void ExecutePass(PassData data, RasterGraphContext context)
        {
            Blitter.BlitTexture(context.cmd, data.src, new Vector4(1, 1, 0, 0), data.blitMaterial, 0);
        }

        //Dispose of buffers as necessary to free up data.
        public void Dispose()
        {
            accumulationBuffer?.Release();
            transformerBuffer?.Release();
        }
        
        //This gets the current screen data, and sets up a buffer to write the current screen pixels to. Depending on your injection point, this will change which elements on-screen are going to be affected by the blur.
        private void InitPassDataAccumulation(RenderGraph renderGraph, ContextContainer frameData, ref PassData passData)
        {

            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            
            RenderTextureDescriptor desc = cameraData.cameraTargetDescriptor;
            desc.msaaSamples = 0;
            desc.depthBufferBits = 0;
                
            ReAllocateAccumulation(desc);
            TextureHandle destination = renderGraph.ImportTexture(accumulationBuffer);
            FrameBufferData customData = frameData.Create<FrameBufferData>();
            customData.accumulationTextureHandle = destination;
            
            passData.src = resourceData.activeColorTexture;
            passData.dst = destination;
            passData.blitMaterial = blitMaterial;
        }

        //The transformer pass is responsible for taking the previously established accumulation blur buffer and transforming it, writing its contents to another buffer that will eventually be blitted back into the accumulation buffer later.
        //This might sound impractical, but due to how blitting works we can't just write a texture back onto itself using a shader, so we have to make a new place to deposit the transformed texture.
        private void InitPassDataTransformer(RenderGraph renderGraph, ContextContainer frameData, ref PassData passData)
        {

            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();

            FrameBufferData frameBufferData = frameData.Get<FrameBufferData>();
            TextureHandle accumulationBuffer = frameBufferData.accumulationTextureHandle;

            RenderTextureDescriptor desc = cameraData.cameraTargetDescriptor;
            desc.msaaSamples = 0;
            desc.depthBufferBits = 0;

            ReAllocateTransformer(desc);
            TextureHandle destination = renderGraph.ImportTexture(transformerBuffer);
            FrameBufferData customData = frameData.Get<FrameBufferData>();
            customData.transformationTextureHandle = destination;

            passData.src = accumulationBuffer;
            passData.dst = destination;
            passData.blitMaterial = transformerMaterial;
        }

        //Here's where our passes are queued! We start with accumulation, then we transform it.
        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("AMB Accumulation Pass", out var passData))
            {
                InitPassDataAccumulation(renderGraph, frameData, ref passData);
                builder.UseTexture(passData.src);
                builder.SetRenderAttachment(passData.dst, 0);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc((PassData data, RasterGraphContext context) => ExecutePass(data, context));
            }

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("AMB Transformation Pass", out var passData))
            {
                InitPassDataTransformer(renderGraph, frameData, ref passData);
                builder.UseTexture(passData.src);
                builder.SetRenderAttachment(passData.dst, 0);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc((PassData data, RasterGraphContext context) => ExecutePass(data, context));
            }
        }
    }

    //This pass is responsible for doing a simply blit to write the transformed accumulation buffer back to the base buffer, and to then take the final accumulation buffer for this frame and write it to the screen. voila!
    class BlitForwardPass : ScriptableRenderPass
    {
        private class PassData
        {
            internal TextureHandle src;
            internal TextureHandle dst;
        }
        
        static void ExecutePass(PassData data, RasterGraphContext context)
        {
            Blitter.BlitTexture(context.cmd, data.src, new Vector4(1, 1, 0, 0), 0, false);
        }

        //This is the pass where we take the transformed buffer and write it back onto the accumulation buffer.
        private void InitPassDataBack(RenderGraph renderGraph, ContextContainer frameData, ref PassData passData)
        {
            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            AccumulationPass.FrameBufferData frameBufferData = frameData.Get<AccumulationPass.FrameBufferData>();
            TextureHandle transformedBuffer = frameBufferData.transformationTextureHandle;
            passData.src = transformedBuffer;
            passData.dst = frameData.Get<AccumulationPass.FrameBufferData>().accumulationTextureHandle;
        }

        //This is the pass where the final accumulation buffer - after transformation - is written to the screen.
        private void InitPassDataForward(RenderGraph renderGraph, ContextContainer frameData, ref PassData passData)
        {
            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            AccumulationPass.FrameBufferData frameBufferData = frameData.Get<AccumulationPass.FrameBufferData>();
            TextureHandle blurredBuffer = frameBufferData.accumulationTextureHandle;
            passData.src = blurredBuffer;
            passData.dst = resourceData.activeColorTexture;
        }

        //Here's where we execute the blit passes! First we write back to the accumulation buffer from the transformed buffer, then we write the final accumulation buffer to the screen.
        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {            
            //This pass blits the transformed AMB pixels back to the accumulation buffer so that the next accumulation pass will overlay onto the transformed pixels.
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("AMB Write Back", out var passData))
            {
                InitPassDataBack(renderGraph, frameData, ref passData);
                builder.UseTexture(passData.src);
                builder.SetRenderAttachment(passData.dst, 0);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc((PassData data, RasterGraphContext context) => ExecutePass(data, context));
            }

            //This pass blits the final AMB image onto the screen for this frame.
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("AMB Display", out var passData))
            {
                InitPassDataForward(renderGraph, frameData, ref passData);
                builder.UseTexture(passData.src);
                builder.SetRenderAttachment(passData.dst, 0);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc((PassData data, RasterGraphContext context) => ExecutePass(data, context));
            }
        }
    }



    AccumulationPass ambAccumulationPass;
    BlitForwardPass blitForwardPass;

    Material blitColorMaterial;
    Material bufferTransformerMaterial;

    bool configured = false;
    
    public override void Create()
    {
        configured = false;

        blitColorMaterial = new Material(Shader.Find("Shader Graphs/AccumulationShader"));
        bufferTransformerMaterial = new Material(Shader.Find("Shader Graphs/BufferTransformer"));

        ambAccumulationPass = new AccumulationPass(blitColorMaterial, bufferTransformerMaterial);
        blitForwardPass = new BlitForwardPass();
        ambAccumulationPass.renderPassEvent = injectionPoint;
        blitForwardPass.renderPassEvent = injectionPoint;
    }
    
    protected override void Dispose(bool disposing)
    {
        ambAccumulationPass.Dispose();
    }
    
    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (renderingData.cameraData.cameraType == CameraType.SceneView || !Application.isPlaying)
            return;

        AccumulationBlurVolumeComponent blurVolumeComponent = VolumeManager.instance.stack?.GetComponent<AccumulationBlurVolumeComponent>();

        if (blurVolumeComponent == null)
            return;
        else if (!blurVolumeComponent.IsActive())
            return;

        if(blitColorMaterial == null || bufferTransformerMaterial == null)
        {
            blitColorMaterial = new Material(Shader.Find("Shader Graphs/AccumulationShader"));
            bufferTransformerMaterial = new Material(Shader.Find("Shader Graphs/BufferTransformer"));

            ambAccumulationPass = new AccumulationPass(blitColorMaterial, bufferTransformerMaterial);
        }

        if (configured)
        {
            if (blurVolumeComponent != null)
            {
                if (!blurVolumeComponent.IsActive())
                {
                    blitColorMaterial.SetFloat("_Alpha", 1f);

                    bufferTransformerMaterial.SetVector("_Linear", Vector2.zero);
                    bufferTransformerMaterial.SetFloat("_Alpha", 0f);
                    bufferTransformerMaterial.SetFloat("_Push", 0f);
                }
                else
                {
                    blitColorMaterial.SetFloat("_Alpha", blurVolumeComponent.decayRate.GetValue<float>());

                    bufferTransformerMaterial.SetVector("_Linear", blurVolumeComponent.linearTransformation.GetValue<Vector2>() * 0.1f);
                    bufferTransformerMaterial.SetFloat("_Alpha", 1f);// blurVolumeComponent.blendingAlpha.GetValue<float>());
                    bufferTransformerMaterial.SetFloat("_Push", blurVolumeComponent.radialTransformation.GetValue<float>());
                }
            }
        }
        else
        {
            blitColorMaterial.SetFloat("_Alpha", 1f);

            bufferTransformerMaterial.SetVector("_Linear", Vector2.zero);
            bufferTransformerMaterial.SetFloat("_Alpha", 0f);
            bufferTransformerMaterial.SetFloat("_Push", 0f);

            configured = true;
        }

        renderer.EnqueuePass(ambAccumulationPass);
        renderer.EnqueuePass(blitForwardPass);
    }
}
