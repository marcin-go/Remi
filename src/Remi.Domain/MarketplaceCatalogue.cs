namespace Remi.Domain;

/// <summary>
/// The publication state of a supplier's catalogue for one framework. A pending catalogue
/// never supplies an ID from a previous framework.
/// </summary>
public enum MarketplaceCataloguePublicationStatus
{
    Published,
    PendingPublication,
    NotApplicable,
}

/// <summary>
/// A version-specific Marketplace ID for one recognisable product. ProductKey is stable across
/// frameworks; MarketplaceServiceId deliberately is not.
/// </summary>
public sealed record MarketplaceServiceSuggestion(
    string SupplierName,
    string ProductKey,
    string ProductName,
    FrameworkCode Framework,
    string MarketplaceServiceId,
    string Source,
    DateOnly VerifiedOn);

public sealed record MarketplaceCatalogueState(
    string SupplierName,
    FrameworkCode Framework,
    MarketplaceCataloguePublicationStatus PublicationStatus,
    DateOnly CheckedOn,
    string Source,
    string Notes);

/// <summary>
/// Curated product suggestions used during manual contract registration. Each service is stored
/// against the framework in which its ID was published, so an ID is never assumed to carry over.
/// </summary>
public static class MarketplaceCatalogues
{
    public const string StatMapSupplierName = "STATMAP LTD";

    private static readonly IReadOnlyList<MarketplaceServiceSuggestion> Suggestions =
    [
        // Current G-Cloud 14 services from the public Digital Marketplace supplier search.
        Published("statmap-redistricting", "StatMap Redistricting", FrameworkCode.GCloud14, "183859265339244"),
        Published("statmap-polling-district-review", "StatMap Polling District Review (PDR)", FrameworkCode.GCloud14, "753678834432052"),
        Published("statmap-addressing-geocoding", "StatMap Addressing and Geocoding Data Service", FrameworkCode.GCloud14, "670673251888219"),
        Published("statmap-cluster", "StatMap Cluster", FrameworkCode.GCloud14, "115981361947474"),
        Published("statmap-earthlight-gis", "StatMap Earthlight GIS", FrameworkCode.GCloud14, "779097416520979"),
        Published("statmap-earthlight-public-gis", "StatMap Earthlight Public GIS", FrameworkCode.GCloud14, "454782441161322"),
        Published("statmap-evo-gms", "eVO Gazetteer Management System (GMS)", FrameworkCode.GCloud14, "309912649523824"),
        Published("statmap-evo-snn", "eVO Street Naming and Numbering (SNN)", FrameworkCode.GCloud14, "104649310569296"),
        Published("statmap-horizonext-building-control", "HorizoNext Building Control", FrameworkCode.GCloud14, "420716734760202"),
        Published("statmap-evo-tro", "eVO Traffic Regulation Orders (TRO) / Traffic Management Orders (TMO)", FrameworkCode.GCloud14, "112319169587273"),
        Published("statmap-horizonext-planning", "HorizoNext Planning and Development Management (Development Control)", FrameworkCode.GCloud14, "419925916803898"),
        Published("statmap-horizonext-local-land-charges", "HorizoNext Local Land Charges (LLC)", FrameworkCode.GCloud14, "365927146109460"),

    ];

    private static readonly IReadOnlyList<MarketplaceCatalogueState> States =
    [
        new(
            StatMapSupplierName,
            FrameworkCode.GCloud13,
            MarketplaceCataloguePublicationStatus.NotApplicable,
            new DateOnly(2026, 8, 4),
            string.Empty,
            "G-Cloud 13 is reporting-only. Existing registered contracts supply the Marketplace ID for their invoices; no new-contract suggestions are needed."),
        new(
            StatMapSupplierName,
            FrameworkCode.GCloud14,
            MarketplaceCataloguePublicationStatus.Published,
            new DateOnly(2026, 8, 4),
            "https://www.applytosupply.digitalmarketplace.service.gov.uk/g-cloud/search?q=StatMap",
            "Twelve StatMap Cloud Software services are publicly listed."),
        new(
            StatMapSupplierName,
            FrameworkCode.GCloud15,
            MarketplaceCataloguePublicationStatus.PendingPublication,
            new DateOnly(2026, 8, 4),
            "https://www.applytosupply.digitalmarketplace.service.gov.uk/g-cloud/search?q=StatMap",
            "StatMap's G-Cloud 15 enrolment is known, but no public product or Marketplace ID has been confirmed. Enter the ID from the signed contract only; never reuse an earlier framework's ID."),
    ];

    public static IReadOnlyList<MarketplaceServiceSuggestion> ForFramework(FrameworkCode framework) =>
        Suggestions
            .Where(item => item.Framework == framework)
            .OrderBy(item => item.ProductName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static MarketplaceCatalogueState ForStatMap(FrameworkCode framework) =>
        States.SingleOrDefault(item => item.Framework == framework)
        ?? new MarketplaceCatalogueState(
            StatMapSupplierName,
            framework,
            MarketplaceCataloguePublicationStatus.NotApplicable,
            new DateOnly(2026, 8, 4),
            string.Empty,
            "Digital Marketplace product suggestions apply to G-Cloud only.");

    private static MarketplaceServiceSuggestion Published(
        string productKey,
        string productName,
        FrameworkCode framework,
        string marketplaceServiceId) =>
        new(
            StatMapSupplierName,
            productKey,
            productName,
            framework,
            marketplaceServiceId,
            "https://www.applytosupply.digitalmarketplace.service.gov.uk/g-cloud/search?q=StatMap",
            new DateOnly(2026, 8, 4));

}
