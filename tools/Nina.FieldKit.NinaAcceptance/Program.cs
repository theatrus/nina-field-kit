using System.IO;
using System.Runtime.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NINA.Core.Enum;
using NINA.Profile;

// Generate configuration only. The installed NINA host executes every instruction and hardware call.
if (args.Length is < 1 or > 2 || (args.Length == 2 && args[1] != "--twilight-filters"))
    throw new ArgumentException("Specify a fresh output directory and optional --twilight-filters.");
var twilightFilters = args.Length == 2;
var output = Path.GetFullPath(args[0]);
if (Directory.Exists(output)) throw new IOException("Use a fresh output directory for each simulator run.");
Directory.CreateDirectory(output);
var profile = new Profile(twilightFilters ? "Field Kit twilight acceptance (simulators only)" : "Field Kit rotation acceptance (simulators only)");
profile.CameraSettings.Id = "4C0BBF74-0D95-41F6-AAD8-D6D58668CF2C";
profile.RotatorSettings.Id = "ASCOM.Simulator.Rotator";
if (twilightFilters) {
    profile.FilterWheelSettings.Id = "ASCOM.Simulator.FilterWheel";
    profile.FilterWheelSettings.FilterWheelFilters.Add(new() { Name = "L", Position = 0 });
    profile.FilterWheelSettings.FilterWheelFilters.Add(new() { Name = "R", Position = 1 });
    var simulator = Guid.Parse("65CA9FD7-8984-4609-B206-86F3F9E8C19D");
    profile.PluginSettings.SetValue(simulator, "ImageMean", 32768);
    profile.PluginSettings.SetValue(simulator, "ImageStdDev", 50);
    profile.PluginSettings.SetValue(simulator, "ImageWidth", 256);
    profile.PluginSettings.SetValue(simulator, "ImageHeight", 256);
}
profile.ApplicationSettings.LogLevel = LogLevelEnum.DEBUG;
profile.ImageFileSettings.FilePath = Path.Combine(output, "frames");
profile.ImageFileSettings.FilePattern = "$$IMAGETYPE$$\\$$DATETIME$$_$$FRAMENR$$";
profile.ImageFileSettings.FileType = FileTypeEnum.FITS;
Directory.CreateDirectory(profile.ImageFileSettings.FilePath);
var profilePath = Path.Combine(output, profile.Id + ".profile");
using(var stream = File.Create(profilePath)) new DataContractSerializer(typeof(Profile)).WriteObject(stream, profile);
int counter = 0;
JObject Item(string type, JObject parent, params (string,object)[] properties) {
    var item = new JObject { ["$id"] = (++counter).ToString(), ["$type"] = "NINA.Sequencer.SequenceItem." + type + ", NINA.Sequencer", ["Parent"] = new JObject { ["$ref"] = parent["$id"]!.ToString() }, ["Attempts"] = 1, ["ErrorBehavior"] = 2 };
    foreach(var (key,value) in properties) item[key] = JToken.FromObject(value);
    ((JArray)parent["Items"]!).Add(item); return item;
}
JObject Container(string type, JObject? parent = null, bool custom = false) {
    var customType = twilightFilters ? "TwilightFlatFiltersContainer" : "SkyFlatRotationsContainer";
    var item = new JObject { ["$id"] = (++counter).ToString(), ["$type"] = custom ? "Nina.FieldKit.Plugin." + customType + ", Nina.FieldKit.Plugin" : "NINA.Sequencer.Container." + type + ", NINA.Sequencer", ["Name"] = type, ["Strategy"] = new JObject { ["$type"] = custom ? "Nina.FieldKit.Plugin.FlatRotationStrategy, Nina.FieldKit.Plugin" : "NINA.Sequencer.Container.ExecutionStrategy.SequentialStrategy, NINA.Sequencer" }, ["Items"] = new JArray(), ["Conditions"] = new JArray(), ["Triggers"] = new JArray(), ["Attempts"] = 1, ["ErrorBehavior"] = 2 };
    if(parent != null) { item["Parent"] = new JObject { ["$ref"] = parent["$id"]!.ToString() }; ((JArray)parent["Items"]!).Add(item); }
    return item;
}
JObject Exposure(JObject parent,string type) => Item("Imaging.TakeExposure",parent,("ExposureTime",0.2),("ImageType",type),("Gain",-1),("Offset",-1),("Binning",new JObject{["$type"]="NINA.Core.Model.Equipment.BinningMode, NINA.Core",["X"]=1,["Y"]=1}));
var root = Container("SequenceRootContainer");
var startup = Container("StartAreaContainer", root);
Item("Connect.ConnectEquipment", startup, ("SelectedDevice","Camera"));
Item("Connect.ConnectEquipment", startup, ("SelectedDevice","Rotator"));
if (twilightFilters) Item("Connect.ConnectEquipment", startup, ("SelectedDevice","Filter Wheel"));
var targets = Container("TargetAreaContainer",root);
foreach(var angle in new[]{12.0,12.04,192.0,45.0}) {
    Item("Rotator.MoveRotatorMechanical",targets,("MechanicalPosition",angle));
    Exposure(targets,"LIGHT");
    Item("Utility.WaitForTimeSpan",targets,("Time",3));
}
Item("Utility.WaitForTimeSpan",targets,("Time",5));
var flats = Container(twilightFilters ? "Twilight flats by filter and rotation" : "Sky flats at captured rotations",targets,true);
flats["ToleranceDegrees"]=0.1; flats["MoveTimeoutSeconds"]=180;
if (twilightFilters) {
    flats["FilterOrder"] = new JArray(new JObject { ["Name"]="R", ["Position"]=1, ["Enabled"]=true }, new JObject { ["Name"]="L", ["Position"]=0, ["Enabled"]=true });
    var skyFlat = Container("Twilight Sky Flats", flats);
    skyFlat["$type"] = "NINA.Sequencer.SequenceItem.FlatDevice.SkyFlat, NINA.Sequencer";
    skyFlat["MinExposure"] = .05; skyFlat["MaxExposure"] = .2;
    skyFlat["HistogramTargetPercentage"] = .5; skyFlat["HistogramTolerancePercentage"] = .1; skyFlat["ShouldDither"] = false;
    Item("Utility.Annotation", skyFlat); Item("Utility.Annotation", skyFlat);
    Item("FilterWheel.SwitchFilter", skyFlat, ("Filter", JObject.FromObject(profile.FilterWheelSettings.FilterWheelFilters[0])));
    Item("Utility.Annotation", skyFlat);
    var nativeImaging = Container("SequentialContainer", skyFlat);
    ((JArray)nativeImaging["Conditions"]!).Add(new JObject { ["$type"]="NINA.Sequencer.Conditions.LoopCondition, NINA.Sequencer", ["Iterations"]=1, ["Parent"]=new JObject { ["$ref"]=nativeImaging["$id"]!.ToString() } });
    Exposure(nativeImaging, "FLAT");
} else {
    var nested = Container("SequentialContainer",flats);
    ((JArray)nested["Conditions"]!).Add(new JObject{["$type"]="NINA.Sequencer.Conditions.LoopCondition, NINA.Sequencer",["Iterations"]=2,["Parent"]=new JObject{["$ref"]=nested["$id"]!.ToString()}});
    Exposure(nested,"FLAT");
}
Item("Utility.WaitForTimeSpan",targets,("Time",5));
var shutdown=Container("EndAreaContainer",root);
Item("Connect.DisconnectEquipment",shutdown,("SelectedDevice","Rotator"));
if (twilightFilters) Item("Connect.DisconnectEquipment", shutdown, ("SelectedDevice", "Filter Wheel"));
Item("Connect.DisconnectEquipment",shutdown,("SelectedDevice","Camera"));
File.WriteAllText(Path.Combine(output,"acceptance.json"),root.ToString(Formatting.Indented));
var restart = (JObject)root.DeepClone();
var restartItems = (JArray)restart["Items"]![1]!["Items"]!;
while (restartItems.First?["$id"]?.ToString() != flats["$id"]!.ToString()) restartItems.First!.Remove();
File.WriteAllText(Path.Combine(output,"acceptance-restart.json"),restart.ToString(Formatting.Indented));
File.WriteAllText(Path.Combine(output,"run.json"),JsonConvert.SerializeObject(new{profile.Id,ProfilePath=profilePath,SequencePath=Path.Combine(output,"acceptance.json"),Frames=profile.ImageFileSettings.FilePath,TwilightFilters=twilightFilters},Formatting.Indented));
Console.WriteLine(File.ReadAllText(Path.Combine(output,"run.json")));
