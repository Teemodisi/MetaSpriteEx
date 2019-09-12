﻿using System.Collections;
using System.Collections.Generic;
using System.IO;
using System;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

using MetaSprite.Internal;
using System.Linq;

namespace MetaSprite
{

    public class ImportContext
    {

        public ASEFile file;
        public ImportSettings settings;

        public string fileDirectory;
        public string fileName;
        public string fileNameNoExt;

        public string atlasPath;
        public string animControllerPath;
        public string animClipDirectory;
        public string prefabDirectory;

        // The local texture coordinate for bottom-left point of each frame's crop rect, in Unity texture space.
        public List<Vector2> spriteCropPositions = new List<Vector2>();

        public Dictionary<FrameTag, AnimationClip> generatedClips = new Dictionary<FrameTag, AnimationClip>();

        //所有生成独立图集的都是一个层动画意义上的图层
        public Dictionary<string, List<Sprite>> mapSprite = new Dictionary<string, List<Sprite>>();

        public AnimatorController controller;

        public GameObject rootGameObject;
        public Dictionary<string, GameObject> name2GameObject = new Dictionary<string, GameObject>();
    }

    public static class ASEImporter
    {

        static readonly Dictionary<string, MetaLayerProcessor> layerProcessors = new Dictionary<string, MetaLayerProcessor>();

        enum Stage
        {
            LoadFile,
            GenerateAtlas,
            GenerateClips,
            GenerateController,
            GeneratePrefab,
            InvokeMetaLayerProcessor
        }

        static float GetProgress(this Stage stage)
        {
            return (float)(int)stage / Enum.GetValues(typeof(Stage)).Length;
        }

        static string GetDisplayString(this Stage stage)
        {
            return stage.ToString();
        }

        public static void Refresh()
        {
            layerProcessors.Clear();
            var processorTypes = FindAllTypes(typeof(MetaLayerProcessor));
            // Debug.Log("Found " + processorTypes.Length + " layer processor(s).");
            foreach (var type in processorTypes)
            {
                if (type.IsAbstract) continue;
                try
                {
                    var instance = (MetaLayerProcessor)type.GetConstructor(new Type[0]).Invoke(new object[0]);
                    if (layerProcessors.ContainsKey(instance.actionName))
                    {
                        Debug.LogError(string.Format("Duplicate processor with name {0}: {1}", instance.actionName, instance));
                    }
                    else
                    {
                        layerProcessors.Add(instance.actionName, instance);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError("Can't instantiate meta processor " + type);
                    Debug.LogException(ex);
                }
            }
        }

        static Type[] FindAllTypes(Type interfaceType)
        {
            var types = System.Reflection.Assembly.GetExecutingAssembly()
                .GetTypes();
            return types.Where(type => type.IsClass && interfaceType.IsAssignableFrom(type))
                        .ToArray();
        }

        struct LayerAndProcessor
        {
            public Layer layer;
            public MetaLayerProcessor processor;
        }


        public static void Import(DefaultAsset defaultAsset, ImportSettings settings)
        {

            var path = AssetDatabase.GetAssetPath(defaultAsset);

            var context = new ImportContext
            {
                // file = file,
                settings = settings,
                fileDirectory = Path.GetDirectoryName(path),
                fileName = Path.GetFileName(path),
                fileNameNoExt = Path.GetFileNameWithoutExtension(path)
            };

            try
            {
                ImportStage(context, Stage.LoadFile);
                context.file = ASEParser.Parse(File.ReadAllBytes(path));
                context.atlasPath = Path.Combine(settings.atlasOutputDirectory + "/" + context.fileNameNoExt + ".png");//!
                context.prefabDirectory = Path.Combine(settings.prefabsDirectory, context.fileNameNoExt + ".prefab");

                if (settings.controllerPolicy == AnimControllerOutputPolicy.CreateOrOverride)
                    context.animControllerPath = settings.animControllerOutputPath + "/" + context.fileNameNoExt + ".controller";
                context.animClipDirectory = settings.clipOutputDirectory;

                // Create paths in advance
                Directory.CreateDirectory(settings.atlasOutputDirectory);
                Directory.CreateDirectory(context.animClipDirectory);
                if (context.animControllerPath != null)
                    Directory.CreateDirectory(Path.GetDirectoryName(context.animControllerPath));
                if (settings.generatePrefab)
                    Directory.CreateDirectory(settings.prefabsDirectory);
                //

                ImportStage(context, Stage.GenerateAtlas);
                foreach (var group in context.file.mapGroup.Values)
                {
                    if (group.layers.Count == 0) continue;
                    string atlasPath = Path.Combine(settings.atlasOutputDirectory, context.fileNameNoExt + "_" + group.Name + ".png");
                    var sprites = AtlasGenerator.GenerateAtlas(context,
                        group.layers.Where(it => it.type == LayerType.Content).ToList(),
                        atlasPath);
                    context.mapSprite.Add(group.Name, sprites);
                }

                ImportStage(context, Stage.GenerateClips);
                GenerateAnimClips(context);

                ImportStage(context, Stage.GenerateController);
                GenerateAnimController(context);

                if (settings.generatePrefab)
                {
                    ImportStage(context, Stage.GeneratePrefab);
                    GeneratePrefab(context);
                }

                ImportStage(context, Stage.InvokeMetaLayerProcessor);
                context.file.layers.Values
                    .Where(layer => layer.type == LayerType.Meta)
                    .Select(layer =>
                    {
                        MetaLayerProcessor processor;
                        layerProcessors.TryGetValue(layer.actionName, out processor);
                        return new LayerAndProcessor { layer = layer, processor = processor };
                    })
                    .OrderBy(it => it.processor != null ? it.processor.executionOrder : 0)
                    .ToList()
                    .ForEach(it =>
                    {
                        var layer = it.layer;
                        var processor = it.processor;
                        if (processor != null)
                        {
                            processor.Process(context, layer);
                        }
                        else
                        {
                            Debug.LogWarning(string.Format("No processor for meta layer {0}", layer.layerName));
                        }
                    });

                if (context.rootGameObject != null)
                    UnityEngine.Object.DestroyImmediate(context.rootGameObject);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }

            ImportEnd(context);
        }

        static void ImportStage(ImportContext ctx, Stage stage)
        {
            EditorUtility.DisplayProgressBar("Importing " + ctx.fileName, stage.GetDisplayString(), stage.GetProgress());
        }

        static void ImportEnd(ImportContext ctx)
        {
            EditorUtility.ClearProgressBar();
        }

        public static void GenerateClipImageLayer(ImportContext ctx, string childPath, List<Sprite> frameSprites)
        {
            foreach (var tag in ctx.file.frameTags)
            {
                AnimationClip clip = ctx.generatedClips[tag];

                int time = 0;
                var keyFrames = new ObjectReferenceKeyframe[tag.to - tag.from + 2];
                for (int i = tag.from; i <= tag.to; ++i)
                {
                    var aseFrame = ctx.file.frames[i];
                    keyFrames[i - tag.from] = new ObjectReferenceKeyframe
                    {
                        time = time * 1e-3f,
                        value = frameSprites[aseFrame.frameID]
                    };

                    time += aseFrame.duration;
                }

                keyFrames[keyFrames.Length - 1] = new ObjectReferenceKeyframe
                {
                    time = time * 1e-3f - 1.0f / clip.frameRate,
                    value = frameSprites[tag.to]
                };

                var binding = new EditorCurveBinding
                {
                    path = childPath,
                    type = typeof(SpriteRenderer),
                    propertyName = "m_Sprite"
                };

                AnimationUtility.SetObjectReferenceCurve(clip, binding, keyFrames);
            }
        }

        static void GenerateAnimClips(ImportContext ctx)
        {
            Directory.CreateDirectory(ctx.animClipDirectory);
            var fileNamePrefix = ctx.animClipDirectory + '/' + ctx.fileNameNoExt;

            // Generate one animation for each tag
            foreach (var tag in ctx.file.frameTags)
            {
                var clipPath = fileNamePrefix + '_' + tag.name + ".anim";
                AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);

                // Create clip
                if (!clip)
                {
                    clip = new AnimationClip();
                    AssetDatabase.CreateAsset(clip, clipPath);
                }
                else
                {
                    AnimationUtility.SetAnimationEvents(clip, new AnimationEvent[0]);
                }

                // Set loop property
                var loop = tag.properties.Contains("loop");
                var settings = AnimationUtility.GetAnimationClipSettings(clip);
                if (loop)
                {
                    clip.wrapMode = WrapMode.Loop;
                    settings.loopBlend = true;
                    settings.loopTime = true;
                }
                else
                {
                    clip.wrapMode = WrapMode.Clamp;
                    settings.loopBlend = false;
                    settings.loopTime = false;
                }
                AnimationUtility.SetAnimationClipSettings(clip, settings);

                EditorUtility.SetDirty(clip);
                ctx.generatedClips.Add(tag, clip);
            }

            // Generate main image
            foreach (var group in ctx.file.mapGroup.Values)
            {
                if (group.layers.Count == 0) continue;
                GenerateClipImageLayer(ctx, group.Path, ctx.mapSprite[group.Name]);
            }
        }

        static void GenerateAnimController(ImportContext ctx)
        {
            if (ctx.animControllerPath == null)
            {
                Debug.LogWarning("No animator controller specified. Controller generation will be ignored");
                return;
            }

            ctx.controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ctx.animControllerPath);
            if (!ctx.controller)
            {
                ctx.controller = AnimatorController.CreateAnimatorControllerAtPath(ctx.animControllerPath);
            }

            var layer = ctx.controller.layers[0];
            var stateMap = new Dictionary<string, AnimatorState>();
            PopulateStateTable(stateMap, layer.stateMachine);

            foreach (var pair in ctx.generatedClips)
            {
                var frameTag = pair.Key;
                var clip = pair.Value;

                AnimatorState st;
                stateMap.TryGetValue(frameTag.name, out st);
                if (!st)
                {
                    st = layer.stateMachine.AddState(frameTag.name);
                }

                st.motion = clip;
            }

            EditorUtility.SetDirty(ctx.controller);
        }

        static void GeneratePrefab(ImportContext ctx)
        {
            ctx.rootGameObject = new GameObject(ctx.fileNameNoExt);
            ctx.name2GameObject.Add(ctx.fileNameNoExt, ctx.rootGameObject);
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ctx.animControllerPath);
            ctx.rootGameObject.AddComponent<Animator>().runtimeAnimatorController = controller;

            var mapGroup = ctx.file.mapGroup;
            foreach (var group in mapGroup.Values)
            {
                var gameObject = new GameObject(group.Name);
                if (group.Name == "Sprites")
                {
                    gameObject.transform.parent = ctx.rootGameObject.transform;
                }
                else
                {
                    var father = ctx.name2GameObject[mapGroup[group.parent.index].Name];
                    gameObject.transform.parent = father.transform;
                }
                ctx.name2GameObject.Add(group.Name, gameObject);

                if (group.layers.Count != 0)
                {
                    var sr = gameObject.AddComponent<SpriteRenderer>();
                    sr.sprite = ctx.mapSprite[group.Name][0];
                    var a = gameObject.transform.position;
                    a.z = -group.index * 0.01f;
                    gameObject.transform.position = a;
                    sr.sortingOrder = group.index * ctx.settings.orderInLayerInterval;
                    sr.sortingLayerID = ctx.settings.spritesSortInLayer;
                }
            }

            PrefabUtility.SaveAsPrefabAssetAndConnect(ctx.rootGameObject, ctx.prefabDirectory, InteractionMode.UserAction);
        }



        static void PopulateStateTable(Dictionary<string, AnimatorState> table, AnimatorStateMachine machine)
        {
            foreach (var state in machine.states)
            {
                var name = state.state.name;
                if (table.ContainsKey(name))
                {
                    Debug.LogWarning("Duplicate state with name " + name + " in animator controller. Behaviour is undefined.");
                }
                else
                {
                    table.Add(name, state.state);
                }
            }

            foreach (var subMachine in machine.stateMachines)
            {
                PopulateStateTable(table, subMachine.stateMachine);
            }
        }

    }

}