using Microsoft.CodeAnalysis;

/// <summary>
/// Edge resolution-provenance classification (Fathom row
/// edge-resolution-provenance 5.0.80; H2 of the 2026-06-07 context-sufficiency
/// audit). An edge whose target is NOT an in-source graph node carries
/// <c>metadata.resolutionProvenance</c> so the orchestrator — and the audit's
/// pass-2 coverage scan — can tell an honest external boundary from an analyzer
/// bug WITHOUT re-reading source. In-source resolved edges stay untagged
/// (absence == <c>in-source</c>).
///
/// H2 emits the two values cleanly determinable at the external-edge emit site:
///   • <c>external-library</c> — resolves to a referenced-assembly symbol
///     (BCL / NuGet), e.g. <c>this.Rows.Add(x)</c> → System.Data.
///   • <c>dynamic</c> — a string/value-keyed indexer with no static member
///     target (<c>Session[k]=v</c>, <c>ViewState[k]</c>, dictionary indexers);
///     the irreducible reflective tail the LLM must treat as unresolved.
///
/// DEFERRED (documented, not silently absent):
///   • <c>generated-companion</c> — targets in NON-ingested generated/markup
///     companions (WebForms <c>.ascx</c> control fields) do not resolve today;
///     they land with H4 (interactionSurface). Targets in INGESTED generated
///     files (typed-DataSet <c>.Designer.cs</c>) are in-source; their
///     generated-ness is answerable from H1's <c>[GeneratedCode]</c> annotation.
///   • <c>framework-injected</c> — needs base-chain analysis; external framework
///     members default to <c>external-library</c> for now.
///   • <c>resolver-gap</c> — already surfaced file-level by the references-free
///     Limitation (Fathom 5.0.72); a per-edge tag is redundant pending need.
/// </summary>
internal static class ProvenanceHelpers
{
    internal const string InSource = "in-source";
    internal const string ExternalLibrary = "external-library";
    internal const string GeneratedCompanion = "generated-companion";
    internal const string FrameworkInjected = "framework-injected";
    internal const string Dynamic = "dynamic";
    internal const string ResolverGap = "resolver-gap";

    /// <summary>Provenance for an external (no in-source declaration) method
    /// call. External library/BCL → <c>external-library</c>.</summary>
    internal static string ClassifyExternalCall(IMethodSymbol method) => ExternalLibrary;

    /// <summary>Provenance for an external property / indexer access. A
    /// string/value-keyed indexer (Session / ViewState / Items / Dictionary)
    /// has no static member target → <c>dynamic</c>; a named external property
    /// → <c>external-library</c>.</summary>
    internal static string ClassifyExternalProperty(IPropertySymbol prop)
        => prop.IsIndexer ? Dynamic : ExternalLibrary;

    /// <summary>Provenance for an external (no in-source declaration) TYPE
    /// reference — heritage base/interface, `references` return/parameter
    /// type, generic constraint, or an overridden member's containing type
    /// (Fathom rows 3.1.0.15/3.1.0.16). A type has no "dynamic" analogue
    /// (unlike a property/indexer access) — always
    /// <c>external-library</c>.</summary>
    internal static string ClassifyExternalType(INamedTypeSymbol type) => ExternalLibrary;
}
