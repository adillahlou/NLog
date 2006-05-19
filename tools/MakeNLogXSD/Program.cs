using System;
using System.Text;

using NLog;
using NLog.Config;
using NLog.Targets.Wrappers;
using NLog.Targets.Compound;

using System.Xml;
using System.Reflection;
using System.IO;
using System.Collections;
using System.Xml.Schema;
using NLog.Conditions;

namespace MakeNLogXSD
{
    class Program
    {
        static Hashtable _typeDumped = new Hashtable();

        static string SimpleTypeName(Type t)
        {
            if (t == typeof(byte))
                return "xs:byte";
            if (t == typeof(int))
                return "xs:integer";
            if (t == typeof(long))
                return "xs:long";
            if (t == typeof(string))
                return "xs:string";
            if (t == typeof(bool))
                return "xs:boolean";

            TargetAttribute ta = (TargetAttribute)Attribute.GetCustomAttribute(t, typeof(TargetAttribute));
            if (ta != null)
                return ta.Name;

            return t.Name;
        }

        static string MakeCamelCase(string s)
        {
            if (s.Length < 1)
                return s.ToLower();

            int firstLower = s.Length;
            for (int i = 0; i < s.Length; ++i)
            {
                if (Char.IsLower(s[i]))
                {
                    firstLower = i;
                    break;
                }
            }

            if (firstLower == 0)
                return s;

            // DBType
            if (firstLower != 1 && firstLower != s.Length)
                firstLower--;
            return s.Substring(0, firstLower).ToLower() + s.Substring(firstLower);
        }

        static void DumpEnum(XmlTextWriter xtw, Type t)
        {
            xtw.WriteStartElement("xs:simpleType");
            xtw.WriteAttributeString("name", SimpleTypeName(t));

            xtw.WriteStartElement("xs:restriction");
            xtw.WriteAttributeString("base", "xs:string");

            foreach (FieldInfo fi in t.GetFields())
            {
                xtw.WriteStartElement("xs:enumeration");
                xtw.WriteAttributeString("value", fi.Name);
                xtw.WriteEndElement();

            }

            xtw.WriteEndElement(); // xs:restriction
            xtw.WriteEndElement(); // xs:simpleType
        }

        static void DumpType(XmlTextWriter xtw, Type t)
        {
            if (_typeDumped[t] != null)
                return;

            _typeDumped[t] = t;

            if (t.IsArray)
                return;

            if (t.IsPrimitive)
                return;

            if (t == typeof(string))
                return;

            if (t.IsEnum)
            {
                DumpEnum(xtw, t);
                return;
            }

            ArrayList typesToDump = new ArrayList();

            typesToDump.Add(t.BaseType);

            xtw.WriteStartElement("xs:complexType");
            xtw.WriteAttributeString("name", SimpleTypeName(t));

            if (t.BaseType != typeof(object))
            {
                xtw.WriteStartElement("xs:complexContent");
                xtw.WriteStartElement("xs:extension");
                xtw.WriteAttributeString("base", SimpleTypeName(t.BaseType));
            }

            xtw.WriteStartElement("xs:choice");
            xtw.WriteAttributeString("minOccurs", "0");
            xtw.WriteAttributeString("maxOccurs", "unbounded");

            foreach (PropertyInfo pi in t.GetProperties())
            {
                if (pi.DeclaringType != t)
                    continue;

                ArrayParameterAttribute apa = (ArrayParameterAttribute)Attribute.GetCustomAttribute(pi, typeof(ArrayParameterAttribute));
                if (apa != null)
                {
                    xtw.WriteStartElement("xs:element");
                    xtw.WriteAttributeString("name", apa.ElementName);
                    xtw.WriteAttributeString("type", SimpleTypeName(apa.ItemType));
                    xtw.WriteAttributeString("minOccurs", "0");
                    xtw.WriteAttributeString("maxOccurs", "unbounded");
                    xtw.WriteEndElement();
                    typesToDump.Add(apa.ItemType);
                }
            }

            xtw.WriteEndElement();

            foreach (PropertyInfo pi in t.GetProperties())
            {
                if (pi.DeclaringType != t)
                    continue;

                if (pi.IsDefined(typeof(ArrayParameterAttribute), false))
                    continue;

                if (pi.PropertyType == typeof(Target) || pi.PropertyType == typeof(TargetCollection))
                    continue;

                if (pi.PropertyType == typeof(Layout) || pi.PropertyType == typeof(ConditionExpression))
                    continue;

                if (!pi.CanWrite)
                    continue;

                xtw.WriteStartElement("xs:attribute");
                xtw.WriteAttributeString("name", MakeCamelCase(pi.Name));
                if (pi.IsDefined(typeof(AcceptsLayoutAttribute), false))
                    xtw.WriteAttributeString("type", "NLogLayout");
                else if (pi.IsDefined(typeof(AcceptsConditionAttribute), false))
                    xtw.WriteAttributeString("type", "NLogCondition");
                else
                    xtw.WriteAttributeString("type", SimpleTypeName(pi.PropertyType));
                typesToDump.Add(pi.PropertyType);
                xtw.WriteEndElement();
            }

            if (typeof(Target).IsAssignableFrom(t))
            {
                TargetAttribute ta = (TargetAttribute)Attribute.GetCustomAttribute(t, typeof(TargetAttribute));
                if (ta != null && !ta.IgnoresLayout)
                {
                    xtw.WriteStartElement("xs:attribute");
                    xtw.WriteAttributeString("name", "layout");
                    xtw.WriteAttributeString("type", "NLogLayout");
                    xtw.WriteEndElement();
                }
            }

            if (t.BaseType != typeof(object))
            {
                xtw.WriteEndElement(); // xs:extension
                xtw.WriteEndElement(); // xs:complexContent
            }

            xtw.WriteEndElement(); // xs:complexType

            foreach (Type ttd in typesToDump)
            {
                DumpType(xtw, ttd);
            }
        }

        static int Main(string[] args)
        {
            if (args.Length != 1)
            {
                Console.WriteLine("Usage: MakeNLogXSD outputfile.xsd");
                return 1;
            }

            try
            {

                StringWriter sw = new StringWriter();

                sw.Write("<root xmlns:xs=\"http://www.w3.org/2001/XMLSchema\">");

                XmlTextWriter xtw = new XmlTextWriter(sw);
                xtw.Namespaces = false;
                xtw.Formatting = Formatting.Indented;

                _typeDumped[typeof(object)] = 1;
                _typeDumped[typeof(Target)] = 1;
                _typeDumped[typeof(Layout)] = 1;

                foreach (Type targetType in NLog.TargetFactory.TargetTypes)
                {
                    DumpType(xtw, targetType);
                }
                xtw.Flush();
                sw.Write("</root>");
                sw.Flush();

                XmlDocument doc2 = new XmlDocument();

                doc2.LoadXml(sw.ToString());

                using (Stream templateStream = Assembly.GetEntryAssembly().GetManifestResourceStream("MakeNLogXSD.TemplateNLog.xsd"))
                {
                    XmlDocument doc = new XmlDocument();
                    doc.Load(templateStream);

                    XmlNamespaceManager nsmgr = new XmlNamespaceManager(doc.NameTable);
                    nsmgr.AddNamespace("", "http://www.w3.org/2001/XMLSchema");

                    XmlNode n = doc.SelectSingleNode("//types-go-here");

                    foreach (XmlElement el in doc2.DocumentElement.ChildNodes)
                    {
                        XmlNode importedNode = doc.ImportNode(el, true);
                        n.ParentNode.InsertBefore(importedNode, n);
                    }
                    n.ParentNode.RemoveChild(n);
                    Console.WriteLine("Saving schema to: {0}", args[0]);
                    doc.Save(args[0]);
                    return 0;
                }


            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR: {0}", ex);
                return 1;
            }
        }
    }
}
