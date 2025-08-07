using Newtonsoft.Json.Linq;
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

    // OnInit is called when the extension is loaded
    public override void OnInit()
    {
        try{
            SaveFirstFrameParam = T2IParamTypes.Register<bool>(new(
                Name: "Save First Frame",
                Default: "false",
                Description: "When enabled, the first frame of the video will be saved and output",
                OrderPriority: 30,
                Group: T2IParamTypes.GroupOtherFixes,
                IgnoreIf: "false"
            ));
            SaveLastFrameParam = T2IParamTypes.Register<bool>(new(
                Name: "Save Last Frame",
                Default: "false",
                Description: "When enabled, the last frame of the video will be saved and output",
                OrderPriority: 31,
                Group: T2IParamTypes.GroupOtherFixes,
                IgnoreIf: "false"
            ));
            SaveFramesStartParam = T2IParamTypes.Register<int>(new(
                Name: "Save Frames Start",
                Default: "-1",
                Description: "The first frame in the range of frames to save and output. Each frame between this frame and Extract Frames End (inclusive) will be output and saved. Must be less than or equal to Extract Frames End.",
                OrderPriority: 32,
                Group: T2IParamTypes.GroupOtherFixes,
                Max: 1000000
            ));
            SaveFramesEndParam = T2IParamTypes.Register<int>(new(
                Name: "Save Frames End",
                Default: "-1",
                Description: "The last frame in the range of frames to save and output. Each frame between this frame and Extract Frames Start (inclusive) will be output and saved. Must be greater than or equal to Extract Frames Start.",
                OrderPriority: 33,
                Group: T2IParamTypes.GroupOtherFixes,
                Max: 1000000
            ));
        }
        catch (Exception ex)
        {
            Logs.Error($"FrameSaver Extension Init Error: {ex.Message}");
        }

        // Add the workflow modification step
        WorkflowGenerator.AddStep(g => 
        {
            string lastVAEDecodeId = null;
            // Add nodes to save the first and last frames if enabled
            if (g.UserInput.Get(SaveLastFrameParam, false) || g.UserInput.Get(SaveFirstFrameParam, false))
            {
                //Get the last VAEDecode node
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
                    // Create GetImageFromBatch node to extract the first frame
                    string getFirstImageNode = g.CreateNode("ImageFromBatch", new JObject() {
                        ["batch_index"] = 0,
                        ["length"] = 1,
                        ["image"] = new JArray { lastVAEDecodeId, 0 }
                    });

                    // Create SwarmSaveImageWS node to save the extracted frame
                    var saveFirstImageNode = g.CreateImageSaveNode([getFirstImageNode, 0], g.GetStableDynamicID(50000, 0));
                }

                if (g.UserInput.Get(SaveLastFrameParam, false))
                {
                    // Create SwarmCountFrames node to get the number of frames
                    string frameCountNode = g.CreateNode("SwarmCountFrames", new JObject() {
                        ["image"] = g.FinalImageOut
                    });
                    JArray frameCount = [frameCountNode, 0];

                    // Create GetImageFromBatch node to extract the last frame
                    string getLastImageNode = g.CreateNode("ImageFromBatch", new JObject() {
                        ["batch_index"] = frameCount,
                        ["length"] = 1,
                        ["image"] = new JArray { lastVAEDecodeId, 0 }
                    });

                    // Create SwarmSaveImageWS node to save the extracted frame
                    var saveLastImageNode = g.CreateImageSaveNode([getLastImageNode, 0], g.GetStableDynamicID(50001, 0));
                }
            }

            //Add nodes to extract a range of frames if specified
            var frameExtractStart = g.UserInput.Get(SaveFramesStartParam, -1);
            var frameExtractEnd = g.UserInput.Get(SaveFramesEndParam, -1);
            if (frameExtractEnd >= 0 && frameExtractStart >= 0 && frameExtractStart <= frameExtractEnd)
            {
                // If VAE Decode nodes were not previously found, try to find them now
                if (lastVAEDecodeId == null)
                {
                    //Get the last VAEDecode node
                    var VAEDecodeNodes = g.NodesOfClass("VAEDecode");
                    var lastVAEDecodeNode = VAEDecodeNodes.LastOrDefault();
                    if (lastVAEDecodeNode == null)
                    {
                        Logs.Error("FrameSaverExtension: No VAE Decode nodes found to extract frames from.");
                        return;
                    }
                    lastVAEDecodeId = lastVAEDecodeNode?.Name;
                }

                // Create GetImageFromBatch node to extract the specified frames
                string getImageNode = g.CreateNode("ImageFromBatch", new JObject() {
                    ["batch_index"] = frameExtractStart,
                    ["length"] = (frameExtractEnd-frameExtractStart)+1,
                    ["image"] = new JArray { lastVAEDecodeId, 0 }
                });

                // Create SwarmSaveImageWS node to save the extracted frames
                var saveImageNode = g.CreateNode("SwarmSaveImageWS", new JObject() {
                    ["images"] = new JArray { getImageNode, 0 }
                }, g.GetStableDynamicID(50002, 0));
            }
        }, 20);
    }
}