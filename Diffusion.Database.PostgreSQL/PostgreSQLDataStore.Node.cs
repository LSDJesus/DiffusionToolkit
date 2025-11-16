using Dapper;
using Npgsql;
using System.Text;
using Diffusion.Common;
using Diffusion.IO;
using ComfyUINode = Diffusion.IO.Node;

namespace Diffusion.Database.PostgreSQL;

/// <summary>
/// ComfyUI node operations for PostgreSQLDataStore
/// Handles workflow node storage and retrieval
/// </summary>
public partial class PostgreSQLDataStore
{
    /// <summary>
    /// Add workflow nodes for images
    /// </summary>
    public void AddNodes(NpgsqlConnection conn, IEnumerable<ComfyUINode> nodes, CancellationToken cancellationToken)
    {
        AddNodesInternal(conn, nodes);
    }

    private void AddNodesInternal(NpgsqlConnection conn, IEnumerable<ComfyUINode> nodes)
    {
        var nodeList = nodes.ToList();
        if (nodeList.Count == 0) return;

        // Insert nodes
        var nodeQuery = new StringBuilder("INSERT INTO node (image_id, node_id, name) VALUES ");
        var nodeParams = new DynamicParameters();
        var nodeHolders = new List<string>();

        for (int i = 0; i < nodeList.Count; i++)
        {
            var node = nodeList[i];
            var image = (Models.Image)node.ImageRef;
            
            nodeParams.Add($"@img{i}", image.Id);
            nodeParams.Add($"@nid{i}", node.Id);
            nodeParams.Add($"@name{i}", node.Name);
            
            nodeHolders.Add($"(@img{i}, @nid{i}, @name{i})");
        }

        nodeQuery.Append(string.Join(", ", nodeHolders));
        nodeQuery.Append(" RETURNING id");

        var nodeIds = conn.Query<int>(nodeQuery.ToString(), nodeParams).ToList();

        // Assign returned IDs
        for (int i = 0; i < Math.Min(nodeList.Count, nodeIds.Count); i++)
        {
            nodeList[i].RefId = nodeIds[i];
        }

        // Insert node properties
        var nodeProperties = nodeList
            .SelectMany(n => n.Inputs.Select(p => new
            {
                NodeId = n.RefId,
                Name = p.Name,
                Value = p.Value?.ToString() ?? ""
            }))
            .ToList();

        if (nodeProperties.Count == 0) return;

        // Break into chunks to avoid parameter limit
        foreach (var chunk in nodeProperties.Chunk(100))
        {
            var chunkList = chunk.ToList();
            
            var propQuery = new StringBuilder("INSERT INTO node_property (node_id, name, value) VALUES ");
            var propParams = new DynamicParameters();
            var propHolders = new List<string>();

            for (int i = 0; i < chunkList.Count; i++)
            {
                var prop = chunkList[i];
                propParams.Add($"@nid{i}", prop.NodeId);
                propParams.Add($"@name{i}", prop.Name);
                propParams.Add($"@val{i}", prop.Value);
                
                propHolders.Add($"(@nid{i}, @name{i}, @val{i})");
            }

            propQuery.Append(string.Join(", ", propHolders));

            lock (_lock)
            {
                conn.Execute(propQuery.ToString(), propParams);
            }
        }
    }

    /// <summary>
    /// Update workflow nodes (delete old, insert new)
    /// </summary>
    public void UpdateNodes(NpgsqlConnection conn, IReadOnlyCollection<ComfyUINode> nodes, CancellationToken cancellationToken)
    {
        DeleteNodesInternal(conn, nodes);
        AddNodesInternal(conn, nodes);
    }

    private void DeleteNodesInternal(NpgsqlConnection conn, IReadOnlyCollection<ComfyUINode> nodes)
    {
        var imageIds = nodes
            .Select(d => (Models.Image)d.ImageRef)
            .Select(d => d.Id)
            .Distinct()
            .ToArray();

        if (imageIds.Length == 0) return;

        lock (_lock)
        {
            // Delete node properties first (foreign key constraint)
            conn.Execute(@"
                DELETE FROM node_property 
                WHERE node_id IN (
                    SELECT id FROM node WHERE image_id = ANY(@ImageIds)
                )", new { ImageIds = imageIds });

            // Delete nodes
            conn.Execute(
                "DELETE FROM node WHERE image_id = ANY(@ImageIds)",
                new { ImageIds = imageIds });
        }
    }
}
