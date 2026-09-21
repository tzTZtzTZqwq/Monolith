using System.Numerics;
using Content.Shared._NSV.Administration;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface;
using Robust.Shared.Input;
using Robust.Shared.Maths;

namespace Content.Client._NSV.Administration.UI;

/// <summary>
/// Draws the strategic starmap: nodes positioned by their prototype coordinates, edges from
/// their reciprocal connections, coloured by sector state. Clicking near a node selects it and
/// raises <see cref="OnNodeSelected"/>.
/// </summary>
public sealed class NsvStarmapControl : Control
{
    private const float NodeRadius = 7f;
    private const float SelectRadius = 16f;
    private const float Padding = 28f;

    private readonly Font _font;

    private NsvSectorMonitorNode[] _nodes = System.Array.Empty<NsvSectorMonitorNode>();

    public Action<string>? OnNodeSelected;
    private string? _selectedNodeId;

    public NsvStarmapControl()
    {
        var cache = IoCManager.Resolve<IResourceCache>();
        _font = new VectorFont(cache.GetResource<FontResource>("/Fonts/NotoSans/NotoSans-Regular.ttf"), 9);
        MinSize = new Vector2(360, 360);
        RectClipContent = true;
        MouseFilter = MouseFilterMode.Stop;
    }

    public void SetNodes(NsvSectorMonitorNode[] nodes)
    {
        _nodes = nodes;
        if (_selectedNodeId != null && System.Array.TrueForAll(_nodes, n => n.Id != _selectedNodeId))
            _selectedNodeId = null;
    }

    public void SetSelected(string? nodeId)
    {
        _selectedNodeId = nodeId;
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);

        if (_nodes.Length == 0)
            return;

        var minX = float.MaxValue;
        var minY = float.MaxValue;
        var maxX = float.MinValue;
        var maxY = float.MinValue;
        foreach (var node in _nodes)
        {
            minX = MathF.Min(minX, node.PosX);
            minY = MathF.Min(minY, node.PosY);
            maxX = MathF.Max(maxX, node.PosX);
            maxY = MathF.Max(maxY, node.PosY);
        }

        var size = PixelSize;
        var pad = Padding * UIScale;

        Vector2 ToScreen(float x, float y)
        {
            var nx = maxX > minX ? (x - minX) / (maxX - minX) : 0.5f;
            var ny = maxY > minY ? (y - minY) / (maxY - minY) : 0.5f;
            return new Vector2(
                pad + nx * (size.X - 2 * pad),
                pad + ny * (size.Y - 2 * pad));
        }

        // Edges first so nodes sit on top. Connections are reciprocal; draw each once.
        var positions = new Dictionary<string, Vector2>();
        foreach (var node in _nodes)
            positions[node.Id] = ToScreen(node.PosX, node.PosY);

        var edgeColor = new Color(0.35f, 0.4f, 0.5f);
        foreach (var node in _nodes)
        {
            foreach (var target in node.Connections)
            {
                if (!positions.TryGetValue(target, out var targetPos))
                    continue;
                // Draw only when this id sorts first, so each undirected edge is drawn once.
                if (string.CompareOrdinal(node.Id, target) < 0)
                    handle.DrawLine(positions[node.Id], targetPos, edgeColor);
            }
        }

        foreach (var node in _nodes)
        {
            var pos = positions[node.Id];
            var radius = NodeRadius * UIScale;

            if (node.Id == _selectedNodeId)
                handle.DrawCircle(pos, radius + 3 * UIScale, Color.White);

            handle.DrawCircle(pos, radius, NodeColor(node));
            handle.DrawString(_font, pos + new Vector2(radius + 2 * UIScale, -radius), node.Id, Color.White);
        }
    }

    private static Color NodeColor(NsvSectorMonitorNode node)
    {
        if (!node.HasSector)
            return new Color(0.5f, 0.5f, 0.5f);

        return node.SectorState switch
        {
            "Sleeping" => new Color(0.3f, 0.5f, 1f),
            "Failed" => new Color(0.9f, 0.3f, 0.3f),
            _ => new Color(0.3f, 0.9f, 0.4f),
        };
    }

    protected override void KeyBindDown(GUIBoundKeyEventArgs args)
    {
        base.KeyBindDown(args);

        if (args.Function != EngineKeyFunctions.UIClick || _nodes.Length == 0)
            return;

        var minX = float.MaxValue;
        var minY = float.MaxValue;
        var maxX = float.MinValue;
        var maxY = float.MinValue;
        foreach (var node in _nodes)
        {
            minX = MathF.Min(minX, node.PosX);
            minY = MathF.Min(minY, node.PosY);
            maxX = MathF.Max(maxX, node.PosX);
            maxY = MathF.Max(maxY, node.PosY);
        }

        var size = Size;
        var click = args.RelativePosition;

        string? best = null;
        var bestDist = SelectRadius;
        foreach (var node in _nodes)
        {
            var nx = maxX > minX ? (node.PosX - minX) / (maxX - minX) : 0.5f;
            var ny = maxY > minY ? (node.PosY - minY) / (maxY - minY) : 0.5f;
            var screen = new Vector2(
                Padding + nx * (size.X - 2 * Padding),
                Padding + ny * (size.Y - 2 * Padding));
            var dist = (screen - click).Length();
            if (dist < bestDist)
            {
                bestDist = dist;
                best = node.Id;
            }
        }

        if (best == null)
            return;

        _selectedNodeId = best;
        args.Handle();
        OnNodeSelected?.Invoke(best);
    }
}
