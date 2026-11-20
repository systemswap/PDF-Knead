namespace PdfKnead.Core.Pdf.Text.Entities;

/// <summary>What a recognised stretch of text is (KONZEPT.md, section 5.23); each part of an address is a hit of its own.</summary>
public enum EntityKind
{
    Person,
    Organisation,

    /// <summary>The name of a street with its street word („Musterstraße“, „Rue Victor Hugo“), without the house number.</summary>
    Street,
    HouseNumber,
    PostalCode,

    /// <summary>A place: in an address (after the postal code, after a street), after a preposition or — with „Orte überall“ — anywhere.</summary>
    Place,
    Country,
    PostBox,
}

/// <summary>Why a hit is only a suggestion (offered unticked, with the reason beside it).</summary>
public enum EntityDoubt
{
    /// <summary>A sure hit (ticked).</summary>
    None,

    /// <summary>Given and family name are both also common words („Mark Smith“, „Due Date“).</summary>
    AlsoAWord,

    /// <summary>Only a single given or family name of the lists, in the middle of a sentence.</summary>
    SingleName,

    /// <summary>A title that is also a word („Saint“, „General“) before a name, but no given and family name of the lists.</summary>
    TitleIsAWord,

    /// <summary>„Gründlich“: a capitalised word that is no known word — perhaps a name the lists do not know.</summary>
    Unknown,
}

/// <summary>A recognised stretch: position in the text, kind, and whether it is sure (else why not).</summary>
public readonly record struct EntityHit(int Start, int Length, EntityKind Kind, EntityDoubt Doubt = EntityDoubt.None)
{
    public int End => Start + Length;

    /// <summary>Sure hits are ticked; suggestions are offered unticked with their reason.</summary>
    public bool Sure => Doubt == EntityDoubt.None;
}

/// <summary>What the recognition looks for besides persons, companies and addresses.</summary>
/// <param name="PlacesEverywhere">„Orte (überall)“: known places also outside addresses and without a preposition.</param>
/// <param name="Thorough">„Gründlich“: every capitalised word that is no known word as a suggestion (Philter's whitelist turned round).</param>
public sealed record EntityOptions(bool PlacesEverywhere = false, bool Thorough = false);
