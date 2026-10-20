using System.IO;
using System.Runtime.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NINA.Core.Enum;
using NINA.Profile;

// Generate configuration only. The installed NINA host executes every instruction and hardware call.
if (args.Length != 1) throw new ArgumentException("Specify a fresh output directory.");
var output = Path.GetFullPath(args[0]);
if (Directory.Exists(output)) throw new IOException("Use a fresh output directory for each simulator run.");
Directory.CreateDirectory(output);
var profile = new Profile("Field Kit rotation acceptance (simulators only)");
profile.CameraSettings.Id = "4C0BBF74-0D95-41F6-AAD8-D6D58668CF2C";
profile.RotatorSettings.Id = "ASCOM.Simulator.Rotator";
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
    var item = new JObject { ["$id"] = (++counter).ToString(), ["$type"] = custom ? "Nina.FieldKit.Plugin.SkyFlatRotationsContainer, Nina.FieldKit.Plugin" : "NINA.Sequencer.Container." + type + ", NINA.Sequencer", ["Name"] = type, ["Strategy"] = new JObject { ["$type"] = custom ? "Nina.FieldKit.Plugin.FlatRotationStrategy, Nina.FieldKit.Plugin" : "NINA.Sequencer.Container.ExecutionStrategy.SequentialStrategy, NINA.Sequencer" }, ["Items"] = new JArray(), ["Conditions"] = new JArray(), ["Triggers"] = new JArray(), ["Attempts"] = 1, ["ErrorBehavior"] = 2 };
    if(parent != null) { item["Parent"] = new JObject { ["$ref"] = parent["$id"]!.ToString() }; ((JArray)parent["Items"]!).Add(item); }
    return item;
}
JObject Exposure(JObject parent,string type) => Item("Imaging.TakeExposure",parent,("ExposureTime",0.2),("ImageType",type),("Gain",-1),("Offset",-1),("Binning",new JObject{["$type"]="NINA.Core.Model.Equipment.BinningMode, NINA.Core",["X"]=1,["Y"]=1}));
var root = Container("SequenceRootContainer");
var startup = Container("StartAreaContainer", root);
Item("Connect.ConnectEquipment", startup, ("SelectedDevice","Camera"));
Item("Connect.ConnectEquipment", startup, ("SelectedDevice","Rotator"));
var targets = Container("TargetAreaContainer",root);
foreach(var angle in new[]{12.0,12.04,192.0,45.0}) {
    Item("Rotator.MoveRotatorMechanical",targets,("MechanicalPosition",angle));
    Exposure(targets,"LIGHT");
    Item("Utility.WaitForTimeSpan",targets,("Time",3));
}
Item("Utility.WaitForTimeSpan",targets,("Time",5));
var flats = Container("Sky flats at captured rotations",targets,true);
flats["ToleranceDegrees"]=0.1; flats["MoveTimeoutSeconds"]=180;
var nested = Container("SequentialContainer",flats);
((JArray)nested["Conditions"]!).Add(new JObject{["$type"]="NINA.Sequencer.Conditions.LoopCondition, NINA.Sequencer",["Iterations"]=2,["Parent"]=new JObject{["$ref"]=nested["$id"]!.ToString()}});
Exposure(nested,"FLAT");
Item("Utility.WaitForTimeSpan",targets,("Time",5));
var shutdown=Container("EndAreaContainer",root);
Item("Connect.DisconnectEquipment",shutdown,("SelectedDevice","Rotator"));
Item("Connect.DisconnectEquipment",shutdown,("SelectedDevice","Camera"));
File.WriteAllText(Path.Combine(output,"acceptance.json"),root.ToString(Formatting.Indented));
var restart = (JObject)root.DeepClone();
var restartItems = (JArray)restart["Items"]![1]!["Items"]!;
while (restartItems.First?["$id"]?.ToString() != flats["$id"]!.ToString()) restartItems.First!.Remove();
File.WriteAllText(Path.Combine(output,"acceptance-restart.json"),restart.ToString(Formatting.Indented));
File.WriteAllText(Path.Combine(output,"run.json"),JsonConvert.SerializeObject(new{profile.Id,ProfilePath=profilePath,SequencePath=Path.Combine(output,"acceptance.json"),Frames=profile.ImageFileSettings.FilePath},Formatting.Indented));
Console.WriteLine(File.ReadAllText(Path.Combine(output,"run.json")));
