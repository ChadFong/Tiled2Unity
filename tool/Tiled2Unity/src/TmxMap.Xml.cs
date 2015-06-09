﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Tiled2Unity
{
    // Partial class methods for creating TmxMap data from xml files/data
    partial class TmxMap
    {
        public static TmxMap LoadFromFile(string tmxPath)
        {
            // Refresh uniqueId counter
            TmxMap.NextUniqueId = 0;

            string fullTmxPath = Path.GetFullPath(tmxPath);
            using (ChDir chdir = new ChDir(fullTmxPath))
            {
                TmxMap tmxMap = new TmxMap();
                XDocument doc = tmxMap.LoadDocument(fullTmxPath);

                tmxMap.Name = Path.GetFileNameWithoutExtension(fullTmxPath);
                tmxMap.ParseMap(doc);

                // We're done reading and parsing the tmx file
                Program.WriteLine("Map details: {0}", tmxMap.ToString());
                Program.WriteSuccess("Finished parsing file: {0}", fullTmxPath);

                // Let listeners know of our success
                if (TmxMap.OnReadTmxFileCompleted != null)
                {
                    TmxMap.OnReadTmxFileCompleted(tmxMap);
                }

                return tmxMap;
            }
        }

        private XDocument LoadDocument(string xmlPath)
        {
            XDocument doc = null;
            Program.WriteLine("Opening {0} ...", xmlPath);
            try
            {
                doc = XDocument.Load(xmlPath);
            }
            catch (FileNotFoundException fnf)
            {
                string msg = String.Format("File not found: {0}", fnf.FileName);
                throw new TmxException(msg, fnf);
            }
            catch (XmlException xml)
            {
                string msg = String.Format("Xml error in {0}\n  {1}", xmlPath, xml.Message);
                throw new TmxException(msg, xml);
            }
            return doc;
        }

        private void ParseMap(XDocument doc)
        {
            Program.WriteLine("Parsing map root ...");
            Program.WriteVerbose(doc.ToString());
            XElement map = doc.Element("map");
            try
            {
                this.Orientation = TmxHelper.GetAttributeAsEnum<MapOrientation>(map, "orientation");
                this.StaggerAxis = TmxHelper.GetAttributeAsEnum(map, "staggeraxis", MapStaggerAxis.Y);
                this.StaggerIndex = TmxHelper.GetAttributeAsEnum(map, "staggerindex", MapStaggerIndex.Odd);
                this.HexSideLength = TmxHelper.GetAttributeAsInt(map, "hexsidelength", 0);
                this.DrawOrderHorizontal = TmxHelper.GetAttributeAsString(map, "renderorder", "right-down").Contains("right") ? 1 : -1;
                this.DrawOrderVertical = TmxHelper.GetAttributeAsString(map, "renderorder", "right-down").Contains("down") ? 1 : -1;
                this.Width = TmxHelper.GetAttributeAsInt(map, "width");
                this.Height = TmxHelper.GetAttributeAsInt(map, "height");
                this.TileWidth = TmxHelper.GetAttributeAsInt(map, "tilewidth");
                this.TileHeight = TmxHelper.GetAttributeAsInt(map, "tileheight");
                this.BackgroundColor = TmxHelper.GetAttributeAsColor(map, "backgroundcolor", Color.FromArgb(128, 128, 128));
            }
            catch (Exception e)
            {
                TmxException.FromAttributeException(e, map);
            }

            // Collect our map properties
            this.Properties = TmxProperties.FromXml(map);

            ParseAllTilesets(doc);
            ParseAllLayers(doc);
            ParseAllObjectGroups(doc);
        }

        private void ParseAllTilesets(XDocument doc)
        {
            Program.WriteLine("Parsing tileset elements ...");
            var tilesets = from item in doc.Descendants("tileset")
                           select item;

            foreach (var ts in tilesets)
            {
                ParseSingleTileset(ts);
            }
        }

        private void ParseSingleTileset(XElement elem)
        {
            // Parse the tileset data and populate the tiles from it
            uint firstId = TmxHelper.GetAttributeAsUInt(elem, "firstgid");

            // Does the element contain all tileset data or reference an external tileset?
            XAttribute attrSource = elem.Attribute("source");
            if (attrSource == null)
            {
                ParseInternalTileset(elem, firstId);
            }
            else
            {
                // Need to load the tileset data from an external file first
                // Then we'll parse it as if it's internal data
                Program.WriteVerbose(elem.ToString());
                ParseExternalTileset(attrSource.Value, firstId);
            }
        }

        // This method is called eventually for external tilesets too
        // Only the gid attribute has been consumed at this point for the tileset
        private void ParseInternalTileset(XElement elemTileset, uint firstId)
        {
            string tilesetName = TmxHelper.GetAttributeAsString(elemTileset, "name");

            Program.WriteLine("Parse internal tileset '{0}' (gid = {1}) ...", tilesetName, firstId);
            Program.WriteVerbose(elemTileset.ToString());

            int tileWidth = TmxHelper.GetAttributeAsInt(elemTileset, "tilewidth");
            int tileHeight = TmxHelper.GetAttributeAsInt(elemTileset, "tileheight");
            int spacing = TmxHelper.GetAttributeAsInt(elemTileset, "spacing", 0);
            int margin = TmxHelper.GetAttributeAsInt(elemTileset, "margin", 0);

            IList<TmxTile> tilesToAdd = new List<TmxTile>();

            // Tilesets may have an image for all tiles within it, or it may have an image per tile
            if (elemTileset.Element("image") != null)
            {
                TmxImage tmxImage = TmxImage.FromXml(elemTileset.Element("image"));
                RegisterImagePath(tmxImage.Path);

                // Create all the tiles
                // This is a bit complicated because of spacing and margin
                // (Margin is ignored from Width and Height)
                for (int end_y = margin + tileHeight; end_y <= tmxImage.Size.Height; end_y += spacing + tileHeight)
                {
                    for (int end_x = margin + tileWidth; end_x <= tmxImage.Size.Width; end_x += spacing + tileWidth)
                    {
                        uint localId = (uint) tilesToAdd.Count();
                        uint globalId = firstId + localId;
                        TmxTile tile = new TmxTile(globalId, localId, tilesetName, tmxImage);
                        tile.SetTileSize(tileWidth, tileHeight);
                        tile.SetLocationOnSource(end_x - tileWidth, end_y - tileHeight);
                        tilesToAdd.Add(tile);
                    }
                }
            }
            else
            {
                // Each tile will have it's own image
                foreach (var t in elemTileset.Elements("tile"))
                {
                    TmxImage tmxImage = TmxImage.FromXml(t.Element("image"));
                    RegisterImagePath(tmxImage.Path);

                    uint localId = (uint)tilesToAdd.Count();
                    uint globalId = firstId + localId;
                    TmxTile tile = new TmxTile(globalId, localId, tilesetName, tmxImage);
                    tile.SetTileSize(tmxImage.Size.Width, tmxImage.Size.Height);
                    tile.SetLocationOnSource(0, 0);
                    tilesToAdd.Add(tile);
                }
            }

            StringBuilder builder = new StringBuilder();
            foreach (TmxTile tile in tilesToAdd)
            {
                builder.AppendFormat("{0}", tile.ToString());
                if (tile != tilesToAdd.Last()) builder.Append("\n");
                this.Tiles[tile.GlobalId] = tile;
            }
            Program.WriteLine("Added {0} tiles", tilesToAdd.Count);
            Program.WriteVerbose(builder.ToString());

            // Add any extra data to tiles
            foreach (var elemTile in elemTileset.Elements("tile"))
            {
                int localTileId = TmxHelper.GetAttributeAsInt(elemTile, "id");
                var tiles = from t in this.Tiles
                            where t.Value.GlobalId == localTileId + firstId
                            select t.Value;
                tiles.First().ParseXml(elemTile, this, firstId);
            }
        }

        private void ParseExternalTileset(string tsxPath, uint firstId)
        {
            string fullTsxPath = Path.GetFullPath(tsxPath);
            using (ChDir chdir = new ChDir(fullTsxPath))
            {
                XDocument tsx = LoadDocument(fullTsxPath);
                ParseInternalTileset(tsx.Root, firstId);
            }
        }

        private void ParseAllLayers(XDocument doc)
        {
            Program.WriteLine("Parsing layer elements ...");
            var layers = (from item in doc.Descendants("layer")
                           select item).ToList();

            foreach (var lay in layers)
            {
                TmxLayer tmxLayer = TmxLayer.FromXml(lay, this.Layers.Count);

                // Layers may be ignored
                if (tmxLayer.Properties.GetPropertyValueAsBoolean("unity:ignore", false) == true)
                {
                    // We don't care about this layer
                    Program.WriteLine("Ignoring layer due to unity:ignore property: {0}", tmxLayer.UniqueName);
                    continue;
                }

                int maxVertices = 65535;
                int numVertices = (from tileId in tmxLayer.TileIds
                                   where tileId != 0
                                   select tileId).Count() * 4;
                if (numVertices > maxVertices)
                {
                    Program.WriteWarning("Layer '{0}' will have more than {1} vertices (vertex count = {2}) and will be split into {3} parts by Unity.",
                        tmxLayer.UniqueName,
                        maxVertices,
                        numVertices,
                        numVertices / maxVertices + 1);
                }

                this.Layers.Add(tmxLayer);
            }
        }

        private void ParseAllObjectGroups(XDocument doc)
        {
            Program.WriteLine("Parsing objectgroup elements ...");
            var groups = from item in doc.Root.Elements("objectgroup")
                         select item;

            foreach (var g in groups)
            {
                TmxObjectGroup tmxObjectGroup = TmxObjectGroup.FromXml(g, this);
                this.ObjectGroups.Add(tmxObjectGroup);
            }
        }

    }
}
