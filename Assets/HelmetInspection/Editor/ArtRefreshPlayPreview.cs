using System;
using System.IO;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace HelmetInspection.Editor
{
    // Batch entry point: omit -quit. Capture actual Play Mode frames with the
    // production SRP Batcher and MSAA enabled, not immediate editor requests.
    public static class ArtRefreshPlayPreview
    {
        const string Key="HelmetInspection.ArtRefreshPlayPreview";
        static Camera camera;
        static RenderTexture target;
        static int readyFrame;
        static double deadline;

        public static void Run()
        {
            EditorSceneManager.OpenScene(ArtRefreshInstaller.ScenePath);
            SessionState.SetBool(Key,true);
            Resume();
            EditorApplication.EnterPlaymode();
        }

        [InitializeOnLoadMethod]
        static void Resume()
        {
            if(!SessionState.GetBool(Key,false))return;
            deadline=EditorApplication.timeSinceStartup+120;
            EditorApplication.update-=Tick;
            EditorApplication.update+=Tick;
        }

        static void Tick()
        {
            try
            {
                if(EditorApplication.timeSinceStartup>deadline)throw new TimeoutException("Play Mode preview timed out.");
                if(!EditorApplication.isPlaying||Time.timeSinceLevelLoad<1f)return;
                if(target==null)
                {
                    camera=UnityEngine.Object.FindFirstObjectByType<XROrigin>().Camera;
                    var pipeline=GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
                    if(pipeline==null||!pipeline.useSRPBatcher)throw new InvalidOperationException("Production SRP batching unexpectedly disabled.");
                    camera.GetUniversalAdditionalCameraData().allowXRRendering=false;
                    camera.stereoTargetEye=StereoTargetEyeMask.None;
                    camera.allowMSAA=true;camera.aspect=1.6f;
                    target=new RenderTexture(1600,1000,24,RenderTextureFormat.ARGB32,RenderTextureReadWrite.sRGB){antiAliasing=4};
                    target.Create();camera.targetTexture=target;
                    readyFrame=Time.frameCount+60;
                    return;
                }
                if(Time.frameCount<readyFrame)return;
                var resolved=RenderTexture.GetTemporary(1600,1000,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.sRGB,1);
                var previous=RenderTexture.active;
                try
                {
                    Graphics.Blit(target,resolved);
                    RenderTexture.active=resolved;
                    var image=new Texture2D(1600,1000,TextureFormat.RGBA32,false,false);
                    image.ReadPixels(new Rect(0,0,1600,1000),0,0);image.Apply();
                    File.WriteAllBytes(ArtRefreshInstaller.Root+"/Previews/PlayModeHMD.png",image.EncodeToPNG());
                    UnityEngine.Object.DestroyImmediate(image);
                }
                finally{RenderTexture.active=previous;RenderTexture.ReleaseTemporary(resolved);}
                Debug.Log("[ArtRefreshPlayPreview] PASS: live HMD camera after 60 normal Play Mode frames; SRP Batcher enabled, 4x MSAA. Preview saved.");
                Finish(0);
            }
            catch(Exception error){Debug.LogException(error);Finish(1);}
        }
        static void Finish(int code)
        {
            EditorApplication.update-=Tick;
            SessionState.SetBool(Key,false);
            if(camera!=null)camera.targetTexture=null;
            if(target!=null){target.Release();UnityEngine.Object.DestroyImmediate(target);target=null;}
            if(Application.isBatchMode)EditorApplication.Exit(code);else EditorApplication.ExitPlaymode();
        }
    }
}
