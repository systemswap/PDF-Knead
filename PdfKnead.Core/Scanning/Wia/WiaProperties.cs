using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PdfKnead.Core.Scanning.Wia;

/// <summary>How the allowed values of a WIA property are given (WiaDef.h: WIA_PROP_RANGE, WIA_PROP_LIST, WIA_PROP_FLAG, else WIA_PROP_NONE).</summary>
internal enum WiaAllowedKind
{
    /// <summary>Any value (or the driver does not say).</summary>
    None,
    /// <summary>A range with a step.</summary>
    Range,
    /// <summary>A list of values.</summary>
    List,
    /// <summary>A set of bits that may be combined.</summary>
    Flags,
}

/// <summary>What a property allows, as the driver reports it.</summary>
/// <param name="Kind">How the values are given.</param>
/// <param name="Writable">Whether the property may be set.</param>
/// <param name="Values">The values of a list; for flags the one mask of the allowed bits.</param>
/// <param name="Min">The lowest value of a range.</param>
/// <param name="Max">The highest value of a range.</param>
/// <param name="Step">The step of a range (0 or 1 = any).</param>
/// <param name="Guids">The values of a list of GUIDs (image formats).</param>
internal sealed record WiaAllowed(WiaAllowedKind Kind, bool Writable, IReadOnlyList<int> Values, int Min = 0, int Max = 0, int Step = 0,
    IReadOnlyList<Guid>? Guids = null)
{
    /// <summary>The allowed bits of a flag property.</summary>
    public int Bits => Kind == WiaAllowedKind.Flags && Values.Count > 0 ? Values[0] : 0;

    /// <summary>Whether <paramref name="value"/> is allowed.</summary>
    public bool Allows(int value) => Kind switch
    {
        WiaAllowedKind.Range => value >= Min && value <= Max && (Step <= 1 || (value - Min) % Step == 0),
        WiaAllowedKind.List => Values.Contains(value),
        WiaAllowedKind.Flags => (value & ~Bits) == 0,
        _ => true,
    };
}

/// <summary>
/// The properties of a WIA item or device as the mapping needs them: read a number, a text or a GUID, read what is allowed, set a value.
/// Reading never throws (a missing property is null); setting returns the HRESULT. Implemented over COM and, in the tests, over a table.
/// </summary>
internal interface IWiaProperties
{
    /// <summary>A number (VT_I4, VT_UI4, VT_I2 …), or null if the property is missing or not a number.</summary>
    int? Int(int id);

    /// <summary>A text (VT_BSTR, VT_LPWSTR), or null.</summary>
    string? Text(int id);

    /// <summary>A GUID (VT_CLSID), or null.</summary>
    Guid? Guid(int id);

    /// <summary>What the property allows, or null if it is missing.</summary>
    WiaAllowed? Allowed(int id);

    /// <summary>Sets a number; returns the HRESULT.</summary>
    int Set(int id, int value);

    /// <summary>Sets a GUID; returns the HRESULT.</summary>
    int Set(int id, Guid value);
}

/// <summary>The properties of a WIA object over its IWiaPropertyStorage.</summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class WiaPropertyStorage(IWiaPropertyStorage storage) : IWiaProperties
{
    /// <summary>The most elements read from a vector of allowed values (a driver reporting millions is broken).</summary>
    internal const int MaxElements = 4096;

    /// <summary>The longest text read (names and ids are short).</summary>
    internal const int MaxText = 4096;

    /// <summary>PID_FIRST_USABLE (propidl.h): the first id for names written with WriteMultiple — unused, but required.</summary>
    private const uint FirstUsable = 2;

    public int? Int(int id) => Read(id, static v => NumberOf(v));

    public string? Text(int id) => Read(id, static v => v.Vt switch
    {
        Wia.VtBstr when v.Pointer != 0 => Cut(Marshal.PtrToStringBSTR(v.Pointer)),
        Wia.VtLpwstr when v.Pointer != 0 => Cut(Marshal.PtrToStringUni(v.Pointer)),
        _ => null,
    });

    public Guid? Guid(int id) => Read<Guid?>(id, static v => v.Vt == Wia.VtClsid && v.Pointer != 0 ? *(Guid*)v.Pointer : null);

    public WiaAllowed? Allowed(int id)
    {
        var spec = PropSpec.Of(id);
        uint flags = 0;
        PropVariant value = default;
        var hr = storage.GetPropertyAttributes(1, &spec, &flags, &value);
        try
        {
            if (hr != Wia.SOk) return null;
            var writable = (flags & Wia.PropWrite) != 0;
            if ((value.Vt & Wia.VtVector) == 0 || value.Elements == 0) return new WiaAllowed(WiaAllowedKind.None, writable, []);
            var type = (ushort)(value.Vt & ~Wia.VtVector);
            var count = (int)Math.Min(value.Count, MaxElements);
            if (type == Wia.VtClsid)
            {
                // a list of GUIDs (formats): taken whole — whether a slot holds the nominal value is not needed to see what is allowed
                var guids = new Guid[count];
                for (var i = 0; i < count; i++) guids[i] = ((Guid*)value.Elements)[i];
                return new WiaAllowed(WiaAllowedKind.List, writable, [], Guids: guids);
            }
            var numbers = Numbers(type, value.Elements, count);
            if ((flags & Wia.PropRange) != 0 && numbers.Length > Wia.RangeStep)
                return new WiaAllowed(WiaAllowedKind.Range, writable, [], numbers[Wia.RangeMin], numbers[Wia.RangeMax], numbers[Wia.RangeStep]);
            if ((flags & Wia.PropList) != 0 && numbers.Length > Wia.ListValues)
            {
                // the first slot holds the number of values, the second the nominal value; never trust the count beyond the vector
                var listed = Math.Clamp(numbers[Wia.ListCount], 0, numbers.Length - Wia.ListValues);
                return new WiaAllowed(WiaAllowedKind.List, writable, numbers.AsSpan(Wia.ListValues, listed).ToArray());
            }
            if ((flags & Wia.PropFlag) != 0 && numbers.Length > Wia.FlagValues)
                return new WiaAllowed(WiaAllowedKind.Flags, writable, [numbers[Wia.FlagValues]]);
            return new WiaAllowed(WiaAllowedKind.None, writable, []);
        }
        finally
        {
            Com.PropVariantClear(&value);
        }
    }

    public int Set(int id, int value)
    {
        var spec = PropSpec.Of(id);
        var v = new PropVariant { Vt = Wia.VtI4, I4 = value };
        return storage.WriteMultiple(1, &spec, &v, FirstUsable);
    }

    public int Set(int id, Guid value)
    {
        var spec = PropSpec.Of(id);
        var v = new PropVariant { Vt = Wia.VtClsid, Pointer = (nint)(&value) };
        return storage.WriteMultiple(1, &spec, &v, FirstUsable);
    }

    private T? Read<T>(int id, Func<PropVariant, T?> convert)
    {
        var spec = PropSpec.Of(id);
        PropVariant value = default;
        var hr = storage.ReadMultiple(1, &spec, &value);
        try
        {
            // S_FALSE: none of the asked properties exists
            return hr == Wia.SOk ? convert(value) : default;
        }
        finally
        {
            Com.PropVariantClear(&value);
        }
    }

    private static int? NumberOf(PropVariant v) => v.Vt switch
    {
        Wia.VtI4 or Wia.VtInt or Wia.VtUi4 or Wia.VtUint => v.I4,
        Wia.VtI2 => (short)(v.I4 & 0xFFFF),
        Wia.VtUi2 => v.I4 & 0xFFFF,
        _ => null,
    };

    private static int[] Numbers(ushort type, nint elements, int count)
    {
        var size = type is Wia.VtI2 or Wia.VtUi2 ? 2 : type is Wia.VtI4 or Wia.VtUi4 or Wia.VtInt or Wia.VtUint ? 4 : 0;
        if (size == 0) return [];
        var result = new int[count];
        for (var i = 0; i < count; i++)
            result[i] = size == 4 ? ((int*)elements)[i] : type == Wia.VtI2 ? ((short*)elements)[i] : ((ushort*)elements)[i];
        return result;
    }

    private static string? Cut(string? text) => text is { Length: > MaxText } ? text[..MaxText] : text;
}
