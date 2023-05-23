using Mapster.Common.MemoryMappedTypes;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Mapster.Rendering;

public static class TileRenderer
{
    public static BaseShape Tessellate(this MapFeatureData feature, ref BoundingBox boundingBox, ref PriorityQueue<BaseShape, int> shapes)
    {
        BaseShape? baseShape = null;

        var featureType = feature.Type;
        if (feature.Properties.Any(p => (p.Key == Keys.Highway) && 
                                    MapFeature.HighwayTypes.Any(v => p.Value == v)))
        {
            var coordinates = feature.Coordinates;
            var road = new Road(coordinates);
            baseShape = road;
            shapes.Enqueue(road, road.ZIndex);
        }
        else if (feature.Properties.Any(p => (p.Key == Keys.Water || p.Key == Keys.WaterWay || p.Key == Keys.Water_Point)) 
                                        && feature.Type != GeometryType.Point)
        {
            var coordinates = feature.Coordinates;

            var waterway = new Waterway(coordinates, feature.Type == GeometryType.Polygon);
            baseShape = waterway;
            shapes.Enqueue(waterway, waterway.ZIndex);
        }
        else if (Border.ShouldBeBorder(feature))
        {
            var coordinates = feature.Coordinates;
            var border = new Border(coordinates);
            baseShape = border;
            shapes.Enqueue(border, border.ZIndex);
        }
        else if (PopulatedPlace.ShouldBePopulatedPlace(feature))
        {
            var coordinates = feature.Coordinates;
            var popPlace = new PopulatedPlace(coordinates, feature);
            baseShape = popPlace;
            shapes.Enqueue(popPlace, popPlace.ZIndex);
        }
        else if (feature.Properties.Any(p => p.Key == Keys.Railway))
        {
            var coordinates = feature.Coordinates;
            var railway = new Railway(coordinates);
            baseShape = railway;
            shapes.Enqueue(railway, railway.ZIndex);
        }
        else if (feature.Properties.Any(p => p.Key == Keys.Natural && featureType == GeometryType.Polygon))
        {
            var coordinates = feature.Coordinates;
            var geoFeature = new GeoFeature(coordinates, feature);
            baseShape = geoFeature;
            shapes.Enqueue(geoFeature, geoFeature.ZIndex);
        }
        else if (feature.Properties.Any(p => p.Key == Keys.Boundary && p.Value == Values.Boundary_Forest))
        {
            var coordinates = feature.Coordinates;
            var geoFeature = new GeoFeature(coordinates, GeoFeature.GeoFeatureType.Forest);
            baseShape = geoFeature;
            shapes.Enqueue(geoFeature, geoFeature.ZIndex);
        }
        else if (feature.Properties.Any(p => p.Key == Keys.Landuse && (p.Value == Values.Landuse_Forest || p.Value == Values.Landuse_Orchard)))
        {
            var coordinates = feature.Coordinates;
            var geoFeature = new GeoFeature(coordinates, GeoFeature.GeoFeatureType.Forest);
            baseShape = geoFeature;
            shapes.Enqueue(geoFeature, geoFeature.ZIndex);
        }
        else if (feature.Type == GeometryType.Polygon && feature.Properties.Any(p
                     => p.Key == Keys.Landuse && (p.Value == Values.Landuse_Residential || p.Value == Values.Landuse_Cemetery || p.Value == Values.Landuse_Industrial || p.Value == Values.Landuse_Commercial ||
                                                        p.Value == Values.Landuse_Square || p.Value == Values.Landuse_Construction || p.Value == Values.Landuse_Military || p.Value == Values.Landuse_Quarry ||
                                                        p.Value == Values.Landuse_Brownfield)))
        {
            var coordinates = feature.Coordinates;
            var geoFeature = new GeoFeature(coordinates, GeoFeature.GeoFeatureType.Residential);
            baseShape = geoFeature;
            shapes.Enqueue(geoFeature, geoFeature.ZIndex);
        }
        else if (feature.Type == GeometryType.Polygon && feature.Properties.Any(p
                     => p.Key == Keys.Landuse && (p.Value == Values.Landuse_Farm || p.Value == Values.Landuse_Meadow || p.Value == Values.Landuse_Grass || p.Value == Values.Landuse_Greenfield ||
                                                        p.Value == Values.Landuse_Recreation_Ground || p.Value == Values.Landuse_Winter_Sports || p.Value == Values.Landuse_Allotments)))
        {
            var coordinates = feature.Coordinates;
            var geoFeature = new GeoFeature(coordinates, GeoFeature.GeoFeatureType.Plain);
            baseShape = geoFeature;
            shapes.Enqueue(geoFeature, geoFeature.ZIndex);
        }
        else if (feature.Type == GeometryType.Polygon &&
                 feature.Properties.Any(p => p.Key == Keys.Landuse && (p.Value == Values.Landuse_Reservoir || p.Value == Values.Landuse_Basin)))
        {
            var coordinates = feature.Coordinates;
            var geoFeature = new GeoFeature(coordinates, GeoFeature.GeoFeatureType.Water);
            baseShape = geoFeature;
            shapes.Enqueue(geoFeature, geoFeature.ZIndex);
        }
        else if (feature.Type == GeometryType.Polygon && feature.Properties.Any(p => (p.Key == Keys.Building ||
                                                                                        p.Key == Keys.Building_2020 ||
                                                                                        p.Key == Keys.Building_Architecture ||
                                                                                        p.Key == Keys.Building_Levels)))
        {
            var coordinates = feature.Coordinates;
            var geoFeature = new GeoFeature(coordinates, GeoFeature.GeoFeatureType.Residential);
            baseShape = geoFeature;
            shapes.Enqueue(geoFeature, geoFeature.ZIndex);
        }
        else if (feature.Type == GeometryType.Polygon && feature.Properties.Any(p => p.Key == Keys.Leisure))
        {
            var coordinates = feature.Coordinates;
            var geoFeature = new GeoFeature(coordinates, GeoFeature.GeoFeatureType.Residential);
            baseShape = geoFeature;
            shapes.Enqueue(geoFeature, geoFeature.ZIndex);
        }
        else if (feature.Type == GeometryType.Polygon && feature.Properties.Any(p => (p.Key == Keys.Amenity ||
                                                                                        p.Key == Keys.Amenity_2020)))
        {
            var coordinates = feature.Coordinates;
            var geoFeature = new GeoFeature(coordinates, GeoFeature.GeoFeatureType.Residential);
            baseShape = geoFeature;
            shapes.Enqueue(geoFeature, geoFeature.ZIndex);
        }

        if (baseShape != null)
        {
            for (var j = 0; j < baseShape.ScreenCoordinates.Length; ++j)
            {
                boundingBox.MinX = Math.Min(boundingBox.MinX, baseShape.ScreenCoordinates[j].X);
                boundingBox.MaxX = Math.Max(boundingBox.MaxX, baseShape.ScreenCoordinates[j].X);
                boundingBox.MinY = Math.Min(boundingBox.MinY, baseShape.ScreenCoordinates[j].Y);
                boundingBox.MaxY = Math.Max(boundingBox.MaxY, baseShape.ScreenCoordinates[j].Y);
            }
        }

        return baseShape;
    }

    public static Image<Rgba32> Render(this PriorityQueue<BaseShape, int> shapes, BoundingBox boundingBox, int width, int height)
    {
        var canvas = new Image<Rgba32>(width, height);

        // Calculate the scale for each pixel, essentially applying a normalization
        var scaleX = canvas.Width / (boundingBox.MaxX - boundingBox.MinX);
        var scaleY = canvas.Height / (boundingBox.MaxY - boundingBox.MinY);
        var scale = Math.Min(scaleX, scaleY);

        // Background Fill
        canvas.Mutate(x => x.Fill(Color.White));
        while (shapes.Count > 0)
        {
            var entry = shapes.Dequeue();
            // FIXME: Hack
            if (entry.ScreenCoordinates.Length < 2)
            {
                continue;
            }
            entry.TranslateAndScale(boundingBox.MinX, boundingBox.MinY, scale, canvas.Height);
            canvas.Mutate(x => entry.Render(x));
        }

        return canvas;
    }

    public struct BoundingBox
    {
        public float MinX;
        public float MaxX;
        public float MinY;
        public float MaxY;
    }

    public static Keys ConvertStringToUniqueKey(string str) {
        if (str == "name") {
            return Keys.Name;
        }

        if (str == "highway") {
            return Keys.Highway;
        }

        if (str.StartsWith("water")) {
            return Keys.Water;
        }

        if (str.StartsWith("boundary")) {
            return Keys.Boundary;
        }

        if (str.StartsWith("admin_level")) {
            return Keys.Admin_Level;
        }

        if (str.StartsWith("place")) {
            return Keys.Place;
        }

        if (str.StartsWith("railway")) {
            return Keys.Railway;
        }

        if (str.StartsWith("natural")) {
            return Keys.Natural;
        }

        if (str.StartsWith("landuse")) {
            return Keys.Landuse;
        }

        if (str.StartsWith("building")) {
            return Keys.Building;
        }

        if (str.StartsWith("leisure")) {
            return Keys.Leisure;
        }

        if (str.StartsWith("amenity")) {
            return Keys.Amenity;
        }

        return Keys.NULL;
    }

    public static Values ConvertStringToUniqueValue(string key, string str) {
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

        if (str.StartsWith("forest") && key.StartsWith("boundary")) {
            return Values.Boundary_Forest;
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
