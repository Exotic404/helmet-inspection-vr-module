using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using TMPro;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace HelmetInspection.Editor
{
    /// <summary>Imports Blender meshes and replaces presentation while preserving authored gameplay.</summary>
    public static class ArtRefreshInstaller
    {
        public const string Root = "Assets/HelmetInspection/ArtRefresh";
        public const string ScenePath = "Assets/HelmetInspection/Scenes/HelmetDefectInspection.unity";
        public const string ArtName = "ART - Metrology Studio";
        const string ScannerName = "VISUAL - Precision Inspector";
        const string HelmetMat = "Assets/HelmetInspection/Materials/M_Helmet_Triplanar.mat";
        static readonly Color Ink = new Color(.035f,.064f,.08f);
        static readonly Color Paper = new Color(.78f,.80f,.76f);
        static readonly Color Cyan = new Color(.12f,.65f,.75f);
        static readonly Color Copper = new Color(.82f,.24f,.085f);

        [Serializable] class Source { public int version; public string coordinateSystem; public MatData[] materials; public MeshData[] meshes; }
        [Serializable] class MatData { public string name; public float[] baseColor; public float metallic; public float roughness; public float[] emission; public float emissionStrength; }
        [Serializable] class MeshData { public string name; public int materialIndex; public float[] positions; public float[] normals; public float[] uv; public int[] triangles; }

        public static void InstallAndReview()
        {
            Install();
            ArtRefreshVerifier.Verify();
            RenderViews();
        }

        [MenuItem("Helmet Inspection/Metrology Studio/Install Blender Art Refresh")]
        public static void Install()
        {
            if(!Application.isBatchMode&&!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())return;
            var scene = EditorSceneManager.OpenScene(ScenePath);
            EnsureFolders();
            Require(File.Exists(Root+"/Source/MetrologyLab.mesh.json") && File.Exists(Root+"/Source/InspectorScanner.mesh.json"), "Run the Blender asset script before installing.");
            var protection = Snapshot();
            var dataHash = Hash("Assets/HelmetInspection/Data/DefectSet_A2.asset");
            var helmetMeshes = Object.FindObjectsByType<HelmetOutOfBoundsRecovery>(FindObjectsInactive.Include)
                .SelectMany(h=>h.GetComponentsInChildren<MeshFilter>(true)).ToDictionary(f=>f, f=>f.sharedMesh);
            RetireOldVisuals();
            var env = GameObject.Find("ENVIRONMENT - QA Metrology Lab");
            var art = new GameObject(ArtName);
            art.transform.SetParent(env.transform,false);
            ImportModel("MetrologyLab", art.transform);
            StyleShell();
            DressSigns(art.transform);
            DressControls(art.transform);
            DressScanner();
            // The new grip is longer than the old prop. Its existing physical
            // hull is deliberately refit; every other collider stays identical.
            var scannerHull=Object.FindFirstObjectByType<InspectionScanner>().GetComponent<BoxCollider>();
            protection[scannerHull]=ColliderSignature(scannerHull);
            StyleHelmets();
            ConfigureLighting();
            CreateReflection(art.transform);
            AssertSnapshot(protection);
            Require(Hash("Assets/HelmetInspection/Data/DefectSet_A2.asset")==dataHash,"Authored defect data changed.");
            foreach(var pair in helmetMeshes) Require(pair.Key!=null && pair.Key.sharedMesh==pair.Value,"Helmet mesh geometry changed.");
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            Debug.Log($"[ArtRefresh] Installed Blender lab and scanner; {protection.Count-1} gameplay colliders unchanged, existing scanner hull fitted to new grip. Defect data SHA256={dataHash}");
        }

        static Dictionary<Collider,string> Snapshot() => Object.FindObjectsByType<Collider>(FindObjectsInactive.Include).ToDictionary(c=>c,ColliderSignature);
        static string ColliderSignature(Collider c) => EditorJsonUtility.ToJson(c)+c.transform.position.ToString("F6")+c.transform.rotation.ToString("F6")+c.transform.lossyScale.ToString("F6");
        static void AssertSnapshot(Dictionary<Collider,string> original)
        {
            Require(Object.FindObjectsByType<Collider>(FindObjectsInactive.Include).Length==original.Count,"A visual import changed collider count.");
            foreach(var p in original) Require(p.Key!=null && ColliderSignature(p.Key)==p.Value,"Gameplay collider or pose changed: "+p.Key?.name);
        }
        static string Hash(string path) { using var sha=SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-",""); }

        static void RetireOldVisuals()
        {
            foreach(var name in new[]{EnvironmentArtPassInstaller.ArtRootName,ArtName,ScannerName})
            {
                var root=Find(name);
                if(root==null)continue;
                Require(root.GetComponentsInChildren<Collider>(true).Length==0,"Generated visual hierarchy acquired a collider: "+name);
                Require(root.GetComponentsInChildren<MonoBehaviour>(true).All(m=>m is TMP_Text),"Generated visuals have unexpected behavior: "+name);
                Object.DestroyImmediate(root);
            }
            var fixtures=Find("Professional QA Lab Fixtures");
            if(fixtures!=null) foreach(var r in fixtures.GetComponentsInChildren<Renderer>(true)) r.enabled=false;
            var table=Find("Metrology Inspection Table - Top 0.85m");
            if(table!=null) foreach(var r in table.GetComponentsInChildren<Renderer>(true))
            {
                if(r.name=="Stand Base"||r.name=="Stand Collar")continue;
                r.enabled=false;
            }
            foreach(var name in new[]{"Back Wall Navy Band","Floor Inlay Left","Floor Inlay Right"})
                if(Find(name)?.GetComponent<Renderer>() is Renderer r)r.enabled=false;
            foreach(var text in Object.FindObjectsByType<TMP_Text>(FindObjectsInactive.Include))
                if(text.name.StartsWith("Label - A1")||text.name.StartsWith("Label - A2"))text.enabled=false;
        }

        static void EnsureFolders()
        {
            foreach(var p in new[]{Root,Root+"/Generated",Root+"/Generated/Materials",Root+"/Generated/Meshes",Root+"/Generated/Textures",Root+"/Previews"})
                if(!AssetDatabase.IsValidFolder(p))AssetDatabase.CreateFolder(Path.GetDirectoryName(p).Replace('\\','/'),Path.GetFileName(p));
        }
        static void ImportModel(string file,Transform parent)
        {
            var src=JsonUtility.FromJson<Source>(File.ReadAllText(Root+"/Source/"+file+".mesh.json"));
            Require(src.version==1&&src.coordinateSystem=="UnityLeftHandedYUp","Unsupported Blender export coordinates.");
            var mats=src.materials.Select(d=>Material(file+"_"+d.name,ColorOf(d.baseColor),d.metallic,1f-d.roughness,
                d.emission==null?Color.black:ColorOf(d.emission)*d.emissionStrength)).ToArray();
            foreach(var d in src.meshes)
            {
                var mesh=Asset<Mesh>(Root+"/Generated/Meshes/"+file+"_"+d.name+".asset",()=>new Mesh());
                mesh.Clear(); mesh.name=d.name; mesh.indexFormat=IndexFormat.UInt32;
                mesh.vertices=Enumerable.Range(0,d.positions.Length/3).Select(i=>new Vector3(d.positions[i*3],d.positions[i*3+1],d.positions[i*3+2])).ToArray();
                mesh.normals=Enumerable.Range(0,d.normals.Length/3).Select(i=>new Vector3(d.normals[i*3],d.normals[i*3+1],d.normals[i*3+2])).ToArray();
                mesh.uv=Enumerable.Range(0,d.uv.Length/2).Select(i=>new Vector2(d.uv[i*2],d.uv[i*2+1])).ToArray();
                mesh.triangles=d.triangles;mesh.RecalculateBounds(); mesh.RecalculateTangents();EditorUtility.SetDirty(mesh);
                var go=new GameObject(d.name);go.transform.SetParent(parent,false);
                go.AddComponent<MeshFilter>().sharedMesh=mesh;
                var r=go.AddComponent<MeshRenderer>(); r.sharedMaterial=mats[d.materialIndex];r.reflectionProbeUsage=ReflectionProbeUsage.BlendProbes;
                // Baked room fill is supplied as ambient SH; scene has no stale baked lightmaps.
                r.lightProbeUsage=LightProbeUsage.Off;
                if(file=="MetrologyLab")GameObjectUtility.SetStaticEditorFlags(go,StaticEditorFlags.BatchingStatic|StaticEditorFlags.ReflectionProbeStatic);
            }
        }
        static Color ColorOf(float[] c) => new Color(c[0],c[1],c[2],c.Length>3?c[3]:1f);
        static T Asset<T>(string path,Func<T> make) where T:Object
        {
            var result=AssetDatabase.LoadAssetAtPath<T>(path);if(result!=null)return result;
            result=make();AssetDatabase.CreateAsset(result,path);return result;
        }
        static Material Material(string name,Color color,float metal=0f,float smooth=.35f,Color? emission=null)
        {
            var m=Asset<Material>(Root+"/Generated/Materials/"+name+".mat",()=>new Material(Shader.Find("Universal Render Pipeline/Lit")));
            m.name=name;m.SetColor("_BaseColor",color);m.SetFloat("_Metallic",metal);m.SetFloat("_Smoothness",smooth);m.enableInstancing=true;
            if(emission.HasValue && emission.Value.maxColorComponent>0f){m.globalIlluminationFlags=MaterialGlobalIlluminationFlags.RealtimeEmissive;m.EnableKeyword("_EMISSION");m.SetColor("_EmissionColor",emission.Value);}
            else{m.globalIlluminationFlags=MaterialGlobalIlluminationFlags.EmissiveIsBlack;m.DisableKeyword("_EMISSION");m.SetColor("_EmissionColor",Color.black);}
            EditorUtility.SetDirty(m);return m;
        }

        static void StyleShell()
        {
            var wall=Material("ArchitecturalWarmWhite",new Color(.72f,.72f,.67f),0,.28f);
            var floor=Material("FineAggregateFloor",new Color(.19f,.23f,.25f),.05f,.32f);
            var ceiling=Material("CeilingSoftWhite",new Color(.77f,.79f,.77f),0,.18f);
            foreach(var name in new[]{"Back Wall","Front Wall","Left Wall","Right Wall","Floor","Ceiling"})
            {
                var r=Find(name).GetComponent<Renderer>();r.enabled=true;r.sharedMaterial=name=="Floor"?floor:name=="Ceiling"?ceiling:wall;
                r.shadowCastingMode=ShadowCastingMode.Off;r.lightProbeUsage=LightProbeUsage.Off;
            }
            var metal=Material("StandAnodizedGraphite",Ink,.65f,.6f);
            foreach(var r in Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Include).Where(r=>r.name=="Stand Base"))r.sharedMaterial=metal;
        }

        static void DressSigns(Transform art)
        {
            foreach(var img in Object.FindObjectsByType<Image>(FindObjectsInactive.Include))
            {
                if(img.name.Contains("Plate")){img.color=Ink;img.raycastTarget=false;}
            }
            foreach(var text in Object.FindObjectsByType<TMP_Text>(FindObjectsInactive.Include))
            {
                if(text.GetComponentInParent<Canvas>()==null)continue;
                text.text=text.text.Replace("Press BEGIN","Press START").Replace("Press RESTART","Press RESET");
                text.color=Color.white;text.outlineWidth=.06f;text.raycastTarget=false;
                if(text.name.Contains("Eyebrow")||text.name=="Progress")text.color=new Color(.35f,.84f,.87f);
            }
            var bezel=Material("DisplayGraphite",Ink,.3f,.35f);
            var silver=Material("DisplayMachinedEdge",new Color(.4f,.46f,.49f),.65f,.55f);
            // Existing live panels keep their authored transforms. A backing sits behind
            // each actual canvas instead of painting decorative bars across its text.
            foreach(var canvas in Object.FindObjectsByType<Canvas>(FindObjectsInactive.Include))
            {
                if(canvas.name!="Start Panel"&&canvas.name!="Findings Panel"&&canvas.name!="Module Header Canvas")continue;
                var rect=(RectTransform)canvas.transform;var corners=new Vector3[4];rect.GetWorldCorners(corners);
                var center=(corners[0]+corners[2])*.5f;var width=Vector3.Distance(corners[0],corners[3]);var height=Vector3.Distance(corners[0],corners[1]);
                Box("Backing "+canvas.name,art,center+rect.forward*.028f,new Vector3(width+.048f,height+.048f,.04f),silver,rect.rotation);
            }
            Screen(art,"PROFILE",new Vector3(.20f,2.16f,2.31f),true,bezel);
            Screen(art,"INSTRUMENT STATUS",new Vector3(.20f,1.78f,2.31f),false,bezel);
            Text(art,"Lab Identity","MATERIALS LAB  /  01",new Vector3(0,2.72f,2.385f),Quaternion.identity,.056f,Ink,2.5f);
            Text(art,"Table Station A1","A1  /  REFERENCE",new Vector3(-.45f,.865f,.36f),Quaternion.Euler(90,0,0),.022f,Color.white,.45f);
            Text(art,"Table Station A2","A2  /  INSPECTION",new Vector3(.45f,.865f,.36f),Quaternion.Euler(90,0,0),.022f,Color.white,.45f);
        }

        static void Screen(Transform parent,string title,Vector3 center,bool profile,Material bezel)
        {
            var tex=DisplayTexture(profile);
            var mat=Material(profile?"SchematicDisplay":"SensorDisplay",Color.white,0f,.15f,Color.white*.75f);
            mat.SetTexture("_BaseMap",tex);mat.SetTexture("_EmissionMap",tex);EditorUtility.SetDirty(mat);
            // The visible -Z face of Unity's cube has its UV axes reversed.
            mat.SetTextureScale("_BaseMap",new Vector2(-1,-1));mat.SetTextureOffset("_BaseMap",Vector2.one);
            Box(title+" Frame",parent,center,new Vector3(.78f,.30f,.04f),bezel);
            Box(title+" Glass",parent,center+Vector3.back*.024f,new Vector3(.72f,.24f,.007f),mat);
            Text(parent,title+" Heading",title,center+new Vector3(-.07f,.081f,-.031f),Quaternion.identity,.018f,new Color(.75f,.90f,.92f),.56f);
            Text(parent,title+" Footer",profile?"SHELL PROFILE  /  A1-A2":"OPTICS ONLINE  |  QA READY",center+new Vector3(-.02f,-.094f,-.031f),Quaternion.identity,.014f,new Color(.4f,.84f,.85f),.59f);
            var green=Material("StatusIndicator",new Color(.05f,.55f,.16f),0,.4f,new Color(.05f,.8f,.16f));
            Box(title+" Power LED",parent,center+new Vector3(.338f,-.133f,-.024f),new Vector3(.018f,.005f,.003f),green);
        }
        static Texture2D DisplayTexture(bool profile)
        {
            const int w=768,h=256;var t=new Texture2D(w,h,TextureFormat.RGBA32,false);var pixels=new Color[w*h];
            for(int y=0;y<h;y++)for(int x=0;x<w;x++)
            {
                var c=new Color(.015f,.035f,.047f);if(x%32==0||y%32==0)c=new Color(.025f,.065f,.077f);
                if(profile)
                {
                    var xx=(x-363f)/180f;var yy=(y-116f)/73f;
                    if(y>90&&Mathf.Abs(xx*xx+yy*yy-1f)<.047f)c=Cyan;
                    if(x>166&&x<561&&Mathf.Abs(y-90)<2)c=Cyan;
                    if(x>177&&x<548&&(y==70||y==72))c=Copper;
                    if((x==177||x==548)&&y>60&&y<171)c=Copper;
                }
                else
                {
                    if(x>45&&x<485&&Mathf.Abs(y-(121+Mathf.Sin(x*.035f)*18+Mathf.Cos(x*.071f)*7))<2)c=Cyan;
                    if(x>528&&x<698&&x%34<20&&y>65&&y<85+((x-528)/34)*14)c=(x/34)%2==0?Cyan:Copper;
                    if(x>42&&x<478&&y>65&&y<72)c=new Color(.16f,.54f,.36f);
                }
                if(y%4==0)c*=.92f;c.a=1;pixels[y*w+x]=c;
            }
            t.SetPixels(pixels);t.Apply();var path=Root+"/Generated/Textures/"+(profile?"ShellProfile.png":"OpticsStatus.png");
            File.WriteAllBytes(path,t.EncodeToPNG());Object.DestroyImmediate(t);AssetDatabase.ImportAsset(path,ImportAssetOptions.ForceSynchronousImport|ImportAssetOptions.ForceUpdate);
            var imp=(TextureImporter)AssetImporter.GetAtPath(path);imp.textureCompression=TextureImporterCompression.Uncompressed;imp.mipmapEnabled=true;imp.wrapMode=TextureWrapMode.Clamp;imp.filterMode=FilterMode.Trilinear;imp.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }
        static void DressControls(Transform art)
        {
            var metal=Material("ControlsBrushedAluminum",new Color(.46f,.51f,.54f),.72f,.55f);
            var dark=Material("ControlsGraphite",Ink,.15f,.4f);
            foreach(var b in Object.FindObjectsByType<MechanicalTrainingButtonBase>(FindObjectsInactive.Include))
            {
                var cap=b.GetComponentsInChildren<Transform>(true).First(t=>t.name=="Raised 3D Button Cap - 18mm");
                var center=cap.GetComponent<Renderer>().bounds.center;
                var label=b.GetComponentsInChildren<TMP_Text>(true).First();label.enabled=false;
                var start=b is TrainingStartButton;
                cap.GetComponent<Renderer>().sharedMaterial=Material(start?"StartEmerald":"ResetBlue",start?new Color(.05f,.45f,.21f):new Color(.075f,.36f,.62f),.28f,.58f,new Color(.015f,.04f,.025f));
                b.GetComponentsInChildren<Transform>(true).First(t=>t.name=="Rounded Recessed Bezel").GetComponent<Renderer>().sharedMaterial=metal;
                Text(art,start?"START Label":"RESET Label",start?"START":"RESET",center+new Vector3(-.04f,.105f,0),Quaternion.Euler(0,90,0),.038f,Paper,.37f);
            }
            var plinth=Find("Shared Recessed Control Plinth").GetComponent<Renderer>();plinth.sharedMaterial=dark;
            Text(art,"Control Panel Identity","SESSION CONTROL",new Vector3(2.31f,1.35f,-.46f),Quaternion.Euler(0,90,0),.026f,Ink,.90f);
        }
        static void DressScanner()
        {
            var scanner=Object.FindFirstObjectByType<InspectionScanner>(FindObjectsInactive.Include);
            Require(scanner!=null,"Scanner is missing.");
            foreach(var r in scanner.GetComponentsInChildren<MeshRenderer>(true))r.enabled=false;
            foreach(var t in scanner.GetComponentsInChildren<TMP_Text>(true))t.enabled=false;
            var root=new GameObject(ScannerName);root.transform.SetParent(scanner.transform,false);
            ImportModel("InspectorScanner",root.transform);
            var emitter=root.GetComponentsInChildren<MeshRenderer>().FirstOrDefault(r=>r.sharedMaterial.name.IndexOf("OpticalTeal",StringComparison.OrdinalIgnoreCase)>=0||r.sharedMaterial.name.IndexOf("emitter",StringComparison.OrdinalIgnoreCase)>=0||r.sharedMaterial.name.IndexOf("cyan",StringComparison.OrdinalIgnoreCase)>=0);
            Require(emitter!=null,"Blender scanner must provide a cyan/emitter material for live proximity feedback.");
            // Keep the existing optical origin, socket, grab and collider. Only the
            // visual renderer receiving proximity feedback is replaced.
            var so=new SerializedObject(scanner);so.FindProperty("emitterRenderer").objectReferenceValue=emitter;so.ApplyModifiedPropertiesWithoutUndo();
            var origin=(Transform)so.FindProperty("beamOrigin").objectReferenceValue;
            root.transform.localPosition=origin.localPosition-new Vector3(0,0,.085f);
            var hull=scanner.GetComponent<BoxCollider>();Require(hull!=null,"Existing scanner physical hull is missing.");
            var bounds=new Bounds();var first=true;
            foreach(var f in root.GetComponentsInChildren<MeshFilter>())
                foreach(var v in f.sharedMesh.vertices)
                {var p=hull.transform.InverseTransformPoint(f.transform.TransformPoint(v));if(first){bounds=new Bounds(p,Vector3.zero);first=false;}else bounds.Encapsulate(p);}
            // Keep the original grab/collision component and pivot, just fit its
            // volume so the new handle cannot sink into the bench or floor.
            hull.center=bounds.center;hull.size=bounds.size+Vector3.one*.002f;
            Text(root.transform,"Scanner Model Label","INS-01",new Vector3(0,.042f,-.022f),Quaternion.Euler(90,0,0),.007f,Color.white,.052f,false);
            Text(root.transform,"Scanner Display Readout","QA  /  READY",new Vector3(0,.038f,.013f),Quaternion.Euler(90,0,0),.004f,new Color(.35f,.95f,.88f),.043f,false);
        }
        static void StyleHelmets()
        {
            var mat=AssetDatabase.LoadAssetAtPath<Material>(HelmetMat);Require(mat!=null,"Shared helmet material missing.");
            // The default graph branch reads Grid_Texture, not SlopeColor. Keep the
            // UV-free shader but give its actual input a new red lacquer texture.
            var tex=new Texture2D(128,128,TextureFormat.RGBA32,true);var pixels=new Color[128*128];
            for(int i=0;i<pixels.Length;i++){var grain=Mathf.Sin(i*17.13f)*.005f;pixels[i]=new Color(.64f+grain,.038f+grain*.2f,.024f+grain*.1f,1);}
            tex.SetPixels(pixels);tex.Apply();var texturePath=Root+"/Generated/Textures/MetallicRedLacquer.png";
            File.WriteAllBytes(texturePath,tex.EncodeToPNG());Object.DestroyImmediate(tex);AssetDatabase.ImportAsset(texturePath,ImportAssetOptions.ForceSynchronousImport|ImportAssetOptions.ForceUpdate);
            var ti=(TextureImporter)AssetImporter.GetAtPath(texturePath);ti.textureCompression=TextureImporterCompression.Uncompressed;ti.mipmapEnabled=true;ti.wrapMode=TextureWrapMode.Repeat;ti.SaveAndReimport();
            mat.SetTexture("_Grid_Texture",AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath));
            mat.SetColor("_SlopeColor",new Color(.64f,.038f,.024f,1));mat.SetFloat("_Slope",1f);EditorUtility.SetDirty(mat);
            foreach(var h in Object.FindObjectsByType<HelmetOutOfBoundsRecovery>(FindObjectsInactive.Include))
                foreach(var r in h.GetComponentsInChildren<MeshRenderer>(true))
                    if(r.sharedMaterial==mat){r.lightProbeUsage=LightProbeUsage.Off;r.reflectionProbeUsage=ReflectionProbeUsage.BlendProbes;}
        }

        static void ConfigureLighting()
        {
            RenderSettings.ambientMode=AmbientMode.Flat;RenderSettings.ambientLight=new Color(.5f,.55f,.59f);RenderSettings.ambientIntensity=1;
            var sh=new SphericalHarmonicsL2();sh.AddAmbientLight(new Color(.38f,.42f,.46f));RenderSettings.ambientProbe=sh;
            RenderSettings.reflectionIntensity=1f;RenderSettings.fog=false;
            var sun=Find("Directional - Soft Lab Fill").GetComponent<Light>();sun.color=new Color(1,.96f,.90f);sun.intensity=1.15f;
            sun.transform.rotation=Quaternion.Euler(78,-25,0);sun.shadows=LightShadows.Soft;sun.shadowStrength=.55f;sun.shadowBias=.03f;sun.shadowNormalBias=.2f;
            foreach(var spec in new[]{("A1 Inspection Spot",new Vector3(-.75f,2.74f,.32f),new Vector3(-.45f,.85f,.6f)),("A2 Inspection Spot",new Vector3(.75f,2.74f,.32f),new Vector3(.45f,.85f,.6f)),("Panel Wash Left",new Vector3(-1.65f,2.68f,-.65f),new Vector3(-1.1f,1.4f,2.0f)),("Panel Wash Right",new Vector3(1.65f,2.68f,-.65f),new Vector3(1.8f,1.2f,1.5f))})
            {
                var l=Find(spec.Item1).GetComponent<Light>();l.transform.SetPositionAndRotation(spec.Item2,Quaternion.LookRotation(spec.Item3-spec.Item2));
                l.color=new Color(.93f,.97f,1);l.intensity=1.7f;l.range=5;l.spotAngle=95;l.innerSpotAngle=65;l.shadows=LightShadows.None;EditorUtility.SetDirty(l);
            }
            var urp=AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>("Assets/HelmetInspection/Settings/HelmetLabURP.asset");
            var so=new SerializedObject(urp);so.FindProperty("m_ReflectionProbeBlending").boolValue=true;so.FindProperty("m_ReflectionProbeBoxProjection").boolValue=true;so.FindProperty("m_SoftShadowsSupported").boolValue=true;so.ApplyModifiedPropertiesWithoutUndo();
            var profile=AssetDatabase.LoadAssetAtPath<VolumeProfile>("Assets/HelmetInspection/Settings/HelmetLabVolume.asset");
            if(profile.TryGet<ColorAdjustments>(out var color)){color.contrast.Override(3);color.saturation.Override(0);color.postExposure.Override(0);EditorUtility.SetDirty(color);}
        }

        static void CreateReflection(Transform art)
        {
            Require(SystemInfo.graphicsDeviceType!=GraphicsDeviceType.Null,"Room reflection capture needs graphics; use -force-d3d11.");
            const int size=256;
            var cameraObject=new GameObject("Temporary Room Capture");var camera=cameraObject.AddComponent<Camera>();
            camera.transform.position=new Vector3(0,1.25f,.6f);camera.fieldOfView=90;camera.aspect=1;camera.nearClipPlane=.03f;camera.farClipPlane=15;
            camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=Color.black;camera.allowHDR=false;camera.allowMSAA=false;camera.stereoTargetEye=StereoTargetEyeMask.None;
            camera.GetUniversalAdditionalCameraData().allowXRRendering=false;
            var hidden=new List<Renderer>();
            hidden.AddRange(Object.FindFirstObjectByType<XROrigin>().GetComponentsInChildren<Renderer>(true));
            hidden.AddRange(Object.FindFirstObjectByType<InspectionScanner>().GetComponentsInChildren<Renderer>(true));
            foreach(var h in Object.FindObjectsByType<HelmetOutOfBoundsRecovery>(FindObjectsInactive.Include))hidden.AddRange(h.GetComponentsInChildren<Renderer>(true));
            var states=hidden.Select(r=>r.enabled).ToArray();foreach(var r in hidden)r.enabled=false;
            var cube=Asset<Cubemap>(Root+"/Generated/Textures/StudioReflection.asset",()=>new Cubemap(size,TextureFormat.RGBA32,true));
            var dirs=new[]{Vector3.right,Vector3.left,Vector3.up,Vector3.down,Vector3.forward,Vector3.back};
            var ups=new[]{Vector3.up,Vector3.up,Vector3.back,Vector3.forward,Vector3.up,Vector3.up};
            try
            {
                for(int f=0;f<6;f++)
                {
                    camera.transform.rotation=Quaternion.LookRotation(dirs[f],ups[f]);
                    var tex=Capture(camera,size,size);var px=tex.GetPixels();
                    Require(px.Any(c=>Mathf.Abs(c.r-c.b)>.01f),"Reflection face is uniform/uninitialized: "+f);
                    cube.SetPixels(px,(CubemapFace)f);File.WriteAllBytes(Root+"/Previews/ReflectionFace"+f+".png",tex.EncodeToPNG());Object.DestroyImmediate(tex);
                }
                cube.Apply(true,false);cube.filterMode=FilterMode.Trilinear;cube.wrapMode=TextureWrapMode.Clamp;
                cube.imageContentsHash=Hash128.Compute("MetrologyStudioRoom-v1");EditorUtility.SetDirty(cube);
            }
            finally{for(int i=0;i<hidden.Count;i++)hidden[i].enabled=states[i];Object.DestroyImmediate(cameraObject);}
            var go=new GameObject("Studio Baked Room Reflection");go.transform.SetParent(art,false);go.transform.position=new Vector3(0,1.5f,0);
            var probe=go.AddComponent<ReflectionProbe>();probe.mode=ReflectionProbeMode.Custom;probe.customBakedTexture=cube;probe.resolution=size;probe.size=new Vector3(4.9f,3,4.9f);probe.boxProjection=true;probe.intensity=1;probe.importance=3;probe.blendDistance=.2f;
            RenderSettings.defaultReflectionMode=DefaultReflectionMode.Custom;RenderSettings.customReflection=cube;
        }
        internal static Texture2D Capture(Camera camera,int width,int height)
        {
            var rt=new RenderTexture(width,height,24,RenderTextureFormat.ARGB32,RenderTextureReadWrite.sRGB){antiAliasing=1};rt.Create();
            var previous=RenderTexture.active;
            // Multiple editor render requests in one update can reuse stale SRP
            // material buffers. Only the offline capture bypasses batching; the
            // normal Quest rendering path keeps the SRP Batcher enabled.
            var batching=GraphicsSettings.useScriptableRenderPipelineBatching;
            var pipeline=GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            var assetBatching=pipeline!=null&&pipeline.useSRPBatcher;
            try
            {
                if(pipeline!=null)pipeline.useSRPBatcher=false;
                GraphicsSettings.useScriptableRenderPipelineBatching=false;
                var req=new UniversalRenderPipeline.SingleCameraRequest {destination=rt};
                RenderPipeline.SubmitRenderRequest(camera,req);
                RenderTexture.active=rt;var image=new Texture2D(width,height,TextureFormat.RGBA32,false,false);
                image.ReadPixels(new Rect(0,0,width,height),0,0);image.Apply(false,false);return image;
            }
            finally{if(pipeline!=null)pipeline.useSRPBatcher=assetBatching;GraphicsSettings.useScriptableRenderPipelineBatching=batching;RenderTexture.active=previous;rt.Release();Object.DestroyImmediate(rt);}
        }
        [MenuItem("Helmet Inspection/Metrology Studio/Render Review Views")]
        public static void RenderViews()
        {
            if(!Application.isBatchMode&&!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())return;
            EditorSceneManager.OpenScene(ScenePath);var obj=new GameObject("Temporary Review Camera");var c=obj.AddComponent<Camera>();
            c.nearClipPlane=.025f;c.farClipPlane=20;c.allowHDR=false;c.allowMSAA=false;c.stereoTargetEye=StereoTargetEyeMask.None;
            c.GetUniversalAdditionalCameraData().allowXRRendering=false;c.clearFlags=CameraClearFlags.SolidColor;c.backgroundColor=new Color(.06f,.08f,.09f);
            var rig=Object.FindFirstObjectByType<XROrigin>();var scanner=Object.FindFirstObjectByType<InspectionScanner>();
            var hidden=rig.GetComponentsInChildren<Renderer>(true).Concat(scanner.GetComponentsInChildren<Renderer>(true)).ToArray();
            var states=hidden.Select(r=>r.enabled).ToArray();foreach(var r in hidden)r.enabled=false;
            try
            {
                Review(c,"Room",new Vector3(-.15f,1.65f,-2.12f),new Vector3(.05f,1.45f,.8f),70);
                Review(c,"HelmetDetail",new Vector3(.75f,1.42f,-.08f),new Vector3(.25f,1.08f,.6f),52);
                Review(c,"Controls",new Vector3(1.15f,1.40f,-1.22f),new Vector3(2.3f,1.13f,-.45f),55);
                Review(c,"RoomReverse",new Vector3(1.4f,1.65f,1.8f),new Vector3(-.3f,1.55f,-1.7f),72);
                for(int i=0;i<hidden.Length;i++)hidden[i].enabled=states[i];
                Review(c,"DefaultHMD",rig.Camera.transform.position,rig.Camera.transform.position+rig.Camera.transform.forward*3,72);
                var pose=scanner.transform.position;var rot=scanner.transform.rotation;
                scanner.transform.SetPositionAndRotation(new Vector3(0,1.35f,-.5f),Quaternion.Euler(0,-22,0));
                Review(c,"Scanner",new Vector3(.23f,1.51f,-.23f),scanner.transform.position,42);
                scanner.transform.SetPositionAndRotation(pose,rot);
            }
            finally{for(int i=0;i<hidden.Length;i++)hidden[i].enabled=states[i];Object.DestroyImmediate(obj);}
        }
        static void Review(Camera c,string name,Vector3 pos,Vector3 at,float fov)
        {
            c.transform.SetPositionAndRotation(pos,Quaternion.LookRotation(at-pos));c.fieldOfView=fov;c.aspect=1.6f;
            foreach(var t in Object.FindObjectsByType<TMP_Text>(FindObjectsInactive.Include))if(t.enabled)t.ForceMeshUpdate();
            Canvas.ForceUpdateCanvases();var image=Capture(c,1600,1000);File.WriteAllBytes(Root+"/Previews/"+name+".png",image.EncodeToPNG());Object.DestroyImmediate(image);
            Debug.Log("[ArtRefresh] Rendered "+name);
        }
        static GameObject Box(string name,Transform parent,Vector3 center,Vector3 size,Material mat,Quaternion? rotation=null)
        {
            var go=GameObject.CreatePrimitive(PrimitiveType.Cube);go.name=name;Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.SetParent(parent,false);go.transform.SetPositionAndRotation(center,rotation??Quaternion.identity);go.transform.localScale=size;
            go.GetComponent<Renderer>().sharedMaterial=mat;return go;
        }
        static void Text(Transform parent,string name,string value,Vector3 position,Quaternion rotation,float letterHeight,Color color,float width,bool world=true)
        {
            var go=new GameObject(name);var t=go.AddComponent<TextMeshPro>();go.transform.SetParent(parent,false);
            if(world)go.transform.SetPositionAndRotation(position,rotation);else go.transform.SetLocalPositionAndRotation(position,rotation);
            go.transform.localScale=Vector3.one*.01f;t.text=value;t.fontSize=letterHeight*1000f;t.color=color;t.alignment=TextAlignmentOptions.Center;
            t.fontStyle=FontStyles.Bold;t.textWrappingMode=TextWrappingModes.NoWrap;t.raycastTarget=false;t.rectTransform.sizeDelta=new Vector2(width*100,12);
            t.outlineWidth=.04f;t.outlineColor=new Color(0,0,0,.5f);t.ForceMeshUpdate();
        }
        static GameObject Find(string name)=>Object.FindObjectsByType<Transform>(FindObjectsInactive.Include).FirstOrDefault(t=>t.name==name)?.gameObject;
        internal static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    }
}
