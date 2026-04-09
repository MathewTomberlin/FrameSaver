using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

// NOTE: Namespace must NOT contain "SwarmUI" (this is reserved for built-ins)
namespace SwarmExtensions.FrameSaver;

// NOTE: Classname must match filename
public class FrameSaver : Extension
{
    public static T2IRegisteredParam<bool> SaveLastFrameParam, SaveFirstFrameParam;
    public static T2IRegisteredParam<int> SaveFramesStartParam, SaveFramesEndParam;
    public static T2IRegisteredParam<string> CustomFramesOutputDirectoryParam;

    public static T2IParamGroup FrameSaverParamGroup;

    static bool _handlersRegistered;

    public override void OnPreInit()
    {
        string comfyNodes = Path.GetFullPath(Utilities.CombinePathWithAbsolute(Environment.CurrentDirectory, $"{FilePath}ComfyNodes"));
        if (Directory.Exists(comfyNodes))
        {
            ComfyUISelfStartBackend.CustomNodePaths.Add(comfyNodes);
            Logs.Init($"FrameSaver: added {comfyNodes} to ComfyUI custom node paths");
        }
        else
        {
            Logs.Warning($"FrameSaver: ComfyNodes folder not found at '{comfyNodes}' — custom directory saves will not work until it exists.");
        }
    }

    public override void OnInit()
    {
        if (!_handlersRegistered)
        {
            _handlersRegistered = true;
            ComfyUIAPIAbstractBackend.AltCustomMetadataHandlers.Add(HandleFramesaverMetadata);
            T2IEngine.PostBatchEvent += OnPostBatch;
        }

        FrameSaverParamGroup = new("Frame Saver", Toggles: false, Open: false, IsAdvanced: true, Parent: T2IParamTypes.GroupAdvancedVideo);

        try
        {
            CustomFramesOutputDirectoryParam = T2IParamTypes.Register<string>(new(
                Name: "Custom Frames Output Folder",
                Default: "",
                Description: "Optional absolute or Swarm-root-relative folder where extracted frames are saved. Leave empty to use default output path.",
                OrderPriority: 29,
                Group: FrameSaverParamGroup,
                IgnoreIf: "",
                ViewType: ParamViewType.BIG,
                FeatureFlag: "comfyui"
            ));
            SaveFirstFrameParam = T2IParamTypes.Register<bool>(new(
                Name: "Save First Frame",
                Default: "false",
                Description: "When enabled, the first frame of the video will be saved and output",
                OrderPriority: 30,
                Group: FrameSaverParamGroup,
                IgnoreIf: "false"
            ));
            SaveLastFrameParam = T2IParamTypes.Register<bool>(new(
                Name: "Save Last Frame",
                Default: "false",
                Description: "When enabled, the last frame of the video will be saved and output",
                OrderPriority: 31,
                Group: FrameSaverParamGroup,
                IgnoreIf: "false"
            ));
            SaveFramesStartParam = T2IParamTypes.Register<int>(new(
                Name: "Save Frames Start",
                Default: "-1",
                Description: "The first frame in the range of frames to save and output. Each frame between this frame and Extract Frames End (inclusive) will be output and saved. Must be less than or equal to Extract Frames End.",
                OrderPriority: 32,
                Group: FrameSaverParamGroup,
                Max: 1000000,
                Min: -1
            ));
            SaveFramesEndParam = T2IParamTypes.Register<int>(new(
                Name: "Save Frames End",
                Default: "-1",
                Description: "The last frame in the range of frames to save and output. Each frame between this frame and Extract Frames Start (inclusive) will be output and saved. Must be greater than or equal to Extract Frames Start.",
                OrderPriority: 33,
                Group: FrameSaverParamGroup,
                Max: 1000000,
                Min: -1
            ));
        }
        catch (Exception ex)
        {
            Logs.Error($"FrameSaver Extension Init Error: {ex.Message}");
        }

        WorkflowGenerator.AddStep(g =>
        {
            string pathB64 = null;
            if (g.UserInput.TryGet(CustomFramesOutputDirectoryParam, out string rawDir) && !string.IsNullOrWhiteSpace(rawDir))
            {
                string abs = ResolveOutputDirectory(rawDir.Trim());
                if (abs is null)
                {
                    Logs.Error($"FrameSaver: invalid Custom Frames Output Folder '{rawDir}'.");
                }
                else
                {
                    try
                    {
                        Directory.CreateDirectory(abs);
                        pathB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(abs));
                    }
                    catch (Exception ex)
                    {
                        Logs.Error($"FrameSaver: could not use output folder '{abs}': {ex.Message}");
                    }
                }
            }

            string lastVAEDecodeId = null;
            if (g.UserInput.Get(SaveLastFrameParam, false) || g.UserInput.Get(SaveFirstFrameParam, false))
            {
                var VAEDecodeNodes = g.NodesOfClass("VAEDecode");
                var lastVAEDecodeNode = VAEDecodeNodes.LastOrDefault();
                if (lastVAEDecodeNode == null)
                {
                    Logs.Error("FrameSaverExtension: No VAE Decode nodes found to extract frames from.");
                    return;
                }
                lastVAEDecodeId = lastVAEDecodeNode?.Name;

                if (g.UserInput.Get(SaveFirstFrameParam, false))
                {
                    string getFirstImageNode = g.CreateNode("ImageFromBatch", new JObject()
                    {
                        ["batch_index"] = 0,
                        ["length"] = 1,
                        ["image"] = new JArray { lastVAEDecodeId, 0 }
                    });
                    EmitFrameImageSave(g, [getFirstImageNode, 0], g.GetStableDynamicID(50000, 0), pathB64);
                }

                if (g.UserInput.Get(SaveLastFrameParam, false))
                {
                    JArray countFramesSource = g.CurrentMedia?.AsRawImage(g.CurrentVae)?.Path ?? WorkflowGenerator.NodePath(lastVAEDecodeId, 0);
                    string frameCountNode = g.CreateNode("SwarmCountFrames", new JObject()
                    {
                        ["image"] = countFramesSource
                    });
                    JArray frameCount = [frameCountNode, 0];

                    string getLastImageNode = g.CreateNode("ImageFromBatch", new JObject()
                    {
                        ["batch_index"] = frameCount,
                        ["length"] = 1,
                        ["image"] = new JArray { lastVAEDecodeId, 0 }
                    });
                    EmitFrameImageSave(g, [getLastImageNode, 0], g.GetStableDynamicID(50001, 0), pathB64);
                }
            }

            var frameExtractStart = g.UserInput.Get(SaveFramesStartParam, -1);
            var frameExtractEnd = g.UserInput.Get(SaveFramesEndParam, -1);
            if (frameExtractEnd >= 0 && frameExtractStart >= 0 && frameExtractStart <= frameExtractEnd)
            {
                if (lastVAEDecodeId == null)
                {
                    var VAEDecodeNodes = g.NodesOfClass("VAEDecode");
                    var lastVAEDecodeNode = VAEDecodeNodes.LastOrDefault();
                    if (lastVAEDecodeNode == null)
                    {
                        Logs.Error("FrameSaverExtension: No VAE Decode nodes found to extract frames from.");
                        return;
                    }
                    lastVAEDecodeId = lastVAEDecodeNode?.Name;
                }

                string getImageNode = g.CreateNode("ImageFromBatch", new JObject()
                {
                    ["batch_index"] = frameExtractStart,
                    ["length"] = (frameExtractEnd - frameExtractStart) + 1,
                    ["image"] = new JArray { lastVAEDecodeId, 0 }
                });
                EmitFrameImageSave(g, [getImageNode, 0], g.GetStableDynamicID(50002, 0), pathB64);
            }
        }, 20);
    }

    static void EmitFrameImageSave(WorkflowGenerator g, JArray imagesSocket, string nodeId, string pathB64)
    {
        if (pathB64 is not null)
        {
            g.CreateNode("FrameSaverSaveImageWithPathWS", new JObject()
            {
                ["images"] = imagesSocket,
                ["path_b64"] = pathB64,
                ["bit_depth"] = g.UserInput.Get(T2IParamTypes.BitDepth, "8bit")
            }, nodeId);
        }
        else
        {
            _ = new WGNodeData(imagesSocket, g, WGNodeData.DT_IMAGE, g.CurrentCompat()).SaveOutput(null, null, id: nodeId);
        }
    }

    static string ResolveOutputDirectory(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        try
        {
            string combined = Path.IsPathRooted(raw)
                ? raw
                : Utilities.CombinePathWithAbsolute(Environment.CurrentDirectory, raw);
            return Path.GetFullPath(combined);
        }
        catch
        {
            return null;
        }
    }

    static string VirtualDirKey(T2IParamInput input) => $"_fs_{input.UserRequestId}/";

    static bool HandleFramesaverMetadata(T2IParamInput user_input, string keyRaw, string valueRaw)
    {
        string key = keyRaw.Trim();
        if (key == "framesaver_push64")
        {
            return TryFramesaverPush(user_input, valueRaw);
        }
        if (key == "framesaver_pop")
        {
            return TryFramesaverPop(user_input);
        }
        return false;
    }

    static bool TryFramesaverPush(T2IParamInput user_input, string valueB64)
    {
        if (user_input?.SourceSession?.User is null)
        {
            return true;
        }
        try
        {
            string abs = Encoding.UTF8.GetString(Convert.FromBase64String(valueB64.Trim()));
            abs = Path.GetFullPath(abs);
            Directory.CreateDirectory(abs);
            string vkey = VirtualDirKey(user_input);
            UserImageHistoryHelper.SharedSpecialFolders[vkey] = abs;
            string userFmt = user_input.SourceSession.User.Settings.OutPathBuilder.Format;
            string effectiveBefore = user_input.Get(T2IParamTypes.OverrideOutpathFormat, userFmt);
            user_input.ExtraMeta["framesaver_prev_fmt"] = effectiveBefore;
            user_input.Set(T2IParamTypes.OverrideOutpathFormat, $"{vkey}{effectiveBefore}");
        }
        catch (Exception ex)
        {
            Logs.Error($"FrameSaver: framesaver_push64 failed: {ex.ReadableString()}");
        }
        return true;
    }

    static bool TryFramesaverPop(T2IParamInput user_input)
    {
        if (user_input?.SourceSession?.User is null)
        {
            return true;
        }
        try
        {
            string vkey = VirtualDirKey(user_input);
            UserImageHistoryHelper.SharedSpecialFolders.TryRemove(vkey, out _);
            if (user_input.ExtraMeta.TryGetValue("framesaver_prev_fmt", out object prev) && prev is string s)
            {
                user_input.Set(T2IParamTypes.OverrideOutpathFormat, s);
                user_input.ExtraMeta.Remove("framesaver_prev_fmt");
            }
        }
        catch (Exception ex)
        {
            Logs.Error($"FrameSaver: framesaver_pop failed: {ex.ReadableString()}");
        }
        return true;
    }

    static void OnPostBatch(T2IEngine.PostBatchEventParams e)
    {
        if (e.UserInput is null)
        {
            return;
        }
        UserImageHistoryHelper.SharedSpecialFolders.TryRemove(VirtualDirKey(e.UserInput), out _);
        if (e.UserInput.ExtraMeta.TryGetValue("framesaver_prev_fmt", out object prev) && prev is string s)
        {
            e.UserInput.Set(T2IParamTypes.OverrideOutpathFormat, s);
            e.UserInput.ExtraMeta.Remove("framesaver_prev_fmt");
        }
    }
}
