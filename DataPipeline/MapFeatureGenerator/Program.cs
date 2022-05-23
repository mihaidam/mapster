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

        var featureIds = new List<long>();
        // var geometryTypes = new List<GeometryType>();
        // var coordinates = new List<(long id, (int offset, List<Coordinate> coordinates) values)>();

        var labels = new List<int>();
        // cut from here
        //var propKeys = new List<(long id, (int offset, IEnumerable<string> keys) values)>();
        //var propValues = new List<(long id, (int offset, IEnumerable<string> values) values)>();

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
            usedNodes.Clear();

            featureIds.Clear();
            labels.Clear();

            var totalCoordinateCount = 0;
            var totalPropertyCount = 0;

            var featuresData = new Dictionary<long, FeatureData>();

            foreach (var way in mapData.Ways)
            {
                var featureData = new FeatureData
                {
                    Id = way.Id,
                    Coordinates = (totalCoordinateCount, new List<Coordinate>()),
                    PropertyKeys = (totalPropertyCount, new List<MapFeatureData.PropertiesKeysEnum>(way.Tags.Count)),
                    PropertyValues = (totalPropertyCount, new List<MapFeatureData.PropertiesValueStruct>(way.Tags.Count))
                };

                featureIds.Add(way.Id);
                var geometryType = GeometryType.Polyline;

                labels.Add(-1);

                MapFeatureData.PropertiesKeysEnum convertedKey;
                MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum convertedValue;
                //MapFeatureData.PropertiesValueStruct propertiesValueStruct = new MapFeatureData.PropertiesValueStruct();

                var addedKey = true;
                foreach (var tag in way.Tags)
                {
                    addedKey = true;
                    if (tag.Key.StartsWith("place"))
                    {
                        featureData.PropertyKeys.keys.Add(MapFeatureData.PropertiesKeysEnum.place);
                    }
                    else if (tag.Key.StartsWith("boundary"))
                    {
                        featureData.PropertyKeys.keys.Add(MapFeatureData.PropertiesKeysEnum.boundary);
                    }
                    else if (tag.Key.StartsWith("admin_level"))
                    {
                        featureData.PropertyKeys.keys.Add(MapFeatureData.PropertiesKeysEnum.admin_level);
                    }
                    else if (tag.Key.StartsWith("water"))
                    {
                        featureData.PropertyKeys.keys.Add(MapFeatureData.PropertiesKeysEnum.water);
                    }
                    else if (tag.Key.StartsWith("highway"))
                    {
                        featureData.PropertyKeys.keys.Add(MapFeatureData.PropertiesKeysEnum.highway);
                    }
                    else if (tag.Key.StartsWith("railway"))
                    {
                        featureData.PropertyKeys.keys.Add(MapFeatureData.PropertiesKeysEnum.railway);
                    }
                    else if (tag.Key.StartsWith("natural"))
                    {
                        featureData.PropertyKeys.keys.Add(MapFeatureData.PropertiesKeysEnum.natural);
                    }
                    else if (tag.Key.StartsWith("landuse"))
                    {
                        featureData.PropertyKeys.keys.Add(MapFeatureData.PropertiesKeysEnum.landuse);
                    }
                    else if (tag.Key.StartsWith("building"))
                    {
                        featureData.PropertyKeys.keys.Add(MapFeatureData.PropertiesKeysEnum.building);
                    }
                    else if (tag.Key.StartsWith("leisure"))
                    {
                        featureData.PropertyKeys.keys.Add(MapFeatureData.PropertiesKeysEnum.leisure);
                    }
                    else if (tag.Key.StartsWith("amenity"))
                    {
                        featureData.PropertyKeys.keys.Add(MapFeatureData.PropertiesKeysEnum.amenity);
                    }
                    else if (tag.Key.StartsWith("name"))
                    {
                        labels[^1] = totalPropertyCount * 2 + featureData.PropertyKeys.keys.Count * 2 + 1;
                        featureData.PropertyKeys.keys.Add(MapFeatureData.PropertiesKeysEnum.name);
                        MapFeatureData.PropertiesValueStruct propertiesValueStruct = new MapFeatureData.PropertiesValueStruct(MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum.none, tag.Value);
                        featureData.PropertyValues.values.Add(propertiesValueStruct);
                        addedKey = false;
                    }
                    else
                    {
                        addedKey = false;
                    }
                    //else if (Enum.TryParse<MapFeatureData.PropertiesKeysEnum>(tag.Key, out convertedKey))
                    //{
                    //    featureData.PropertyKeys.keys.Add(convertedKey);
                    //}

                    if (addedKey)
                    {
                        if (tag.Value.StartsWith("city"))
                        {
                            propertiesValueStruct.MapFeatureData.PropertiesValueStruct propertiesValueStruct = new MapFeatureData.PropertiesValueStruct(MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum.city, "");
                            ;
                            featureData.PropertyValues.values.Add(propertiesValueStruct);
                        }
                        else if (tag.Value.StartsWith("town"))
                        {
                            MapFeatureData.PropertiesValueStruct propertiesValueStruct = new MapFeatureData.PropertiesValueStruct(MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum.town, "");
                            featureData.PropertyValues.values.Add(propertiesValueStruct);
                        }
                        else if (tag.Value.StartsWith("locality"))
                        {
                            MapFeatureData.PropertiesValueStruct propertiesValueStruct = new MapFeatureData.PropertiesValueStruct(MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum.locality, "");
                            featureData.PropertyValues.values.Add(propertiesValueStruct);
                        }
                        else if (tag.Value.StartsWith("hamlet"))
                        {
                            MapFeatureData.PropertiesValueStruct propertiesValueStruct = new MapFeatureData.PropertiesValueStruct(MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum.hamlet, "");
                            featureData.PropertyValues.values.Add(propertiesValueStruct);
                        }
                        else if (tag.Value.StartsWith("administrative"))
                        {
                            MapFeatureData.PropertiesValueStruct propertiesValueStruct = new MapFeatureData.PropertiesValueStruct(MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum.administrative, "");
                            featureData.PropertyValues.values.Add(propertiesValueStruct);
                        }
                        else if (tag.Value.StartsWith("2"))
                        {
                            MapFeatureData.PropertiesValueStruct propertiesValueStruct = new MapFeatureData.PropertiesValueStruct(MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum.two, "");
                            featureData.PropertyValues.values.Add(propertiesValueStruct);
                        }
                        else if (tag.Value.StartsWith("motorway") || tag.Value.StartsWith("trunk") || tag.Value.StartsWith("primary") ||
                            tag.Value.StartsWith("secondary") || tag.Value.StartsWith("tertiary") || tag.Value.StartsWith("road") || tag.Value.StartsWith("path") ||
                            tag.Value.StartsWith("service") || tag.Value.StartsWith("footway") || tag.Value.StartsWith("track") || tag.Value.StartsWith("steps"))
                        {
                            MapFeatureData.PropertiesValueStruct propertiesValueStruct = new MapFeatureData.PropertiesValueStruct(MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum.motorway, "");
                            featureData.PropertyValues.values.Add(propertiesValueStruct);

                        }
                        else if (tag.Value.StartsWith("forest"))
                        {
                            MapFeatureData.PropertiesValueStruct propertiesValueStruct = new MapFeatureData.PropertiesValueStruct(MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum.forest, "");
                            featureData.PropertyValues.values.Add(propertiesValueStruct);
                        }
                        else if (tag.Value.StartsWith("orchard"))
                        {
                            MapFeatureData.PropertiesValueStruct propertiesValueStruct = new MapFeatureData.PropertiesValueStruct(MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum.orchard, "");
                            featureData.PropertyValues.values.Add(propertiesValueStruct);
                        }
                        else if (tag.Value.StartsWith("residential") || tag.Value.StartsWith("cemetery") || tag.Value.StartsWith("industrial") ||
                            tag.Value.StartsWith("commercial") || tag.Value.StartsWith("square") || tag.Value.StartsWith("construction") ||
                            tag.Value.StartsWith("military") || tag.Value.StartsWith("quarry") || tag.Value.StartsWith("brownfield") ||
                            tag.Value.StartsWith("office") || tag.Value.StartsWith("apartment") || tag.Value.StartsWith("house"))
                        {
                            MapFeatureData.PropertiesValueStruct propertiesValueStruct = new MapFeatureData.PropertiesValueStruct(MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum.residential, "");
                            featureData.PropertyValues.values.Add(propertiesValueStruct);
                        }
                        else if (tag.Value.StartsWith("farm") || tag.Value.StartsWith("meadow") || tag.Value.StartsWith("grass") ||
                            tag.Value.StartsWith("greenfield") || tag.Value.StartsWith("recreation_ground") || tag.Value.StartsWith("winter_sports") ||
                            tag.Value.StartsWith("allotments"))
                        {
                            MapFeatureData.PropertiesValueStruct propertiesValueStruct = new MapFeatureData.PropertiesValueStruct(MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum.farm, "");
                            featureData.PropertyValues.values.Add(propertiesValueStruct);
                        }
                        else if (tag.Value.StartsWith("reservoir"))
                        {
                            MapFeatureData.PropertiesValueStruct propertiesValueStruct = new MapFeatureData.PropertiesValueStruct(MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum.reservoir, "");
                            featureData.PropertyValues.values.Add(propertiesValueStruct);
                        }
                        else if (tag.Value.StartsWith("basin") || tag.Value.StartsWith("stream"))
                        {
                            MapFeatureData.PropertiesValueStruct propertiesValueStruct = new MapFeatureData.PropertiesValueStruct(MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum.basin, "");
                            featureData.PropertyValues.values.Add(propertiesValueStruct);
                        }
                        else if (tag.Value.StartsWith("restaurant"))
                        {
                            MapFeatureData.PropertiesValueStruct propertiesValueStruct = new MapFeatureData.PropertiesValueStruct(MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum.restaurant, "");
                            featureData.PropertyValues.values.Add(propertiesValueStruct);
                        }
                        else if (tag.Value.StartsWith("water") || tag.Value.StartsWith("river"))
                        {
                            MapFeatureData.PropertiesValueStruct propertiesValueStruct = new MapFeatureData.PropertiesValueStruct(MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum.water, "");

                            featureData.PropertyValues.values.Add(propertiesValueStruct);
                        }
                        else if (tag.Value.StartsWith("yes") || tag.Value.StartsWith("swimming") || tag.Value.StartsWith("parking"))
                        {
                            MapFeatureData.PropertiesValueStruct propertiesValueStruct = new MapFeatureData.PropertiesValueStruct(MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum.yes, "");
                            featureData.PropertyValues.values.Add(propertiesValueStruct);
                        }
                        else
                        {
                            //Console.WriteLine(tag.Key + "---" + tag.Value);
                            featureData.PropertyKeys.keys.RemoveAt(featureData.PropertyKeys.keys.Count() - 1);
                        }
                        //else if (Enum.TryParse<MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum>(tag.Value, out convertedValue))
                        //{
                        //    propertiesValueStruct.PropertiesValues = convertedValue;
                        //    propertiesValueStruct.name = "";
                        //    featureData.PropertyValues.values.Add(propertiesValueStruct);
                        //}
                    }
                    foreach (var val in featureData.PropertyValues.values)
                    {
                        Enum.TryParse(val.ToString(), out MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum myStatus);
                        if (myStatus != MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum.wetland)
                            Console.WriteLine("---" + myStatus.ToString());
                        else
                            Console.WriteLine("---" + (int)myStatus);
                    }
                }

                foreach (var nodeId in way.NodeIds)
                {
                    var node = mapData.Nodes[nodeId];
                    usedNodes.Add(nodeId);

                    foreach (var (key, value) in node.Tags)
                    {
                        addedKey = true;
                        if (key.StartsWith("place"))
                        {
                            featureData.PropertyKeys.keys.Add(MapFeatureData.PropertiesKeysEnum.place);
                        }
                        else if (key.StartsWith("boundary"))
                        {
                            featureData.PropertyKeys.keys.Add(MapFeatureData.PropertiesKeysEnum.boundary);
                        }
                        else if (key.StartsWith("admin_level"))
                        {
                            featureData.PropertyKeys.keys.Add(MapFeatureData.PropertiesKeysEnum.admin_level);
                        }
                        else if (key.StartsWith("water"))
                        {
                            featureData.PropertyKeys.keys.Add(MapFeatureData.PropertiesKeysEnum.water);
                        }
                        else if (key.StartsWith("highway"))
                        {
                            featureData.PropertyKeys.keys.Add(MapFeatureData.PropertiesKeysEnum.highway);
                        }
                        else if (key.StartsWith("railway"))
                        {
                            featureData.PropertyKeys.keys.Add(MapFeatureData.PropertiesKeysEnum.railway);
                        }
                        else if (key.StartsWith("natural"))
                        {
                            featureData.PropertyKeys.keys.Add(MapFeatureData.PropertiesKeysEnum.natural);
                        }
                        else if (key.StartsWith("landuse"))
                        {
                            featureData.PropertyKeys.keys.Add(MapFeatureData.PropertiesKeysEnum.landuse);
                        }
                        else if (key.StartsWith("building"))
                        {
                            featureData.PropertyKeys.keys.Add(MapFeatureData.PropertiesKeysEnum.building);
                        }
                        else if (key.StartsWith("leisure"))
                        {
                            featureData.PropertyKeys.keys.Add(MapFeatureData.PropertiesKeysEnum.leisure);
                        }
                        else if (key.StartsWith("amenity"))
                        {
                            featureData.PropertyKeys.keys.Add(MapFeatureData.PropertiesKeysEnum.amenity);
                        }
                        else if (key.StartsWith("name"))
                        {
                            labels[^1] = totalPropertyCount * 2 + featureData.PropertyKeys.keys.Count * 2 + 1;
                            featureData.PropertyKeys.keys.Add(MapFeatureData.PropertiesKeysEnum.name);
                            MapFeatureData.PropertiesValueStruct propertiesValueStruct = new MapFeatureData.PropertiesValueStruct(MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum.none, value);
                            featureData.PropertyValues.values.Add(propertiesValueStruct);
                            addedKey = false;
                        }
                        else
                        {
                            addedKey = false;
                        }
                        //else if (Enum.TryParse<MapFeatureData.PropertiesKeysEnum>(tag.Key, out convertedKey))
                        //{
                        //    featureData.PropertyKeys.keys.Add(convertedKey);
                        //}

                        if (addedKey)
                        {
                            if (value.StartsWith("city"))
                            {
                                MapFeatureData.PropertiesValueStruct propertiesValueStruct = new MapFeatureData.PropertiesValueStruct(MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum.city, "");
                                featureData.PropertyValues.values.Add(propertiesValueStruct);
                            }
                            else if (value.StartsWith("town"))
                            {
                                MapFeatureData.PropertiesValueStruct propertiesValueStruct = new MapFeatureData.PropertiesValueStruct(MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum.town, "");
                                featureData.PropertyValues.values.Add(propertiesValueStruct);
                            }
                            else if (value.StartsWith("locality"))
                            {
                                MapFeatureData.PropertiesValueStruct propertiesValueStruct = new MapFeatureData.PropertiesValueStruct(MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum.locality, "");
                                featureData.PropertyValues.values.Add(propertiesValueStruct);
                            }
                            else if (value.StartsWith("hamlet"))
                            {
                                MapFeatureData.PropertiesValueStruct propertiesValueStruct = new MapFeatureData.PropertiesValueStruct(MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum.hamlet, "");
                                featureData.PropertyValues.values.Add(propertiesValueStruct);
                            }
                            else if (value.StartsWith("administrative"))
                            {
                                MapFeatureData.PropertiesValueStruct propertiesValueStruct = new MapFeatureData.PropertiesValueStruct(MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum.administrative, "");
                                featureData.PropertyValues.values.Add(propertiesValueStruct);
                            }
                            else if (value.StartsWith("2"))
                            {
                                MapFeatureData.PropertiesValueStruct propertiesValueStruct = new MapFeatureData.PropertiesValueStruct(MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum.two, "");
                                featureData.PropertyValues.values.Add(propertiesValueStruct);
                            }
                            else if (value.StartsWith("motorway") || value.StartsWith("trunk") || value.StartsWith("primary") ||
                                value.StartsWith("secondary") || value.StartsWith("tertiary") || value.StartsWith("road") || value.StartsWith("path") ||
                                value.StartsWith("service") || value.StartsWith("footway") || value.StartsWith("track") || value.StartsWith("steps"))
                            {
                                MapFeatureData.PropertiesValueStruct propertiesValueStruct = new MapFeatureData.PropertiesValueStruct(MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum.motorway, "");
                                featureData.PropertyValues.values.Add(propertiesValueStruct);
                            }
                            else if (value.StartsWith("forest"))
                            {
                                MapFeatureData.PropertiesValueStruct propertiesValueStruct = new MapFeatureData.PropertiesValueStruct(MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum.forest, "");
                                featureData.PropertyValues.values.Add(propertiesValueStruct);
                            }
                            else if (value.StartsWith("orchard"))
                            {
                                MapFeatureData.PropertiesValueStruct propertiesValueStruct = new MapFeatureData.PropertiesValueStruct(MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum.orchard, "");
                                featureData.PropertyValues.values.Add(propertiesValueStruct);
                            }
                            else if (value.StartsWith("residential") || value.StartsWith("cemetery") || value.StartsWith("industrial") ||
                                value.StartsWith("commercial") || value.StartsWith("square") || value.StartsWith("construction") ||
                                value.StartsWith("military") || value.StartsWith("quarry") || value.StartsWith("brownfield") ||
                                value.StartsWith("office") || value.StartsWith("apartment") || value.StartsWith("house"))
                            {
                                MapFeatureData.PropertiesValueStruct propertiesValueStruct = new MapFeatureData.PropertiesValueStruct(MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum.residential, "");
                                featureData.PropertyValues.values.Add(propertiesValueStruct);
                            }
                            else if (value.StartsWith("farm") || value.StartsWith("meadow") || value.StartsWith("grass") ||
                                value.StartsWith("greenfield") || value.StartsWith("recreation_ground") || value.StartsWith("winter_sports") ||
                                value.StartsWith("allotments"))
                            {
                                MapFeatureData.PropertiesValueStruct propertiesValueStruct = new MapFeatureData.PropertiesValueStruct(MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum.farm, "");
                                featureData.PropertyValues.values.Add(propertiesValueStruct);
                            }
                            else if (value.StartsWith("reservoir"))
                            {
                                MapFeatureData.PropertiesValueStruct propertiesValueStruct = new MapFeatureData.PropertiesValueStruct(MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum.reservoir, "");
                                featureData.PropertyValues.values.Add(propertiesValueStruct);
                            }
                            else if (value.StartsWith("basin") || value.StartsWith("stream"))
                            {
                                propertiesValueStruct.PropertiesValues = MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum.basin;
                                propertiesValueStruct.name = "";
                                featureData.PropertyValues.values.Add(propertiesValueStruct);
                            }
                            else if (value.StartsWith("restaurant"))
                            {
                                propertiesValueStruct.PropertiesValues = MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum.restaurant;
                                propertiesValueStruct.name = "";
                                featureData.PropertyValues.values.Add(propertiesValueStruct);
                            }
                            else if (value.StartsWith("water") || value.StartsWith("river"))
                            {
                                propertiesValueStruct.PropertiesValues = MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum.water;
                                propertiesValueStruct.name = "";
                                featureData.PropertyValues.values.Add(propertiesValueStruct);
                            }
                            else if (value.StartsWith("yes") || value.StartsWith("swimming") || value.StartsWith("parking"))
                            {
                                propertiesValueStruct.PropertiesValues = MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum.yes;
                                propertiesValueStruct.name = "";
                                featureData.PropertyValues.values.Add(propertiesValueStruct);
                            }
                            else
                            {
                                featureData.PropertyKeys.keys.RemoveAt(featureData.PropertyKeys.keys.Count() - 1);
                            }
                            //else if (Enum.TryParse<MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum>(value, out convertedValue))
                            //{
                            //    propertiesValueStruct.PropertiesValues = convertedValue;
                            //    propertiesValueStruct.name = "";
                            //    featureData.PropertyValues.values.Add(propertiesValueStruct);
                            //}
                        }
                    }

                    featureData.Coordinates.coordinates.Add(new Coordinate(node.Latitude, node.Longitude));
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

                featuresData.Add(way.Id, featureData);
            }

            //foreach (var t in featureIds)
            //{
            //    var featureData = featuresData[t];
            //    for (var i = 0; i < featureData.PropertyKeys.keys.Count; ++i)
            //    {
            //        ReadOnlySpan<char> k = Convert.ToString(featureData.PropertyKeys.keys[i]);
            //        ReadOnlySpan<char> v = Convert.ToString(featureData.PropertyValues.values[i]);

            //        Enum.TryParse(v.ToString(), out MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum myStatus);
            //        if (myStatus == MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum.wetland)
            //            Console.WriteLine(k.ToString() + "---" + myStatus.ToString());
            //    }
            //}


            foreach (var (nodeId, node) in mapData.Nodes.Where(n => !usedNodes.Contains(n.Key)))
            {
                featureIds.Add(nodeId);

                var featurePropKeys = new List<MapFeatureData.PropertiesKeysEnum>();
                var featurePropValues = new List<MapFeatureData.PropertiesValueStruct>();

                labels.Add(-1);
                MapFeatureData.PropertiesKeysEnum convertedKey;
                MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum convertedValue;
                MapFeatureData.PropertiesValueStruct propertiesValueStruct = new MapFeatureData.PropertiesValueStruct();

                var addedKey = true;

                for (var i = 0; i < node.Tags.Count; ++i)
                {
                    var tag = node.Tags[i];
                    addedKey = true;
                    if (tag.Key.StartsWith("place"))
                    {
                        featurePropKeys.Add(MapFeatureData.PropertiesKeysEnum.place);
                    }
                    else if (tag.Key.StartsWith("boundary"))
                    {
                        featurePropKeys.Add(MapFeatureData.PropertiesKeysEnum.boundary);
                    }
                    else if (tag.Key.StartsWith("admin_level"))
                    {
                        featurePropKeys.Add(MapFeatureData.PropertiesKeysEnum.admin_level);
                    }
                    else if (tag.Key.StartsWith("water"))
                    {
                        featurePropKeys.Add(MapFeatureData.PropertiesKeysEnum.water);
                    }
                    else if (tag.Key.StartsWith("highway"))
                    {
                        featurePropKeys.Add(MapFeatureData.PropertiesKeysEnum.highway);
                    }
                    else if (tag.Key.StartsWith("railway"))
                    {
                        featurePropKeys.Add(MapFeatureData.PropertiesKeysEnum.railway);
                    }
                    else if (tag.Key.StartsWith("natural"))
                    {
                        featurePropKeys.Add(MapFeatureData.PropertiesKeysEnum.natural);
                    }
                    else if (tag.Key.StartsWith("landuse"))
                    {
                        featurePropKeys.Add(MapFeatureData.PropertiesKeysEnum.landuse);
                    }
                    else if (tag.Key.StartsWith("building"))
                    {
                        featurePropKeys.Add(MapFeatureData.PropertiesKeysEnum.building);
                    }
                    else if (tag.Key.StartsWith("leisure"))
                    {
                        featurePropKeys.Add(MapFeatureData.PropertiesKeysEnum.leisure);
                    }
                    else if (tag.Key.StartsWith("amenity"))
                    {
                        featurePropKeys.Add(MapFeatureData.PropertiesKeysEnum.amenity);
                    }
                    else if (tag.Key.StartsWith("name"))
                    {
                        labels[^1] = totalPropertyCount * 2 + featurePropKeys.Count * 2 + 1;
                        featurePropKeys.Add(MapFeatureData.PropertiesKeysEnum.name);
                        propertiesValueStruct.PropertiesValues = MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum.none;
                        propertiesValueStruct.name = tag.Value;
                        featurePropValues.Add(propertiesValueStruct);
                        addedKey = false;
                    }
                    else
                    {
                        addedKey = false;
                    }
                    //else if (Enum.TryParse<MapFeatureData.PropertiesKeysEnum>(tag.Key, out convertedKey))
                    //{
                    //    featureData.PropertyKeys.keys.Add(convertedKey);
                    //}

                    if (addedKey)
                    {
                        if (tag.Value.StartsWith("city"))
                        {
                            propertiesValueStruct.PropertiesValues = MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum.city;
                            propertiesValueStruct.name = "";
                            featurePropValues.Add(propertiesValueStruct);
                        }
                        else if (tag.Value.StartsWith("town"))
                        {
                            propertiesValueStruct.PropertiesValues = MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum.town;
                            propertiesValueStruct.name = "";
                            featurePropValues.Add(propertiesValueStruct);
                        }
                        else if (tag.Value.StartsWith("locality"))
                        {
                            propertiesValueStruct.PropertiesValues = MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum.locality;
                            propertiesValueStruct.name = "";
                            featurePropValues.Add(propertiesValueStruct);
                        }
                        else if (tag.Value.StartsWith("hamlet"))
                        {
                            propertiesValueStruct.PropertiesValues = MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum.hamlet;
                            propertiesValueStruct.name = "";
                            featurePropValues.Add(propertiesValueStruct);
                        }
                        else if (tag.Value.StartsWith("administrative"))
                        {
                            propertiesValueStruct.PropertiesValues = MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum.administrative;
                            propertiesValueStruct.name = "";
                            featurePropValues.Add(propertiesValueStruct);
                        }
                        else if (tag.Value.StartsWith("2"))
                        {
                            propertiesValueStruct.PropertiesValues = MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum.two;
                            propertiesValueStruct.name = "";
                            featurePropValues.Add(propertiesValueStruct);
                        }
                        else if (tag.Value.StartsWith("motorway") || tag.Value.StartsWith("trunk") || tag.Value.StartsWith("primary") ||
                            tag.Value.StartsWith("secondary") || tag.Value.StartsWith("tertiary") || tag.Value.StartsWith("road") || tag.Value.StartsWith("path") ||
                            tag.Value.StartsWith("service") || tag.Value.StartsWith("footway") || tag.Value.StartsWith("track") || tag.Value.StartsWith("steps"))
                        {
                            propertiesValueStruct.PropertiesValues = MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum.motorway;
                            propertiesValueStruct.name = "";
                            featurePropValues.Add(propertiesValueStruct);
                        }
                        else if (tag.Value.StartsWith("forest"))
                        {
                            propertiesValueStruct.PropertiesValues = MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum.forest;
                            propertiesValueStruct.name = "";
                            featurePropValues.Add(propertiesValueStruct);
                        }
                        else if (tag.Value.StartsWith("orchard"))
                        {
                            propertiesValueStruct.PropertiesValues = MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum.orchard;
                            propertiesValueStruct.name = "";
                            featurePropValues.Add(propertiesValueStruct);
                        }
                        else if (tag.Value.StartsWith("residential") || tag.Value.StartsWith("cemetery") || tag.Value.StartsWith("industrial") ||
                            tag.Value.StartsWith("commercial") || tag.Value.StartsWith("square") || tag.Value.StartsWith("construction") ||
                            tag.Value.StartsWith("military") || tag.Value.StartsWith("quarry") || tag.Value.StartsWith("brownfield") ||
                            tag.Value.StartsWith("office") || tag.Value.StartsWith("apartment") || tag.Value.StartsWith("house"))
                        {
                            propertiesValueStruct.PropertiesValues = MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum.residential;
                            propertiesValueStruct.name = "";
                            featurePropValues.Add(propertiesValueStruct);
                        }
                        else if (tag.Value.StartsWith("farm") || tag.Value.StartsWith("meadow") || tag.Value.StartsWith("grass") ||
                            tag.Value.StartsWith("greenfield") || tag.Value.StartsWith("recreation_ground") || tag.Value.StartsWith("winter_sports") ||
                            tag.Value.StartsWith("allotments"))
                        {
                            propertiesValueStruct.PropertiesValues = MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum.farm;
                            propertiesValueStruct.name = "";
                            featurePropValues.Add(propertiesValueStruct);
                        }
                        else if (tag.Value.StartsWith("reservoir"))
                        {
                            propertiesValueStruct.PropertiesValues = MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum.reservoir;
                            propertiesValueStruct.name = "";
                            featurePropValues.Add(propertiesValueStruct);
                        }
                        else if (tag.Value.StartsWith("basin") || tag.Value.StartsWith("stream"))
                        {
                            propertiesValueStruct.PropertiesValues = MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum.basin;
                            propertiesValueStruct.name = "";
                            featurePropValues.Add(propertiesValueStruct);
                        }
                        else if (tag.Value.StartsWith("restaurant"))
                        {
                            propertiesValueStruct.PropertiesValues = MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum.restaurant;
                            propertiesValueStruct.name = "";
                            featurePropValues.Add(propertiesValueStruct);
                        }
                        else if (tag.Value.StartsWith("water") || tag.Value.StartsWith("river"))
                        {
                            propertiesValueStruct.PropertiesValues = MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum.water;
                            propertiesValueStruct.name = "";
                            featurePropValues.Add(propertiesValueStruct);
                        }
                        else if (tag.Value.StartsWith("yes") || tag.Value.StartsWith("swimming") || tag.Value.StartsWith("parking"))
                        {
                            propertiesValueStruct.PropertiesValues = MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum.yes;
                            propertiesValueStruct.name = "";
                            featurePropValues.Add(propertiesValueStruct);
                        }
                        else
                        {
                            featurePropKeys.RemoveAt(featurePropKeys.Count() - 1);
                        }
                        //else if (Enum.TryParse<MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum>(tag.Value, out convertedValue))
                        //{
                        //    propertiesValueStruct.PropertiesValues = convertedValue;
                        //    propertiesValueStruct.name = "";
                        //    featurePropValues.Add(propertiesValueStruct);
                        //}
                    }

                }

                if (featurePropKeys.Count != featurePropValues.Count)
                {
                    throw new InvalidDataContractException("Property keys and values should have the same count");
                }

                featuresData.Add(nodeId, new FeatureData
                {
                    Id = nodeId,
                    GeometryType = (byte)GeometryType.Point,
                    Coordinates = (totalCoordinateCount, new List<Coordinate>
                    {
                        new Coordinate(node.Latitude, node.Longitude)
                    }),
                    PropertyKeys = (totalPropertyCount, featurePropKeys),
                    PropertyValues = (totalPropertyCount, featurePropValues)
                });

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
            fileWriter.Seek((int)coPosition, SeekOrigin.Begin);
            // Write the recorded 'currentPosition'
            fileWriter.Write(currentPosition); // TileBlockHeader: CoordinatesOffsetInBytes
                                               // And seek forward to continue updating the file
            fileWriter.Seek((int)currentPosition, SeekOrigin.Begin);
            foreach (var t in featureIds)
            {
                var featureData = featuresData[t];

                foreach (var c in featureData.Coordinates.coordinates)
                {
                    fileWriter.Write(c.Latitude); // Coordinate: Latitude
                    fileWriter.Write(c.Longitude); // Coordinate: Longitude
                }
            }

            // Record the current position in the stream
            currentPosition = fileWriter.BaseStream.Position;
            // Seek back in the file to the position of the field
            fileWriter.Seek((int)soPosition, SeekOrigin.Begin);
            // Write the recorded 'currentPosition'
            fileWriter.Write(currentPosition); // TileBlockHeader: StringsOffsetInBytes
                                               // And seek forward to continue updating the file
            fileWriter.Seek((int)currentPosition, SeekOrigin.Begin);

            var stringOffset = 0;
            foreach (var t in featureIds)
            {
                var featureData = featuresData[t];
                for (var i = 0; i < featureData.PropertyKeys.keys.Count; ++i)
                {
                    ReadOnlySpan<char> k = Convert.ToString(featureData.PropertyKeys.keys[i]);
                    ReadOnlySpan<char> v = Convert.ToString(featureData.PropertyValues.values[i]);

                    Enum.TryParse(v.ToString(), out MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum myStatus);
                    if (myStatus != MapFeatureData.PropertiesValueStruct.PropertiesValuesEnum.wetland)
                        Console.WriteLine(k.ToString() + "---" + myStatus.ToString());

                    fileWriter.Write(stringOffset); // StringEntry: Offset
                    fileWriter.Write(k.Length); // StringEntry: Length
                    stringOffset += k.Length;

                    fileWriter.Write(stringOffset); // StringEntry: Offset
                    fileWriter.Write(v.Length); // StringEntry: Length
                    stringOffset += v.Length;
                }
            }

            // Record the current position in the stream
            currentPosition = fileWriter.BaseStream.Position;
            // Seek back in the file to the position of the field
            fileWriter.Seek((int)choPosition, SeekOrigin.Begin);
            // Write the recorded 'currentPosition'
            fileWriter.Write(currentPosition); // TileBlockHeader: CharactersOffsetInBytes
                                               // And seek forward to continue updating the file
            fileWriter.Seek((int)currentPosition, SeekOrigin.Begin);
            foreach (var t in featureIds)
            {
                var featureData = featuresData[t];
                for (var i = 0; i < featureData.PropertyKeys.keys.Count; ++i)
                {
                    ReadOnlySpan<char> k = Convert.ToString(featureData.PropertyKeys.keys[i]);
                    foreach (var c in k)
                    {
                        fileWriter.Write((short)c);
                    }

                    ReadOnlySpan<char> v = Convert.ToString(featureData.PropertyValues.values[i]);
                    foreach (var c in v)
                    {
                        fileWriter.Write((short)c);
                    }
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

    private struct FeatureData
    {
        public long Id { get; init; }

        public byte GeometryType { get; set; }
        public (int offset, List<Coordinate> coordinates) Coordinates { get; init; }
        public (int offset, List<MapFeatureData.PropertiesKeysEnum> keys) PropertyKeys { get; init; }
        public (int offset, List<MapFeatureData.PropertiesValueStruct> values) PropertyValues { get; init; }
    }
}
