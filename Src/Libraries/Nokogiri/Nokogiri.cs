/* ****************************************************************************
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A
 * copy of the license can be found in the License.html file at the root of this distribution. If
 * you cannot locate the  Apache License, Version 2.0, please send an email to
 * ironruby@microsoft.com. By using this source code in any fashion, you are agreeing to be bound
 * by the terms of the Apache License, Version 2.0.
 *
 * You must not remove this notice, or any other, from this software.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using AngleSharp.Xml.Parser;
using IronRuby.Builtins;
using IronRuby.Runtime;

namespace IronRuby.StandardLibrary.Nokogiri {

    /// <summary>
    /// The DOM primitives Nokogiri is built on here.
    ///
    /// Nokogiri is a C extension over libxml2 (XML, HTML4) and gumbo (HTML5).  Neither can be
    /// compiled here, and nothing in the .NET base class library parses tag soup - XmlDocument
    /// rejects the first unclosed &lt;p&gt;.  So the parser underneath is AngleSharp, which
    /// implements the WHATWG tokenizer and tree-construction algorithm that gumbo implements,
    /// and whose DOM is a real mutable DOM with a CSS selector engine.
    ///
    /// The split is the one io/console and Ripper already use: everything that needs a member
    /// C# can reach but Ruby cannot - AngleSharp implements INode explicitly, so ChildNodes,
    /// ParentNode and Owner are invisible to a dynamic call - is here, and every bit of
    /// Nokogiri-shaped behaviour (node classes, NodeSet, serialization, CSS and XPath
    /// semantics, document decorators) is Ruby, in Src/StdLib/ironruby/nokogiri.
    ///
    /// Nodes cross the boundary as the AngleSharp objects themselves; the Ruby side keeps one
    /// wrapper per node in a per-document identity map.
    /// </summary>
    [RubyModule("Nokogiri")]
    public static class NokogiriOps {

        /// <summary>
        /// The primitives.  Not public API: Src/StdLib/ironruby/nokogiri is the only caller,
        /// and it is what the name Nokogiri means to a Ruby program.
        /// </summary>
        [RubyModule("Native")]
        public static class NativeOps {

            #region parsing

            private static readonly HtmlParserOptions HtmlOptions = new HtmlParserOptions {
                IsNotConsumingCharacterReferences = false,
                IsKeepingSourceReferences = false,
                IsScripting = false,
            };

            private static string Str(MutableString s) {
                return s == null ? null : s.ConvertToString();
            }

            private static MutableString Out(string s) {
                return s == null ? null : MutableString.Create(s, RubyEncoding.UTF8);
            }

            /// <summary>
            /// Parse a complete HTML document the way the WHATWG algorithm says to, which is
            /// what Nokogiri::HTML5.parse does.  Never raises: tag soup is what this is for.
            /// </summary>
            [RubyMethod("parse_html", RubyMethodAttributes.PublicSingleton)]
            public static object ParseHtml(RubyModule/*!*/ self, [DefaultProtocol]MutableString markup) {
                var parser = new HtmlParser(HtmlOptions);
                return parser.ParseDocument(Str(markup) ?? String.Empty);
            }

            /// <summary>
            /// An empty HTML document, the tree Nokogiri::HTML4::Document.new answers.
            /// </summary>
            [RubyMethod("new_html_document", RubyMethodAttributes.PublicSingleton)]
            public static object NewHtmlDocument(RubyModule/*!*/ self) {
                return new HtmlParser(HtmlOptions).ParseDocument(String.Empty);
            }

            /// <summary>
            /// Parse XML.  Returns the document, or a String holding the first error when the
            /// markup is not well formed - the Ruby side turns that into
            /// Nokogiri::XML::SyntaxError, or swallows it in the recover mode Nokogiri parses
            /// in by default.
            /// </summary>
            [RubyMethod("parse_xml", RubyMethodAttributes.PublicSingleton)]
            public static object ParseXml(RubyModule/*!*/ self, [DefaultProtocol]MutableString markup) {
                try {
                    var parser = new XmlParser(new XmlParserOptions { IsSuppressingErrors = true });
                    return parser.ParseDocument(Str(markup) ?? String.Empty);
                } catch (Exception e) {
                    return Out(e.Message);
                }
            }

            /// <summary>
            /// The HTML fragment-parsing algorithm, run in the context of
            /// <paramref name="context"/> (a &lt;body&gt; when the caller has nothing better),
            /// with the results adopted into <paramref name="document"/>.
            /// </summary>
            [RubyMethod("parse_html_fragment", RubyMethodAttributes.PublicSingleton)]
            public static RubyArray/*!*/ ParseHtmlFragment(RubyModule/*!*/ self, [DefaultProtocol]MutableString markup,
                object context, object document) {

                var ctx = context as IElement;
                var doc = document as IDocument;
                var result = new RubyArray();
                if (ctx == null || doc == null) {
                    return result;
                }
                var parser = new HtmlParser(HtmlOptions);
                var nodes = parser.ParseFragment(Str(markup) ?? String.Empty, ctx);
                // ParseFragment hands back nodes still owned by the parser's own document;
                // adopting them first is what makes AppendChild legal.
                var list = new List<INode>();
                foreach (var node in nodes) {
                    list.Add(node);
                }
                foreach (var node in list) {
                    result.Add(doc.Adopt(node));
                }
                return result;
            }

            #endregion

            #region shape

            /// <summary>
            /// The libxml2 node type numbers Nokogiri::XML::Node exposes as constants, which
            /// are not AngleSharp's numbering: libxml2 counts CDATA 4 and comments 8, the DOM
            /// counts CDATA 4 and comments 8 too, but document fragments 11 against AngleSharp's
            /// own enum value.  Mapped by name so a change in either numbering is caught here.
            /// </summary>
            [RubyMethod("node_type", RubyMethodAttributes.PublicSingleton)]
            public static int NodeType(RubyModule/*!*/ self, object node) {
                var n = node as INode;
                if (n == null) {
                    return 0;
                }
                switch (n.NodeType) {
                    case AngleSharp.Dom.NodeType.Element: return 1;
                    case AngleSharp.Dom.NodeType.Attribute: return 2;
                    case AngleSharp.Dom.NodeType.Text: return 3;
                    case AngleSharp.Dom.NodeType.CharacterData: return 4;
                    case AngleSharp.Dom.NodeType.EntityReference: return 5;
                    case AngleSharp.Dom.NodeType.Entity: return 6;
                    case AngleSharp.Dom.NodeType.ProcessingInstruction: return 7;
                    case AngleSharp.Dom.NodeType.Comment: return 8;
                    case AngleSharp.Dom.NodeType.Document: return 9;
                    case AngleSharp.Dom.NodeType.DocumentType: return 10;
                    case AngleSharp.Dom.NodeType.DocumentFragment: return 11;
                    case AngleSharp.Dom.NodeType.Notation: return 12;
                    default: return 0;
                }
            }

            /// <summary>
            /// The element's name as Nokogiri spells it: lower case for HTML (AngleSharp keeps
            /// NodeName upper case, the DOM's own convention), prefix included for XML.
            /// </summary>
            [RubyMethod("element_name", RubyMethodAttributes.PublicSingleton)]
            public static MutableString ElementName(RubyModule/*!*/ self, object node) {
                var e = node as IElement;
                if (e == null) {
                    var n = node as INode;
                    return n == null ? null : Out(n.NodeName);
                }
                return Out(String.IsNullOrEmpty(e.Prefix) ? e.LocalName : e.Prefix + ":" + e.LocalName);
            }

            /// <summary>
            /// The element's namespace.  It decides whether &lt;style&gt; is a raw-text element
            /// (HTML) or an ordinary one whose text is escaped on the way out (inside &lt;svg&gt;
            /// or &lt;math&gt;) - the difference two of rails-html-sanitizer's mXSS regression
            /// tests are about.
            /// </summary>
            [RubyMethod("namespace_uri", RubyMethodAttributes.PublicSingleton)]
            public static MutableString NamespaceUri(RubyModule/*!*/ self, object node) {
                var e = node as IElement;
                return e == null ? null : Out(e.NamespaceUri);
            }

            [RubyMethod("children", RubyMethodAttributes.PublicSingleton)]
            public static RubyArray/*!*/ Children(RubyModule/*!*/ self, object node) {
                var result = new RubyArray();
                var n = node as INode;
                if (n != null) {
                    foreach (var child in n.ChildNodes) {
                        result.Add(child);
                    }
                }
                return result;
            }

            [RubyMethod("parent", RubyMethodAttributes.PublicSingleton)]
            public static object Parent(RubyModule/*!*/ self, object node) {
                var n = node as INode;
                return n == null ? null : n.Parent;
            }

            [RubyMethod("owner_document", RubyMethodAttributes.PublicSingleton)]
            public static object OwnerDocument(RubyModule/*!*/ self, object node) {
                if (node is IDocument) {
                    return node;
                }
                var n = node as INode;
                return n == null ? null : n.Owner;
            }

            [RubyMethod("next_sibling", RubyMethodAttributes.PublicSingleton)]
            public static object NextSibling(RubyModule/*!*/ self, object node) {
                var n = node as INode;
                return n == null ? null : n.NextSibling;
            }

            [RubyMethod("previous_sibling", RubyMethodAttributes.PublicSingleton)]
            public static object PreviousSibling(RubyModule/*!*/ self, object node) {
                var n = node as INode;
                return n == null ? null : n.PreviousSibling;
            }

            [RubyMethod("document_element", RubyMethodAttributes.PublicSingleton)]
            public static object DocumentElement(RubyModule/*!*/ self, object document) {
                var doc = document as IDocument;
                return doc == null ? null : doc.DocumentElement;
            }

            [RubyMethod("body", RubyMethodAttributes.PublicSingleton)]
            public static object Body(RubyModule/*!*/ self, object document) {
                var doc = document as IHtmlDocument;
                return doc == null ? null : doc.Body;
            }

            /// <summary>
            /// The character data of a text, comment or CDATA node - not the concatenated text
            /// of a subtree, which is <see cref="TextContent"/>.
            /// </summary>
            [RubyMethod("node_value", RubyMethodAttributes.PublicSingleton)]
            public static MutableString NodeValue(RubyModule/*!*/ self, object node) {
                var n = node as INode;
                return n == null ? null : Out(n.NodeValue);
            }

            [RubyMethod("set_node_value", RubyMethodAttributes.PublicSingleton)]
            public static object SetNodeValue(RubyModule/*!*/ self, object node, [DefaultProtocol]MutableString value) {
                var n = node as INode;
                if (n != null) {
                    n.NodeValue = Str(value) ?? String.Empty;
                }
                return value;
            }

            [RubyMethod("text_content", RubyMethodAttributes.PublicSingleton)]
            public static MutableString TextContent(RubyModule/*!*/ self, object node) {
                var n = node as INode;
                return n == null ? null : Out(n.TextContent);
            }

            [RubyMethod("set_text_content", RubyMethodAttributes.PublicSingleton)]
            public static object SetTextContent(RubyModule/*!*/ self, object node, [DefaultProtocol]MutableString value) {
                var n = node as INode;
                if (n != null) {
                    n.TextContent = Str(value) ?? String.Empty;
                }
                return value;
            }

            #endregion

            #region mutation

            [RubyMethod("append_child", RubyMethodAttributes.PublicSingleton)]
            public static object AppendChild(RubyModule/*!*/ self, object parent, object child) {
                var p = parent as INode;
                var c = child as INode;
                if (p == null || c == null) {
                    return null;
                }
                return p.AppendChild(Adopted(p, c));
            }

            [RubyMethod("insert_before", RubyMethodAttributes.PublicSingleton)]
            public static object InsertBefore(RubyModule/*!*/ self, object parent, object newChild, object refChild) {
                var p = parent as INode;
                var c = newChild as INode;
                if (p == null || c == null) {
                    return null;
                }
                return p.InsertBefore(Adopted(p, c), refChild as INode);
            }

            [RubyMethod("remove_child", RubyMethodAttributes.PublicSingleton)]
            public static object RemoveChild(RubyModule/*!*/ self, object parent, object child) {
                var p = parent as INode;
                var c = child as INode;
                if (p == null || c == null) {
                    return null;
                }
                return p.RemoveChild(c);
            }

            [RubyMethod("replace_child", RubyMethodAttributes.PublicSingleton)]
            public static object ReplaceChild(RubyModule/*!*/ self, object parent, object newChild, object oldChild) {
                var p = parent as INode;
                var n = newChild as INode;
                var o = oldChild as INode;
                if (p == null || n == null || o == null) {
                    return null;
                }
                return p.ReplaceChild(Adopted(p, n), o);
            }

            private static INode Adopted(INode parent, INode child) {
                var owner = parent as IDocument ?? parent.Owner;
                if (owner != null && child.Owner != owner && !(child is IDocument)) {
                    return owner.Adopt(child);
                }
                return child;
            }

            /// <summary>
            /// Nokogiri's Node#name= renames an element in place.  A DOM element's name is
            /// immutable, so this builds the replacement, moves the attributes and children
            /// across, and puts it where the old one was; the Ruby wrapper re-points at the
            /// result so that a caller holding the node keeps holding it.
            /// </summary>
            [RubyMethod("rename", RubyMethodAttributes.PublicSingleton)]
            public static object Rename(RubyModule/*!*/ self, object node, [DefaultProtocol]MutableString name) {
                var old = node as IElement;
                if (old == null) {
                    return node;
                }
                var doc = old.Owner;
                if (doc == null) {
                    return node;
                }
                var replacement = doc.CreateElement(Str(name));
                foreach (var attribute in old.Attributes) {
                    replacement.SetAttribute(attribute.NamespaceUri, attribute.Name, attribute.Value);
                }
                var children = new List<INode>();
                foreach (var child in old.ChildNodes) {
                    children.Add(child);
                }
                foreach (var child in children) {
                    old.RemoveChild(child);
                    replacement.AppendChild(child);
                }
                var parent = old.Parent;
                if (parent != null) {
                    parent.ReplaceChild(replacement, old);
                }
                return replacement;
            }

            #endregion

            #region attributes

            /// <summary>
            /// The element's attributes, in document order, as a flat [name, value, prefix,
            /// namespace_uri, ...].  One call rather than four per attribute: scrubbing walks
            /// every attribute of every node.
            /// </summary>
            [RubyMethod("attributes", RubyMethodAttributes.PublicSingleton)]
            public static RubyArray/*!*/ Attributes(RubyModule/*!*/ self, object node) {
                var result = new RubyArray();
                var e = node as IElement;
                if (e != null) {
                    foreach (var attribute in e.Attributes) {
                        // Name drops the prefix for a namespaced attribute; xlink:href has to
                        // stay xlink:href, because that is the name the safe lists carry.
                        result.Add(Out(String.IsNullOrEmpty(attribute.Prefix)
                            ? attribute.Name
                            : attribute.Prefix + ":" + attribute.LocalName));
                        result.Add(Out(attribute.Value));
                        result.Add(Out(attribute.Prefix));
                        result.Add(Out(attribute.NamespaceUri));
                    }
                }
                return result;
            }

            [RubyMethod("get_attribute", RubyMethodAttributes.PublicSingleton)]
            public static MutableString GetAttribute(RubyModule/*!*/ self, object node, [DefaultProtocol]MutableString name) {
                var e = node as IElement;
                if (e == null) {
                    return null;
                }
                var attribute = e.Attributes.GetNamedItem(Str(name));
                return attribute == null ? null : Out(attribute.Value);
            }

            [RubyMethod("set_attribute", RubyMethodAttributes.PublicSingleton)]
            public static object SetAttribute(RubyModule/*!*/ self, object node,
                [DefaultProtocol]MutableString name, [DefaultProtocol]MutableString value) {

                var e = node as IElement;
                if (e == null) {
                    return null;
                }
                try {
                    e.SetAttribute(Str(name), Str(value) ?? String.Empty);
                } catch (DomException) {
                    // An attribute name the DOM refuses (a space, a quote) is one libxml2
                    // simply keeps.  Dropping it is the safe direction for a sanitizer.
                    return null;
                }
                return value;
            }

            [RubyMethod("remove_attribute", RubyMethodAttributes.PublicSingleton)]
            public static object RemoveAttribute(RubyModule/*!*/ self, object node, [DefaultProtocol]MutableString name) {
                var e = node as IElement;
                if (e != null) {
                    e.RemoveAttribute(Str(name));
                }
                return null;
            }

            #endregion

            #region creation and selection

            [RubyMethod("create_element", RubyMethodAttributes.PublicSingleton)]
            public static object CreateElement(RubyModule/*!*/ self, object document, [DefaultProtocol]MutableString name) {
                var doc = document as IDocument;
                if (doc == null) {
                    return null;
                }
                try {
                    return doc.CreateElement(Str(name));
                } catch (DomException) {
                    return null;
                }
            }

            [RubyMethod("create_text", RubyMethodAttributes.PublicSingleton)]
            public static object CreateText(RubyModule/*!*/ self, object document, [DefaultProtocol]MutableString text) {
                var doc = document as IDocument;
                return doc == null ? null : doc.CreateTextNode(Str(text) ?? String.Empty);
            }

            [RubyMethod("create_comment", RubyMethodAttributes.PublicSingleton)]
            public static object CreateComment(RubyModule/*!*/ self, object document, [DefaultProtocol]MutableString text) {
                var doc = document as IDocument;
                return doc == null ? null : doc.CreateComment(Str(text) ?? String.Empty);
            }

            /// <summary>
            /// A copy of the node, deep or shallow.  Nokogiri's #dup is a copy of the tree,
            /// not a round trip through markup - the difference shows the moment a document
            /// serializes to something its own parser reads back differently.
            /// </summary>
            [RubyMethod("clone_node", RubyMethodAttributes.PublicSingleton)]
            public static object CloneNode(RubyModule/*!*/ self, object node, bool deep) {
                var n = node as INode;
                return n == null ? null : n.Clone(deep);
            }

            [RubyMethod("create_fragment", RubyMethodAttributes.PublicSingleton)]
            public static object CreateFragment(RubyModule/*!*/ self, object document) {
                var doc = document as IDocument;
                return doc == null ? null : doc.CreateDocumentFragment();
            }

            /// <summary>
            /// The CSS selector engine, over the descendants of <paramref name="node"/>.
            /// Answers nil - not an empty list - for a selector the engine cannot parse, so
            /// that the Ruby side can raise Nokogiri::CSS::SyntaxError.
            /// </summary>
            [RubyMethod("query_selector_all", RubyMethodAttributes.PublicSingleton)]
            public static object QuerySelectorAll(RubyModule/*!*/ self, object node, [DefaultProtocol]MutableString selector) {
                var parent = node as IParentNode;
                if (parent == null) {
                    return new RubyArray();
                }
                IHtmlCollection<IElement> found;
                try {
                    found = parent.QuerySelectorAll(Str(selector));
                } catch (DomException) {
                    return null;
                } catch (Exception) {
                    return null;
                }
                var result = new RubyArray(found.Length);
                foreach (var element in found) {
                    result.Add(element);
                }
                return result;
            }

            /// <summary>
            /// Whether the node itself matches the selector - Nokogiri::XML::Node#matches?.
            /// </summary>
            [RubyMethod("matches", RubyMethodAttributes.PublicSingleton)]
            public static object Matches(RubyModule/*!*/ self, object node, [DefaultProtocol]MutableString selector) {
                var e = node as IElement;
                if (e == null) {
                    return false;
                }
                try {
                    return e.Matches(Str(selector));
                } catch (Exception) {
                    return null;
                }
            }

            #endregion
        }
    }
}
