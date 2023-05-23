using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Mapster.Common.MemoryMappedTypes;

/// <summary>
///     Action to be called when iterating over <see cref="MapFeature" /> in a given bounding box via a call to
///     <see cref="DataFile.ForeachFeature" />
/// </summary>
/// <param name="feature">The current <see cref="MapFeature" />.</param>
/// <param name="label">The label of the feature, <see cref="string.Empty" /> if not available.</param>
/// <param name="coordinates">The coordinates of the <see cref="MapFeature" />.</param>
/// <returns></returns>
public delegate bool MapFeatureDelegate(MapFeatureData featureData);


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

/// <summary>
///     Aggregation of all the data needed to render a map feature
/// </summary>
public readonly ref struct MapFeatureData
{
    public long Id { get; init; }

    public GeometryType Type { get; init; }
    public ReadOnlySpan<char> Label { get; init; }
    public ReadOnlySpan<Coordinate> Coordinates { get; init; }
    public Dictionary<Keys, Values> Properties { get; init; }
}

/// <summary>
///     Represents a file with map data organized in the following format:<br />
///     <see cref="FileHeader" /><br />
///     Array of <see cref="TileHeaderEntry" /> with <see cref="FileHeader.TileCount" /> records<br />
///     Array of tiles, each tile organized:<br />
///     <see cref="TileBlockHeader" /><br />
///     Array of <see cref="MapFeature" /> with <see cref="TileBlockHeader.FeaturesCount" /> at offset
///     <see cref="TileHeaderEntry.OffsetInBytes" /> + size of <see cref="TileBlockHeader" /> in bytes.<br />
///     Array of <see cref="Coordinate" /> with <see cref="TileBlockHeader.CoordinatesCount" /> at offset
///     <see cref="TileBlockHeader.CharactersOffsetInBytes" />.<br />
///     Array of <see cref="StringEntry" /> with <see cref="TileBlockHeader.StringCount" /> at offset
///     <see cref="TileBlockHeader.StringsOffsetInBytes" />.<br />
///     Array of <see cref="char" /> with <see cref="TileBlockHeader.CharactersCount" /> at offset
///     <see cref="TileBlockHeader.CharactersOffsetInBytes" />.<br />
/// </summary>
public unsafe class DataFile : IDisposable
{
    private readonly FileHeader* _fileHeader;
    private readonly MemoryMappedViewAccessor _mma;
    private readonly MemoryMappedFile _mmf;

    private readonly byte* _ptr;
    private readonly int CoordinateSizeInBytes = Marshal.SizeOf<Coordinate>();
    private readonly int FileHeaderSizeInBytes = Marshal.SizeOf<FileHeader>();
    private readonly int MapFeatureSizeInBytes = Marshal.SizeOf<MapFeature>();
    private readonly int StringEntrySizeInBytes = Marshal.SizeOf<StringEntry>();
    private readonly int TileBlockHeaderSizeInBytes = Marshal.SizeOf<TileBlockHeader>();
    private readonly int TileHeaderEntrySizeInBytes = Marshal.SizeOf<TileHeaderEntry>();

    private bool _disposedValue;

    public DataFile(string path)
    {
        _mmf = MemoryMappedFile.CreateFromFile(path);
        _mma = _mmf.CreateViewAccessor();
        _mma.SafeMemoryMappedViewHandle.AcquirePointer(ref _ptr);
        _fileHeader = (FileHeader*)_ptr;
    }

    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                _mma?.SafeMemoryMappedViewHandle.ReleasePointer();
                _mma?.Dispose();
                _mmf?.Dispose();
            }

            _disposedValue = true;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private TileHeaderEntry* GetNthTileHeader(int i)
    {
        return (TileHeaderEntry*)(_ptr + i * TileHeaderEntrySizeInBytes + FileHeaderSizeInBytes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private (TileBlockHeader? Tile, ulong TileOffset) GetTile(int tileId)
    {
        ulong tileOffset = 0;
        for (var i = 0; i < _fileHeader->TileCount; ++i)
        {
            var tileHeaderEntry = GetNthTileHeader(i);
            if (tileHeaderEntry->ID == tileId)
            {
                tileOffset = tileHeaderEntry->OffsetInBytes;
                return (*(TileBlockHeader*)(_ptr + tileOffset), tileOffset);
            }
        }

        return (null, 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private MapFeature* GetFeature(int i, ulong offset)
    {
        return (MapFeature*)(_ptr + offset + TileBlockHeaderSizeInBytes + i * MapFeatureSizeInBytes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private ReadOnlySpan<Coordinate> GetCoordinates(ulong coordinateOffset, int ithCoordinate, int coordinateCount)
    {
        return new ReadOnlySpan<Coordinate>(_ptr + coordinateOffset + ithCoordinate * CoordinateSizeInBytes, coordinateCount);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private void GetString(ulong stringsOffset, ulong charsOffset, int i, out ReadOnlySpan<char> value)
    {
        var stringEntry = (StringEntry*)(_ptr + stringsOffset + i * StringEntrySizeInBytes);
        value = new ReadOnlySpan<char>(_ptr + charsOffset + stringEntry->Offset * 2, stringEntry->Length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private void GetProperty(ulong stringsOffset, ulong charsOffset, int i, out ReadOnlySpan<char> key, out ReadOnlySpan<char> value)
    {
        if (i % 2 != 0)
        {
            throw new ArgumentException("Properties are key-value pairs and start at even indices in the string list (i.e. i % 2 == 0)");
        }

        GetString(stringsOffset, charsOffset, i, out key);
        GetString(stringsOffset, charsOffset, i + 1, out value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public void ForeachFeature(BoundingBox b, MapFeatureDelegate? action)
    {
        if (action == null)
        {
            return;
        }

        long lastFeatureID = 0;
        var fileReaderFeatures = new StreamReader("MapData/andorra_features.bin");
        //using var fileWriterFeatures = new StreamWriter("feature_ids.txt");
        var tiles = TiligSystem.GetTilesForBoundingBox(b.MinLat, b.MinLon, b.MaxLat, b.MaxLon);
        for (var i = 0; i < tiles.Length; ++i)
        {
            var header = GetTile(tiles[i]);
            if (header.Tile == null)
            {
                continue;
            }

            for (var j = 0; j < header.Tile.Value.FeaturesCount; ++j)
            {
                var feature = GetFeature(j, header.TileOffset);
                var coordinates = GetCoordinates(header.Tile.Value.CoordinatesOffsetInBytes, feature->CoordinateOffset, feature->CoordinateCount);
                var isFeatureInBBox = false;

                for (var k = 0; k < coordinates.Length; ++k)
                {
                    if (b.Contains(coordinates[k]))
                    {
                        isFeatureInBBox = true;
                        break;
                    }
                }

                var label = ReadOnlySpan<char>.Empty;
                /*if (feature->LabelOffset >= 0)
                {
                    GetString(header.Tile.Value.StringsOffsetInBytes, header.Tile.Value.CharactersOffsetInBytes, feature->LabelOffset, out label);
                }*/

                if (isFeatureInBBox)
                {
                    var properties = new Dictionary<Keys, Values>(feature->PropertyCount);
                    
                    if (lastFeatureID == 0) {
                        lastFeatureID = feature->Id;
                    }
                    SeekInFile(ref fileReaderFeatures, feature->Id, lastFeatureID);
                    lastFeatureID = feature->Id;

                    long propertyCount = Int32.Parse(fileReaderFeatures.ReadLine());
                    for (long p = 0; p < propertyCount; p++)
                    {
                        Keys key = (Keys)Int32.Parse(fileReaderFeatures.ReadLine());
                        Values value = (Values)Int32.Parse(fileReaderFeatures.ReadLine());
                        if (key == Keys.NULL) {
                            continue;
                        }

                        properties.Add(key, value);
                    }

                    if (!action(new MapFeatureData
                        {
                            Id = feature->Id,
                            Label = label,
                            Coordinates = coordinates,
                            Type = feature->GeometryType,
                            Properties = properties
                        }))
                    {
                        break;
                    }
                }
            }
        }
        fileReaderFeatures.Close();
    }

    public void SeekInFile(ref StreamReader file, long featureID, long lastFeatureID) {
        for (long i = lastFeatureID + 1; i < featureID; i++) {
            int count = Int32.Parse(file.ReadLine());

            for (int j = 0; j < count; j++) {
                file.ReadLine();
                file.ReadLine();
            }
        }
    }
}
