using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Content;

/// <summary>
/// Transparency groups and soft masks (11.4–11.6). Groups are ranges of the flat display list
/// (<see cref="GroupNode"/>); as in widely used readers, a soft mask applies to the run of entries drawn under it as a whole
/// (exact would be to mask each one individually; the result only looks different for overlapping semi-transparent
/// entries under the same mask).
/// </summary>
public sealed partial class PageInterpreter
{
    // innermost open group (−1 = none) and the open run under a mask within it
    private int _group = -1;
    private int _maskRun = -1;
    private int _maskRunMask = -1;
    private byte _maskRunBlend;
    private readonly Stack<(int Group, int Run, int RunMask, byte RunBlend)> _groupFrames = new();

    // masks in masks in …: real files need no more than two levels
    private const int MaxMaskDepth = 8;
    private int _maskDepth;

    // Masks per (mask, CTM when it was set) — the content only once per mask, in mask space (_maskContents) — and the /G streams
    // currently being built (shared with the interpreters of the masks: a mask that uses itself counts as none there)
    private readonly Dictionary<(PdfDictionary Mask, Matrix Ctm), int> _maskIndex = [];
    private readonly Dictionary<PdfDictionary, SoftMask?> _maskContents = new(ReferenceEqualityComparer.Instance);

    /// <summary>Maximum number of distinct mask placements per page (crafted files: a new CTM per operator, millions of masks).</summary>
    private const int MaxMaskPlacements = 10_000;
    private HashSet<PdfStream> _masksBuilding = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Called before every entry: if it lies under a different mask or uses a different blend mode than the previous one, the previous
    /// run ends and a new one begins. The run is composited with the blend mode; otherwise an entry inside would blend only against the
    /// empty layer and look like Normal (blend mode and mask together occur in 4 files of the test corpus). The run is no group of the
    /// file, the mask belongs to its entries: not isolated, so that a group inside it blends with the backdrop (No. 567).
    /// </summary>
    private void EnsureMask()
    {
        if (_gs.SoftMask == _maskRunMask && (_maskRun < 0 || _gs.BlendMode == _maskRunBlend)) return;
        if (_maskRun >= 0)
        {
            _builder.EndGroup(_maskRun);
            _group = _builder.Group(_maskRun).Parent;
            _maskRun = -1;
        }
        _maskRunMask = _gs.SoftMask;
        _maskRunBlend = _gs.BlendMode;
        if (_gs.SoftMask >= 0) _group = _maskRun = _builder.BeginGroup(_group, 1, _gs.BlendMode, false, false, _gs.SoftMask);
    }

    /// <summary>
    /// Begins a form as a transparency group (/Group /S /Transparency): composited as a whole with the current opacity
    /// (ca), blend mode and mask; inside, these start afresh (11.6.6). A non-isolated, non-knockout group without any of these looks
    /// the same as no group, so everything stays as it is. Not so under a mask: inside, the mask starts afresh (an inner one or
    /// /SMask /None) and the outer one applies to the group as a whole, which would be lost without the group.
    /// −1 if no group was begun.
    /// </summary>
    private int BeginTransparencyGroup(PdfDictionary form)
    {
        if (_observer is not null || form.GetDictionary(PdfName.Get("Group")) is not { } group
            || group.GetName(PdfName.Get("S"))?.Value != "Transparency") return -1;
        var isolated = group.GetBoolean(PdfName.Get("I")) ?? false;
        var knockout = group.GetBoolean(PdfName.Get("K")) ?? false;
        if (_gs.FillAlpha >= 1 && _gs.BlendMode == 0 && !isolated && !knockout && _gs.SoftMask < 0)
        {
            // no group needed for the fill, but inside a group the stroking alpha starts at 1 as well (8.4.1, Table 51; before, the outer CA
            // stayed — docs/FEHLERGEDAECHTNIS.md, No. 1123); the state is the form's own and restored after it
            if (_gs.StrokeAlpha < 1)
            {
                _gs.StrokeAlpha = 1;
                _gs.PaintIndex = -1;
            }
            return -1;
        }
        EnsureMask();   // the group lies in the run under the current mask
        _groupFrames.Push((_group, _maskRun, _maskRunMask, _maskRunBlend));
        _group = _builder.BeginGroup(_group, _gs.FillAlpha, _gs.BlendMode, isolated, knockout, -1);
        _maskRun = _maskRunMask = -1;
        _gs.FillAlpha = _gs.StrokeAlpha = 1;
        _gs.BlendMode = 0;
        _gs.SoftMask = -1;
        _gs.PaintIndex = -1;
        return _group;
    }

    private void EndTransparencyGroup(int group)
    {
        if (_maskRun >= 0) _builder.EndGroup(_maskRun);
        _builder.EndGroup(group);
        (_group, _maskRun, _maskRunMask, _maskRunBlend) = _groupFrames.Pop();
    }

    // Device colour spaces redefined by the ICC colour space of the innermost isolated group or the page group, by number of components
    // (index 1, 3, 4); null outside of such a group
    private ColorSpace?[]? _groupDevice;

    /// <summary>
    /// /Group /S /Transparency with /CS of a page or an isolated group: inside, its blending space applies; is it ICC-based, device colours
    /// with the same number of components are read in it until the group ends (ISO 32000-2, 11.7.2; docs/FEHLERGEDAECHTNIS.md, No. 568) —
    /// a DeviceCMYK fill in a group with an ICC CMYK profile takes that profile, not the output intent. A device space as /CS takes that of
    /// the nearest group around it with the same number of components (11.7.2), so it keeps a redefinition from outside (No. 594; before,
    /// it ended it); another /CS (Lab, CalRGB …) ends it; without /CS, or in a non-isolated group, everything stays as it is. The blending
    /// itself stays in sRGB (deliberate deviation, No. 567).
    /// </summary>
    /// <returns>The previous state for <see cref="LeaveGroupSpace"/>.</returns>
    private ColorSpace?[]? EnterGroupSpace(PdfDictionary owner, PdfDictionary? resources, bool page)
    {
        var saved = _groupDevice;
        if (owner.GetDictionary(PdfName.Get("Group")) is not { } group || group.GetName(PdfName.Get("S"))?.Value != "Transparency"
            || !page && group.GetBoolean(PdfName.Get("I")) != true || group.Get(PdfName.Get("CS")) is not { } cs || cs is PdfNull
            || cs is PdfName { Value: "DeviceGray" or "DeviceRGB" or "DeviceCMYK" or "G" or "RGB" or "CMYK" })
            return saved;
        ColorSpace?[]? next = null;
        if (cs is PdfArray { Count: >= 2 } array && array.Get(0) is PdfName { Value: "ICCBased" })
            try
            {
                // an unreadable profile falls back to /Alternate or a device space: then nothing is redefined
                var space = ColorSpace.Create(cs, resources, _doc);
                if (space is { Components: 1 or 3 or 4, IsDeviceCmyk: false } && !ReferenceEquals(space, ColorSpace.DeviceGray)
                    && !ReferenceEquals(space, ColorSpace.DeviceRgb))
                {
                    next = new ColorSpace?[5];
                    next[space.Components] = space;
                }
            }
            catch (Exception e) when (IsBrokenData(e))
            {
            }
        if (ReferenceEquals(next, saved)) return saved;
        _groupDevice = next;
        _deviceSpaces = null;
        return saved;
    }

    private void LeaveGroupSpace(ColorSpace?[]? saved)
    {
        if (ReferenceEquals(saved, _groupDevice)) return;
        _groupDevice = saved;
        _deviceSpaces = null;
    }

    /// <summary>At the end of a build: closes open runs and groups (broken content, aborted forms).</summary>
    private void FinishGroups()
    {
        _builder.MisreadGlyphs = _reading.Misread;   // the page is complete: every build passes here
        // Level by level from the inside out: first the open run (it is then the innermost group), then the group of the frame.
        // Each run must be closed exactly once and the surrounding group must be closed as well (forms close their own groups
        // in a finally block).
        while (true)
        {
            if (_maskRun >= 0)
            {
                _builder.EndGroup(_maskRun);
                _group = _builder.Group(_maskRun).Parent;
                _maskRun = _maskRunMask = -1;
            }
            if (_groupFrames.Count == 0) break;
            _builder.EndGroup(_group);
            (_group, _maskRun, _maskRunMask, _maskRunBlend) = _groupFrames.Pop();
        }
        _group = -1;
    }

    /// <summary>
    /// /SMask from the ExtGState: /None or a mask dictionary. The mask lies in the space of the current CTM (11.6.5.2): its
    /// content is built once per mask and used for each placement with only this matrix (building it per placement made a symbol
    /// with a mask, placed 100,000 times, hold 60 MB).
    /// </summary>
    private int SoftMaskIndex(PdfObject value, PdfDictionary? resources)
    {
        if (_observer is not null || value.AsDictionary() is not { } dict || dict.Get(PdfName.Get("G")) is not PdfStream g) return -1;
        var key = (dict, _gs.Ctm);
        if (_maskIndex.TryGetValue(key, out var index)) return index;
        if (!(Math.Abs(_gs.Ctm.Determinant) > 1e-12)) return -1;   // mapped onto nothing
        if (_maskIndex.Count >= MaxMaskPlacements)
        {
            _doc.AddProblem(Tr.T("Eine Seite ist zu aufwendig und wird nur teilweise gezeigt."));
            return -1;
        }
        if (!_maskContents.TryGetValue(dict, out var mask)) _maskContents[dict] = mask = BuildMask(dict, g, resources);
        index = mask is null ? -1 : _builder.AddMask(mask with { ToPage = _gs.Ctm });
        return _maskIndex[key] = index;
    }

    /// <summary>
    /// Content of a mask in mask space, as a display list of its own built by a separate interpreter whose work counts towards the
    /// work limit of this build; null for recursion, too deep nesting or an empty luminosity mask.
    /// </summary>
    private SoftMask? BuildMask(PdfDictionary dict, PdfStream g, PdfDictionary? resources)
    {
        if (_maskDepth >= MaxMaskDepth || !_masksBuilding.Add(g)) return null;
        // the page in mask space differs for each placement, so "sh" without a clip fills a generous area
        var inner = new PageInterpreter(_doc, _forPrint, _forExport)   // the mask for the same use as the page (No. 767)
        {
            _cancel = _cancel, _maxOperations = Math.Max(0, _maxOperations - _operations), _maskDepth = _maskDepth + 1,
            _patternDepth = _patternDepth, _tilingCells = _tilingCells, _masksBuilding = _masksBuilding,
            _currentPageBox = RectD.FromLtrb(-1e7, -1e7, 1e7, 1e7),
            // the page's output intent and the group's device spaces hold in the mask too (docs/FEHLERGEDAECHTNIS.md, No. 593)
            _pageCmyk = _pageCmyk, _groupDevice = _groupDevice, _pageResources = _pageResources,
        };
        try
        {
            inner.Form(g, Matrix.Identity, resources);
            inner.FinishGroups();
            var luminosity = dict.GetName(PdfName.Get("S"))?.Value != "Alpha";
            var content = inner._builder.Build(inner._currentPageBox);
            // Empty luminosity mask: according to the standard it would block everything (black backdrop), but other readers ignore it
            // (issue14200: a photo with "q Q" as its mask). An empty alpha mask, by contrast, blocks in other readers too
            // (nonisolated_blend_smask: the red must stay invisible)
            if (content.Count == 0 && luminosity) return null;
            // BBox of the group in mask space (after the /Matrix of the group)
            var bbox = Document.PdfRectangle.From(g.Dictionary.Get(PdfName.Get("BBox")));
            var groupMatrix = Matrix.FromArray(g.Dictionary.GetArray(PdfName.Get("Matrix"))) ?? Matrix.Identity;
            return new SoftMask(content, luminosity, luminosity ? Backdrop(dict, g, resources).ToArgb(1) : 0, Transfer(dict.Get(PdfName.Get("TR"))))
            {
                Bounds = bbox is { } b ? groupMatrix.TransformBounds(RectD.FromLtrb(b.Left, b.Bottom, b.Right, b.Top)) : null,
            };
        }
        finally
        {
            _operations += inner._operations + inner._builder.ShadingWork;
            _masksBuilding.Remove(g);
        }
    }

    /// <summary>/BC in the colour space of the group (/Group /CS; without it, chosen by the number of values); black by default.</summary>
    private Rgb Backdrop(PdfDictionary mask, PdfStream g, PdfDictionary? resources)
    {
        var values = PdfFunction.Numbers(mask.GetArray(PdfName.Get("BC")));
        if (values is null || values.Length == 0) return new Rgb(0, 0, 0);
        try
        {
            var space = g.Dictionary.GetDictionary(PdfName.Get("Group"))?.Get(PdfName.Get("CS")) is { } cs and not PdfNull
                ? ColorSpace.Create(cs, g.Dictionary.GetDictionary(PdfName.Resources) ?? _pageResources ?? resources, _doc)
                : values.Length switch { >= 4 => ColorSpace.DeviceCmykOf(_doc), 3 => ColorSpace.DeviceRgb, _ => ColorSpace.DeviceGray };
            return values.Length >= space.Components ? space.ToRgb(values) : new Rgb(0, 0, 0);
        }
        catch (Exception e) when (IsBrokenData(e))
        {
            return new Rgb(0, 0, 0);
        }
    }

    /// <summary>/TR: a function with one input and one output as a table of 256 levels; null for /Identity or if unreadable.</summary>
    private byte[]? Transfer(PdfObject? tr)
    {
        if (tr is null or PdfNull or PdfName) return null;
        try
        {
            if (PdfFunction.Create(tr, _doc) is not { Inputs: 1, Outputs: >= 1 } f) return null;
            var table = new byte[256];
            Span<double> output = stackalloc double[Math.Clamp(f.Outputs, 1, 32)];
            for (var i = 0; i < 256; i++)
            {
                f.Evaluate([i / 255.0], output);
                table[i] = (byte)Math.Round(Math.Clamp(double.IsNaN(output[0]) ? 0 : output[0], 0, 1) * 255);
            }
            return table;
        }
        catch (Exception e) when (IsBrokenData(e))
        {
            return null;
        }
    }
}
