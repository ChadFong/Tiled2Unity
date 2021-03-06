﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Tiled2Unity
{
    public partial class TmxObjectGroup
    {
        public static TmxObjectGroup FromXml(XElement xml, TmxMap tmxMap)
        {
            Debug.Assert(xml.Name == "objectgroup");

            TmxObjectGroup tmxObjectGroup = new TmxObjectGroup();

            tmxObjectGroup.Name = TmxHelper.GetAttributeAsString(xml, "name", "");
            tmxObjectGroup.Visible = TmxHelper.GetAttributeAsInt(xml, "visible", 1) == 1;
            tmxObjectGroup.Color = TmxHelper.GetAttributeAsColor(xml, "color", Color.FromArgb(128, 128, 128));
            tmxObjectGroup.Properties = TmxProperties.FromXml(xml);

            // Get all the objects
            Program.WriteLine("Parsing objects in object group '{0}'", tmxObjectGroup.Name);
            var objects = from obj in xml.Elements("object")
                            select TmxObject.FromXml(obj, tmxMap);

            tmxObjectGroup.Objects = objects.ToList();

            return tmxObjectGroup;
        }

    }
}
