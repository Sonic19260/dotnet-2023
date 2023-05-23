using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using CommandLine;
using Mapster.Common;
using Mapster.Common.MemoryMappedTypes;
using OSMDataParser;
using OSMDataParser.Elements;

namespace MapFeatureGenerator;

public static class Program
{
    private static MapData LoadOsmFile(ReadOnlySpan<char> osmFilePath)
    {
        var nodes = new ConcurrentDictionary<long, AbstractNode>();
        var ways = new ConcurrentBag<Way>();

        Parallel.ForEach(new PBFFile(osmFilePath), (blob, _) =>
        {
            switch (blob.Type)
            {
                case BlobType.Primitive:
                    {
                        var primitiveBlock = blob.ToPrimitiveBlock();
                        foreach (var primitiveGroup in primitiveBlock)
                            switch (primitiveGroup.ContainedType)
                            {
                                case PrimitiveGroup.ElementType.Node:
                                    foreach (var node in primitiveGroup) nodes[node.Id] = (AbstractNode)node;
                                    break;

                                case PrimitiveGroup.ElementType.Way:
                                    foreach (var way in primitiveGroup) ways.Add((Way)way);
                                    break;
                            }

                        break;
                    }
            }
        });

        var tiles = new Dictionary<int, List<long>>();
        foreach (var (id, node) in nodes)
        {
            var tileId = TiligSystem.GetTile(new Coordinate(node.Latitude, node.Longitude));
            if (tiles.TryGetValue(tileId, out var nodeIds))
            {
                nodeIds.Add(id);
            }
            else
            {
                tiles[tileId] = new List<long>
                {
                    id
                };
            }
        }

        return new MapData
        {
            Nodes = nodes.ToImmutableDictionary(),
            Tiles = tiles.ToImmutableDictionary(),
            Ways = ways.ToImmutableArray()
        };
    }

    private static void CreateMapDataFile(ref MapData mapData, string filePath)
    {
        var usedNodes = new HashSet<long>();

        long featureIdCounter = -1;
        var featureIds = new List<long>();
        // var geometryTypes = new List<GeometryType>();
        // var coordinates = new List<(long id, (int offset, List<Coordinate> coordinates) values)>();

        var labels = new List<int>();
        // var propKeys = new List<(long id, (int offset, IEnumerable<string> keys) values)>();
        // var propValues = new List<(long id, (int offset, IEnumerable<string> values) values)>();

        using var fileWriter = new BinaryWriter(File.OpenWrite(filePath));
        var offsets = new Dictionary<int, long>(mapData.Tiles.Count);

        // Write FileHeader
        fileWriter.Write((long)1); // FileHeader: Version
        fileWriter.Write(mapData.Tiles.Count); // FileHeader: TileCount

        // Write TileHeaderEntry
        foreach (var tile in mapData.Tiles)
        {
            fileWriter.Write(tile.Key); // TileHeaderEntry: ID
            fileWriter.Write((long)0); // TileHeaderEntry: OffsetInBytes
        }

        foreach (var (tileId, _) in mapData.Tiles)
        {
            // FIXME: Not thread safe
            usedNodes.Clear();

            // FIXME: Not thread safe
            featureIds.Clear();
            labels.Clear();

            var totalCoordinateCount = 0;
            var totalPropertyCount = 0;

            var featuresData = new Dictionary<long, FeatureData>();

            foreach (var way in mapData.Ways)
            {
                var featureId = Interlocked.Increment(ref featureIdCounter);

                var featureData = new FeatureData
                {
                    Id = featureId,
                    Coordinates = (totalCoordinateCount, new List<Coordinate>()),
                    PropertyKeys = (totalPropertyCount, new List<Keys>(way.Tags.Count)),
                    PropertyValues = (totalPropertyCount, new List<Values>(way.Tags.Count))
                };

                var geometryType = GeometryType.Polyline;

                labels.Add(-1);
                foreach (var tag in way.Tags)
                {
                    if (tag.Key == "name")
                    {
                        labels[^1] = totalPropertyCount * 2 + featureData.PropertyKeys.keys.Count * 2 + 1;
                    }
                    featureData.PropertyKeys.keys.Add(ConvertStringToUniqueKey(tag.Key));
                    featureData.PropertyValues.values.Add(ConvertStringToUniqueValue(tag.Key, tag.Value, featureData.PropertyKeys.keys[featureData.PropertyKeys.keys.Count - 1]));
                }

                foreach (var nodeId in way.NodeIds)
                {
                    var node = mapData.Nodes[nodeId];
                    if (TiligSystem.GetTile(new Coordinate(node.Latitude, node.Longitude)) != tileId)
                    {
                        continue;
                    }

                    usedNodes.Add(nodeId);

                    foreach (var (key, value) in node.Tags)
                    {
                        if (!featureData.PropertyKeys.keys.Contains(ConvertStringToUniqueKey(key)))
                        {
                            featureData.PropertyKeys.keys.Add(ConvertStringToUniqueKey(key));
                            featureData.PropertyValues.values.Add(ConvertStringToUniqueValue(key, value, featureData.PropertyKeys.keys[featureData.PropertyKeys.keys.Count - 1]));
                        }
                    }

                    featureData.Coordinates.coordinates.Add(new Coordinate(node.Latitude, node.Longitude));
                }

                // This feature is not located within this tile, skip it
                if (featureData.Coordinates.coordinates.Count == 0)
                {
                    // Remove the last item since we added it preemptively
                    labels.RemoveAt(labels.Count - 1);
                    continue;
                }

                if (featureData.Coordinates.coordinates[0] == featureData.Coordinates.coordinates[^1])
                {
                    geometryType = GeometryType.Polygon;
                }
                featureData.GeometryType = (byte)geometryType;

                totalPropertyCount += featureData.PropertyKeys.keys.Count;
                totalCoordinateCount += featureData.Coordinates.coordinates.Count;

                if (featureData.PropertyKeys.keys.Count != featureData.PropertyValues.values.Count)
                {
                    throw new InvalidDataContractException("Property keys and values should have the same count");
                }

                featureIds.Add(featureId);
                featuresData.Add(featureId, featureData);
            }

            foreach (var (nodeId, node) in mapData.Nodes.Where(n => !usedNodes.Contains(n.Key)))
            {
                if (TiligSystem.GetTile(new Coordinate(node.Latitude, node.Longitude)) != tileId)
                {
                    continue;
                }

                var featureId = Interlocked.Increment(ref featureIdCounter);

                var featurePropKeys = new List<Keys>();
                var featurePropValues = new List<Values>();

                labels.Add(-1);
                for (var i = 0; i < node.Tags.Count; ++i)
                {
                    var tag = node.Tags[i];
                    if (tag.Key == "name")
                    {
                        labels[^1] = totalPropertyCount * 2 + featurePropKeys.Count * 2 + 1;
                    }

                    featurePropKeys.Add(ConvertStringToUniqueKey(tag.Key));
                    featurePropValues.Add(ConvertStringToUniqueValue(tag.Key, tag.Value, featurePropKeys[featurePropKeys.Count - 1]));
                }

                if (featurePropKeys.Count != featurePropValues.Count)
                {
                    throw new InvalidDataContractException("Property keys and values should have the same count");
                }

                var fData = new FeatureData
                {
                    Id = featureId,
                    GeometryType = (byte)GeometryType.Point,
                    Coordinates = (totalCoordinateCount, new List<Coordinate>
                    {
                        new Coordinate(node.Latitude, node.Longitude)
                    }),
                    PropertyKeys = (totalPropertyCount, featurePropKeys),
                    PropertyValues = (totalPropertyCount, featurePropValues)
                };
                featuresData.Add(featureId, fData);
                featureIds.Add(featureId);

                totalPropertyCount += featurePropKeys.Count;
                ++totalCoordinateCount;
            }

            offsets.Add(tileId, fileWriter.BaseStream.Position);

            // Write TileBlockHeader
            fileWriter.Write(featureIds.Count); // TileBlockHeader: FeatureCount
            fileWriter.Write(totalCoordinateCount); // TileBlockHeader: CoordinateCount
            fileWriter.Write(totalPropertyCount * 2); // TileBlockHeader: StringCount
            fileWriter.Write(0); //TileBlockHeader: CharactersCount

            // Take note of the offset within the file for this field
            var coPosition = fileWriter.BaseStream.Position;
            // Write a placeholder value to reserve space in the file
            fileWriter.Write((long)0); // TileBlockHeader: CoordinatesOffsetInBytes (placeholder)

            // Take note of the offset within the file for this field
            var soPosition = fileWriter.BaseStream.Position;
            // Write a placeholder value to reserve space in the file
            fileWriter.Write((long)0); // TileBlockHeader: StringsOffsetInBytes (placeholder)

            // Take note of the offset within the file for this field
            var choPosition = fileWriter.BaseStream.Position;
            // Write a placeholder value to reserve space in the file
            fileWriter.Write((long)0); // TileBlockHeader: CharactersOffsetInBytes (placeholder)

            // Write MapFeatures
            for (var i = 0; i < featureIds.Count; ++i)
            {
                var featureData = featuresData[featureIds[i]];

                fileWriter.Write(featureIds[i]); // MapFeature: Id
                fileWriter.Write(labels[i]); // MapFeature: LabelOffset
                fileWriter.Write(featureData.GeometryType); // MapFeature: GeometryType
                fileWriter.Write(featureData.Coordinates.offset); // MapFeature: CoordinateOffset
                fileWriter.Write(featureData.Coordinates.coordinates.Count); // MapFeature: CoordinateCount
                fileWriter.Write(featureData.PropertyKeys.offset * 2); // MapFeature: PropertiesOffset 
                fileWriter.Write(featureData.PropertyKeys.keys.Count); // MapFeature: PropertyCount
            }

            // Record the current position in the stream
            var currentPosition = fileWriter.BaseStream.Position;
            // Seek back in the file to the position of the field
            fileWriter.BaseStream.Position = coPosition;
            // Write the recorded 'currentPosition'
            fileWriter.Write(currentPosition); // TileBlockHeader: CoordinatesOffsetInBytes
            // And seek forward to continue updating the file
            fileWriter.BaseStream.Position = currentPosition;
            foreach (var t in featureIds)
            {
                var featureData = featuresData[t];

                foreach (var c in featureData.Coordinates.coordinates)
                {
                    fileWriter.Write(c.Latitude); // Coordinate: Latitude
                    fileWriter.Write(c.Longitude); // Coordinate: Longitude
                }
            }

            using var fileWriterFeatures = new StreamWriter(filePath.Replace(".bin", "_features.bin"));

            foreach (var t in featureIds)
            {
                var featureData = featuresData[t];

                fileWriterFeatures.WriteLine(featureData.PropertyKeys.keys.Count);
                for (var i = 0; i < featureData.PropertyKeys.keys.Count; ++i)
                {
                    fileWriterFeatures.WriteLine((int)featureData.PropertyKeys.keys[i]);
                    fileWriterFeatures.WriteLine((int)featureData.PropertyValues.values[i]);
                }
            }
        }

        // Seek to the beginning of the file, just before the first TileHeaderEntry
        fileWriter.Seek(Marshal.SizeOf<FileHeader>(), SeekOrigin.Begin);
        foreach (var (tileId, offset) in offsets)
        {
            fileWriter.Write(tileId);
            fileWriter.Write(offset);
        }

        fileWriter.Flush();
    }

    public static void Main(string[] args)
    {
        Options? arguments = null;
        var argParseResult =
            Parser.Default.ParseArguments<Options>(args).WithParsed(options => { arguments = options; });

        if (argParseResult.Errors.Any())
        {
            Environment.Exit(-1);
        }

        var mapData = LoadOsmFile(arguments!.OsmPbfFilePath);
        CreateMapDataFile(ref mapData, arguments!.OutputFilePath!);
    }

    public class Options
    {
        [Option('i', "input", Required = true, HelpText = "Input osm.pbf file")]
        public string? OsmPbfFilePath { get; set; }

        [Option('o', "output", Required = true, HelpText = "Output binary file")]
        public string? OutputFilePath { get; set; }
    }

    private readonly struct MapData
    {
        public ImmutableDictionary<long, AbstractNode> Nodes { get; init; }
        public ImmutableDictionary<int, List<long>> Tiles { get; init; }
        public ImmutableArray<Way> Ways { get; init; }
    }

    public enum Keys {
        Highway,
        Highway_2020,
        Highway_Lanes_Backward,
        Highway_Lanes_Forward,
        Water,
        WaterWay,
        Water_Point,
        Railway,
        Natural,
        Boundary,
        Landuse,
        Building,
        Building_Levels,
        Building_2020,
        Building_Architecture,
        Leisure,
        Amenity,
        Amenity_2020,
        Admin_Level,
        Place,
        Placement,
        Placement_Forward,
        Name,
        NULL
    }

    public enum Values {
        Highway_Motorway,
        Highway_Trunk,
        Highway_Primary,
        Highway_Secondary,
        Highway_Tertiary,
        Highway_Unclassified,
        Highway_Residential,
        Highway_Road,
        Boundary_Administrative,
        Boundary_Forest,
        Admin_Level_Two,
        Place_City,
        Place_Town,
        Place_Locality,
        Place_Hamlet,
        Natural_Fell,
        Natural_Grassland,
        Natural_Heath,
        Natural_Moor,
        Natural_Scrub,
        Natural_Wetland,
        Natural_Wood,
        Natural_Tree_Row,
        Natural_Bare_Rock,
        Natural_Rock,
        Natural_Scree,
        Natural_Beach,
        Natural_Sand,
        Natural_Water,
        Landuse_Forest,
        Landuse_Orchard,
        Landuse_Residential,
        Landuse_Cemetery,
        Landuse_Industrial,
        Landuse_Commercial,
        Landuse_Square,
        Landuse_Construction,
        Landuse_Military,
        Landuse_Quarry,
        Landuse_Brownfield,
        Landuse_Farm,
        Landuse_Meadow,
        Landuse_Grass,
        Landuse_Greenfield,
        Landuse_Recreation_Ground,
        Landuse_Winter_Sports,
        Landuse_Allotments,
        Landuse_Reservoir,
        Landuse_Basin,
        Name_ID,
        NULL
    }

    private struct FeatureData
    {
        public long Id { get; init; }

        public byte GeometryType { get; set; }
        public (int offset, List<Coordinate> coordinates) Coordinates { get; init; }
        public (int offset, List<Keys> keys) PropertyKeys { get; init; }
        public (int offset, List<Values> values) PropertyValues { get; init; }
    }

    public static Keys ConvertStringToUniqueKey(string str) {
        if (str == "name") {
            return Keys.Name;
        }

        if (str == "highway") {
            return Keys.Highway;
        }

        if (str == "highway:2020") {
            return Keys.Highway_2020;
        }

        if (str == "highway:lanes:backward") {
            return Keys.Highway_Lanes_Backward;
        }

        if (str == "highway:lanes:forward") {
            return Keys.Highway_Lanes_Forward;
        }

        if (str == "water") {
            return Keys.Water;
        }

        if (str == "waterway") {
            return Keys.WaterWay;
        }

        if (str == "water_point") {
            return Keys.Water_Point;
        }

        if (str == "boundary") {
            return Keys.Boundary;
        }

        if (str == "admin_level") {
            return Keys.Admin_Level;
        }

        if (str == "place") {
            return Keys.Place;
        }

        if (str == "placement") {
            return Keys.Placement;
        }

        if (str == "placement:forward") {
            return Keys.Placement_Forward;
        }

        if (str == "railway") {
            return Keys.Railway;
        }

        if (str == "natural") {
            return Keys.Natural;
        }

        if (str == "landuse") {
            return Keys.Landuse;
        }

        if (str == "building") {
            return Keys.Building;
        }

        if (str == "building:levels") {
            return Keys.Building_Levels;
        }

        if (str == "building:architecture") {
            return Keys.Building_Architecture;
        }

        if (str == "building:2020") {
            return Keys.Building_2020;
        }

        if (str == "leisure") {
            return Keys.Leisure;
        }
        
        if (str == "amenity") {
            return Keys.Amenity;
        }
        
        if (str == "amenity:2020") {
            return Keys.Amenity_2020;
        }

        //Console.WriteLine(str);

        return Keys.NULL;
    }

    public static Values ConvertStringToUniqueValue(string key, string str, Keys keyEnum) {
        if (str.StartsWith("motorway")) {
            return Values.Highway_Motorway;
        }

        if (str.StartsWith("trunk")) {
            return Values.Highway_Trunk;
        }

        if (str.StartsWith("primary")) {
            return Values.Highway_Primary;
        }

        if (str.StartsWith("secondary")) {
            return Values.Highway_Secondary;
        }

        if (str.StartsWith("tertiary")) {
            return Values.Highway_Tertiary;
        }

        if (str.StartsWith("unclassified")) {
            return Values.Highway_Unclassified;
        }

        if (str.StartsWith("residential") && key == "highway") {
            return Values.Highway_Residential;
        }

        if (str.StartsWith("road")) {
            return Values.Highway_Road;
        }

        if (str.StartsWith("administrative")) {
            return Values.Boundary_Administrative;
        }

        if (str.StartsWith("forest") && key.StartsWith("boundary")) {
            return Values.Boundary_Forest;
        }

        if (str == "2") {
            return Values.Admin_Level_Two;
        }

        if (str.StartsWith("city")) {
            return Values.Place_City;
        }

        if (str.StartsWith("town")) {
            return Values.Place_Town;
        }

        if (str.StartsWith("locality")) {
            return Values.Place_Locality;
        }

        if (str.StartsWith("hamlet")) {
            return Values.Place_Hamlet;
        }

        if (str == "fell") {
            return Values.Natural_Fell;
        }

        if (str == "grassland") {
            return Values.Natural_Grassland;
        }

        if (str == "heath") {
            return Values.Natural_Heath;
        }

        if (str == "moor") {
            return Values.Natural_Moor;
        }

        if (str == "scrub") {
            return Values.Natural_Scrub;
        }

        if (str == "wetland") {
            return Values.Natural_Wetland;
        }

        if (str == "wood") {
            return Values.Natural_Wood;
        }

        if (str == "tree_row") {
            return Values.Natural_Tree_Row;
        }

        if (str == "bare_rock") {
            return Values.Natural_Bare_Rock;
        }

        if (str == "rock") {
            return Values.Natural_Rock;
        }

        if (str == "scree") {
            return Values.Natural_Scree;
        }

        if (str == "beach") {
            return Values.Natural_Beach;
        }

        if (str == "sand") {
            return Values.Natural_Sand;
        }

        if (str == "water") {
            return Values.Natural_Water;
        }
 
        if (str.StartsWith("forest") && key.StartsWith("landuse")) {
            return Values.Landuse_Forest;
        }

        if (str.StartsWith("orchard")) {
            return Values.Landuse_Orchard;
        }

        if (str.StartsWith("residential") && key.StartsWith("landuse")) {
            return Values.Landuse_Residential;
        }

        if (str.StartsWith("cemetery")) {
            return Values.Landuse_Cemetery;
        }

        if (str.StartsWith("industrial")) {
            return Values.Landuse_Industrial;
        }

        if (str.StartsWith("commercial")) {
            return Values.Landuse_Commercial;
        }

        if (str.StartsWith("square")) {
            return Values.Landuse_Square;
        }

        if (str.StartsWith("construction")) {
            return Values.Landuse_Construction;
        }

        if (str.StartsWith("military")) {
            return Values.Landuse_Military;
        }

        if (str.StartsWith("quarry")) {
            return Values.Landuse_Quarry;
        }

        if (str.StartsWith("brownfield")) {
            return Values.Landuse_Brownfield;
        }

        if (str.StartsWith("farm")) {
            return Values.Landuse_Farm;
        }

        if (str.StartsWith("meadow")) {
            return Values.Landuse_Meadow;
        }

        if (str.StartsWith("grass")) {
            return Values.Landuse_Grass;
        }

        if (str.StartsWith("greenfield")) {
            return Values.Landuse_Greenfield;
        }

        if (str.StartsWith("recreation_ground")) {
            return Values.Landuse_Recreation_Ground;
        }

        if (str.StartsWith("winter_sports")) {
            return Values.Landuse_Winter_Sports;
        }

        if (str.StartsWith("allotments")) {
            return Values.Landuse_Allotments;
        }

        if (str.StartsWith("reservoir")) {
            return Values.Landuse_Reservoir;
        }

        if (str.StartsWith("basin")) {
            return Values.Landuse_Basin;
        }

        if (key == "name") {
            return Values.Name_ID;
        }

        return Values.NULL;
    }
}
