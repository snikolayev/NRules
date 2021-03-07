﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;

namespace NRules.Diagnostics.Dgml
{
    /// <summary>
    /// Creates a <see href="https://en.wikipedia.org/wiki/DGML">DGML</see> document writer
    /// that can be used to serialize a Rete graph for a given rules session into XML.
    /// </summary>
    public class DgmlWriter
    {
        private readonly ReteGraph _schema;

        private HashSet<string> _ruleNameFilter;
        private IMetricsProvider _metricsProvider;
        
        /// <summary>
        /// Sets a filter for Rete graph nodes, such that only nodes that belong to the given
        /// set of rules is serialized, along with the connecting graph edges.
        /// </summary>
        /// <param name="ruleNames">Set of rules to use as a filter, or <c>null</c> to remove the filter.</param>
        public void SetRuleFilter(IEnumerable<string> ruleNames)
        {
            _ruleNameFilter = ruleNames == null ? null : new HashSet<string>(ruleNames);
        }

        /// <summary>
        /// Sets the <see cref="IMetricsProvider"/> to retrieve performance metrics for serialized nodes,
        /// so that performance metrics are included in the output.
        /// </summary>
        /// <param name="metricsProvider">Performance metrics provider or <c>null</c> to exclude performance metrics from the output.</param>
        public void SetMetricsProvider(IMetricsProvider metricsProvider)
        {
            _metricsProvider = metricsProvider;
        }

        /// <summary>
        /// Creates an instance of a <c>DgmlWriter</c> for a given session schema.
        /// </summary>
        /// <param name="schema">Rules session schema.</param>
        public DgmlWriter(ReteGraph schema)
        {
            _schema = schema;
        }

        /// <summary>
        /// Writes DGML graph representing a given rules session to a file.
        /// </summary>
        /// <param name="fileName">File to write the session to.</param>
        public void WriteAllText(string fileName)
        {
            string contents = GetContents();
            File.WriteAllText(fileName, contents);
        }

        /// <summary>
        /// Writes DGML graph representing a given rules session to the provided
        /// <see cref="XmlWriter"/>.
        /// </summary>
        /// <param name="writer"><see cref="XmlWriter"/> to write the session to.</param>
        public void WriteXml(XmlWriter writer)
        {
            var graph = CreateGraph();
            writer.WriteStartDocument();
            graph.WriteXml(writer);
            writer.WriteEndDocument();
        }

        /// <summary>
        /// Retrieves a serialized DGML graph as an XML string.
        /// </summary>
        /// <returns>Contents of the serialized DGML graph as an XML string.</returns>
        public string GetContents()
        {
            using var stringWriter = new Utf8StringWriter();
            var xmlWriter = new XmlTextWriter(stringWriter);
            xmlWriter.Formatting = Formatting.Indented;
            WriteXml(xmlWriter);
            var contents = stringWriter.ToString();
            return contents;
        }

        private DirectedGraph CreateGraph()
        {
            var graph = new DirectedGraph {Title = "ReteNetwork"};
            graph.Nodes.AddRange(CreateNodes(Filter(_schema.Nodes)));
            graph.Links.AddRange(CreateLinks(Filter(_schema.Links)));
            graph.Categories.AddRange(CreateNodeTypeCategories());

            if (_metricsProvider != null)
                AddPerformanceMetrics(graph);
            else
                graph.Styles.AddRange(CreateSchemaStyles());

            return graph;
        }

        private IEnumerable<Node> CreateNodes(IEnumerable<ReteNode> reteNodes)
        {
            foreach (var reteNode in reteNodes)
            {
                var node = new Node(Id(reteNode))
                {
                    Label = GetNodeLabel(reteNode),
                    Category = reteNode.NodeType.ToString()
                };

                node.Properties.Add("ElementType", reteNode.ElementType?.FullName);

                foreach (var valueGroup in reteNode.Properties.GroupBy(x => x.Key, x => x.Value))
                {
                    var key = valueGroup.Key;
                    var value = string.Join("; ", valueGroup);
                    node.Properties.Add(key, value);
                }

                foreach (var expressionGroup in reteNode.Expressions.GroupBy(x => x.Key, x => x.Value))
                {
                    var key = expressionGroup.Key;
                    var value = string.Join("; ", expressionGroup);
                    node.Properties.Add(key, value);
                }

                if (reteNode.Rules.Length > 0)
                {
                    var value = string.Join("; ", reteNode.Rules.Select(x => x.Name));
                    node.Properties.Add("Rule", value);
                }

                yield return node;
            }
        }

        private IEnumerable<Link> CreateLinks(IEnumerable<ReteLink> reteLinks)
        {
            foreach (var linkInfo in reteLinks)
            {
                yield return new Link(Id(linkInfo.Source), Id(linkInfo.Target));
            }
        }

        private static IEnumerable<Category> CreateNodeTypeCategories()
        {
            foreach (var nodeType in Enum.GetValues(typeof(NodeType)))
            {
                yield return new Category($"{nodeType}");
            }
        }
        
        private static string GetNodeLabel(ReteNode reteNode)
        {
            var labelParts = new List<object>();
            labelParts.Add(reteNode.NodeType.ToString());
            switch (reteNode.NodeType)
            {
                case NodeType.Type:
                    labelParts.Add(reteNode.ElementType.Name);
                    break;
                case NodeType.Selection:
                    labelParts.AddRange(reteNode.Expressions.Select(x => $"{x.Value.Body}"));
                    break;
                case NodeType.Join:
                    labelParts.AddRange(reteNode.Expressions.Select(x => $"{x.Value.Body}"));
                    break;
                case NodeType.Aggregate:
                    labelParts.Add(reteNode.Properties.Single(x => x.Key == "AggregateName").Value);
                    labelParts.AddRange(reteNode.Expressions.Select(x => $"{x.Key}={x.Value.Body}"));
                    break;
                case NodeType.Binding:
                    labelParts.AddRange(reteNode.Expressions.Select(x => $"{x.Value.Body}"));
                    break;
                case NodeType.Rule:
                    labelParts.Add(reteNode.Rules.Single().Name);
                    break;
            }

            var label = string.Join("\n", labelParts);
            return label;
        }

        private void AddPerformanceMetrics(DirectedGraph graph)
        {
            long maxTotalDurationMilliseconds = 0;
            var reteNodeLookup = _schema.Nodes.ToDictionary(Id);
            foreach (var node in graph.Nodes)
            {
                var reteNode = reteNodeLookup[node.Id];
                INodeMetrics nodeMetrics = _metricsProvider?.FindByNodeId(reteNode.Id);
                if (nodeMetrics == null) continue;

                maxTotalDurationMilliseconds = Math.Max(maxTotalDurationMilliseconds,
                    nodeMetrics.InsertDurationMilliseconds +
                    nodeMetrics.UpdateDurationMilliseconds +
                    nodeMetrics.RetractDurationMilliseconds);
                AddPerformanceMetrics(node, nodeMetrics);
            }

            graph.Styles.AddRange(CreatePerformanceStyles(maxTotalDurationMilliseconds));
        }

        private void AddPerformanceMetrics(Node node, INodeMetrics nodeMetrics)
        {
            if (nodeMetrics.ElementCount.HasValue)
                node.Properties.Add("Perf_ElementCount", nodeMetrics.ElementCount.Value);
            node.Properties.Add("Perf_InsertCount", nodeMetrics.InsertCount);
            node.Properties.Add("Perf_UpdateCount", nodeMetrics.UpdateCount);
            node.Properties.Add("Perf_RetractCount", nodeMetrics.RetractCount);
            node.Properties.Add("Perf_InsertDurationMilliseconds", nodeMetrics.InsertDurationMilliseconds);
            node.Properties.Add("Perf_UpdateDurationMilliseconds", nodeMetrics.UpdateDurationMilliseconds);
            node.Properties.Add("Perf_RetractDurationMilliseconds", nodeMetrics.RetractDurationMilliseconds);
            node.Properties.Add("Perf_TotalFactFlowCount", 
                nodeMetrics.InsertCount + nodeMetrics.UpdateCount + nodeMetrics.RetractCount);
            node.Properties.Add("Perf_TotalDurationMilliseconds",
                nodeMetrics.InsertDurationMilliseconds + nodeMetrics.UpdateDurationMilliseconds + nodeMetrics.RetractDurationMilliseconds);
        }
        
        private IEnumerable<Style> CreatePerformanceStyles(long maxTotalDurationMilliseconds)
        {
            yield return new Style("Link")
                .Condition("Source.Perf_TotalFactFlowCount > 0")
                .Setter(nameof(Link.StrokeThickness),
                    expression: "Math.Min(25,Math.Max(1,Math.Log((Source.Perf_TotalFactFlowCount),2)))");
            yield return new Style("Node")
                .Condition($"HasCategory('{NodeType.AlphaMemory}') or HasCategory('{NodeType.BetaMemory}')")
                .Condition("Perf_ElementCount > 0")
                .Setter(nameof(Node.FontSize),
                    expression: "Math.Min(72,Math.Max(8,8+4*Math.Log(Perf_ElementCount,2)))");
            yield return new Style("Node")
                .Condition("Perf_TotalDurationMilliseconds <= 100")
                .Setter(nameof(Node.Background), value: "Green");
            yield return new Style("Node")
                .Condition("Perf_TotalDurationMilliseconds > 100")
                .Setter(nameof(Node.Background),
                    expression: $"Color.FromRgb(255, 255 - 255 * Perf_TotalDurationMilliseconds / {maxTotalDurationMilliseconds}, 0)");
        }
        
        private IEnumerable<Style> CreateSchemaStyles()
        {
            yield return new Style(nameof(Node))
                .HasCategory($"{NodeType.Root}")
                .Setter(nameof(Node.Background), value: "Black");
            yield return new Style(nameof(Node))
                .HasCategory($"{NodeType.Dummy}")
                .Setter(nameof(Node.Background), value: "Silver");
            yield return new Style(nameof(Node))
                .HasCategory($"{NodeType.Type}")
                .Setter(nameof(Node.Background), value: "Orange");
            yield return new Style(nameof(Node))
                .HasCategory($"{NodeType.Selection}")
                .Setter(nameof(Node.Background), value: "Blue");
            yield return new Style(nameof(Node))
                .HasCategory($"{NodeType.AlphaMemory}")
                .Setter(nameof(Node.Background), value: "Red");
            yield return new Style(nameof(Node))
                .HasCategory($"{NodeType.Join}")
                .Setter(nameof(Node.Background), value: "Blue");
            yield return new Style(nameof(Node))
                .HasCategory($"{NodeType.Not}")
                .Setter(nameof(Node.Background), value: "Brown");
            yield return new Style(nameof(Node))
                .HasCategory($"{NodeType.Exists}")
                .Setter(nameof(Node.Background), value: "Brown");
            yield return new Style(nameof(Node))
                .HasCategory($"{NodeType.Aggregate}")
                .Setter(nameof(Node.Background), value: "Brown");
            yield return new Style(nameof(Node))
                .HasCategory($"{NodeType.Adapter}")
                .Setter(nameof(Node.Background), value: "Silver");
            yield return new Style(nameof(Node))
                .HasCategory($"{NodeType.BetaMemory}")
                .Setter(nameof(Node.Background), value: "Green");
            yield return new Style(nameof(Node))
                .HasCategory($"{NodeType.Binding}")
                .Setter(nameof(Node.Background), value: "LightBlue");
            yield return new Style(nameof(Node))
                .HasCategory($"{NodeType.Rule}")
                .Setter(nameof(Node.Background), value: "Purple");
        }
        
        private IEnumerable<ReteNode> Filter(ReteNode[] reteNodes)
        {
            foreach (var reteNode in reteNodes)
            {
                if (reteNode.NodeType == NodeType.Root) yield return reteNode;
                else if (reteNode.NodeType == NodeType.Dummy) yield return reteNode;
                else if (Accept(reteNode)) yield return reteNode;
            }
        }
        
        private IEnumerable<ReteLink> Filter(ReteLink[] reteLinks)
        {
            foreach (var reteLink in reteLinks)
            {
                if (Accept(reteLink.Source)) yield return reteLink;
                else if (Accept(reteLink.Target)) yield return reteLink;
            }
        }

        private bool Accept(ReteNode reteNode)
        {
            if (_ruleNameFilter == null)
                return true;
            if (reteNode.Rules.Any(r => _ruleNameFilter.Contains(r.Name)))
                return true;
            return false;
        }

        private string Id(ReteNode reteNode)
        {
            return $"{reteNode.Id}";
        }

        private class Utf8StringWriter : StringWriter
        {
            public override Encoding Encoding => Encoding.UTF8;
        }
    }
}
