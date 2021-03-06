﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Media.Media3D;

namespace Tiled2Unity
{
    // Partial class that concentrates on creating the Wavefront Mesh (.obj) string
    partial class TiledMapExporter
    {
        // Helper class to treat tiles as frames of an animation
        // Handles non-animation tiles as a single frame
        class TileFrame
        {
            public TmxTile Tile { get; private set; }
            public float Position_z { get; private set; }

            public static IEnumerable<TileFrame> EnumerateFramesFromTile(TmxTile tile, TmxMap map)
            {
                if (tile.Animation == null)
                {
                    // Treat the tile as a single-frame animation
                    yield return new TileFrame { Tile = tile, Position_z = 0 };
                }
                else
                {
                    // Visit all the frames of the animated tile
                    float sign = 1.0f;
                    foreach (var f in tile.Animation.Frames)
                    {
                        // The frame Id is baked into the z value of the tile
                        // (Negative values frames are not shown, so we start off with only the first tile being positive/visible)
                        float z = (float)f.UniqueFrameId * sign;
                        yield return new TileFrame { Tile = map.Tiles[f.GlobalTileId], Position_z = z };

                        // Next frames start off invisible / negative
                        sign = -1.0f;
                    }
                }
            }
        }

        private StringWriter BuildObjString()
        {
            // Creates the text for a Wavefront OBJ file for the TmxMap
            StringWriter objWriter = new StringWriter();

            // Gather the information for every face
            var faces = from layer in this.tmxMap.Layers
                        where layer.Visible == true
                        where layer.Properties.GetPropertyValueAsBoolean("unity:collisionOnly", false) == false

                        // Draw order forces us to visit tiles in a particular order
                        from y in (this.tmxMap.DrawOrderVertical == 1) ? Enumerable.Range(0, layer.Height) : Enumerable.Range(0, layer.Height).Reverse()
                        from x in (this.tmxMap.DrawOrderHorizontal == 1) ? Enumerable.Range(0, layer.Width) : Enumerable.Range(0, layer.Width).Reverse()

                        let rawTileId = layer.GetRawTileIdAt(x, y)
                        let tileId = TmxMath.GetTileIdWithoutFlags(rawTileId)
                        where tileId != 0
                        let fd = TmxMath.IsTileFlippedDiagonally(rawTileId)
                        let fh = TmxMath.IsTileFlippedHorizontally(rawTileId)
                        let fv = TmxMath.IsTileFlippedVertically(rawTileId)
                        let animTile = this.tmxMap.Tiles[tileId]

                        // Enumerate through all frames of a tile. (Tiles without animation are treated as a single frame)
                        from frame in TileFrame.EnumerateFramesFromTile(animTile, this.tmxMap)
                        select new
                        {
                            LayerName = layer.UniqueName,
                            Vertices = CalculateFaceVertices(this.tmxMap.GetMapPositionAt(x, y), frame.Tile.TileSize, this.tmxMap.TileHeight, frame.Position_z),
                            TextureCoordinates = CalculateFaceTextureCoordinates(frame.Tile, fd, fh, fv),
                            ImagePath = frame.Tile.TmxImage.Path,
                            ImageName = Path.GetFileNameWithoutExtension(frame.Tile.TmxImage.Path),
                        };

            // We have all the information we need now to build our list of vertices, texture coords, and grouped faces
            // (Faces are grouped by LayerName.TextureName combination because Wavefront Obj only supports one texture per face)
            objWriter.WriteLine("# Wavefront OBJ file automatically generated by Tiled2Unity");
            objWriter.WriteLine();

            // We may have a ton of vertices so use a set right now
            HashSet<Vector3D> vertexSet = new HashSet<Vector3D>();
            Program.WriteLine("Building face vertices");
            foreach (var face in faces)
            {
                // Index the vertices
                foreach (var v in face.Vertices)
                {
                    vertexSet.Add(v);
                }
            }

            HashSet<PointF> textureCoordinateSet = new HashSet<PointF>();
            Program.WriteLine("Building face texture coordinates");
            foreach (var face in faces)
            {
                // Index the texture coordinates
                foreach (var tc in face.TextureCoordinates)
                {
                    textureCoordinateSet.Add(tc);
                }
            }

            // Write the indexed vertices
            Program.WriteLine("Writing indexed vertices");
            IList<Vector3D> vertices = vertexSet.ToList();
            objWriter.WriteLine("# Vertices (Count = {0})", vertices.Count);
            foreach (var v in vertices)
            {
                objWriter.WriteLine("v {0} {1} {2}", v.X, v.Y, v.Z);
            }
            objWriter.WriteLine();

            // Write the indexed texture coordinates
            Program.WriteLine("Writing indexed texture coordinates");
            IList<PointF> textureCoordinates = textureCoordinateSet.ToList();
            objWriter.WriteLine("# Texture Coorindates (Count = {0})", textureCoordinates.Count);
            foreach (var vt in textureCoordinates)
            {
                objWriter.WriteLine("vt {0} {1}", vt.X, vt.Y);
            }
            objWriter.WriteLine();

            // Write the one indexed normal
            objWriter.WriteLine("# Normal");
            objWriter.WriteLine("vn 0 0 -1");
            objWriter.WriteLine();

            // Group faces by Layer+TileSet
            var groups = from f in faces
                         group f by TiledMapExpoterUtils.UnityFriendlyMeshName(tmxMap, f.LayerName, f.ImageName);

            // Write out the faces
            objWriter.WriteLine("# Groups (Count = {0})", groups.Count());

            // Need dictionaries with index as value.
            var vertexDict = Enumerable.Range(0, vertices.Count()).ToDictionary(i => vertices[i], i => i);
            var texCoordDict = Enumerable.Range(0, textureCoordinates.Count()).ToDictionary(i => textureCoordinates[i], i => i);

            foreach (var g in groups)
            {
                Program.WriteLine("Writing '{0}' mesh group", g.Key);

                objWriter.WriteLine("g {0}", g.Key);
                foreach (var f in g)
                {
                    objWriter.Write("f ");
                    for (int i = 0; i < 4; ++i)
                    {
                        int vertexIndex = vertexDict[f.Vertices[i]] + 1;
                        int textureCoordinateIndex = texCoordDict[f.TextureCoordinates[i]] + 1;

                        objWriter.Write(" {0}/{1}/1 ", vertexIndex, textureCoordinateIndex);
                    }
                    objWriter.WriteLine();
                }
            }

            Program.WriteLine("Done writing Wavefront Obj data for '{0}'", tmxMap.Name);

            return objWriter;
        }

        private Vector3D[] CalculateFaceVertices(Point mapLocation, Size tileSize, int mapTileHeight, float pos_z)
        {
            // Location on map is complicated by tiles that are 'higher' than the tile size given for the overall map
            mapLocation.Offset(0, -tileSize.Height + mapTileHeight);

            PointF pt0 = mapLocation;
            PointF pt1 = PointF.Add(mapLocation, new Size(tileSize.Width, 0));
            PointF pt2 = PointF.Add(mapLocation, tileSize);
            PointF pt3 = PointF.Add(mapLocation, new Size(0, tileSize.Height));

            // We need to use ccw winding for Wavefront objects
            Vector3D[] vertices  = new Vector3D[4];
            vertices[3] = PointFToObjVertex(pt0, pos_z);
            vertices[2] = PointFToObjVertex(pt1, pos_z);
            vertices[1] = PointFToObjVertex(pt2, pos_z);
            vertices[0] = PointFToObjVertex(pt3, pos_z);
            return vertices;
        }

        private PointF[] CalculateFaceTextureCoordinates(TmxTile tmxTile, bool flipDiagonal, bool flipHorizontal, bool flipVertical)
        {
            Point imageLocation = tmxTile.LocationOnSource;
            Size tileSize = tmxTile.TileSize;
            Size imageSize = tmxTile.TmxImage.Size;

            PointF[] points = new PointF[4];
            points[0] = imageLocation;
            points[1] = PointF.Add(imageLocation, new Size(tileSize.Width, 0));
            points[2] = PointF.Add(imageLocation, tileSize);
            points[3] = PointF.Add(imageLocation, new Size(0, tileSize.Height));

            PointF center = new PointF(tileSize.Width * 0.5f, tileSize.Height * 0.5f);
            center.X += imageLocation.X;
            center.Y += imageLocation.Y;
            TmxMath.TransformPoints_DiagFirst(points, center, flipDiagonal, flipHorizontal, flipVertical);
            //TmxMath.TransformPoints(points, center, flipDiagonal, flipHorizontal, flipVertical);

            PointF[] coordinates = new PointF[4];
            coordinates[3] = PointToTextureCoordinate(points[0], imageSize);
            coordinates[2] = PointToTextureCoordinate(points[1], imageSize);
            coordinates[1] = PointToTextureCoordinate(points[2], imageSize);
            coordinates[0] = PointToTextureCoordinate(points[3], imageSize);

            // Apply a small bias to the "inner" edges of the texels
            // This keeps us from seeing seams
            // (If seams continue along "outer" edges we can try applying the bias there as well)
            // Note: On Oct 25, a user was having issues with outer edges, so I brought those in as well afterall (for version 0.9.5.4)
            //const float bias = 1.0f / 8192.0f;
            //const float bias = 1.0f / 4096.0f;
            //const float bias = 1.0f / 2048.0f;
            float bias = 1.0f / Program.TexelBias;
            coordinates[0].X += bias;
            coordinates[0].Y += bias;

            coordinates[1].X -= bias;
            coordinates[1].Y += bias;

            coordinates[2].X -= bias;
            coordinates[2].Y -= bias;

            coordinates[3].X += bias;
            coordinates[3].Y -= bias;

            return coordinates;
        }
    }
}
